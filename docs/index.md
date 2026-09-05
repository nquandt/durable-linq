# DurableLinq

Durable execution for .NET. A workflow is a C# query expression or an `async Durable<T>` method. Each
`from` clause, or each awaited durable, is a step. Steps are memoized to an append-only log. If the
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
[Two ways to write a workflow](two-forms.md).

## Where to start

- New to the library? Start with [Getting started](getting-started.md).
- Want the reasoning behind the design? Read [Why this shape](concepts/why-this-shape.md) and
  [How the engine works](concepts/how-it-works.md).
- Ready to pick a store for production? See [Stores](stores/index.md).
- Want to see it run? See [Examples](examples/index.md).

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
| `DurableLinq.Analyzers` | Roslyn analyzer: `DL0001` and `DL0002` | none at runtime (compile-time only) |
