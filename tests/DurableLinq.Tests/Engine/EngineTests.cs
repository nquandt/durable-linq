using DurableLinq.Stores;
using Xunit;

namespace DurableLinq.Tests.Engine;

public sealed class EngineTests
{
    private static WorkflowRunner NewRunner(IWorkflowStore? store = null, TimeSpan? leaseTtl = null) =>
        new(store ?? new InMemoryWorkflowStore(), new WorkflowRunnerOptions
        {
            Namespace = "tests",
            LeaseTtl = leaseTtl ?? TimeSpan.FromSeconds(5),
            Events = new DurableEventSource(),
        });

    private static T Throw<T>(string message) => throw new Exception(message);

    private sealed record Order(string Id, int Qty, decimal Price);
    private sealed record Receipt(string Key, decimal Total, string Label);

    [Fact]
    public async Task Query_syntax_runs_steps_in_order_and_threads_values()
    {
        var calls = new List<string>();
        var def = WorkflowDefinition.Create<Order, Receipt>("checkout", 1, order =>
            from key in Durable.NewGuid("key")
            from cust in Durable.Step("load", () => { calls.Add("load"); return "alice"; })
            let total = order.Qty * order.Price
            from charge in Durable.Step("charge", async () => { calls.Add("charge"); await Task.Yield(); return $"{cust}:{total}"; })
            from label in Durable.Step("ship", () => { calls.Add("ship"); return "L-1"; })
            select new Receipt(key.ToString(), total, label + "/" + charge));

        var runner = NewRunner();
        var run = await runner.RunAsync(def, "o1", new Order("o1", 2, 10m));

        Assert.True(run.IsCompleted, run.ToString());
        Assert.Equal(20m, run.Value.Total);
        Assert.Equal("L-1/alice:20", run.Value.Label);
        Assert.Equal(new[] { "load", "charge", "ship" }, calls);

        var steps = await runner.Store.ListStepsAsync(runner.KeyFor(def, "o1"));
        Assert.Equal(new[] { "key", "load", "charge", "ship" }, steps.Select(s => s.Name));
        Assert.Equal(WorkflowStatus.Completed, await runner.GetStatusAsync(def, "o1"));
    }

    [Fact]
    public async Task Crash_mid_way_then_rerun_replays_completed_steps_and_resumes()
    {
        var executed = new List<string>();
        var crash = true;
        var def = WorkflowDefinition.Create<int, int>("crashy", 1, n =>
            from a in Durable.Step("a", () => { executed.Add("a"); return n + 1; })
            from b in Durable.Step("b", () => { executed.Add("b"); return a * 2; })
            from c in Durable.Step("c", () =>
            {
                if (crash) throw new InvalidOperationException("boom");
                executed.Add("c");
                return b + 100;
            })
            select c);

        var runner = NewRunner();
        var failed = await runner.RunAsync(def, "x", 1);
        Assert.True(failed.IsFailed);
        Assert.Equal("c", failed.Error!.StepName);
        Assert.Equal("boom", failed.Error.Message);
        Assert.Equal("InvalidOperationException", failed.Error.Code);
        Assert.False(failed.Error.IsTerminal);
        Assert.Equal(WorkflowStatus.Failed, await runner.GetStatusAsync(def, "x"));

        // failed instances stay failed until someone retries them
        Assert.True((await runner.RunAsync(def, "x", 999)).IsFailed);

        crash = false;
        Assert.True(await runner.RetryFailedAsync(runner.KeyFor(def, "x")));
        var result = await runner.RunAsync(def, "x", 999); // input ignored: stored input wins
        Assert.Equal(104, result.Value);
        Assert.Equal(new[] { "a", "b", "c" }, executed); // a and b did not run twice
    }

    [Fact]
    public async Task Completed_instance_short_circuits_without_executing()
    {
        var count = 0;
        var def = WorkflowDefinition.Create<Unit, int>("once", 1, _ => Durable.Step("inc", () => ++count));
        var runner = NewRunner();
        Assert.Equal(1, (await runner.RunAsync(def, "i", Unit.Value)).Value);
        Assert.Equal(1, (await runner.RunAsync(def, "i", Unit.Value)).Value);
        Assert.Equal(1, count);
        Assert.Equal(1, await runner.GetResultAsync<int>(runner.KeyFor(def, "i")));
    }

    [Fact]
    public async Task Changed_step_name_is_detected_as_nondeterminism_and_throws()
    {
        var store = new InMemoryWorkflowStore();
        var v1 = WorkflowDefinition.Create<Unit, int>("wf", 1, _ =>
            from a in Durable.Step("a", () => 1)
            from b in Durable.Step("b", () => Throw<int>("stop here"))
            select a + b);
        var runner = NewRunner(store);
        Assert.True((await runner.RunAsync(v1, "k", Unit.Value)).IsFailed);
        await runner.RetryFailedAsync(runner.KeyFor(v1, "k"));

        // same name and version, but the code now has a different first step: this is an engine invariant, not a business failure
        var changed = WorkflowDefinition.Create<Unit, int>("wf", 1, _ =>
            from x in Durable.Step("renamed", () => 1)
            select x);
        var ex = await Assert.ThrowsAsync<NondeterminismException>(() => runner.RunAsync(changed, "k", Unit.Value));
        Assert.Equal("a", ex.RecordedName);
        Assert.Equal("renamed", ex.ActualName);
        // the instance is left Running, not Failed: the fix is to register the right code
        Assert.Equal(WorkflowStatus.Running, await runner.GetStatusAsync(changed, "k"));
    }

    [Fact]
    public async Task WaitFor_blocks_until_signal_and_records_payload()
    {
        var def = WorkflowDefinition.Create<Unit, string>("waiter", 1, input =>
            from _ in Durable.Step("prep", () => 0)
            from ok in Durable.WaitFor<bool>("approved")
            from who in Durable.WaitFor<string>("approver")
            select $"{ok}:{who}");
        var runner = NewRunner();
        var run = runner.RunAsync(def, "w", Unit.Value);
        await Task.Delay(100);
        Assert.False(run.IsCompleted);

        await runner.SignalAsync(def, "w", "approved", true);
        await runner.SignalAsync(def, "w", "approver", "bob");
        Assert.Equal("True:bob", (await run).Value);

        var steps = await runner.Store.ListStepsAsync(runner.KeyFor(def, "w"));
        Assert.Equal(new[] { "prep", "wait:approved", "wait:approver" }, steps.Select(s => s.Name));
    }

    [Fact]
    public async Task WaitFor_with_timeout_fails_the_run_with_the_timeout_code()
    {
        var def = WorkflowDefinition.Create<Unit, bool>("timeout", 1, _ => Durable.WaitFor<bool>("never", TimeSpan.FromMilliseconds(200)));
        var runner = NewRunner();
        var run = await runner.RunAsync(def, "t", Unit.Value);
        Assert.True(run.IsFailed);
        Assert.Equal(WorkflowError.TimeoutCode, run.Error!.Code);
        Assert.Equal("wait:never", run.Error.StepName);
        Assert.False(run.Error.IsTerminal);
    }

    [Fact]
    public async Task Sleep_records_wake_time_and_resume_waits_only_remainder()
    {
        var store = new InMemoryWorkflowStore();
        var runner = NewRunner(store);
        var def = WorkflowDefinition.Create<Unit, string>("sleepy", 1, input =>
            from _ in Durable.Sleep(TimeSpan.FromMilliseconds(400))
            from r in Durable.Step("after", () => "done")
            select r);

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(def, "s", Unit.Value, cts.Token));
        var key = runner.KeyFor(def, "s");
        Assert.Equal(WorkflowStatus.Running, await runner.GetStatusAsync(key));
        var recorded = (await store.ListStepsAsync(key)).Single();
        Assert.Equal("sleep", recorded.Name);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("done", (await runner.RunAsync(def, "s", Unit.Value)).Value);
        Assert.True(sw.ElapsedMilliseconds < 400, $"resume waited {sw.ElapsedMilliseconds}ms, expected less than the full 400ms");
    }

    [Fact]
    public async Task A_completed_step_is_recorded_even_when_cancellation_is_requested_as_it_finishes()
    {
        var store = new InMemoryWorkflowStore();
        var runner = NewRunner(store);
        using var cts = new CancellationTokenSource();
        var aRuns = 0;
        var def = WorkflowDefinition.Create<Unit, string>("commit", 1, _ =>
            from a in Durable.Step("a", () => { aRuns++; cts.Cancel(); return "done-a"; })
            from b in Durable.Step("b", (CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult("done-b"); })
            select a + b);

        // "a"'s work finishes and cancellation is requested in the same instant. "a" must still be
        // recorded: the work already happened, so losing that record would mean redoing it on resume.
        // "b" never starts: cancellation gates new work, so it throws before running.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(def, "k", Unit.Value, cts.Token));

        var key = runner.KeyFor(def, "k");
        Assert.Equal(WorkflowStatus.Running, await runner.GetStatusAsync(key));
        var steps = await store.ListStepsAsync(key);
        Assert.Equal(new[] { "a" }, steps.Select(s => s.Name));

        // resume: "a" replays from the log instead of running again; "b" runs for the first time
        Assert.Equal("done-adone-b", (await runner.RunAsync(def, "k", Unit.Value)).Value);
        Assert.Equal(1, aRuns);
    }

    [Fact]
    public async Task WaitFor_can_be_cancelled_while_parked_and_a_signal_sent_afterward_still_completes_the_run()
    {
        var store = new InMemoryWorkflowStore();
        var runner = NewRunner(store);
        var def = WorkflowDefinition.Create<Unit, string>("parked", 1, _ =>
            from v in Durable.WaitFor<string>("go")
            select v);

        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(def, "k", Unit.Value, cts.Token);
        await Task.Delay(30); // give it time to reach WaitFor and start polling the store
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var key = runner.KeyFor(def, "k");
        Assert.Equal(WorkflowStatus.Running, await runner.GetStatusAsync(key));
        Assert.Empty(await store.ListStepsAsync(key));

        // nothing is actively waiting right now; the signal is still just a store write
        await runner.SignalAsync(def, "k", "go", "hello");

        // resume: the queued signal is picked up immediately, no repeat wait
        Assert.Equal("hello", (await runner.RunAsync(def, "k", Unit.Value)).Value);
    }

    [Fact]
    public async Task A_step_result_that_cannot_be_serialized_throws_instead_of_being_treated_as_retryable()
    {
        var runner = NewRunner();
        var attempts = 0;
        // System.Text.Json refuses to serialize a Type instance by design; stands in for "any result type
        // the configured serializer cannot handle."
        var def = WorkflowDefinition.Create<Unit, Type>("bad-result", 1, _ =>
            Durable.Step("work", () => { attempts++; return typeof(string); }));

        var ex = await Assert.ThrowsAsync<StepResultNotSerializableException>(() => runner.RunAsync(def, "k", Unit.Value));
        Assert.Equal("work", ex.StepName);

        // not recorded as a business failure: the instance is untouched, so a human can decide what to do
        Assert.Equal(WorkflowStatus.Running, await runner.GetStatusAsync(runner.KeyFor(def, "k")));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ForEach_If_and_Sub_produce_stable_scoped_names()
    {
        var def = WorkflowDefinition.Create<int[], int>("combi", 1, items =>
            from parts in Durable.ForEach(items, (x, i) => Durable.Step($"double[{i}]", () => x * 2))
            let sum = parts.Sum()
            from bonus in Durable.If(sum > 5, Durable.Step("big", () => 100), Durable.Step("small", () => 1))
            from sub in Durable.Sub("audit", Durable.Step("log", () => sum))
            from sub2 in Durable.Sub("audit2", () => Durable.Step("log", () => sum))
            select sum + bonus + sub + sub2);

        var runner = NewRunner();
        Assert.Equal(6 + 100 + 6 + 6, (await runner.RunAsync(def, "c", new[] { 1, 2 })).Value);
        var names = (await runner.Store.ListStepsAsync(runner.KeyFor(def, "c"))).Select(s => s.Name).ToArray();
        Assert.Equal(new[] { "double[0]", "double[1]", "if", "big", "audit/log", "audit2/log" }, names);
    }

    [Fact]
    public async Task While_loops_with_scoped_iterations()
    {
        var def = WorkflowDefinition.Create<Unit, int>("loop", 1, _ =>
            Durable.While(i => Durable.Step("tick", () => i < 2)));
        var runner = NewRunner();
        Assert.Equal(2, (await runner.RunAsync(def, "l", Unit.Value)).Value);
        var names = (await runner.Store.ListStepsAsync(runner.KeyFor(def, "l"))).Select(s => s.Name).ToArray();
        Assert.Equal(new[] { "loop[0]/tick", "loop[1]/tick", "loop[2]/tick" }, names);
    }

    [Fact]
    public async Task Step_retries_per_options_before_failing()
    {
        var attempts = 0;
        var def = WorkflowDefinition.Create<Unit, int>("retry", 1, _ =>
            Durable.Step("flaky", () =>
            {
                attempts++;
                if (attempts < 3) throw new TimeoutException("flaky");
                return attempts;
            }, StepOptions.Retry(5, TimeSpan.FromMilliseconds(5))));
        var runner = NewRunner();
        Assert.Equal(3, (await runner.RunAsync(def, "r", Unit.Value)).Value);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Compensate_runs_once_on_failure_and_Recover_produces_a_result()
    {
        var compensated = 0;
        var def = WorkflowDefinition.Create<Unit, string>("saga", 1, _ =>
            (from a in Durable.Step("reserve", () => "r")
             from b in Durable.Step("charge", () => Throw<string>("declined"))
             select a + b)
            .Compensate(Durable.Step("release", () => { compensated++; }))
            .Recover(error => Durable.Step("fallback", () => "recovered:" + error.Message)));

        var runner = NewRunner();
        Assert.Equal("recovered:declined", (await runner.RunAsync(def, "s", Unit.Value)).Value);
        Assert.Equal(1, compensated);
        var steps = await runner.Store.ListStepsAsync(runner.KeyFor(def, "s"));
        Assert.Equal(new[] { "reserve", "charge", "compensate/release", "recover/fallback" }, steps.Select(s => s.Name));
        Assert.True(steps[1].Failed);

        // a fresh run over the same log replays the recorded failure and does not compensate again
        await runner.Store.UpdateStatusAsync(runner.KeyFor(def, "s"), WorkflowStatus.Running, null);
        Assert.Equal("recovered:declined", (await runner.RunAsync(def, "s", Unit.Value)).Value);
        Assert.Equal(1, compensated);
    }

    [Fact]
    public async Task Second_runner_gets_Busy_while_first_runs()
    {
        var store = new InMemoryWorkflowStore();
        var gate = new TaskCompletionSource();
        var def = WorkflowDefinition.Create<Unit, int>("leased", 1, _ =>
            Durable.Step("slow", async () => { await gate.Task; return 1; }));

        var a = new WorkflowRunner(store, new WorkflowRunnerOptions { Namespace = "tests", Owner = "a", Events = new() });
        var b = new WorkflowRunner(store, new WorkflowRunnerOptions { Namespace = "tests", Owner = "b", Events = new() });

        var first = a.RunAsync(def, "k", Unit.Value);
        await Task.Delay(50);
        var busy = await b.RunAsync(def, "k", Unit.Value);
        Assert.True(busy.IsBusy);
        Assert.Equal("a", busy.LeaseOwner);
        Assert.Throws<WorkflowBusyException>(() => busy.ValueOrThrow());

        gate.SetResult();
        Assert.Equal(1, (await first).Value);
        Assert.Equal(1, (await b.RunAsync(def, "k", Unit.Value)).Value); // now completed: short-circuit
    }

    [Fact]
    public async Task Lease_is_renewed_during_long_runs()
    {
        var store = new InMemoryWorkflowStore();
        var runner = NewRunner(store, leaseTtl: TimeSpan.FromMilliseconds(200));
        var def = WorkflowDefinition.Create<Unit, int>("long", 1, _ =>
            Durable.Step("slow", async () => { await Task.Delay(700); return 1; }));
        var key = runner.KeyFor(def, "k");
        var run = runner.RunAsync(def, "k", Unit.Value);
        await Task.Delay(500);
        var h = await store.GetHeaderAsync(key);
        Assert.True(h!.IsLeasedAt(DateTimeOffset.UtcNow), "lease should still be held mid-run");
        Assert.Equal(1, (await run).Value);
        h = await store.GetHeaderAsync(key);
        Assert.Null(h!.LeaseOwner);
    }

    [Fact]
    public async Task Events_are_raised_for_execute_and_replay()
    {
        var events = new DurableEventSource();
        var seen = new List<DurableEventKind>();
        events.Raised += e => seen.Add(e.Kind);
        var runner = new WorkflowRunner(new InMemoryWorkflowStore(), new WorkflowRunnerOptions { Namespace = "tests", Events = events });
        var def = WorkflowDefinition.Create<Unit, int>("ev", 1, _ => Durable.Step("a", () => 1));

        await runner.RunAsync(def, "e", Unit.Value);
        Assert.Equal(new[] { DurableEventKind.InstanceStarted, DurableEventKind.StepExecuted, DurableEventKind.InstanceCompleted }, seen);
    }

    [Fact]
    public async Task Services_are_available_to_definitions()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, "hello");
        var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        var def = WorkflowDefinition.Create<Unit, string>("svc", 1, (_, svc) => Durable.Step("read", () => svc.Get<string>()));
        var runner = new WorkflowRunner(new InMemoryWorkflowStore(), new WorkflowRunnerOptions { Namespace = "tests", Events = new() }, sp);
        Assert.Equal("hello", (await runner.RunAsync(def, "s", Unit.Value)).Value);
    }

    [Fact]
    public async Task File_store_survives_a_new_runner_over_the_same_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "durablelinq-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var executed = 0;
            var crash = true;
            var def = WorkflowDefinition.Create<Unit, int>("file", 1, _ =>
                from a in Durable.Step("a", () => { executed++; return 1; })
                from b in Durable.Step("b", () => crash ? Throw<int>("crash") : 2)
                select a + b);

            var r1 = NewRunner(new FileWorkflowStore(dir));
            Assert.True((await r1.RunAsync(def, "f", Unit.Value)).IsFailed);

            crash = false;
            var r2 = NewRunner(new FileWorkflowStore(dir));
            Assert.True(await r2.RetryFailedAsync(r2.KeyFor(def, "f")));
            Assert.Equal(3, (await r2.RunAsync(def, "f", Unit.Value)).Value);
            Assert.Equal(1, executed);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ValueOrThrow_throws_the_matching_exception()
    {
        var runner = NewRunner();
        var failing = WorkflowDefinition.Create<Unit, int>("f", 1, _ => Durable.Step("a", () => Throw<int>("nope")));
        var ex = await Assert.ThrowsAsync<WorkflowFailedException>(async () => (await runner.RunAsync(failing, "k", Unit.Value)).ValueOrThrow());
        Assert.Equal("nope", ex.Error.Message);
    }
}
