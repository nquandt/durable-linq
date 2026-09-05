# Failures and validation

DurableLinq never throws for a business outcome. `RunAsync`/`ResumeAsync` return `RunResult<TOut>`, whose
`Status` is one of `Completed`, `Rejected`, `Failed`, or `Busy`. Use `ValueOrThrow()` when you want the
one-line happy path instead.

| Situation | What to write | What happens |
|---|---|---|
| Bad input | `Durable.Start(input).Ensure(v => ..., "message")` as the first clause in `Handle`, the same as any other guard | The instance is created, then fails with a terminal `validation` error at that first step. Re-evaluated on replay, so no log entry is needed. |
| A step result is unacceptable | `.Ensure(v => ..., "message")` on the step, or a `where` clause | The instance fails with a terminal `validation` error that names the step. Re-evaluated on replay, so no log entry is needed. |
| A step can never succeed | throw `TerminalException(code, message)`, or implement `IWorkflowFailure` on your own exception | Retries stop, `RetryFailedAsync` refuses to reset it, and the stored error is marked `IsTerminal`. |
| A step failed for now | throw anything else | It is captured as a retryable error keyed by the exception's type name. `StepOptions.Retry(n)` retries inside the run; `RetryFailedAsync` resets the instance so the next run replays and re-executes the failed step. |
| A failure is part of the design | `Durable.Try(name, () => Outcome.Fail<T>(code, message, terminal))` | The step fails with the given code without throwing. |
| Handle a failure | `.Recover(handler)` or `.Recover(code, handler)` on the durable, before the `from` or the `await` | The failure is recorded at its position and the handler's result becomes the workflow's result; `Recover(code, ...)` only catches failures with that code, letting others propagate. `Compensate` runs a cleanup step and then re-fails with the original error. In an `async` body, do not `catch (StepFailedException)`: it records nothing. |

```csharp
public Durable<Receipt> Handle(Order order) =>
    from _ in Durable.Start(order).Ensure(o => o.Lines.Length != 0, "order has no lines")
    from customer in Durable.Step("load-customer", () => customers.LoadCustomerAsync(order.CustomerId))
        .Ensure(c => c.Card is not null, "customer has no card on file")
        .Then("audit", c => audit.RecordAsync(c.Id))          // a side step that keeps the value
    from auth in Durable.Step("charge", () => payments.ChargeAsync(customer.Card!, total, key))
    select ...;

var result = await runner.RunAsync(checkout, order.Id, order);
if (result.IsFailed) Console.WriteLine($"Failed at '{result.Error!.StepName}': {result.Error.Message}");
```

The same in the `async` form:

```csharp
public async Durable<Receipt> Handle(Order order)
{
    await Durable.Start(order).Ensure(o => o.Lines.Length != 0, "order has no lines");
    var customer = await Durable.Step("load-customer", () => customers.LoadCustomerAsync(order.CustomerId))
        .Ensure(c => c.Card is not null, "customer has no card on file")
        .Then("audit", c => audit.RecordAsync(c.Id));
    var decision = await Durable.WaitFor<Review>("manager-decision", timeout: TimeSpan.FromMinutes(2))
        .Select(r => new Decision(r.Approved, r.By))
        .Recover(WorkflowError.TimeoutCode, _ => Durable.Step("escalate", () => Decision.Escalated));
    ...
}
```

## Rules that follow from this

1. All I/O, time, and randomness go through a primitive. Code between `from` clauses, or between
   `await`s, is pure.
2. Step names are stable. If you insert or rename a step, increase the definition version.
3. Step bodies should be idempotent when the callee allows it. A crash between the external call and
   the log write re-runs the call. Pass a logged idempotency key to anything that charges money.

`DurableLinq.Analyzers` catches two of the common mistakes at compile time. See
[Analyzers](../analyzers.md).
