# How the engine works

`Durable<T>` wraps a function from a workflow context to a task:

```csharp
public sealed class Durable<T>(Func<WorkflowContext, Task<T>> run)
{
    public Task<T> RunAsync(WorkflowContext ctx) => run(ctx);

    public Durable<R> Select<R>(Func<T, R> f) =>
        new(async ctx => f(await run(ctx)));

    public Durable<R> SelectMany<U, R>(Func<T, Durable<U>> bind, Func<T, U, R> project) =>
        new(async ctx =>
        {
            var t = await run(ctx);
            var u = await bind(t).RunAsync(ctx);
            return project(t, u);
        });
}
```

Durability lives in `Step`, which is the only constructor that touches the log:

```csharp
public static Durable<T> Step<T>(string name, Func<Task<T>> work) =>
    new(async ctx =>
    {
        var seq = ctx.NextSeq();                             // position in this run
        var prior = await ctx.Log.TryGetAsync(seq);
        if (prior is not null)
        {
            if (prior.Name != name) throw new NondeterminismException(seq, prior.Name, name);
            return prior.Payload.Deserialize<T>();           // replay: no work, no side effect
        }
        var result = await work();
        await ctx.Log.AppendAsync(new LogEntry(seq, name, Serialize(result)));
        return result;
    });
```

Running a workflow means creating a fresh context over an existing log and calling `RunAsync`. The Nth
step in code always lines up with the Nth entry in the log because the code between steps is
deterministic. If the names disagree, the code has changed in a way the history can't support, and the
engine refuses rather than guessing.

Everything else is built on `Step`. See [Primitives](primitives.md) for the full list.

## The rules a workflow author follows

1. All I/O, time, and randomness go through a `Step` or one of the primitives.
2. `let` clauses, and code between `await`s, are pure projections of earlier results.
3. Step names are stable. Inserting a step above existing ones changes the sequence, so definitions
   carry a version, and old instances resume on the old version.
4. Step bodies should be idempotent where the callee allows it. The log guarantees a step's *result* is
   used at most once, but a crash between the external call returning and the log write landing re-runs
   the call. Pass a logged idempotency key to anything that charges money.

For the class-level design (packages, keys, the runner, and store implementations), see
[Packages and design](../architecture/design.md).
