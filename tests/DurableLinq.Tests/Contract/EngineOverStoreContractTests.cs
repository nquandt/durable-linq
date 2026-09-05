using Xunit;

namespace DurableLinq.Tests.Contract;

/// <summary>
/// Runs the real engine against a store: crash and resume, signals, lease exclusivity across two runners,
/// recorded failures, and sleep. Store packages derive from this class next to <see cref="WorkflowStoreContractTests"/>.
/// </summary>
public abstract class EngineOverStoreContractTests : IAsyncLifetime
{
    protected IWorkflowStore Store { get; private set; } = null!;

    protected abstract Task<IWorkflowStore> CreateStoreAsync();
    protected virtual Task DisposeStoreAsync(IWorkflowStore store) => Task.CompletedTask;

    public async Task InitializeAsync() => Store = await CreateStoreAsync();
    public Task DisposeAsync() => DisposeStoreAsync(Store);

    private WorkflowRunner NewRunner(string owner = "a", TimeSpan? leaseTtl = null) =>
        new(Store, new WorkflowRunnerOptions
        {
            Namespace = "engine-" + Guid.NewGuid().ToString("N")[..8],
            Owner = owner,
            LeaseTtl = leaseTtl ?? TimeSpan.FromSeconds(10),
            Events = new DurableEventSource(),
        });

    private static T Fail<T>(string message) => throw new Exception(message);

    private sealed record Order(string Id, int Qty, decimal Price);
    private sealed record Receipt(Guid Key, decimal Total, string Label, bool Delivered);

    [Fact]
    public async Task Crash_then_resume_replays_from_the_store_and_runs_each_step_once()
    {
        var executed = new List<string>();
        var crash = true;
        var def = WorkflowDefinition.Create<Order, Receipt>("checkout", 1, order =>
            from key in Durable.NewGuid("key")
            from lines in Durable.ForEach(Enumerable.Range(0, order.Qty), i => Durable.Step($"reserve[{i}]", () => { executed.Add($"reserve[{i}]"); return i; }))
            let total = order.Qty * order.Price
            from charge in Durable.Step("charge", () => { executed.Add("charge"); return $"AUTH-{total}"; })
            from label in Durable.Step("ship", () =>
            {
                if (crash) throw new InvalidOperationException("simulated crash");
                executed.Add("ship");
                return "LBL";
            })
            select new Receipt(key, total, label, false));

        var runner = NewRunner();
        var failed = await runner.RunAsync(def, "o1", new Order("o1", 3, 2m));
        Assert.True(failed.IsFailed);
        Assert.Equal("ship", failed.Error!.StepName);

        crash = false;
        var key = runner.KeyFor(def, "o1");
        Assert.True(await runner.RetryFailedAsync(key));
        var receipt = (await runner.RunAsync(def, "o1", new Order("o1", 99, 99m))).Value; // stored input wins

        Assert.Equal(6m, receipt.Total);
        Assert.Equal("LBL", receipt.Label);
        Assert.Equal(new[] { "reserve[0]", "reserve[1]", "reserve[2]", "charge", "ship" }, executed);

        var steps = await Store.ListStepsAsync(key);
        Assert.Equal(new[] { "key", "reserve[0]", "reserve[1]", "reserve[2]", "charge", "ship" }, steps.Select(s => s.Name));
        Assert.All(steps, s => Assert.False(s.Failed));

        // the recorded guid survives serialization through the store
        Assert.Equal(receipt.Key, JsonWorkflowSerializer.Instance.Deserialize<Guid>(steps[0].Payload!));
        Assert.Equal(receipt, await runner.GetResultAsync<Receipt>(key));
    }

    [Fact]
    public async Task Signal_delivered_through_the_store_completes_a_waiting_run()
    {
        var def = WorkflowDefinition.Create<Unit, string>("waiter", 1, input =>
            from _ in Durable.Step("prep", () => "p")
            from who in Durable.WaitFor<string>("approved")
            from n in Durable.WaitFor<int>("count")
            select $"{who}:{n}");

        var runner = NewRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runner.RunAsync(def, "w", Unit.Value, cts.Token);
        await Task.Delay(300);
        Assert.False(run.IsCompleted);

        await runner.SignalAsync(def, "w", "approved", "lee");
        await runner.SignalAsync(def, "w", "count", 7);
        Assert.Equal("lee:7", (await run).Value);

        var steps = await Store.ListStepsAsync(runner.KeyFor(def, "w"));
        Assert.Equal(new[] { "prep", "wait:approved", "wait:count" }, steps.Select(s => s.Name));
    }

    [Fact]
    public async Task Two_runners_on_the_same_instance_take_turns_through_the_lease()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var def = WorkflowDefinition.Create<Unit, int>("leased", 1, _ =>
            Durable.Step("slow", async () => { Interlocked.Increment(ref runs); await gate.Task; return 42; }));

        var a = NewRunner("host-a");
        var b = new WorkflowRunner(Store, new WorkflowRunnerOptions { Namespace = a.Namespace, Owner = "host-b", Events = new() });

        var first = a.RunAsync(def, "k", Unit.Value);
        await Task.Delay(300);
        var busy = await b.RunAsync(def, "k", Unit.Value);
        Assert.True(busy.IsBusy);
        Assert.Equal("host-a", busy.LeaseOwner);

        gate.SetResult();
        Assert.Equal(42, (await first).Value);
        Assert.Equal(42, (await b.RunAsync(def, "k", Unit.Value)).Value);
        Assert.Equal(1, runs);
        Assert.Null((await Store.GetHeaderAsync(a.KeyFor(def, "k")))!.LeaseOwner);
    }

    [Fact]
    public async Task Recorded_failure_replays_deterministically_through_the_store()
    {
        var compensated = 0;
        var def = WorkflowDefinition.Create<Unit, string>("saga", 1, _ =>
            (from a in Durable.Step("reserve", () => "r")
             from b in Durable.Step("charge", () => Fail<string>("declined"))
             select a + b)
            .Compensate(Durable.Step("release", () => { compensated++; }))
            .Recover(error => Durable.Step("fallback", () => "recovered:" + error.Message)));

        var runner = NewRunner();
        Assert.Equal("recovered:declined", (await runner.RunAsync(def, "s", Unit.Value)).Value);

        var key = runner.KeyFor(def, "s");
        var steps = await Store.ListStepsAsync(key);
        Assert.Equal(new[] { "reserve", "charge", "compensate/release", "recover/fallback" }, steps.Select(s => s.Name));
        Assert.True(steps[1].Failed);

        await Store.UpdateStatusAsync(key, WorkflowStatus.Running, null);
        Assert.Equal("recovered:declined", (await runner.RunAsync(def, "s", Unit.Value)).Value);
        Assert.Equal(1, compensated);
    }

    [Fact]
    public async Task Terminal_error_with_detail_round_trips_through_the_store()
    {
        var def = WorkflowDefinition.Create<Unit, string>("terminal", 1, _ =>
            Durable.Try("charge", () => Outcome.Fail<string>("payments.declined", "over limit", terminal: true, detail: new { amount = 500 })));
        var runner = NewRunner();
        var run = await runner.RunAsync(def, "k", Unit.Value);
        Assert.True(run.IsFailed);

        var stored = await runner.GetErrorAsync(runner.KeyFor(def, "k"));
        Assert.Equal("payments.declined", stored!.Code);
        Assert.True(stored.IsTerminal);
        Assert.Equal("charge", stored.StepName);
        Assert.Equal(500, stored.Detail!.Value.GetProperty("amount").GetInt32());
        Assert.False(await runner.RetryFailedAsync(runner.KeyFor(def, "k")));
    }

    [Fact]
    public async Task Sleep_wake_time_survives_the_store_and_a_second_runner_waits_only_the_remainder()
    {
        var def = WorkflowDefinition.Create<Unit, string>("sleepy", 1, input =>
            from _ in Durable.Sleep(TimeSpan.FromSeconds(2))
            from r in Durable.Step("after", () => "done")
            select r);

        var a = NewRunner("host-a");
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.RunAsync(def, "s", Unit.Value, cts.Token));

        var key = a.KeyFor(def, "s");
        var recorded = Assert.Single(await Store.ListStepsAsync(key));
        Assert.Equal("sleep", recorded.Name);
        var wakeAt = JsonWorkflowSerializer.Instance.Deserialize<DateTimeOffset>(recorded.Payload!);

        var b = new WorkflowRunner(Store, new WorkflowRunnerOptions { Namespace = a.Namespace, Owner = "host-b", Events = new() });
        var startedAt = DateTimeOffset.UtcNow;
        var remainder = wakeAt - startedAt;
        Assert.InRange(remainder, TimeSpan.Zero, TimeSpan.FromSeconds(2)); // part of the sleep already elapsed
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("done", (await b.RunAsync(def, "s", Unit.Value)).Value);
        Assert.True(DateTimeOffset.UtcNow >= wakeAt, "woke before the recorded wake time");
        Assert.True(sw.Elapsed < remainder + TimeSpan.FromSeconds(1), $"waited {sw.Elapsed} but only {remainder} remained");
        Assert.Equal(2, (await Store.ListStepsAsync(key)).Count); // the sleep was replayed, not re-recorded
    }
}
