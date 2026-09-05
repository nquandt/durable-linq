# Packages and design

Goal: a developer adds one NuGet package, registers a store and some workflow definitions in
`Program.cs`, and calls `RunAsync` from wherever messages arrive. One process may host many workflows
fed by many queues. Swapping the store from file to Redis to SQL is a one-line change and never touches
workflow code.

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| `DurableLinq` | `Durable<T>`, primitives, `WorkflowDefinition`, `IWorkflowStore`, runner, in-memory and file stores | none |
| `DurableLinq.Hosting` | `AddDurable(...)` DI registration, lease renewal, recovery sweeper as hosted services | `Microsoft.Extensions.Hosting.Abstractions` |
| `DurableLinq.Redis` | Redis store | `StackExchange.Redis` |
| `DurableLinq.SqlServer`, `DurableLinq.Postgres` | SQL stores plus schema scripts | the respective ADO provider |
| `DurableLinq.Analyzers` | Roslyn analyzer. `DL0001`: a `catch` in an `async Durable<T>` method that can swallow `StepFailedException`. `DL0002`: an `await` on a non-durable in such a method. | none at runtime; Microsoft.CodeAnalysis at compile time only |

The core package is what people evaluate. It must run with zero configuration, which is why the file
store lives there and not in a separate package.

## Two authoring forms, one engine

`Durable<T>` carries `[AsyncMethodBuilder(typeof(DurableMethodBuilder<>))]` and a `GetAwaiter()`. That
makes `async Durable<T>` a legal method signature and `await someDurable` legal inside it.

- **Deferred start.** The builder's `Start` stores the state machine and returns. The durable it hands
  back runs the machine only when `RunAsync(ctx)` is called, once per `Durable<T>` object. A second
  `RunAsync` on the same object throws with a message that points to `Durable.Sub(name, () => Method())`.
- **Context flow.** `DurableAwaiter<T>` always reports `IsCompleted == false`, so the compiler routes
  every `await` through the builder's `AwaitUnsafeOnCompleted`. The builder recognizes the awaiter, runs
  the inner durable with the stored context, and resumes the machine when it completes. Awaits on
  ordinary tasks pass through unchanged; they are non-durable, and the analyzer reports them as `DL0002`.
- **Failures.** A failed outcome throws `StepFailedException` at the `await`. The builder's
  `SetException` maps it back to `Outcome<T>.Failure(error)`; other capturable exceptions go through
  `ctx.Capture`, and engine exceptions propagate. `RunResult<T>` and the runner do not change.
- **Log equality.** Sequence numbers are consumed in `await` order, which is source order, so an
  `async` body and a query with the same steps write the same log and can resume each other's instances.
  `Durable.If` records an `if` entry and a plain `if` does not, which is the one place the forms differ.

The `async` form is the friendlier one for most readers; the query form keeps every step visible as one
clause. Both are supported equally, and the examples ship both. See
[Two ways to write a workflow](../two-forms.md).

## Registration

The house style is a class per workflow (see [Getting started](../getting-started.md)). A lambda form
also exists, and is what the runner sees underneath either style:

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

`Define` registers a `WorkflowDefinition<TIn, TOut>`: a name, a version, and a factory from input to
`Durable<TOut>`. The `svc` argument is a `WorkflowServices` handle that resolves dependencies from DI at
run time, so workflow functions stay static and testable while still using the container.

Definitions are plain objects and can also be created without DI for tests:

```csharp
var def = WorkflowDefinition.Create<OrderRequest, Receipt>("checkout", 2, req => Checkout(req, fakeDb, fakeStripe, ...));
var result = await new WorkflowRunner(new InMemoryStore()).RunAsync(def, "order-42", order);
```

## Hosting in a multi-queue processor

Nothing in the engine knows about queues. Each consumer handler resolves the runner and calls `RunAsync`
with its definition and an instance id from the message:

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

Definitions are registered as singletons keyed by their `TIn, TOut` types, so consumers take them by
constructor injection. Four queues means four consumers and four `Define` calls; the store, runner, and
hosted services are shared.

Two hosted services come from `DurableLinq.Hosting`:

- **Lease renewer.** Tracks in-flight runs on this host and extends their leases at half the TTL.
- **Recovery sweeper.** Periodically calls `ListRunnableAsync` for instances whose lease expired while
  still `Running` (a host died mid-run) and re-invokes `RunAsync` for them using the stored input.
  Because the runner short-circuits completed instances and replays completed steps, resuming is always
  safe.

A message that a host was processing when it died will usually be redelivered by the queue as well. Both
paths converge on the same instance key, the lease makes them take turns, and the second one finds the
instance completed.

## Observability

The runner raises events on a `DurableEventSource`: instance started, step executed, step replayed,
signal received, lease lost, instance completed or failed, with the instance key and step name as tags.
`DurableLinq.Hosting` wires these into `ILogger` and `System.Diagnostics.Metrics` so replay ratio, step
latency, and running-instance counts show up in whatever the host already exports.

## Adoption path

1. Add `DurableLinq`, call `UseFileStore`, wrap one existing handler's body in a query expression, run
   it, kill the process mid-way, run it again.
2. Move to `UseRedis` or a SQL store when a second host appears.
3. Add `DurableLinq.Analyzers` once more than one person writes workflows. It ships two rules today, both
   for `async Durable<T>` methods; rules for query bodies (non-step I/O in `let` and `select`) are future
   work.
4. An agent skill for converting an existing async handler into the query shape is mechanical: identify
   each awaited external call, give it a step name, lift it into a `from` clause, keep pure code in
   `let`. The skill would produce a diff plus a list of calls it couldn't classify.
