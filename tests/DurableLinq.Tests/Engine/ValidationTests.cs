using DurableLinq.Stores;
using Xunit;

namespace DurableLinq.Tests.Engine;

public sealed class ValidationTests
{
    private static WorkflowRunner NewRunner(IWorkflowStore? store = null, IFailureClassifier? classifier = null) =>
        new(store ?? new InMemoryWorkflowStore(), new WorkflowRunnerOptions
        {
            Namespace = "tests",
            Events = new(),
            Classifier = classifier ?? DefaultFailureClassifier.Instance,
        });

    private sealed record Customer(string Id, string? Card);

    // ---------------------------------------------------------------- Ensure and where

    [Fact]
    public async Task Ensure_passes_values_through_and_adds_no_log_entries()
    {
        var def = WorkflowDefinition.Create<Unit, string>("ok", 1, _ =>
            from c in Durable.Step("load", () => new Customer("c1", "card")).Ensure(c => c.Card is not null, "no card")
            from n in Durable.Step("count", () => 3).Ensure(n => n > 0, n => $"count was {n}")
            select $"{c.Id}:{n}");
        var runner = NewRunner();
        Assert.Equal("c1:3", (await runner.RunAsync(def, "k", Unit.Value)).Value);
        Assert.Equal(new[] { "load", "count" }, (await runner.Store.ListStepsAsync(runner.KeyFor(def, "k"))).Select(s => s.Name));
    }

    [Fact]
    public async Task Ensure_failure_is_terminal_and_names_the_step()
    {
        var def = WorkflowDefinition.Create<Unit, string>("bad", 1, _ =>
            from c in Durable.Step("load", () => new Customer("c1", null)).Ensure(c => c.Card is not null, "customer has no card")
            select c.Id);
        var runner = NewRunner();
        var run = await runner.RunAsync(def, "k", Unit.Value);

        Assert.True(run.IsFailed);
        Assert.Equal(WorkflowError.ValidationCode, run.Error!.Code);
        Assert.True(run.Error.IsTerminal);
        Assert.Equal("customer has no card", run.Error.Message);
        Assert.Equal("load", run.Error.StepName);
        Assert.Equal(WorkflowStatus.Failed, await runner.GetStatusAsync(def, "k"));
        Assert.False(await runner.RetryFailedAsync(runner.KeyFor(def, "k")), "terminal failures cannot be retried");
    }

    [Fact]
    public async Task Ensure_re_evaluates_deterministically_on_replay()
    {
        var store = new InMemoryWorkflowStore();
        var runner = NewRunner(store);
        var def = WorkflowDefinition.Create<Unit, string>("replay", 1, _ =>
            from c in Durable.Step("load", () => new Customer("c1", null)).Ensure(c => c.Card is not null, "no card")
            select c.Id);
        Assert.True((await runner.RunAsync(def, "k", Unit.Value)).IsFailed);

        // force a re-run over the same log: the step replays and the guard fails again with the same error
        await store.UpdateStatusAsync(runner.KeyFor(def, "k"), WorkflowStatus.Running, null);
        var run = await runner.RunAsync(def, "k", Unit.Value);
        Assert.Equal("no card", run.Error!.Message);
        Assert.Single(await store.ListStepsAsync(runner.KeyFor(def, "k")));
    }

    [Fact]
    public async Task Ensure_with_a_ValidationResult_collects_messages()
    {
        var def = WorkflowDefinition.Create<Order, string>("multi", 1, order =>
            from o in Durable.Start(order).Ensure(o => ValidationResult.Check(
                (o.Lines > 0, "no lines"),
                (o.Amount > 0, "amount must be positive")))
            select o.Id);
        var runner = NewRunner();
        var run = await runner.RunAsync(def, "k", new Order("o1", 0, 0));
        Assert.True(run.IsFailed);
        Assert.Equal("no lines; amount must be positive", run.Error!.Message);
        Assert.Equal(new[] { "no lines", "amount must be positive" }, run.Error.DetailAs<string[]>());
        Assert.NotNull(await runner.GetHeaderAsync(runner.KeyFor(def, "k"))); // in-run: the failed instance exists
    }

    [Fact]
    public async Task Where_clause_is_a_guard_with_a_generated_message()
    {
        var def = WorkflowDefinition.Create<int, int>("where", 1, n =>
            from doubled in Durable.Step("double", () => n * 2)
            where doubled < 10
            select doubled);
        var runner = NewRunner();
        Assert.Equal(6, (await runner.RunAsync(def, "small", 3)).Value);
        var run = await runner.RunAsync(def, "big", 30);
        Assert.True(run.IsFailed);
        Assert.True(run.Error!.IsTerminal);
        Assert.Equal("double", run.Error.StepName);
        Assert.Contains("guard failed", run.Error.Message);
    }

    [Fact]
    public async Task Ensure_failures_can_be_recovered_inside_the_workflow()
    {
        var def = WorkflowDefinition.Create<Unit, string>("recovered", 1, _ =>
            (from c in Durable.Step("load", () => new Customer("c1", null)).Ensure(c => c.Card is not null, "no card")
             select c.Id)
            .Recover(WorkflowError.ValidationCode, error => Durable.Step("fallback", () => "guest:" + error.Message)));
        var runner = NewRunner();
        Assert.Equal("guest:no card", (await runner.RunAsync(def, "k", Unit.Value)).Value);
    }

    // ---------------------------------------------------------------- input checks in Handle

    private sealed record Order(string Id, int Lines, decimal Amount);

    public sealed class Guarded : IWorkflow<int, string>
    {
        public static string Name => "guarded";
        public static int Version => 1;
        public Durable<string> Handle(int input) =>
            from _ in Durable.Start(input).Ensure(n => n != 0, "zero is not allowed")
            from s in Durable.Step("fmt", () => input.ToString())
            select s;
    }

    [Fact]
    public async Task Class_workflows_check_input_through_Ensure_in_Handle()
    {
        var def = WorkflowDefinitionClassExtensions.For<Guarded, int, string>(new Guarded());
        var runner = NewRunner();
        Assert.True((await runner.RunAsync(def, "z", 0)).IsFailed);
        Assert.Equal("7", (await runner.RunAsync(def, "s", 7)).Value);
    }

    // ---------------------------------------------------------------- exceptions from user code

    private sealed class CardDeclinedException(string reason) : Exception("card declined"), IWorkflowFailure
    {
        public string Code => "payments.declined";
        public bool IsTerminal => true;
        public object? Detail => new { reason };
    }

    private sealed record DeclineDetail(string Reason);

    [Fact]
    public async Task User_exception_implementing_IWorkflowFailure_is_classified_by_itself()
    {
        var attempts = 0;
        var def = WorkflowDefinition.Create<Unit, string>("declined", 1, _ =>
            Durable.Step("charge", () => { attempts++; throw new CardDeclinedException("insufficient funds"); return ""; },
                StepOptions.Retry(5, TimeSpan.FromMilliseconds(1))));
        var runner = NewRunner();
        var run = await runner.RunAsync(def, "k", Unit.Value);

        Assert.True(run.IsFailed);
        Assert.Equal(1, attempts);
        Assert.Equal("payments.declined", run.Error!.Code);
        Assert.True(run.Error.IsTerminal);
        Assert.Equal("charge", run.Error.StepName);
        Assert.Equal("insufficient funds", run.Error.DetailAs<DeclineDetail>()!.Reason);
        Assert.EndsWith("CardDeclinedException", run.Error.ExceptionType);
        Assert.False(await runner.RetryFailedAsync(runner.KeyFor(def, "k")));

        // terminal failures are recorded, so a replay yields the same failure without re-running the step
        await runner.Store.UpdateStatusAsync(runner.KeyFor(def, "k"), WorkflowStatus.Running, null);
        var again = await runner.RunAsync(def, "k", Unit.Value);
        Assert.Equal("payments.declined", again.Error!.Code);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task TerminalException_is_the_one_line_form()
    {
        var def = WorkflowDefinition.Create<Unit, int>("term", 1, _ =>
            Durable.Step("x", () => { throw new TerminalException("inventory.gone", "sold out", new { sku = "book" }); return 1; }));
        var run = await NewRunner().RunAsync(def, "k", Unit.Value);
        Assert.Equal("inventory.gone", run.Error!.Code);
        Assert.True(run.Error.IsTerminal);
    }

    private sealed class ThirdPartyException(int status) : Exception($"http {status}") { public int Status { get; } = status; }

    private sealed class HttpClassifier : IFailureClassifier
    {
        public WorkflowError? Classify(Exception exception) => exception is ThirdPartyException t
            ? WorkflowError.Create($"http.{t.Status}", t.Message, terminal: t.Status is >= 400 and < 500)
            : null;
    }

    [Fact]
    public async Task A_registered_classifier_upgrades_exceptions_you_do_not_own()
    {
        var runner = NewRunner(classifier: new HttpClassifier());
        var notFound = WorkflowDefinition.Create<Unit, int>("nf", 1, _ => Durable.Step("call", () => { throw new ThirdPartyException(404); return 1; }));
        var unavailable = WorkflowDefinition.Create<Unit, int>("ua", 1, _ => Durable.Step("call", () => { throw new ThirdPartyException(503); return 1; }));

        var a = await runner.RunAsync(notFound, "k", Unit.Value);
        Assert.Equal("http.404", a.Error!.Code);
        Assert.True(a.Error.IsTerminal);

        var b = await runner.RunAsync(unavailable, "k", Unit.Value);
        Assert.Equal("http.503", b.Error!.Code);
        Assert.False(b.Error.IsTerminal);
        Assert.True(await runner.RetryFailedAsync(runner.KeyFor(unavailable, "k")));
    }

    [Fact]
    public async Task Unclassified_exceptions_are_retryable_and_keyed_by_type_name()
    {
        var def = WorkflowDefinition.Create<Unit, int>("plain", 1, _ => Durable.Step("x", () => { throw new InvalidOperationException("odd"); return 1; }));
        var runner = NewRunner();
        var run = await runner.RunAsync(def, "k", Unit.Value);
        Assert.Equal("InvalidOperationException", run.Error!.Code);
        Assert.False(run.Error.IsTerminal);
        Assert.NotNull(run.Error.StackTrace);
        Assert.True(await runner.RetryFailedAsync(runner.KeyFor(def, "k")));
    }

    [Fact]
    public async Task Exceptions_in_let_and_select_are_captured_too()
    {
        var def = WorkflowDefinition.Create<Unit, int>("proj", 1, _ =>
            from a in Durable.Step("a", () => 1)
            let b = a / (a - 1)
            select b);
        var run = await NewRunner().RunAsync(def, "k", Unit.Value);
        Assert.True(run.IsFailed);
        Assert.Equal("DivideByZeroException", run.Error!.Code);
        Assert.Equal("a", run.Error.StepName);
    }

    // ---------------------------------------------------------------- designed failures

    [Fact]
    public async Task Try_returns_designed_failures_without_exceptions_and_Recover_matches_on_code()
    {
        var charges = 0;
        var def = WorkflowDefinition.Create<decimal, string>("try", 1, amount =>
            (from auth in Durable.Try("charge", () =>
                {
                    charges++;
                    return amount > 100
                        ? Outcome.Fail<string>("payments.declined", "over limit", terminal: true, detail: new { amount })
                        : Outcome.Ok("AUTH-1");
                })
             select "paid:" + auth)
            .Recover("payments.declined", error => Durable.Return("declined:" + error.DetailAs<AmountDetail>()!.Amount)));
        var runner = NewRunner();

        Assert.Equal("paid:AUTH-1", (await runner.RunAsync(def, "small", 50m)).Value);
        Assert.Equal("declined:500", (await runner.RunAsync(def, "big", 500m)).Value);

        var steps = await runner.Store.ListStepsAsync(runner.KeyFor(def, "big"));
        Assert.Equal(new[] { "charge" }, steps.Select(s => s.Name));
        Assert.True(steps[0].Failed);
        Assert.Equal(2, charges);
    }

    private sealed record AmountDetail(decimal Amount);

    [Fact]
    public async Task Recover_with_a_different_code_passes_the_failure_through()
    {
        var def = WorkflowDefinition.Create<Unit, string>("passthrough", 1, _ =>
            Durable.Try("x", () => Outcome.Fail<string>("a", "not b"))
                .Recover("b", _ => Durable.Return("handled")));
        var run = await NewRunner().RunAsync(def, "k", Unit.Value);
        Assert.True(run.IsFailed);
        Assert.Equal("a", run.Error!.Code);
    }

    [Fact]
    public async Task Retryable_Try_failures_are_not_recorded_so_a_retry_re_executes()
    {
        var attempts = 0;
        var def = WorkflowDefinition.Create<Unit, string>("soft", 1, _ =>
            Durable.Try("x", () => ++attempts < 2 ? Outcome.Fail<string>("later", "try again") : Outcome.Ok("done")));
        var runner = NewRunner();
        Assert.True((await runner.RunAsync(def, "k", Unit.Value)).IsFailed);
        Assert.Empty(await runner.Store.ListStepsAsync(runner.KeyFor(def, "k")));
        Assert.True(await runner.RetryFailedAsync(runner.KeyFor(def, "k")));
        Assert.Equal("done", (await runner.RunAsync(def, "k", Unit.Value)).Value);
    }

    [Fact]
    public async Task Fail_short_circuits_the_rest_of_the_query()
    {
        var ran = false;
        var def = WorkflowDefinition.Create<Unit, int>("fail", 1, _ =>
            from a in Durable.Step("a", () => 1)
            from b in Durable.Fail<int>("nope", "stopped here")
            from c in Durable.Step("c", () => { ran = true; return 3; })
            select a + b + c);
        var run = await NewRunner().RunAsync(def, "k", Unit.Value);
        Assert.Equal("nope", run.Error!.Code);
        Assert.False(ran);
    }

    // ---------------------------------------------------------------- Then

    [Fact]
    public async Task Then_runs_a_side_step_and_keeps_the_value()
    {
        var sent = new List<string>();
        var def = WorkflowDefinition.Create<Unit, string>("then", 1, _ =>
            from c in Durable.Step("load", () => new Customer("c1", "card"))
                .Then("notify", c => sent.Add(c.Id))
                .Then("audit", async (c, ct) => { await Task.Yield(); sent.Add("audit:" + c.Id); })
            select c.Id);
        var runner = NewRunner();
        Assert.Equal("c1", (await runner.RunAsync(def, "k", Unit.Value)).Value);
        Assert.Equal(new[] { "c1", "audit:c1" }, sent);
        Assert.Equal(new[] { "load", "notify", "audit" }, (await runner.Store.ListStepsAsync(runner.KeyFor(def, "k"))).Select(s => s.Name));
    }
}
