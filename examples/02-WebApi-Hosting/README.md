# Example 02: WebApi Hosting

DurableLinq inside an ASP.NET Core app with dependency injection.

`Workflows/` holds `CheckoutWorkflow` (query expression) and `CheckoutAsyncWorkflow` (`async Durable<T>` method). Run with `WORKFLOW_STYLE=async dotnet run` to register the second. Both write the same log.

```
dotnet run
curl -X POST localhost:5002/orders/o-1 -H "content-type: application/json" -d '{"customerId":"c-9","amount":42.5}'
curl localhost:5002/orders/o-1                      -> status: Running, waiting for the delivery signal
curl -X POST localhost:5002/orders/o-1/delivered    -> sends the signal, the workflow finishes
curl localhost:5002/orders/o-1                      -> status: Completed with the receipt
```

Kill the process while an order is running and start it again: the recovery sweeper
(from DurableLinq.Hosting) finds the instance with the expired lease and resumes it.
Uncomment the dashboard lines to browse instances at `/durable`.
