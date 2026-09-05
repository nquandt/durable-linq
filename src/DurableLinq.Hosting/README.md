# DurableLinq.Hosting

`AddDurable(...)` DI registration for DurableLinq, plus the hosted services a running app needs: a lease
renewer and a recovery sweeper that resumes instances left running by a crashed host.

```csharp
builder.Services.AddDurable(d =>
{
    d.UseFileStore("./workflow-state");   // or UseRedis(cs), UsePostgres(cs), UseSqlServer(cs)
    d.AddWorkflow<CheckoutWorkflow, OrderRequest, Receipt>();
});
```

Full documentation is at <https://nquandt.github.io/durable-linq/>.
