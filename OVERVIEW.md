# DurableLinq: durable execution with C# query syntax

## What it is

A workflow engine where the workflow is written as a C# query expression. Each `from` clause is a step. Steps are memoized to an append-only log, so if the process dies partway through, restarting the workflow replays the completed steps from the log and resumes at the first unfinished one. No external service call runs twice.

```csharp
Durable<Receipt> Checkout(OrderRequest req) =>
    from key    in Durable.NewGuid("idempotency-key")
    from cust   in Durable.Step("load-customer", () => db.LoadCustomer(req.CustomerId))
    from lines  in Durable.ForEach(req.Lines, (l, i) => Durable.Step($"reserve[{i}]", () => db.Reserve(l)))
    let total   =  lines.Sum(l => l.UnitPrice * l.Qty)
    from charge in Durable.Step("charge", () => stripe.Charge(cust.Card, total, key))
    from label  in Durable.Step("ship",   () => ups.CreateLabel(cust.Address))
    from _      in Durable.Sleep(TimeSpan.FromHours(1))
    from ok     in Durable.WaitFor<bool>("delivery-confirmed")
    from sent   in Durable.Step("notify", () => mail.Send(cust.Email, label.Tracking))
    select new Receipt(charge.AuthCode, label.Tracking, total);
```

The same workflow as an `async` method. Every `await` on a durable is a step, and the loop is a plain `for`:

```csharp
async Durable<Receipt> Checkout(OrderRequest req)
{
    var key    = await Durable.NewGuid("idempotency-key");
    var cust   = await Durable.Step("load-customer", () => db.LoadCustomer(req.CustomerId));
    var lines  = new List<Reservation>();
    for (var i = 0; i < req.Lines.Count; i++)
        lines.Add(await Durable.Step($"reserve[{i}]", () => db.Reserve(req.Lines[i])));
    var total  = lines.Sum(l => l.UnitPrice * l.Qty);
    var charge = await Durable.Step("charge", () => stripe.Charge(cust.Card, total, key));
    var label  = await Durable.Step("ship",   () => ups.CreateLabel(cust.Address));
    await Durable.Sleep(TimeSpan.FromHours(1));
    var ok     = await Durable.WaitFor<bool>("delivery-confirmed");
    var sent   = await Durable.Step("notify", () => mail.Send(cust.Email, label.Tracking));
    return new Receipt(charge.AuthCode, label.Tracking, total);
}
```

Both return a `Durable<Receipt>` and write the same log.

## Why this shape

**Steps are the only place code runs.** There is no line between two `from` clauses where someone can call `DateTime.Now` or hit an API without the engine seeing it. Every value the workflow uses came out of a step, so the workflow is deterministic by construction rather than by code review.

**The compiler does the plumbing.** Query syntax is duck-typed: the C# compiler rewrites `from a in x from b in f(a) select g(a, b)` into `x.SelectMany(a => f(a), (a, b) => g(a, b))` and only cares that a method with that name and shape exists. Defining `Select` and `SelectMany` on one type is enough. Earlier results stay in scope through the whole expression because the compiler threads them along with transparent identifiers.

**The `async` form is the same value.** `Durable<T>` is a task-like type with its own method builder. When a method is declared `async Durable<T>`, the compiler builds the usual state machine, but the builder does not start it. It wraps the machine in a `Durable<T>` and starts it only when the engine runs that durable against a context. Each `await` on a durable hands the builder an awaiter, and the builder runs the inner durable with the same context. So the context flows by parameter in both forms; the query hides it behind `SelectMany`, the `async` form behind the builder. Loops and branches need no combinators because the state machine is ordinary C#.

**The workflow is a value.** `Checkout(order)` returns a `Durable<Receipt>` without running anything. Workflows can be stored, passed around, composed into larger workflows, and run against different stores.

**The engine is tiny.** The type, the step constructor, the context, and the log interface are under two hundred lines together and have no dependencies.

## How it works

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

Running a workflow means creating a fresh context over an existing log and calling `RunAsync`. The Nth step in code always lines up with the Nth entry in the log because the code between steps is deterministic. If the names disagree, the code has changed in a way the history can't support, and the engine refuses rather than guessing.

Everything else is built on `Step`:

| Primitive | What it records |
|---|---|
| `Now()`, `NewGuid()` | The value, so replays see the same time and id |
| `Sleep(d)` | The wake-up time, so a restart waits only for the remainder |
| `WaitFor<T>(signal)` | Nothing until an external caller appends the signal payload at that position |
| `ForEach`, `If` | Child steps with deterministic names; `If` records which branch ran |
| `Sub(child)` | The child's steps inline in the parent log |

## The rules a workflow author follows

1. All I/O, time, and randomness go through a `Step` or one of the primitives above.
2. `let` clauses, and code between `await`s, are pure projections of earlier results.
3. Step names are stable. Inserting a step above existing ones changes the sequence, so definitions carry a version, and old instances resume on the old version.
4. Step bodies should be idempotent where the callee allows it. The log guarantees a step's *result* is used at most once, but a crash between the external call returning and the log write landing re-runs the call. Pass a logged idempotency key to anything that charges money.

## What it doesn't do

Query syntax has no loops, branches, or exception handling of its own, so those are combinators (`ForEach`, `If`, `Compensate`) instead of language keywords. Deeply conditional workflows read better as `async` methods, or split into several small ones joined with `Sub`. In the `async` form, a failed step throws `StepFailedException` at the `await`; leave it uncaught and the run fails as a value. Handle failures with `Recover` on the durable, not with `catch`, because `Recover` records the decision and `catch` does not.