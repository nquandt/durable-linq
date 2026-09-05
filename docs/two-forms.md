# Two ways to write a workflow

| | Query expression | `async` method |
|---|---|---|
| Signature | `Durable<TOut> Handle(TIn input)` | `async Durable<TOut> Handle(TIn input)` |
| A step | `from x in Durable.Step(...)` | `var x = await Durable.Step(...);` |
| Pure code | `let total = ...` | `var total = ...;` |
| Loops | `Durable.ForEach(items, ...)` | `for`, `foreach`, `while` |
| Branches | `Durable.If(cond, then, else)` records the branch | plain `if` on a value that came from a step or the input |
| Result | `select new Receipt(...)` | `return new Receipt(...);` |
| Registration, definitions, runner, stores, log | the same | the same |

Both forms produce a `Durable<T>`. Nothing runs when `Handle` is called. The runner runs the returned
durable against a context, and every step inside sees that context. In the query form, `SelectMany`
passes the context along. In the `async` form, `Durable<T>` is a task-like type with its own method
builder (`DurableMethodBuilder<T>`), and the builder passes the context to each awaited durable. There is
no ambient state, and no wrapper is needed around `Handle`.

The two forms compose. A query can take an `async` helper with `from x in Durable.Sub("name", Helper(arg))`.
An `async` body can `await` a query or another workflow's `Handle`. Every operator on `Durable<T>`
(`Ensure`, `Then`, `Recover`, `Compensate`) works on both, so the failure model in
[Failures and validation](concepts/failures-and-validation.md) applies to both.

Pick the form that reads best for the workflow. The `async` form is easier for most C# readers and needs
no combinators for loops or branches. The query form keeps every step visible as one clause and makes the
"code between steps is pure" rule easy to see. Each example in [Examples](examples/index.md) ships both;
set `WORKFLOW_STYLE=async` to run the `async` version.

Two things to know in the `async` form:

- A failed step throws `StepFailedException` at the `await`. Leave it uncaught. The builder turns it
  back into the failed outcome, and the run result is `Failed` exactly as in the query form. To handle a
  failure inside the workflow, put `.Recover(...)` on the durable before the `await`. A plain `catch`
  records nothing, so a later retry could take a different path. `DurableLinq.Analyzers` reports `DL0001`
  for such a `catch` and `DL0002` for an `await` on a `Task` or anything else that is not a durable.
- An `async Durable<T>` body runs once per `Durable<T>` object. `Handle` is called once per run, so this
  only matters if you store one in a variable and run it twice; use `Durable.Sub(name, () => Helper())`
  for that.
