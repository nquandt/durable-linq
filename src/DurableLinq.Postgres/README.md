# DurableLinq.Postgres

A `IWorkflowStore` backed by PostgreSQL, using Dapper and Npgsql.

## Registration

```csharp
using DurableLinq.Hosting;

builder.Services.AddDurable(durable =>
{
    durable.UsePostgres("Host=localhost;Database=app;Username=app;Password=secret", options =>
    {
        options.Schema = "durable";      // default
        options.TablePrefix = null;      // optional, e.g. "orders_"
        options.AutoCreateSchema = true; // runs schema.sql idempotently on first use
    });

    durable.Define<OrderRequest, Receipt>("checkout", version: 1, (req, svc) => Checkout(req, svc));
});
```

`PostgresWorkflowStore.EnsureSchemaAsync()` can also be called directly (e.g. during deployment) to
create the schema ahead of time instead of relying on `AutoCreateSchema`.

## Schema

Three tables under the configured schema (default `durable`), each optionally prefixed:

```sql
create table durable.workflow_instances (
    instance_key   varchar(400) primary key,
    namespace      varchar(200) not null,
    workflow_name  varchar(200) not null,
    version        int          not null,
    instance_id    varchar(400) not null,
    status         smallint     not null,   -- 0 running, 1 completed, 2 failed
    input          bytea,
    result         bytea,
    error          bytea,
    lease_owner    varchar(200),
    lease_token    uuid,
    lease_expires  timestamptz,
    created_at     timestamptz not null,
    updated_at     timestamptz not null
);

create table durable.workflow_steps (
    instance_key varchar(400) not null,
    seq          int          not null,
    name         varchar(200) not null,
    payload      bytea,
    recorded_at  timestamptz not null,
    failed       boolean     not null default false,
    primary key (instance_key, seq)          -- duplicate append fails here
);

create table durable.workflow_signals (
    id           bigserial primary key,
    instance_key varchar(400) not null,
    name         varchar(200) not null,
    payload      bytea,
    received_at  timestamptz not null default now()
);
```

## Notes

- Duplicate step append surfaces as `DuplicateStepException` by catching Postgres `SqlState 23505`
  (unique violation) on the `(instance_key, seq)` primary key.
- Lease acquisition is a single `UPDATE ... WHERE lease_owner IS NULL OR lease_expires < @now OR
  lease_owner = @owner`, checked by affected row count.
- `TryTakeSignalAsync` uses `DELETE ... WHERE id = (SELECT id ... ORDER BY id LIMIT 1 FOR UPDATE SKIP
  LOCKED) RETURNING payload` so concurrent takers never block or double-take.
- `WaitForSignalAsync` uses the default polling implementation from `IWorkflowStore`.
- `ListInstancesAsync` matches `InstanceIdContains` case-insensitively with `ILIKE`.
