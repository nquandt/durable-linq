# DurableLinq package design

Goal: a developer adds one NuGet package, registers a store and some workflow definitions in `Program.cs`, and calls `RunAsync` from wherever messages arrive. One process may host many workflows fed by many queues. Swapping the store from file to Redis to SQL is a one-line change and never touches workflow code.

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| `DurableLinq` | `Durable<T>`, primitives, `WorkflowDefinition`, `IWorkflowStore`, runner, in-memory and file stores | none |
| `DurableLinq.Hosting` | `AddDurable(...)` DI registration, lease renewal, recovery sweeper as hosted services | `Microsoft.Extensions.Hosting.Abstractions` |
| `DurableLinq.Redis` | Redis store | `StackExchange.Redis` |
| `DurableLinq.SqlServer`, `DurableLinq.Postgres` | SQL stores plus schema scripts | the respective ADO provider |
| `DurableLinq.Analyzers` | Roslyn analyzer. `DL0001`: a `catch` in an `async Durable<T>` method that can swallow `StepFailedException`. `DL0002`: an `await` on a non-durable in such a method. | none at runtime; Microsoft.CodeAnalysis at compile time only |

The core package is what people evaluate. It must run with zero configuration, which is why the file store lives there and not in a separate package.

## Two authoring forms, one engine

`Durable<T>` carries `[AsyncMethodBuilder(typeof(DurableMethodBuilder<>))]` and a `GetAwaiter()`. That makes
`async Durable<T>` a legal method signature and `await someDurable` legal inside it.

- **Deferred start.** The builder's `Start` stores the state machine and returns. The durable it hands back
  runs the machine only when `RunAsync(ctx)` is called, once per `Durable<T>` object. A second `RunAsync` on the
  same object throws with a message that points to `Durable.Sub(name, () => Method())`.
- **Context flow.** `DurableAwaiter<T>` always reports `IsCompleted == false`, so the compiler routes every
  `await` through the builder's `AwaitUnsafeOnCompleted`. The builder recognizes the awaiter, runs the inner
  durable with the stored context, and resumes the machine when it completes. Awaits on ordinary tasks pass
  through unchanged; they are non-durable, and the analyzer reports them as `DL0002`.
- **Failures.** A failed outcome throws `StepFailedException` at the `await`. The builder's `SetException`
  maps it back to `Outcome<T>.Failure(error)`; other capturable exceptions go through `ctx.Capture`, and engine
  exceptions propagate. `RunResult<T>` and the runner do not change.
- **Log equality.** Sequence numbers are consumed in `await` order, which is source order, so an `async` body
  and a query with the same steps write the same log and can resume each other's instances. `Durable.If`
  records an `if` entry and a plain `if` does not, which is the one place the forms differ.

The `async` form is the friendlier one for most readers; the query form keeps every step visible as one
clause. Both are supported equally, and the examples ship both.

## Registration

```csharp
builder.Services.AddDurable(durable =>
{
    durable.UseFileStore("./workflow-state");            // or UseInMemoryStore(), UseRedis(cs), UseSqlServer(cs)
    durable.Namespace = "orders-api";                    // optional prefix on every key; lets teams share a store

    durable.Define<OrderRequest, Receipt>("checkout", version: 2,
        (req, svc) => Checkout(req, svc.Db, svc.Payments, svc.Shipping, svc.Mail));

    durable.Define<RefundRequest, RefundResult>("refund", version: 1,
        (req, svc) => Refund(req, svc.Payments, svc.Mail));
});
```

`Define` registers a `WorkflowDefinition<TIn, TOut>`: a name, a version, and a factory from input to `Durable<TOut>`. The `svc` argument is a `WorkflowServices` handle that resolves dependencies from DI at run time, so workflow functions stay static and testable while still using the container.

Definitions are plain objects and can also be created without DI for tests:

```csharp
var def = WorkflowDefinition.Create<OrderRequest, Receipt>("checkout", 2, req => Checkout(req, fakeDb, fakeStripe, ...));
var result = await new WorkflowRunner(new InMemoryStore()).RunAsync(def, "order-42", order);
```

## Keys

Every piece of persisted state is addressed by an instance key:

```
{namespace}/{workflowName}/v{version}/{instanceId}
```

- `namespace` isolates applications sharing one store. Defaults to the entry assembly name.
- `workflowName` and `version` come from the definition. Version is part of the key so a v1 instance never replays against v2 code.
- `instanceId` is supplied by the caller. Use something that already identifies the unit of work: the order id, the message id, the webhook event id. Because the runner is idempotent per instance key, a queue redelivering the same message resumes the workflow instead of starting a second one.

Under an instance key the store holds a header (input, status, timestamps, lease), an ordered list of step entries, and a signal inbox.

## The runner

`IWorkflowRunner` is the single entry point workflow hosts call:

```csharp
Task<TOut> RunAsync<TIn, TOut>(WorkflowDefinition<TIn, TOut> def, string instanceId, TIn input, CancellationToken ct);
Task SignalAsync<T>(string instanceKey, string signalName, T payload, CancellationToken ct);
Task<WorkflowStatus> GetStatusAsync(string instanceKey, CancellationToken ct);
```

What `RunAsync` does, in order:

1. **Create or load the instance.** If the header doesn't exist, write it with the serialized input. If it does, ignore the passed input and use the stored one, so a resumed run sees the exact input the original run saw.
2. **Short-circuit finished instances.** Status `Completed` returns the stored result; `Failed` rethrows the stored failure. Redelivered messages become no-ops.
3. **Acquire a lease.** A lease is a token with a TTL on the header. Two processors picking up the same message can't both execute the instance; the loser gets `WorkflowLeasedException` and should nack or requeue with delay. A background service renews the lease while the run is in progress.
4. **Build and run** the `Durable<TOut>` against a `WorkflowContext` over the store.
5. **Persist the outcome** as `Completed` with the result, or `Failed` with the exception, and release the lease. Failures from step bodies that throw are recorded so a retry policy can decide whether to resume (steps re-execute from the first unlogged one) or give up.

Step execution inside the run is unchanged from the core engine: sequence number, log lookup, execute or replay, append.

## Store interface

The store is the only piece that varies by backend. It's deliberately narrow so a new backend is an afternoon of work.

```csharp
public interface IWorkflowStore
{
    // instance lifecycle
    Task<InstanceHeader?> GetHeaderAsync(string key, CancellationToken ct);
    Task<bool> TryCreateAsync(string key, InstanceHeader header, CancellationToken ct);   // false if it exists
    Task UpdateStatusAsync(string key, WorkflowStatus status, byte[]? resultOrError, CancellationToken ct);

    // leases
    Task<Lease?> TryAcquireLeaseAsync(string key, string owner, TimeSpan ttl, CancellationToken ct);
    Task<bool> RenewLeaseAsync(Lease lease, TimeSpan ttl, CancellationToken ct);
    Task ReleaseLeaseAsync(Lease lease, CancellationToken ct);

    // step log
    Task<StepEntry?> TryGetStepAsync(string key, int seq, CancellationToken ct);
    Task AppendStepAsync(string key, StepEntry entry, CancellationToken ct);              // must fail if seq exists

    // signals
    Task AppendSignalAsync(string key, string name, byte[] payload, CancellationToken ct);
    Task<byte[]?> TryTakeSignalAsync(string key, string name, CancellationToken ct);

    // recovery
    IAsyncEnumerable<string> ListRunnableAsync(DateTimeOffset expiredBefore, CancellationToken ct);  // running, lease expired
}
```

Two invariants every implementation must hold:

- `AppendStepAsync` is atomic and rejects duplicate sequence numbers. This is what makes a crash between "external call returned" and "log write landed" safe to reason about: the retry either finds the entry or doesn't, never a half-written one.
- `TryAcquireLeaseAsync` is atomic (compare-and-set). Without it two hosts can execute the same instance.

Serialization is behind `IWorkflowSerializer`; System.Text.Json is the default, and the store only ever sees `byte[]`.

## Store implementations

**In-memory.** Dictionaries behind a lock. For unit tests and for the first ten minutes with the library.

**File.** One directory per instance key, containing `header.json`, `steps.jsonl` (append with write-through and fsync), and `signals/`. Leases are the header's lease fields rewritten with a temp-file-and-rename. Single-process only, since file locks aren't reliable across hosts. Good for local development and for small single-node services.

**Redis.** Per instance key:

| Redis key | Type | Holds |
|---|---|---|
| `{key}:header` | hash | input, status, result, lease owner, lease expiry |
| `{key}:steps` | list | step entries in sequence order; `LINDEX` for lookup, `RPUSH` inside a Lua script that checks `LLEN == seq` for append |
| `{key}:signal:{name}` | list | pending signal payloads; `BLPOP` for wait |
| `{ns}:running` | sorted set | instance keys scored by lease expiry, for the recovery sweep |

Leases use `SET key owner NX PX ttl` on `{key}:lease`, renewal is a Lua compare-and-extend. Redis is the natural first step when moving from one host to several.

**SQL (SQL Server, Postgres).** Three tables:

```sql
create table workflow_instances (
    instance_key   varchar(400) primary key,
    workflow_name  varchar(200) not null,
    version        int          not null,
    status         tinyint      not null,        -- 0 running, 1 completed, 2 failed
    input          varbinary(max),
    result         varbinary(max),
    lease_owner    varchar(100),
    lease_expires  datetime2,
    created_at     datetime2 not null,
    updated_at     datetime2 not null
);
create index ix_instances_recovery on workflow_instances (status, lease_expires);

create table workflow_steps (
    instance_key varchar(400) not null,
    seq          int          not null,
    name         varchar(200) not null,
    payload      varbinary(max),
    recorded_at  datetime2 not null,
    primary key (instance_key, seq)             -- duplicate append fails here; that is the invariant
);

create table workflow_signals (
    id           bigint identity primary key,
    instance_key varchar(400) not null,
    name         varchar(200) not null,
    payload      varbinary(max),
    consumed     bit not null default 0
);
```

Lease acquisition is `update ... where lease_owner is null or lease_expires < @now`, checking rows affected. Waiting on a signal polls the table with backoff; a `NOTIFY` channel on Postgres or a Service Broker queue on SQL Server can replace the poll later without changing the interface. SQL is where most teams end up once they want to query workflow state with the same tools they use for everything else.

## Hosting in a multi-queue processor

Nothing in the engine knows about queues. Each consumer handler resolves the runner and calls `RunAsync` with its definition and an instance id from the message:

```csharp
public sealed class CheckoutConsumer(IWorkflowRunner runner, WorkflowDefinition<OrderRequest, Receipt> checkout)
    : IConsumer<OrderPlaced>
{
    public Task Handle(OrderPlaced msg, CancellationToken ct) =>
        runner.RunAsync(checkout, instanceId: msg.OrderId, msg.Request, ct);
}

public sealed class RefundConsumer(IWorkflowRunner runner, WorkflowDefinition<RefundRequest, RefundResult> refund)
    : IConsumer<RefundRequested>
{
    public Task Handle(RefundRequested msg, CancellationToken ct) =>
        runner.RunAsync(refund, instanceId: msg.RefundId, msg.Request, ct);
}

public sealed class DeliveryWebhook(IWorkflowRunner runner) 
{
    public Task Post(DeliveryEvent e, CancellationToken ct) =>
        runner.SignalAsync($"orders-api/checkout/v2/{e.OrderId}", "delivery-confirmed", e.Confirmed, ct);
}
```

Definitions are registered as singletons keyed by their `TIn, TOut` types, so consumers take them by constructor injection. Four queues means four consumers and four `Define` calls; the store, runner, and hosted services are shared.

Two hosted services come from `DurableLinq.Hosting`:

- **Lease renewer.** Tracks in-flight runs on this host and extends their leases at half the TTL.
- **Recovery sweeper.** Periodically calls `ListRunnableAsync` for instances whose lease expired while still `Running` (a host died mid-run) and re-invokes `RunAsync` for them using the stored input. Because the runner short-circuits completed instances and replays completed steps, resuming is always safe.

A message that a host was processing when it died will usually be redelivered by the queue as well. Both paths converge on the same instance key, the lease makes them take turns, and the second one finds the instance completed.

## Versioning

A definition's version is part of the instance key. Registering `checkout` v1 and v2 side by side means in-flight v1 instances resume on v1 code until they drain, and new messages start on whichever version the consumer references. Removing v1 from `Define` before its instances finish causes the sweeper to log those instances as orphaned rather than run them against the wrong code. That's the whole versioning story; it's crude, and it's the one thing that has never surprised anyone.

## Observability

The runner raises events on a `DurableEventSource`: instance started, step executed, step replayed, signal received, lease lost, instance completed or failed, with the instance key and step name as tags. `DurableLinq.Hosting` wires these into `ILogger` and `System.Diagnostics.Metrics` so replay ratio, step latency, and running-instance counts show up in whatever the host already exports.

## Adoption path

1. Add `DurableLinq`, call `UseFileStore`, wrap one existing handler's body in a query expression, run it, kill the process mid-way, run it again.
2. Move to `UseRedis` or a SQL store when a second host appears.
3. Add `DurableLinq.Analyzers` once more than one person writes workflows. It ships two rules today, both for
   `async Durable<T>` methods; rules for query bodies (non-step I/O in `let` and `select`) are future work.
4. An agent skill for converting an existing async handler into the query shape is mechanical: identify each awaited external call, give it a step name, lift it into a `from` clause, keep pure code in `let`. The skill would produce a diff plus a list of calls it couldn't classify.