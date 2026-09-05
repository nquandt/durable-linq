using System.Text;
using Xunit;

namespace DurableLinq.Tests.Contract;

/// <summary>
/// Behavioral contract every <see cref="IWorkflowStore"/> must satisfy. Store packages derive from this
/// class and implement <see cref="CreateStoreAsync"/>.
/// </summary>
public abstract class WorkflowStoreContractTests : IAsyncLifetime
{
    protected IWorkflowStore Store { get; private set; } = null!;

    protected abstract Task<IWorkflowStore> CreateStoreAsync();
    protected virtual Task DisposeStoreAsync(IWorkflowStore store) => Task.CompletedTask;

    public async Task InitializeAsync() => Store = await CreateStoreAsync();
    public Task DisposeAsync() => DisposeStoreAsync(Store);

    protected static string NewKey(string? id = null) => InstanceKey.Format("contract", "wf", 1, id ?? Guid.NewGuid().ToString("N"));

    protected static InstanceHeader NewHeader(string key)
    {
        var k = InstanceKey.Parse(key);
        var now = DateTimeOffset.UtcNow;
        return new InstanceHeader
        {
            Key = key,
            Namespace = k.Namespace,
            WorkflowName = k.WorkflowName,
            Version = k.Version,
            InstanceId = k.InstanceId,
            Input = Encoding.UTF8.GetBytes("{\"x\":1}"),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    protected async Task<string> CreateInstanceAsync(string? id = null)
    {
        var key = NewKey(id);
        Assert.True(await Store.TryCreateAsync(NewHeader(key)));
        return key;
    }

    // ---------------------------------------------------------------- header

    [Fact]
    public async Task GetHeader_returns_null_for_unknown_key()
    {
        Assert.Null(await Store.GetHeaderAsync(NewKey()));
    }

    [Fact]
    public async Task TryCreate_returns_false_when_exists_and_keeps_original()
    {
        var key = await CreateInstanceAsync();
        var second = NewHeader(key) with { Input = Encoding.UTF8.GetBytes("{\"x\":2}") };
        Assert.False(await Store.TryCreateAsync(second));
        var stored = await Store.GetHeaderAsync(key);
        Assert.NotNull(stored);
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString(stored!.Input!));
        Assert.Equal(WorkflowStatus.Running, stored.Status);
    }

    [Fact]
    public async Task UpdateStatus_completed_stores_result()
    {
        var key = await CreateInstanceAsync();
        await Store.UpdateStatusAsync(key, WorkflowStatus.Completed, [1, 2, 3]);
        var h = await Store.GetHeaderAsync(key);
        Assert.Equal(WorkflowStatus.Completed, h!.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, h.Result);
        Assert.Null(h.Error);
    }

    [Fact]
    public async Task UpdateStatus_failed_stores_error_and_running_clears_it()
    {
        var key = await CreateInstanceAsync();
        await Store.UpdateStatusAsync(key, WorkflowStatus.Failed, [9]);
        var h = await Store.GetHeaderAsync(key);
        Assert.Equal(WorkflowStatus.Failed, h!.Status);
        Assert.Equal(new byte[] { 9 }, h.Error);

        await Store.UpdateStatusAsync(key, WorkflowStatus.Running, null);
        h = await Store.GetHeaderAsync(key);
        Assert.Equal(WorkflowStatus.Running, h!.Status);
        Assert.Null(h.Error);
    }

    // ---------------------------------------------------------------- leases

    [Fact]
    public async Task Lease_is_exclusive_until_released()
    {
        var key = await CreateInstanceAsync();
        var a = await Store.TryAcquireLeaseAsync(key, "a", TimeSpan.FromMinutes(1));
        Assert.NotNull(a);
        Assert.Null(await Store.TryAcquireLeaseAsync(key, "b", TimeSpan.FromMinutes(1)));

        await Store.ReleaseLeaseAsync(a!);
        var b = await Store.TryAcquireLeaseAsync(key, "b", TimeSpan.FromMinutes(1));
        Assert.NotNull(b);
    }

    [Fact]
    public async Task Expired_lease_can_be_taken_by_another_owner()
    {
        var key = await CreateInstanceAsync();
        var a = await Store.TryAcquireLeaseAsync(key, "a", TimeSpan.FromMilliseconds(50));
        Assert.NotNull(a);
        await Task.Delay(150);
        var b = await Store.TryAcquireLeaseAsync(key, "b", TimeSpan.FromMinutes(1));
        Assert.NotNull(b);
        Assert.False(await Store.RenewLeaseAsync(a!, TimeSpan.FromMinutes(1)), "stale lease must not renew");
    }

    [Fact]
    public async Task Renew_extends_and_release_with_wrong_token_is_ignored()
    {
        var key = await CreateInstanceAsync();
        var a = (await Store.TryAcquireLeaseAsync(key, "a", TimeSpan.FromSeconds(1)))!;
        Assert.True(await Store.RenewLeaseAsync(a, TimeSpan.FromMinutes(5)));
        var h = await Store.GetHeaderAsync(key);
        Assert.True(h!.LeaseExpires > DateTimeOffset.UtcNow.AddMinutes(4));

        await Store.ReleaseLeaseAsync(a with { Token = Guid.NewGuid() });
        Assert.Null(await Store.TryAcquireLeaseAsync(key, "b", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Concurrent_acquires_yield_exactly_one_winner()
    {
        var key = await CreateInstanceAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => Store.TryAcquireLeaseAsync(key, $"owner-{i}", TimeSpan.FromMinutes(1))));
        Assert.Equal(1, results.Count(r => r is not null));
    }

    // ---------------------------------------------------------------- steps

    [Fact]
    public async Task Steps_append_in_order_and_read_back()
    {
        var key = await CreateInstanceAsync();
        Assert.Null(await Store.TryGetStepAsync(key, 0));
        var now = DateTimeOffset.UtcNow;
        await Store.AppendStepAsync(key, new StepEntry(0, "a", [1], now));
        await Store.AppendStepAsync(key, new StepEntry(1, "b", null, now));
        await Store.AppendStepAsync(key, new StepEntry(2, "c", [3, 3], now));
        await Store.AppendStepAsync(key, new StepEntry(3, "d", [4], now, Failed: true));

        var s1 = await Store.TryGetStepAsync(key, 1);
        Assert.Equal("b", s1!.Name);
        Assert.True(s1.Payload is null || s1.Payload.Length == 0);
        Assert.Null(await Store.TryGetStepAsync(key, 4));
        Assert.False((await Store.TryGetStepAsync(key, 2))!.Failed);
        Assert.True((await Store.TryGetStepAsync(key, 3))!.Failed);

        var all = await Store.ListStepsAsync(key);
        Assert.Equal(new[] { "a", "b", "c", "d" }, all.Select(s => s.Name));
        Assert.Equal(new byte[] { 3, 3 }, all[2].Payload);
        Assert.True(all[3].Failed);
    }

    [Fact]
    public async Task Duplicate_seq_is_rejected()
    {
        var key = await CreateInstanceAsync();
        await Store.AppendStepAsync(key, new StepEntry(0, "a", [1], DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DuplicateStepException>(() => Store.AppendStepAsync(key, new StepEntry(0, "a", [2], DateTimeOffset.UtcNow)));
        var s = await Store.TryGetStepAsync(key, 0);
        Assert.Equal(new byte[] { 1 }, s!.Payload);
    }

    [Fact]
    public async Task Concurrent_appends_of_same_seq_yield_one_winner()
    {
        var key = await CreateInstanceAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try { await Store.AppendStepAsync(key, new StepEntry(0, "a", [(byte)i], DateTimeOffset.UtcNow)); return true; }
            catch (DuplicateStepException) { return false; }
            catch (DurableException) { return false; }
        }));
        Assert.Equal(1, results.Count(r => r));
        Assert.Single(await Store.ListStepsAsync(key));
    }

    // ---------------------------------------------------------------- signals

    [Fact]
    public async Task Signals_are_fifo_per_name()
    {
        var key = await CreateInstanceAsync();
        Assert.Null(await Store.TryTakeSignalAsync(key, "s"));
        await Store.AppendSignalAsync(key, "s", [1]);
        await Store.AppendSignalAsync(key, "s", [2]);
        await Store.AppendSignalAsync(key, "other", [9]);

        Assert.Equal(new byte[] { 1 }, await Store.TryTakeSignalAsync(key, "s"));
        Assert.Equal(new byte[] { 2 }, await Store.TryTakeSignalAsync(key, "s"));
        Assert.Null(await Store.TryTakeSignalAsync(key, "s"));
        Assert.Equal(new byte[] { 9 }, await Store.TryTakeSignalAsync(key, "other"));
    }

    [Fact]
    public async Task WaitForSignal_returns_when_signal_arrives()
    {
        var key = await CreateInstanceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiting = Store.WaitForSignalAsync(key, "go", cts.Token);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        await Store.AppendSignalAsync(key, "go", [7]);
        Assert.Equal(new byte[] { 7 }, await waiting);
    }

    [Fact]
    public async Task WaitForSignal_honors_cancellation()
    {
        var key = await CreateInstanceAsync();
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.WaitForSignalAsync(key, "never", cts.Token));
    }

    // ---------------------------------------------------------------- recovery and inspection

    [Fact]
    public async Task ListRunnable_returns_running_with_expired_or_missing_lease()
    {
        var noLease = await CreateInstanceAsync();
        var expired = await CreateInstanceAsync();
        var live = await CreateInstanceAsync();
        var done = await CreateInstanceAsync();

        await Store.TryAcquireLeaseAsync(expired, "x", TimeSpan.FromMilliseconds(1));
        await Store.TryAcquireLeaseAsync(live, "x", TimeSpan.FromMinutes(5));
        await Store.UpdateStatusAsync(done, WorkflowStatus.Completed, null);
        await Task.Delay(50);

        var keys = new List<string>();
        await foreach (var k in Store.ListRunnableAsync(DateTimeOffset.UtcNow)) keys.Add(k);

        Assert.Contains(noLease, keys);
        Assert.Contains(expired, keys);
        Assert.DoesNotContain(live, keys);
        Assert.DoesNotContain(done, keys);
    }

    [Fact]
    public async Task ListInstances_filters_and_orders_newest_first()
    {
        var ns = "q-" + Guid.NewGuid().ToString("N");
        var k1 = InstanceKey.Format(ns, "wf", 1, "first");
        var k2 = InstanceKey.Format(ns, "wf", 1, "second");
        var k3 = InstanceKey.Format(ns, "other", 2, "third");
        var h1 = NewHeader(NewKey()) with { Key = k1, Namespace = ns, InstanceId = "first", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2) };
        var h2 = NewHeader(NewKey()) with { Key = k2, Namespace = ns, InstanceId = "second", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var h3 = NewHeader(NewKey()) with { Key = k3, Namespace = ns, WorkflowName = "other", Version = 2, InstanceId = "third", CreatedAt = DateTimeOffset.UtcNow };
        await Store.TryCreateAsync(h1);
        await Store.TryCreateAsync(h2);
        await Store.TryCreateAsync(h3);
        await Store.UpdateStatusAsync(k2, WorkflowStatus.Completed, null);

        var all = await Store.ListInstancesAsync(new InstanceQuery { Namespace = ns });
        Assert.Equal(new[] { k3, k2, k1 }, all.Select(h => h.Key));

        var wfOnly = await Store.ListInstancesAsync(new InstanceQuery { Namespace = ns, WorkflowName = "wf" });
        Assert.Equal(new[] { k2, k1 }, wfOnly.Select(h => h.Key));

        var running = await Store.ListInstancesAsync(new InstanceQuery { Namespace = ns, Status = WorkflowStatus.Running });
        Assert.Equal(new[] { k3, k1 }, running.Select(h => h.Key));

        var search = await Store.ListInstancesAsync(new InstanceQuery { Namespace = ns, InstanceIdContains = "SEC" });
        Assert.Equal(new[] { k2 }, search.Select(h => h.Key));

        var paged = await Store.ListInstancesAsync(new InstanceQuery { Namespace = ns, Limit = 1, Offset = 1 });
        Assert.Equal(new[] { k2 }, paged.Select(h => h.Key));
    }

    [Fact]
    public async Task Delete_removes_everything()
    {
        var key = await CreateInstanceAsync();
        await Store.AppendStepAsync(key, new StepEntry(0, "a", [1], DateTimeOffset.UtcNow));
        await Store.AppendSignalAsync(key, "s", [1]);
        await Store.DeleteAsync(key);
        Assert.Null(await Store.GetHeaderAsync(key));
        Assert.Empty(await Store.ListStepsAsync(key));
        Assert.True(await Store.TryCreateAsync(NewHeader(key)), "key is reusable after delete");
        Assert.Null(await Store.TryGetStepAsync(key, 0));
    }
}
