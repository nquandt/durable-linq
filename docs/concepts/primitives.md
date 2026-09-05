# Primitives

| Primitive | Records |
|---|---|
| `Step(name, work)` | the result; `StepOptions.Retry(n)` retries inside one run before failing |
| `Now()`, `NewGuid()`, `RandomInt()` | the value, so a replay sees the same one |
| `Sleep(d)` | the wake-up time; a restart waits only for the remainder |
| `WaitFor<T>(signal, timeout?)` | the signal payload, once an external caller sends it |
| `ForEach`, `If`, `While` | child steps with deterministic names; `If` records which branch ran |
| `Sub(name, child)` | the child's steps inline, prefixed with `name/` |
| `Recover`, `Compensate` | the failure at its position, so a replay follows the same recovery path |

All primitives are built on `Step`. See [How the engine works](how-it-works.md) for the constructor
that `Step` compiles down to, and [Failures and validation](failures-and-validation.md) for `Recover`
and `Compensate` in detail.
