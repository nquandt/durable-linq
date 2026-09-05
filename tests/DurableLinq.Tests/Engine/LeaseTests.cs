using DurableLinq.Stores;
using Xunit;

namespace DurableLinq.Tests.Engine;

public sealed class LeaseTests
{
    /// <summary>Wraps a store and makes every renewal fail, as if another host took the lease.</summary>
    private sealed class RenewalFailsStore(IWorkflowStore inner) : IWorkflowStore
    {
        public Task<bool> RenewLeaseAsync(Lease lease, TimeSpan ttl, CancellationToken ct = default) => Task.FromResult(false);

        public Task<InstanceHeader?> GetHeaderAsync(string key, CancellationToken ct = default) => inner.GetHeaderAsync(key, ct);
        public Task<bool> TryCreateAsync(InstanceHeader header, CancellationToken ct = default) => inner.TryCreateAsync(header, ct);
        public Task UpdateStatusAsync(string key, WorkflowStatus status, byte[]? payload, CancellationToken ct = default) => inner.UpdateStatusAsync(key, status, payload, ct);
        public Task<Lease?> TryAcquireLeaseAsync(string key, string owner, TimeSpan ttl, CancellationToken ct = default) => inner.TryAcquireLeaseAsync(key, owner, ttl, ct);
        public Task ReleaseLeaseAsync(Lease lease, CancellationToken ct = default) => inner.ReleaseLeaseAsync(lease, ct);
        public Task<StepEntry?> TryGetStepAsync(string key, int seq, CancellationToken ct = default) => inner.TryGetStepAsync(key, seq, ct);
        public Task AppendStepAsync(string key, StepEntry entry, CancellationToken ct = default) => inner.AppendStepAsync(key, entry, ct);
        public Task<IReadOnlyList<StepEntry>> ListStepsAsync(string key, CancellationToken ct = default) => inner.ListStepsAsync(key, ct);
        public Task AppendSignalAsync(string key, string name, byte[] payload, CancellationToken ct = default) => inner.AppendSignalAsync(key, name, payload, ct);
        public Task<byte[]?> TryTakeSignalAsync(string key, string name, CancellationToken ct = default) => inner.TryTakeSignalAsync(key, name, ct);
        public IAsyncEnumerable<string> ListRunnableAsync(DateTimeOffset expiredBefore, CancellationToken ct = default) => inner.ListRunnableAsync(expiredBefore, ct);
        public Task<IReadOnlyList<InstanceHeader>> ListInstancesAsync(InstanceQuery query, CancellationToken ct = default) => inner.ListInstancesAsync(query, ct);
        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    }

    [Fact]
    public async Task Losing_the_lease_cancels_the_run_and_leaves_the_instance_running()
    {
        var events = new DurableEventSource();
        var kinds = new List<DurableEventKind>();
        events.Raised += e => kinds.Add(e.Kind);
        var store = new RenewalFailsStore(new InMemoryWorkflowStore());
        var runner = new WorkflowRunner(store, new WorkflowRunnerOptions
        {
            Namespace = "tests",
            LeaseTtl = TimeSpan.FromMilliseconds(200),
            Events = events,
        });
        var def = WorkflowDefinition.Create<Unit, int>("slow", 1, _ =>
            Durable.Step("slow", async ct => { await Task.Delay(5000, ct); return 1; }));

        await Assert.ThrowsAsync<LeaseLostException>(() => runner.RunAsync(def, "k", Unit.Value));

        Assert.Contains(DurableEventKind.LeaseLost, kinds);
        Assert.Equal(WorkflowStatus.Running, await runner.GetStatusAsync(def, "k"));
        Assert.Empty(await store.ListStepsAsync(runner.KeyFor(def, "k")));
    }

    [Fact]
    public async Task File_store_takes_over_a_lease_held_by_a_dead_process_on_this_machine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "durablelinq-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileWorkflowStore(dir);
            var key = InstanceKey.Format("tests", "wf", 1, "dead");
            var now = DateTimeOffset.UtcNow;
            await store.TryCreateAsync(new InstanceHeader
            {
                Key = key,
                Namespace = "tests",
                WorkflowName = "wf",
                Version = 1,
                InstanceId = "dead",
                CreatedAt = now,
                UpdatedAt = now,
            });

            // a process id that cannot exist: pids are multiples of four on Windows, and this one is far beyond any real range
            var deadOwner = $"{Environment.MachineName}:{int.MaxValue - 1}";
            Assert.NotNull(await store.TryAcquireLeaseAsync(key, deadOwner, TimeSpan.FromMinutes(10)));

            var taken = await store.TryAcquireLeaseAsync(key, "restarted-host", TimeSpan.FromMinutes(1));
            Assert.NotNull(taken);
            Assert.Equal("restarted-host", (await store.GetHeaderAsync(key))!.LeaseOwner);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task File_store_respects_a_lease_held_by_a_live_process()
    {
        var dir = Path.Combine(Path.GetTempPath(), "durablelinq-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileWorkflowStore(dir);
            var key = InstanceKey.Format("tests", "wf", 1, "alive");
            var now = DateTimeOffset.UtcNow;
            await store.TryCreateAsync(new InstanceHeader
            {
                Key = key,
                Namespace = "tests",
                WorkflowName = "wf",
                Version = 1,
                InstanceId = "alive",
                CreatedAt = now,
                UpdatedAt = now,
            });

            var liveOwner = $"{Environment.MachineName}:{Environment.ProcessId}";
            Assert.NotNull(await store.TryAcquireLeaseAsync(key, liveOwner, TimeSpan.FromMinutes(10)));
            Assert.Null(await store.TryAcquireLeaseAsync(key, "other-host", TimeSpan.FromMinutes(1)));

            // an owner on another machine is never treated as dead
            var remote = $"other-machine-{Guid.NewGuid():N}:12345";
            var k2 = InstanceKey.Format("tests", "wf", 1, "remote");
            await store.TryCreateAsync(new InstanceHeader { Key = k2, Namespace = "tests", WorkflowName = "wf", Version = 1, InstanceId = "remote", CreatedAt = now, UpdatedAt = now });
            Assert.NotNull(await store.TryAcquireLeaseAsync(k2, remote, TimeSpan.FromMinutes(10)));
            Assert.Null(await store.TryAcquireLeaseAsync(k2, "me", TimeSpan.FromMinutes(1)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task RetryFailed_rejects_instances_that_are_not_failed()
    {
        var runner = new WorkflowRunner(new InMemoryWorkflowStore(), new WorkflowRunnerOptions { Namespace = "tests", Events = new() });
        var def = WorkflowDefinition.Create<Unit, int>("ok", 1, _ => Durable.Return(1));
        await runner.RunAsync(def, "k", Unit.Value);
        Assert.False(await runner.RetryFailedAsync(runner.KeyFor(def, "k")));
        await Assert.ThrowsAsync<InstanceNotFoundException>(() => runner.RetryFailedAsync(runner.KeyFor(def, "missing")));
    }
}
