# Stores

`IWorkflowStore` is the only piece that changes between backends; workflow code never changes when you
switch stores. See [Store interface](../architecture/store-interface.md) for the contract every
implementation must honor.

| Store | Package | Use it for |
|---|---|---|
| [In-memory](file-and-in-memory.md) | `DurableLinq` | Unit tests and the first ten minutes with the library. |
| [File](file-and-in-memory.md) | `DurableLinq` | Local development and small single-node services. |
| [Redis](redis.md) | `DurableLinq.Redis` | The first step when moving from one host to several. |
| [PostgreSQL](postgres.md) | `DurableLinq.Postgres` | Teams that want to query workflow state with the same tools they use for everything else. |
| [SQL Server](sqlserver.md) | `DurableLinq.SqlServer` | Same as PostgreSQL, for a SQL Server shop. |

Every store package derives its tests from
`DurableLinq.Tests.Contract.WorkflowStoreContractTests`, so all stores are held to the same behavior.

## Choosing a store

1. Start with the file store. Zero configuration, single process.
2. Move to Redis when a second host appears and you don't need to run SQL queries against workflow
   state.
3. Move to Postgres or SQL Server when you want workflow state queryable alongside the rest of your
   data, or your team already runs one of these.

Switching stores is a one-line change in `AddDurable(...)`; see [Getting started](../getting-started.md).
