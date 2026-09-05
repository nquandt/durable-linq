# Why this shape

**Steps are the only place code runs.** There is no line between two `from` clauses where someone can
call `DateTime.Now` or hit an API without the engine seeing it. Every value the workflow uses came out of
a step, so the workflow is deterministic by construction rather than by code review.

**The compiler does the plumbing.** Query syntax is duck-typed: the C# compiler rewrites
`from a in x from b in f(a) select g(a, b)` into `x.SelectMany(a => f(a), (a, b) => g(a, b))` and only
cares that a method with that name and shape exists. Defining `Select` and `SelectMany` on one type is
enough. Earlier results stay in scope through the whole expression because the compiler threads them
along with transparent identifiers.

**The `async` form is the same value.** `Durable<T>` is a task-like type with its own method builder.
When a method is declared `async Durable<T>`, the compiler builds the usual state machine, but the
builder does not start it. It wraps the machine in a `Durable<T>` and starts it only when the engine
runs that durable against a context. Each `await` on a durable hands the builder an awaiter, and the
builder runs the inner durable with the same context. So the context flows by parameter in both forms;
the query hides it behind `SelectMany`, the `async` form behind the builder. Loops and branches need no
combinators because the state machine is ordinary C#.

**The workflow is a value.** `Checkout(order)` returns a `Durable<Receipt>` without running anything.
Workflows can be stored, passed around, composed into larger workflows, and run against different
stores.

**The engine is tiny.** The type, the step constructor, the context, and the log interface are under
two hundred lines together and have no dependencies.

## What it doesn't do

Query syntax has no loops, branches, or exception handling of its own, so those are combinators
(`ForEach`, `If`, `Compensate`) instead of language keywords. Deeply conditional workflows read better
as `async` methods, or split into several small ones joined with `Sub`. In the `async` form, a failed
step throws `StepFailedException` at the `await`; leave it uncaught and the run fails as a value. Handle
failures with `Recover` on the durable, not with `catch`, because `Recover` records the decision and
`catch` does not.

See [How the engine works](how-it-works.md) for the mechanism, and
[Two ways to write a workflow](../two-forms.md) for the full comparison of the two authoring styles.
