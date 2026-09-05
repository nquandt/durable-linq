# DurableLinq.SqlServer

A `IWorkflowStore` backed by SQL Server, using Dapper and Microsoft.Data.SqlClient.

## Registration

```csharp
using DurableLinq.Hosting;

builder.Services.AddDurable(durable =>
{
    durable.UseSqlServer("Server=localhost;Database=app;User Id=sa;Password=secret;TrustServerCertificate=true", options =>
    {
        options.Schema = "durable";      // default
        options.TablePrefix = null;      // optional, e.g. "orders_"
        options.AutoCreateSchema = true; // runs schema.sql idempotently on first use
    });

    durable.Define<OrderRequest, Receipt>("checkout", version: 1, (req, svc) => Checkout(req, svc));
});
```

`SqlServerWorkflowStore.EnsureSchemaAsync()` can also be called directly (e.g. during deployment) to
create the schema ahead of time instead of relying on `AutoCreateSchema`.

## Schema

Three tables under the configured schema (default `durable`), each optionally prefixed:

```sql
create table [durable].[workflow_instances] (
    instance_key   nvarchar(400) not null primary key,
    namespace      nvarchar(200) not null,
    workflow_name  nvarchar(200) not null,
    version        int           not null,
    instance_id    nvarchar(400) not null,
    status         tinyint       not null,   -- 0 running, 1 completed, 2 failed
    input          varbinary(max) null,
    result         varbinary(max) null,
    error          varbinary(max) null,
    lease_owner    nvarchar(200) null,
    lease_token    uniqueidentifier null,
    lease_expires  datetimeoffset null,
    created_at     datetimeoffset not null,
    updated_at     datetimeoffset not null
);

create table [durable].[workflow_steps] (
    instance_key nvarchar(400) not null,
    seq          int           not null,
    name         nvarchar(200) not null,
    payload      varbinary(max) null,
    recorded_at  datetimeoffset not null,
    failed       bit not null default 0,
    primary key (instance_key, seq)           -- duplicate append fails here
);

create table [durable].[workflow_signals] (
    id           bigint identity primary key,
    instance_key nvarchar(400) not null,
    name         nvarchar(200) not null,
    payload      varbinary(max) null,
    received_at  datetimeoffset not null default sysdatetimeoffset()
);
```

## Notes

- Duplicate step append surfaces as `DuplicateStepException` by catching SQL Server error numbers
  `2627` (unique constraint violation) and `2601` (duplicate key on unique index) on the
  `(instance_key, seq)` primary key.
- Lease acquisition is a single `UPDATE ... WHERE lease_owner IS NULL OR lease_expires < @now OR
  lease_owner = @owner`, checked by affected row count.
- `TryTakeSignalAsync` deletes the oldest unconsumed row for `(instance_key, name)` atomically using
  a CTE with `WITH (READPAST, UPDLOCK, ROWLOCK)` and `OUTPUT deleted.payload`, so concurrent takers
  never block or double-take.
- `WaitForSignalAsync` uses the default polling implementation from `IWorkflowStore`.
- `ListInstancesAsync` matches `InstanceIdContains` case-insensitively with `LOWER(...) LIKE LOWER(...)`.
