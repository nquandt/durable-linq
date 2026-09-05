using DurableLinq.Stores;
using Xunit;

namespace DurableLinq.Tests.Engine;

/// <summary>Workflows written as <c>async Durable&lt;T&gt;</c> methods.</summary>
public sealed class AsyncMethodTests
{
    private static WorkflowRunner NewRunner(IWorkflowStore? store = null) =>
        new(store ?? new InMemoryWorkflowStore(), new WorkflowRunnerOptions
        {
            Namespace = "tests",
            LeaseTtl = TimeSpan.FromSeconds(5),
            Events = new DurableEventSource(),
        });

    private sealed record Order(string Id, int Qty, decimal Price);
    private sealed record Receipt(string Key, decimal Total, string Label);

    private static Durable<Receipt> QueryForm(Order order, List<string> calls) =>
        from key in Durable.NewGuid("key")
        from cust in Durable.Step("load", () => { calls.Add("load"); return "alice"; })
        let total = order.Qty * order.Price
        from charge in Durable.Step("charge", () => { calls.Add("charge"); return $"{cust}:{total}"; })
        from label in Durable.Step("ship", () => { calls.Add("ship"); return "L-1"; })
        select new Receipt(key.ToString(), total, label + "/" + charge);

    private static async Durable<Receipt> AsyncForm(Order order, List<string> calls)
    {
        var key = await Durable.NewGuid("key");
        var cust = await Durable.Step("load", () => { calls.Add("load"); return "alice"; });
        var total = order.Qty * order.Price;
        var charge = await Durable.Step("charge", () => { calls.Add("charge"); return $"{cust}:{total}"; });
        var label = await Durable.Step("ship", () => { calls.Add("ship"); return "L-1"; });
        return new Receipt(key.ToString(), total, label + "/" + charge);
    }

    [Fact]
    public async Task Build_does_not_run_the_body()
    {
        var ran = false;
        static async Durable<int> Body(Action mark) { mark(); return await Durable.Step("s", () => 1); }
        _ = Body(() => ran = true);
        Assert.False(ran);
    }

    [Fact]
    public async Task Both_forms_write_the_same_log()
    {
        var queryCalls = new List<string>();
        var asyncCalls = new List<string>();
        var query = WorkflowDefinition.Create<Order, Receipt>("checkout", 1, o => QueryForm(o, queryCalls));
        var asyncDef = WorkflowDefinition.Create<Order, Receipt>("checkout", 1, o => AsyncForm(o, asyncCalls));
        var order = new Order("o1", 2, 10m);

        var r1 = NewRunner();
        var r2 = NewRunner();
        var q = await r1.RunAsync(query, "o1", order);
        var a = await r2.RunAsync(asyncDef, "o1", order);

        Assert.True(q.IsCompleted, q.ToString());
        Assert.True(a.IsCompleted, a.ToString());
        Assert.Equal(q.Value with { Key = "" }, a.Value with { Key = "" });
        Assert.Equal(queryCalls, asyncCalls);

        var qSteps = await r1.Store.ListStepsAsync(r1.KeyFor(query, "o1"));
        var aSteps = await r2.Store.ListStepsAsync(r2.KeyFor(asyncDef, "o1"));
        Assert.Equal(qSteps.Select(s => (s.Seq, s.Name)), aSteps.Select(s => (s.Seq, s.Name)));
    }

    [Fact]
    public async Task Crash_then_rerun_replays_and_resumes()
    {
        var store = new InMemoryWorkflowStore();
        var executed = new List<string>();
        var crash = true;
        var def = WorkflowDefinition.Create<int, int>("crashy", 1, async n =>
        {
            var a = await Durable.Step("a", () => { executed.Add("a"); return n + 1; });
            var b = await Durable.Step("b", () => { executed.Add("b"); return a * 2; });
            var c = await Durable.Step("c", () =>
            {
                if (crash) throw new InvalidOperationException("boom");
                executed.Add("c");
                return b + 100;
            });
            return a + c;
        });

        var first = await NewRunner(store).RunAsync(def, "k", 1);
        Assert.True(first.IsFailed, first.ToString());
        Assert.Equal("InvalidOperationException", first.Error!.Code);
        Assert.Equal(new[] { "a", "b" }, executed);

        crash = false;
        var runner = NewRunner(store);
        Assert.True(await runner.RetryFailedAsync(runner.KeyFor(def, "k")));
        var second = await runner.ResumeAsync(def, runner.KeyFor(def, "k"));
        Assert.True(second.IsCompleted, second.ToString());
        Assert.Equal(2 + 104, second.Value);
        Assert.Equal(new[] { "a", "b", "c" }, executed);
    }

    [Fact]
    public async Task Query_instance_resumes_on_async_code()
    {
        var store = new InMemoryWorkflowStore();
        var crash = true;
        var query = WorkflowDefinition.Create<int, string>("mixed", 1, _ =>
            from a in Durable.Step("a", () => "A")
            from b in Durable.Step("b", () => crash ? throw new InvalidOperationException("boom") : "B")
            select a + b);

        var first = await NewRunner(store).RunAsync(query, "m", 0);
        Assert.True(first.IsFailed);

        crash = false;
        var aRuns = 0;
        var asyncDef = WorkflowDefinition.Create<int, string>("mixed", 1, async _ =>
        {
            var a = await Durable.Step("a", () => { aRuns++; return "X"; });
            var b = await Durable.Step("b", () => "B");
            return a + b;
        });
        var runner = NewRunner(store);
        Assert.True(await runner.RetryFailedAsync(runner.KeyFor(asyncDef, "m")));
        var second = await runner.ResumeAsync(asyncDef, runner.KeyFor(asyncDef, "m"));

        Assert.True(second.IsCompleted, second.ToString());
        Assert.Equal("AB", second.Value); // "a" replayed from the query run's log
        Assert.Equal(0, aRuns);
    }

    [Fact]
    public async Task Loops_and_branches_are_plain_csharp()
    {
        var def = WorkflowDefinition.Create<int, string>("loop", 1, async n =>
        {
            var parts = new List<string>();
            for (var i = 0; i < n; i++)
                parts.Add(await Durable.Step($"item[{i}]", () => i.ToString()));
            var size = await Durable.Step("size", () => parts.Count);
            if (size > 2)
                await Durable.Step("big", () => { });
            return string.Join(",", parts);
        });

        var runner = NewRunner();
        var run = await runner.RunAsync(def, "l", 3);

        Assert.Equal("0,1,2", run.Value);
        var steps = await runner.Store.ListStepsAsync(runner.KeyFor(def, "l"));
        Assert.Equal(new[] { "item[0]", "item[1]", "item[2]", "size", "big" }, steps.Select(s => s.Name));
    }

    [Fact]
    public async Task Renamed_step_on_replay_is_nondeterminism()
    {
        var store = new InMemoryWorkflowStore();
        var v1 = WorkflowDefinition.Create<int, int>("nd", 1, async _ =>
        {
            await Durable.Step("a", () => 1);
            await Durable.Step("b", () => throw new InvalidOperationException("boom"));
            return 0;
        });
        var first = await NewRunner(store).RunAsync(v1, "x", 0);
        Assert.True(first.IsFailed);

        var renamed = WorkflowDefinition.Create<int, int>("nd", 1, async _ =>
        {
            await Durable.Step("z", () => 1);
            return 0;
        });
        var runner = NewRunner(store);
        await runner.RetryFailedAsync(runner.KeyFor(renamed, "x"));
        await Assert.ThrowsAsync<NondeterminismException>(() => runner.ResumeAsync(renamed, runner.KeyFor(renamed, "x")));
    }

    [Fact]
    public async Task Terminal_failure_at_await_fails_the_run_as_a_value()
    {
        var def = WorkflowDefinition.Create<int, int>("term", 1, async _ =>
        {
            await Durable.Step("a", () => throw new TerminalException("bad.input", "nope"));
            return 1;
        });
        var run = await NewRunner().RunAsync(def, "t", 0);
        Assert.True(run.IsFailed);
        Assert.Equal("bad.input", run.Error!.Code);
        Assert.True(run.Error.IsTerminal);
    }

    [Fact]
    public async Task Recover_before_await_takes_the_handler_path()
    {
        var def = WorkflowDefinition.Create<int, string>("rec", 1, async _ =>
        {
            var v = await Durable.Try("pay", () => Outcome.Fail<string>("payments.declined", "no funds", terminal: true))
                .Recover("payments.declined", _ => Durable.Step("fallback", () => "invoice"));
            return v;
        });
        var runner = NewRunner();
        var run = await runner.RunAsync(def, "r", 0);
        Assert.True(run.IsCompleted, run.ToString());
        Assert.Equal("invoice", run.Value);
        var steps = await runner.Store.ListStepsAsync(runner.KeyFor(def, "r"));
        Assert.Equal(new[] { "pay", "recover/fallback" }, steps.Select(s => s.Name));
        Assert.True(steps[0].Failed);
    }

    [Fact]
    public async Task Async_helper_composes_into_a_query_and_a_query_into_an_async_body()
    {
        static async Durable<string> Helper(string prefix)
        {
            var a = await Durable.Step("h1", () => prefix + "1");
            return await Durable.Step("h2", () => a + "2");
        }

        var query = WorkflowDefinition.Create<int, string>("compose", 1, _ =>
            from h in Durable.Sub("helper", Helper("q"))
            from tail in Durable.Step("tail", () => h + "!")
            select tail);

        var asyncDef = WorkflowDefinition.Create<int, string>("compose2", 1, async _ =>
        {
            var q = await (from x in Durable.Step("x", () => "a") from y in Durable.Step("y", () => x + "b") select y);
            return await Durable.Sub("helper", Helper(q));
        });

        var runner = NewRunner();
        Assert.Equal("q12!", (await runner.RunAsync(query, "c", 0)).Value);
        Assert.Equal("ab12", (await runner.RunAsync(asyncDef, "c", 0)).Value);
        var steps = await runner.Store.ListStepsAsync(runner.KeyFor(asyncDef, "c"));
        Assert.Equal(new[] { "x", "y", "helper/h1", "helper/h2" }, steps.Select(s => s.Name));
    }

    [Fact]
    public async Task Sleep_and_signal_are_awaited_like_steps()
    {
        var def = WorkflowDefinition.Create<int, string>("sig", 1, async _ =>
        {
            await Durable.Sleep(TimeSpan.FromMilliseconds(10), "nap");
            var s = await Durable.WaitFor<string>("go");
            return s;
        });
        var runner = NewRunner();
        var key = runner.KeyFor(def, "s");
        var run = runner.RunAsync(def, "s", 0);
        await Task.Delay(50);
        await runner.SignalAsync(key, "go", "signal!");
        Assert.Equal("signal!", (await run).Value);
        var steps = await runner.Store.ListStepsAsync(key);
        Assert.Equal(new[] { "nap", "wait:go" }, steps.Select(s => s.Name));
    }
}
