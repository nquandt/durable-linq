# Getting started

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

## Adoption path

1. Add the `DurableLinq` package, call `UseFileStore`, and wrap one existing handler's body in a query
   expression or an `async Durable<T>` method. Run it, kill the process mid-way, and run it again to see
   the replay.
2. Move to `UseRedis`, `UsePostgres`, or `UseSqlServer` when a second host appears. Workflow code does
   not change; only the registration line changes.
3. Add `DurableLinq.Analyzers` once more than one person writes workflows. See [Analyzers](analyzers.md).
4. Mount `MapDurableDashboard` in the app, or run the `DurableLinq.Dashboard.Host` container next to the
   store. See [Dashboard](dashboard.md).

## Rules for workflow authors

1. All I/O, time, and randomness go through a primitive. Code between `from` clauses, or between
   `await`s, is pure.
2. Step names are stable. If you insert or rename a step, increase the definition version.
3. Step bodies should be idempotent when the callee allows it. A crash between the external call and
   the log write re-runs the call. Pass a logged idempotency key to anything that charges money.

## Build and test the repository

```
dotnet build
dotnet test                                    # core + hosting tests need nothing
dotnet test tests/DurableLinq.Redis.Tests      # needs Docker
docker compose -f docker-compose.dashboard.yml up   # dashboard over ./workflow-state
```
