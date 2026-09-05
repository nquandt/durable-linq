using System.Runtime.CompilerServices;

namespace DurableLinq.Stores;

/// <summary>Dictionaries behind a lock. For unit tests and for the first ten minutes with the library.</summary>
public sealed class InMemoryWorkflowStore : IWorkflowStore
{
    private sealed class Instance
    {
        public required InstanceHeader Header;
        public readonly List<StepEntry> Steps = new();
        public readonly Dictionary<string, Queue<byte[]>> Signals = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _signalArrived = new(0, int.MaxValue);

    public InMemoryWorkflowStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    private DateTimeOffset Now => _time.GetUtcNow();

    public Task<InstanceHeader?> GetHeaderAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_instances.TryGetValue(key, out var i) ? i.Header : null);
    }

    public Task<bool> TryCreateAsync(InstanceHeader header, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_instances.ContainsKey(header.Key)) return Task.FromResult(false);
            _instances[header.Key] = new Instance { Header = header };
            return Task.FromResult(true);
        }
    }

    public Task UpdateStatusAsync(string key, WorkflowStatus status, byte[]? payload, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var i = Get(key);
            i.Header = i.Header with
            {
                Status = status,
                Result = status == WorkflowStatus.Completed ? payload : i.Header.Result,
                Error = status == WorkflowStatus.Failed ? payload : status == WorkflowStatus.Running ? null : i.Header.Error,
                UpdatedAt = Now,
            };
        }
        return Task.CompletedTask;
    }

    public Task<Lease?> TryAcquireLeaseAsync(string key, string owner, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var i = Get(key);
            var now = Now;
            if (i.Header.IsLeasedAt(now) && i.Header.LeaseOwner != owner) return Task.FromResult<Lease?>(null);
            var lease = new Lease(key, owner, Guid.NewGuid(), now + ttl);
            i.Header = i.Header with { LeaseOwner = owner, LeaseToken = lease.Token, LeaseExpires = lease.Expires, UpdatedAt = now };
            return Task.FromResult<Lease?>(lease);
        }
    }

    public Task<bool> RenewLeaseAsync(Lease lease, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(lease.InstanceKey, out var i)) return Task.FromResult(false);
            if (i.Header.LeaseToken != lease.Token) return Task.FromResult(false);
            i.Header = i.Header with { LeaseExpires = Now + ttl };
            return Task.FromResult(true);
        }
    }

    public Task ReleaseLeaseAsync(Lease lease, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_instances.TryGetValue(lease.InstanceKey, out var i) && i.Header.LeaseToken == lease.Token)
                i.Header = i.Header with { LeaseOwner = null, LeaseToken = null, LeaseExpires = null, UpdatedAt = Now };
        }
        return Task.CompletedTask;
    }

    public Task<StepEntry?> TryGetStepAsync(string key, int seq, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var i = Get(key);
            return Task.FromResult(seq < i.Steps.Count ? i.Steps[seq] : null);
        }
    }

    public Task AppendStepAsync(string key, StepEntry entry, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var i = Get(key);
            if (entry.Seq != i.Steps.Count)
            {
                if (entry.Seq < i.Steps.Count) throw new DuplicateStepException(key, entry.Seq);
                throw new DurableException($"Instance '{key}': step #{entry.Seq} appended but the log has {i.Steps.Count} entries. Sequence numbers must be contiguous.");
            }
            i.Steps.Add(entry);
            i.Header = i.Header with { UpdatedAt = Now };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StepEntry>> ListStepsAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<StepEntry>>(_instances.TryGetValue(key, out var i) ? i.Steps.ToArray() : Array.Empty<StepEntry>());
    }

    public Task AppendSignalAsync(string key, string name, byte[] payload, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var i = Get(key);
            if (!i.Signals.TryGetValue(name, out var q)) i.Signals[name] = q = new Queue<byte[]>();
            q.Enqueue(payload);
        }
        _signalArrived.Release();
        return Task.CompletedTask;
    }

    public Task<byte[]?> TryTakeSignalAsync(string key, string name, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_instances.TryGetValue(key, out var i) && i.Signals.TryGetValue(name, out var q) && q.Count > 0)
                return Task.FromResult<byte[]?>(q.Dequeue());
            return Task.FromResult<byte[]?>(null);
        }
    }

    public async Task<byte[]> WaitForSignalAsync(string key, string name, CancellationToken ct = default)
    {
        while (true)
        {
            var p = await TryTakeSignalAsync(key, name, ct).ConfigureAwait(false);
            if (p is not null) return p;
            // wake on any signal append, or every 250ms as a fallback
            await _signalArrived.WaitAsync(250, ct).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<string> ListRunnableAsync(DateTimeOffset expiredBefore, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<string> keys;
        lock (_lock)
        {
            keys = _instances.Values
                .Where(i => i.Header.Status == WorkflowStatus.Running && (i.Header.LeaseExpires is null || i.Header.LeaseExpires < expiredBefore))
                .Select(i => i.Header.Key)
                .ToList();
        }
        foreach (var k in keys)
        {
            ct.ThrowIfCancellationRequested();
            yield return k;
        }
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<InstanceHeader>> ListInstancesAsync(InstanceQuery query, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<InstanceHeader> result = _instances.Values
                .Select(i => i.Header)
                .Where(query.Matches)
                .OrderByDescending(h => h.CreatedAt)
                .Skip(query.Offset)
                .Take(query.Limit)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        lock (_lock) _instances.Remove(key);
        return Task.CompletedTask;
    }

    private Instance Get(string key) =>
        _instances.TryGetValue(key, out var i) ? i : throw new InstanceNotFoundException(key);
}
