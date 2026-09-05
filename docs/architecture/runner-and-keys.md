# Keys, the runner, and versioning

## Keys

Every piece of persisted state is addressed by an instance key:

```
{namespace}/{workflowName}/v{version}/{instanceId}
```

- `namespace` isolates applications sharing one store. Defaults to the entry assembly name.
- `workflowName` and `version` come from the definition. Version is part of the key so a v1 instance
  never replays against v2 code.
- `instanceId` is supplied by the caller. Use something that already identifies the unit of work: the
  order id, the message id, the webhook event id. Because the runner is idempotent per instance key, a
  queue redelivering the same message resumes the workflow instead of starting a second one.

Under an instance key the store holds a header (input, status, timestamps, lease), an ordered list of
step entries, and a signal inbox.

## The runner

`IWorkflowRunner` is the single entry point workflow hosts call:

```csharp
Task<TOut> RunAsync<TIn, TOut>(WorkflowDefinition<TIn, TOut> def, string instanceId, TIn input, CancellationToken ct);
Task SignalAsync<T>(string instanceKey, string signalName, T payload, CancellationToken ct);
Task<WorkflowStatus> GetStatusAsync(string instanceKey, CancellationToken ct);
```

What `RunAsync` does, in order:

1. **Create or load the instance.** If the header doesn't exist, write it with the serialized input. If
   it does, ignore the passed input and use the stored one, so a resumed run sees the exact input the
   original run saw.
2. **Short-circuit finished instances.** Status `Completed` returns the stored result; `Failed` rethrows
   the stored failure. Redelivered messages become no-ops.
3. **Acquire a lease.** A lease is a token with a TTL on the header. Two processors picking up the same
   message can't both execute the instance; the loser gets `WorkflowLeasedException` and should nack or
   requeue with delay. A background service renews the lease while the run is in progress.
4. **Build and run** the `Durable<TOut>` against a `WorkflowContext` over the store.
5. **Persist the outcome** as `Completed` with the result, or `Failed` with the exception, and release
   the lease. Failures from step bodies that throw are recorded so a retry policy can decide whether to
   resume (steps re-execute from the first unlogged one) or give up.

Step execution inside the run is unchanged from the core engine: sequence number, log lookup, execute or
replay, append. See [How the engine works](../concepts/how-it-works.md).

## Versioning

A definition's version is part of the instance key. Registering `checkout` v1 and v2 side by side means
in-flight v1 instances resume on v1 code until they drain, and new messages start on whichever version
the consumer references. Removing v1 from `Define` before its instances finish causes the sweeper to log
those instances as orphaned rather than run them against the wrong code. That's the whole versioning
story; it's crude, and it's the one thing that has never surprised anyone.
