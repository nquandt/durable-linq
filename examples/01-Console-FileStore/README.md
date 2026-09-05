# 01-Console-FileStore

The smallest durable workflow, in a console app with dependency injection and the file store.

```
dotnet run                 runs the checkout workflow for order "order-42"
dotnet run -- --crash      the "ship" step kills the process after "charge" was recorded
dotnet run                 run again: the recorded steps replay, only "ship" executes
dotnet run -- --retry      if a step threw, put the instance back to Running and resume it
dotnet run -- --reset      delete the stored instance to start over
```

Layout:

| Folder | Holds |
|---|---|
| `Program.cs` | composition root: services, `AddDurable`, commands |
| `Workflows/` | `CheckoutWorkflow` (query expression) and `CheckoutAsyncWorkflow` (`async Durable<T>` method); `WORKFLOW_STYLE=async` registers the second |
| `Services/` | `ICustomerDb`, `IWarehouse`, `IPayments`, `IShipping` and their fakes |
| `Commands/` | one class per command line action, resolved by `CommandDispatcher` |
| `Models/` | records and the sample order |
| `Diagnostics/` | prints engine events so replay is visible |

State lives in `./workflow-state`.
