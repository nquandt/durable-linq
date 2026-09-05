# Analyzers

`DurableLinq.Analyzers` is a Roslyn analyzer that checks `async Durable<T>` methods for the two mistakes
that break replay. It has no dependency at runtime; `Microsoft.CodeAnalysis` is a compile-time-only
dependency.

## DL0001 — a `catch` that can swallow a failed step

A failed step throws `StepFailedException` at the `await`. The `async` method builder turns that
exception back into a failed `RunResult<T>`, so it must reach the builder uncaught. A `catch` block
around the `await` records nothing in the log, so a later retry could take a different path than the one
the `catch` block took.

```csharp
// DL0001: this catch can swallow a failed step
try
{
    var charge = await Durable.Step("charge", () => pay.Charge(customer.Card, total, key));
}
catch (Exception ex)
{
    ...
}
```

Use `.Recover(handler)` or `.Recover(code, handler)` on the durable before the `await` instead. See
[Failures and validation](concepts/failures-and-validation.md).

## DL0002 — a non-durable `await`

Every `await` inside an `async Durable<T>` method should be on a durable, so every I/O call the workflow
makes is recorded as a step. Awaiting an ordinary `Task` (a `Task.Delay`, an un-wrapped API call) is
invisible to the log: on replay, it runs again instead of returning the recorded value.

```csharp
// DL0002: awaiting a plain Task, not a durable
await Task.Delay(1000);
```

Use `Durable.Sleep(...)`, `Durable.Step(...)`, or another primitive instead. See
[Primitives](concepts/primitives.md).

## Referencing the analyzer

The analyzer is referenced the same way as any other Roslyn analyzer package: as a
`PackageReference` with `PrivateAssets="all"`. It is only used at compile time; it never reaches a
runtime package. Add it once more than one person writes workflows in the project.
