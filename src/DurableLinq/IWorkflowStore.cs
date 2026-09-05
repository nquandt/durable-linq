namespace DurableLinq;

/// <summary>
/// Persistence for workflow instances. This is the only piece that varies by backend.
/// Two invariants every implementation must hold:
/// <list type="bullet">
/// <item><see cref="AppendStepAsync"/> is atomic and rejects a duplicate sequence number.</item>
/// <item><see cref="TryAcquireLeaseAsync"/> is atomic (compare-and-set).</item>
/// </list>
/// </summary>
public interface IWorkflowStore
{
    // ---- instance lifecycle ----

    Task<InstanceHeader?> GetHeaderAsync(string key, CancellationToken ct = default);

    /// <summary>Creates the header. Returns <c>false</c> without changes when the key already exists.</summary>
    Task<bool> TryCreateAsync(InstanceHeader header, CancellationToken ct = default);

    /// <summary>Sets the status. <paramref name="payload"/> is the result for Completed and the error for Failed.</summary>
    Task UpdateStatusAsync(string key, WorkflowStatus status, byte[]? payload, CancellationToken ct = default);

    // ---- leases ----

    /// <summary>Acquires the lease when it is free or expired. Returns <c>null</c> when another owner holds it.</summary>
    Task<Lease?> TryAcquireLeaseAsync(string key, string owner, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Extends the lease. Returns <c>false</c> when the lease token no longer matches.</summary>
    Task<bool> RenewLeaseAsync(Lease lease, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Releases the lease when the token matches. Does nothing otherwise.</summary>
    Task ReleaseLeaseAsync(Lease lease, CancellationToken ct = default);

    // ---- step log ----

    Task<StepEntry?> TryGetStepAsync(string key, int seq, CancellationToken ct = default);

    /// <summary>Appends one entry. Must throw <see cref="DuplicateStepException"/> when the sequence number exists.</summary>
    Task AppendStepAsync(string key, StepEntry entry, CancellationToken ct = default);

    /// <summary>All entries in sequence order. Used by the dashboard and by diagnostics.</summary>
    Task<IReadOnlyList<StepEntry>> ListStepsAsync(string key, CancellationToken ct = default);

    // ---- signals ----

    Task AppendSignalAsync(string key, string name, byte[] payload, CancellationToken ct = default);

    /// <summary>Removes and returns the oldest pending signal with this name, or <c>null</c>.</summary>
    Task<byte[]?> TryTakeSignalAsync(string key, string name, CancellationToken ct = default);

    /// <summary>
    /// Blocks until a signal arrives or the token cancels. The default implementation polls
    /// <see cref="TryTakeSignalAsync"/> with backoff. Backends with push support override this.
    /// </summary>
    async Task<byte[]> WaitForSignalAsync(string key, string name, CancellationToken ct = default)
    {
        var delay = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            var payload = await TryTakeSignalAsync(key, name, ct).ConfigureAwait(false);
            if (payload is not null) return payload;
            await Task.Delay(delay, ct).ConfigureAwait(false);
            if (delay < TimeSpan.FromSeconds(2)) delay += delay;
        }
    }

    // ---- recovery and inspection ----

    /// <summary>Keys of instances that are Running and whose lease expired before <paramref name="expiredBefore"/>, or that never had a lease.</summary>
    IAsyncEnumerable<string> ListRunnableAsync(DateTimeOffset expiredBefore, CancellationToken ct = default);

    /// <summary>Headers that match the query, newest first.</summary>
    Task<IReadOnlyList<InstanceHeader>> ListInstancesAsync(InstanceQuery query, CancellationToken ct = default);

    /// <summary>Removes an instance and all its state. Used by tests and by the dashboard.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Raised by a store when an append targets a sequence number that already exists.</summary>
public sealed class DuplicateStepException(string instanceKey, int seq)
    : DurableException($"Instance '{instanceKey}' already has a step at #{seq}.")
{
    public string InstanceKey { get; } = instanceKey;
    public int Seq { get; } = seq;
}
