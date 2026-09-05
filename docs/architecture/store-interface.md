# Store interface

The store is the only piece that varies by backend. It's deliberately narrow so a new backend is an
afternoon of work.

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

- `AppendStepAsync` is atomic and rejects duplicate sequence numbers. This is what makes a crash between
  "external call returned" and "log write landed" safe to reason about: the retry either finds the entry
  or doesn't, never a half-written one.
- `TryAcquireLeaseAsync` is atomic (compare-and-set). Without it two hosts can execute the same instance.

Serialization is behind `IWorkflowSerializer`; System.Text.Json is the default, and the store only ever
sees `byte[]`.

`WaitForSignalAsync` has a default polling implementation on the interface; a store overrides it when the
backend supports push (Redis `BLPOP`, Postgres `LISTEN`).

See [Stores](../stores/index.md) for the shipped implementations, and each store's page for its exact
key or table layout.
