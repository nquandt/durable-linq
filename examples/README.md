# Examples

Simplest first. Each folder is a runnable project. The comment block at the top of each `Program.cs`
tells you how to run it and what to look for.

| Example | Store | Shows |
|---|---|---|
| `01-Console-FileStore` | file | A console app with DI, injected services, and one class per command. Crash mid-way with `--crash`, run again, watch the replay. |
| `02-WebApi-Hosting` | file | `AddDurable`, scoped services, a queue-style consumer, `Sleep`, `WaitFor` + signals, retries, recovery sweeper. |
| `03-Redis-Workers` | Redis | Several hosts sharing one store, leases, recovery after a killed worker, the dashboard container. `docker compose up`. |
| `04-Postgres-Approvals` | Postgres | Human-in-the-loop approval with `If`, `WaitFor` with timeout, `Recover`, and the dashboard mounted in-process. `docker compose up`. |

Two forms of every workflow. Each `Workflows/` folder holds the workflow twice: once as a query
expression (`CheckoutWorkflow`) and once as an `async Durable<T>` method (`CheckoutAsyncWorkflow`),
where every `await` on a durable is one recorded step and loops and branches are plain C#. Both
classes share a name and a version, so they read and write the same instances. Set
`WORKFLOW_STYLE=async` to register the async version; anything else registers the query version.

Adoption path:

1. Copy the shape of example 01 into one existing handler. Use `UseFileStore`.
2. When a second host appears, switch to `UseRedis`, `UsePostgres`, or `UseSqlServer`. Workflow code does not change.
3. Mount `MapDurableDashboard` in the app, or run the `DurableLinq.Dashboard.Host` container next to the store.
