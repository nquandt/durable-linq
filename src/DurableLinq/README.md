# DurableLinq

Durable execution for .NET. A workflow is a C# query expression or an `async Durable<T>` method. Each
step is memoized to an append-only log, so a crashed process resumes exactly where it left off, and no
external call runs twice. Zero dependencies; includes in-memory and file-backed stores.

```csharp
Durable<Receipt> Checkout(OrderRequest req, IDb db, IPayments pay) =>
    from key in Durable.NewGuid("idempotency-key")
    from cust in Durable.Step("load-customer", () => db.LoadCustomer(req.CustomerId))
    from charge in Durable.Step("charge", () => pay.Charge(cust.Card, req.Total, key))
    select new Receipt(charge.AuthCode);
```

Full documentation, examples, and the store packages (Redis, PostgreSQL, SQL Server) are at
<https://nquandt.github.io/durable-linq/>.
