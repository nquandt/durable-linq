# DurableLinq

[![CI](https://github.com/nquandt/durable-linq/actions/workflows/ci.yml/badge.svg)](https://github.com/nquandt/durable-linq/actions/workflows/ci.yml)
[![Docs](https://img.shields.io/badge/docs-nquandt.github.io%2Fdurable--linq-blue)](https://nquandt.github.io/durable-linq/)
[![License: MIT](https://img.shields.io/badge/license-MIT-informational)](LICENSE)

Durable execution for .NET. A workflow is an `async Durable<T>` method or a C# query expression. Each
awaited durable, or each `from` clause, is a step. Steps are memoized to an append-only log. If the
process dies, the next run replays the completed steps from the log and resumes at the first unfinished
one. No external call runs twice.

```csharp
Durable<Receipt> Checkout(OrderRequest req, IDb db, IPayments pay, IShipping ship) =>
    from key in Durable.NewGuid("idempotency-key")
    from cust in Durable.Step("load-customer", () => db.LoadCustomer(req.CustomerId))
    from lines in Durable.ForEach(req.Lines, (l, i) => Durable.Step($"reserve[{i}]", () => db.Reserve(l)))
    let total = lines.Sum(l => l.UnitPrice * l.Qty)
    from charge in Durable.Step("charge", () => pay.Charge(cust.Card, total, key))
    from label in Durable.Step("ship", () => ship.CreateLabel(cust.Address))
    from _ in Durable.Sleep(TimeSpan.FromHours(1))
    from ok in Durable.WaitFor<bool>("delivery-confirmed")
    select new Receipt(charge.AuthCode, label.Tracking, total);
```

The same workflow as an `async` method:

```csharp
async Durable<Receipt> Checkout(OrderRequest req, IDb db, IPayments pay, IShipping ship)
{
    var key = await Durable.NewGuid("idempotency-key");
    var cust = await Durable.Step("load-customer", () => db.LoadCustomer(req.CustomerId));
    var lines = new List<Reservation>();
    for (var i = 0; i < req.Lines.Count; i++)
        lines.Add(await Durable.Step($"reserve[{i}]", () => db.Reserve(req.Lines[i])));
    var total = lines.Sum(l => l.UnitPrice * l.Qty);
    var charge = await Durable.Step("charge", () => pay.Charge(cust.Card, total, key));
    var label = await Durable.Step("ship", () => ship.CreateLabel(cust.Address));
    await Durable.Sleep(TimeSpan.FromHours(1));
    var ok = await Durable.WaitFor<bool>("delivery-confirmed");
    return new Receipt(charge.AuthCode, label.Tracking, total);
}
```

Both forms return a `Durable<Receipt>`, run on the same engine, and write the same log. See
"Two ways to write a workflow" below. `OVERVIEW.md` explains why the engine has this shape. `DESIGN.md` describes the packages, keys, runner,
and store interface in detail.

## Packages

| Package | What it adds | Dependencies |
|---|---|---|
| `DurableLinq` | `Durable<T>`, primitives, `WorkflowRunner`, `IWorkflowStore`, in-memory and file stores | none |
| `DurableLinq.Hosting` | `AddDurable(...)`, recovery sweeper, `ILogger` + metrics bridge | Microsoft.Extensions.* abstractions |
| `DurableLinq.Redis` | `UseRedis(...)` store | StackExchange.Redis |
| `DurableLinq.Postgres` | `UsePostgres(...)` store and schema | Npgsql, Dapper |
| `DurableLinq.SqlServer` | `UseSqlServer(...)` store and schema | Microsoft.Data.SqlClient, Dapper |
| `DurableLinq.Dashboard` | `MapDurableDashboard(...)` web UI and JSON API | ASP.NET Core (framework reference) |
| `DurableLinq.Dashboard.Host` | Standalone dashboard container | the packages above |
| `DurableLinq.Analyzers` | Roslyn analyzer: `DL0001` flags a `catch` that can swallow a failed step, `DL0002` flags a non-durable `await`, both inside `async Durable<T>` methods | none at runtime (compile-time only) |

## Quick start (no DI, file store)

```csharp
using DurableLinq;
using DurableLinq.Stores;

var def = WorkflowDefinition.Create<string, string>("greet", 1, name =>
    from now in Durable.Now()
    from text in Durable.Step("compose", () => $"hello {name} at {now:T}")
    from _ in Durable.Step("send", () => Console.Out.WriteLineAsync(text))
    select text);

var runner = new WorkflowRunner(new FileWorkflowStore("./workflow-state"));
var result = await runner.RunAsync(def, instanceId: "ada", input: "Ada");
```

Run it twice. The second run returns the stored result and executes nothing.

## Quick start (ASP.NET Core or Worker)

Write one sealed class per workflow. Dependencies come in through the constructor and are resolved from
that run's DI scope; the query expression lives in `Handle`.

```csharp
public sealed class CheckoutWorkflow(IDb db, IPayments pay, IShipping ship) : IWorkflow<OrderRequest, Receipt>
{
    public static string Name => "checkout";
    public static int Version => 2;

    public Durable<Receipt> Handle(OrderRequest req) =>
        from key in Durable.NewGuid("idempotency-key")
        from cust in Durable.Step("load-customer", () => db.LoadCustomer(req.CustomerId))
        from lines in Durable.ForEach(req.Lines, (l, i) => Durable.Step($"reserve[{i}]", () => db.Reserve(l)))
        let total = lines.Sum(l => l.UnitPrice * l.Qty)
        from charge in Durable.Step("charge", () => pay.Charge(cust.Card, total, key))
        from label in Durable.Step("ship", () => ship.CreateLabel(cust.Address))
        select new Receipt(charge.AuthCode, label.Tracking, total);
}
```

```csharp
builder.Services.AddDurable(d =>
{
    d.UseFileStore("./workflow-state");   // or UseInMemoryStore(), UseRedis(cs), UsePostgres(cs), UseSqlServer(cs)
    d.Namespace = "orders-api";
    d.AddWorkflow<CheckoutWorkflow, OrderRequest, Receipt>();
});

app.MapDurableDashboard("/durable");     // optional, from DurableLinq.Dashboard
```

`d.Define(name, version, (input, svc) => ...)` still exists for defining a workflow as a lambda instead
of a class.

Then, wherever messages arrive:

```csharp
public sealed class CheckoutConsumer(IWorkflowRunner runner, WorkflowDefinition<OrderRequest, Receipt> checkout)
{
    public Task Handle(OrderPlaced msg, CancellationToken ct) =>
        runner.RunAsync(checkout, instanceId: msg.OrderId, msg.Request, ct);
}
```

And to deliver a signal:

```csharp
await runner.SignalAsync(checkout, instanceId: orderId, "delivery-confirmed", true);
```

## Two ways to write a workflow

| | Query expression | `async` method |
|---|---|---|
| Signature | `Durable<TOut> Handle(TIn input)` | `async Durable<TOut> Handle(TIn input)` |
| A step | `from x in Durable.Step(...)` | `var x = await Durable.Step(...);` |
| Pure code | `let total = ...` | `var total = ...;` |
| Loops | `Durable.ForEach(items, ...)` | `for`, `foreach`, `while` |
| Branches | `Durable.If(cond, then, else)` records the branch | plain `if` on a value that came from a step or the input |
| Result | `select new Receipt(...)` | `return new Receipt(...);` |
| Registration, definitions, runner, stores, log | the same | the same |

Both forms produce a `Durable<T>`. Nothing runs when `Handle` is called. The runner runs the returned durable
against a context, and every step inside sees that context. In the query form, `SelectMany` passes the
context along. In the `async` form, `Durable<T>` is a task-like type with its own method builder
(`DurableMethodBuilder<T>`), and the builder passes the context to each awaited durable. There is no ambient
state, and no wrapper is needed around `Handle`.

The two forms compose. A query can take an `async` helper with `from x in Durable.Sub("name", Helper(arg))`.
An `async` body can `await` a query or another workflow's `Handle`. Every operator on `Durable<T>` (`Ensure`,
`Then`, `Recover`, `Compensate`) works on both, so the rules and the failure model below apply to both.

Pick the form that reads best for the workflow. The `async` form is easier for most C# readers and needs no
combinators for loops or branches. The query form keeps every step visible as one clause and makes the
"code between steps is pure" rule easy to see. Each example ships both; set `WORKFLOW_STYLE=async` to run the
`async` version.

Two things to know in the `async` form:

- A failed step throws `StepFailedException` at the `await`. Leave it uncaught. The builder turns it back into
  the failed outcome, and the run result is `Failed` exactly as in the query form. To handle a failure inside
  the workflow, put `.Recover(...)` on the durable before the `await` (see "Failures and validation"). A plain
  `catch` records nothing, so a later retry could take a different path. `DurableLinq.Analyzers` reports
  `DL0001` for such a `catch` and `DL0002` for an `await` on a `Task` or anything else that is not a durable.
- An `async Durable<T>` body runs once per `Durable<T>` object. `Handle` is called once per run, so this only
  matters if you store one in a variable and run it twice; use `Durable.Sub(name, () => Helper())` for that.

## Primitives

| Primitive | Records |
|---|---|
| `Step(name, work)` | the result; `StepOptions.Retry(n)` retries inside one run before failing |
| `Now()`, `NewGuid()`, `RandomInt()` | the value, so a replay sees the same one |
| `Sleep(d)` | the wake-up time; a restart waits only for the remainder |
| `WaitFor<T>(signal, timeout?)` | the signal payload, once an external caller sends it |
| `ForEach`, `If`, `While` | child steps with deterministic names; `If` records which branch ran |
| `Sub(name, child)` | the child's steps inline, prefixed with `name/` |
| `Recover`, `Compensate` | the failure at its position, so a replay follows the same recovery path |

## Failures and validation

DurableLinq never throws for a business outcome. `RunAsync`/`ResumeAsync` return `RunResult<TOut>`, whose
`Status` is one of `Completed`, `Failed`, or `Busy`; use `ValueOrThrow()` when you want the
one-line happy path instead.

| Situation | What to write | What happens |
|---|---|---|
| Bad input | `Durable.Start(input).Ensure(v => ..., "message")` as the first clause in `Handle`, the same as any other guard | The instance is created, then fails with a terminal `validation` error at that first step. Re-evaluated on replay, so no log entry is needed. |
| A step result is unacceptable | `.Ensure(v => ..., "message")` on the step, or a `where` clause | The instance fails with a terminal `validation` error that names the step. Re-evaluated on replay, so no log entry is needed. |
| A step can never succeed | throw `TerminalException(code, message)`, or implement `IWorkflowFailure` on your own exception | Retries stop, `RetryFailedAsync` refuses to reset it, and the stored error is marked `IsTerminal`. |
| A step failed for now | throw anything else | It is captured as a retryable error keyed by the exception's type name. `StepOptions.Retry(n)` retries inside the run; `RetryFailedAsync` resets the instance so the next run replays and re-executes the failed step. |
| A failure is part of the design | `Durable.Try(name, () => Outcome.Fail<T>(code, message, terminal))` | The step fails with the given code without throwing. |
| Handle a failure | `.Recover(handler)` or `.Recover(code, handler)` on the durable, before the `from` or the `await` | The failure is recorded at its position and the handler's result becomes the workflow's result; `Recover(code, ...)` only catches failures with that code, letting others propagate. `Compensate` runs a cleanup step and then re-fails with the original error. In an `async` body, do not `catch (StepFailedException)`: it records nothing. |

```csharp
public Durable<Receipt> Handle(Order order) =>
    from _ in Durable.Start(order).Ensure(o => o.Lines.Length != 0, "order has no lines")
    from customer in Durable.Step("load-customer", () => customers.LoadCustomerAsync(order.CustomerId))
        .Ensure(c => c.Card is not null, "customer has no card on file")
        .Then("audit", c => audit.RecordAsync(c.Id))          // a side step that keeps the value
    from auth in Durable.Step("charge", () => payments.ChargeAsync(customer.Card!, total, key))
    select ...;

var result = await runner.RunAsync(checkout, order.Id, order);
if (result.IsFailed) Console.WriteLine($"Failed at '{result.Error!.StepName}': {result.Error.Message}");
```

The same in the `async` form:

```csharp
public async Durable<Receipt> Handle(Order order)
{
    await Durable.Start(order).Ensure(o => o.Lines.Length != 0, "order has no lines");
    var customer = await Durable.Step("load-customer", () => customers.LoadCustomerAsync(order.CustomerId))
        .Ensure(c => c.Card is not null, "customer has no card on file")
        .Then("audit", c => audit.RecordAsync(c.Id));
    var decision = await Durable.WaitFor<Review>("manager-decision", timeout: TimeSpan.FromMinutes(2))
        .Select(r => new Decision(r.Approved, r.By))
        .Recover(WorkflowError.TimeoutCode, _ => Durable.Step("escalate", () => Decision.Escalated));
    ...
}
```

## Rules for workflow authors

1. All I/O, time, and randomness go through a primitive. Code between `from` clauses, or between `await`s, is pure.
2. Step names are stable. If you insert or rename a step, increase the definition version.
3. Step bodies should be idempotent when the callee allows it. A crash between the external call and
   the log write re-runs the call. Pass a logged idempotency key to anything that charges money.

## Repository layout

```
src/        packages
tests/      xUnit tests; store packages run the shared contract suite in Testcontainers
examples/   runnable examples, simplest first
CLAUDE.md   working brief for contributors and agents
```

## Build and test

```
dotnet build
dotnet test                      # core + hosting tests need nothing
dotnet test tests/DurableLinq.Redis.Tests      # needs Docker
docker compose -f docker-compose.dashboard.yml up   # dashboard over ./workflow-state
```

## Contributing

See `CONTRIBUTING.md` for setup, the pull request checklist, and how releases are versioned. Full docs
are at <https://nquandt.github.io/durable-linq/>.
