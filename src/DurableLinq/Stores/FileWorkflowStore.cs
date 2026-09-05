using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DurableLinq.Stores;

/// <summary>
/// One directory per instance key with <c>header.json</c>, <c>steps.jsonl</c> (append with write-through),
/// and a <c>signals/</c> inbox. Header rewrites use temp-file-and-rename. Single process only:
/// file locks are not reliable across hosts. Good for local development and small single-node services.
/// </summary>
public sealed class FileWorkflowStore : IWorkflowStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLine = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signalArrived = new(0, int.MaxValue);

    public FileWorkflowStore(string rootDirectory, TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("A root directory is required.", nameof(rootDirectory));
        _root = Path.GetFullPath(rootDirectory);
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(_root);
    }

    public string RootDirectory => _root;

    // ---------------------------------------------------------------- paths

    private static string Sanitize(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(segment.Length);
        foreach (var c in segment) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().TrimEnd('.', ' ');
        if (s.Length == 0 || s == "." || s == "..") s = "_";
        return s;
    }

    private string DirFor(string key)
    {
        var k = InstanceKey.Parse(key);
        return Path.Combine(_root, Sanitize(k.Namespace), Sanitize(k.WorkflowName), $"v{k.Version}", Sanitize(k.InstanceId));
    }

    private string HeaderPath(string key) => Path.Combine(DirFor(key), "header.json");
    private string StepsPath(string key) => Path.Combine(DirFor(key), "steps.jsonl");
    private string SignalDir(string key, string name) => Path.Combine(DirFor(key), "signals", Sanitize(name));

    private SemaphoreSlim LockFor(string key) => _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    private async Task<T> WithLock<T>(string key, CancellationToken ct, Func<Task<T>> action)
    {
        var l = LockFor(key);
        await l.WaitAsync(ct).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { l.Release(); }
    }

    private async Task WithLock(string key, CancellationToken ct, Func<Task> action)
    {
        var l = LockFor(key);
        await l.WaitAsync(ct).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { l.Release(); }
    }

    // ---------------------------------------------------------------- header

    private sealed record HeaderDto(
        string Key, string Namespace, string WorkflowName, int Version, string InstanceId, WorkflowStatus Status,
        byte[]? Input, byte[]? Result, byte[]? Error, string? LeaseOwner, Guid? LeaseToken, DateTimeOffset? LeaseExpires,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
    {
        public static HeaderDto From(InstanceHeader h) => new(h.Key, h.Namespace, h.WorkflowName, h.Version, h.InstanceId, h.Status,
            h.Input, h.Result, h.Error, h.LeaseOwner, h.LeaseToken, h.LeaseExpires, h.CreatedAt, h.UpdatedAt);

        public InstanceHeader ToHeader() => new()
        {
            Key = Key,
            Namespace = Namespace,
            WorkflowName = WorkflowName,
            Version = Version,
            InstanceId = InstanceId,
            Status = Status,
            Input = Input,
            Result = Result,
            Error = Error,
            LeaseOwner = LeaseOwner,
            LeaseToken = LeaseToken,
            LeaseExpires = LeaseExpires,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }

    private static InstanceHeader? ReadHeaderFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize<HeaderDto>(bytes, Json)?.ToHeader();
        }
        catch (IOException) { return null; }
    }

    private InstanceHeader? ReadHeader(string key)
    {
        var h = ReadHeaderFile(HeaderPath(key));
        return h is not null && h.Key == key ? h : null;
    }

    private static void WriteHeader(string path, InstanceHeader header)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(fs, HeaderDto.From(header), Json);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public Task<InstanceHeader?> GetHeaderAsync(string key, CancellationToken ct = default) =>
        WithLock(key, ct, () => Task.FromResult(ReadHeader(key)));

    public Task<bool> TryCreateAsync(InstanceHeader header, CancellationToken ct = default) =>
        WithLock(header.Key, ct, () =>
        {
            if (ReadHeader(header.Key) is not null) return Task.FromResult(false);
            WriteHeader(HeaderPath(header.Key), header);
            return Task.FromResult(true);
        });

    public Task UpdateStatusAsync(string key, WorkflowStatus status, byte[]? payload, CancellationToken ct = default) =>
        WithLock(key, ct, () =>
        {
            var h = ReadHeader(key) ?? throw new InstanceNotFoundException(key);
            WriteHeader(HeaderPath(key), h with
            {
                Status = status,
                Result = status == WorkflowStatus.Completed ? payload : h.Result,
                Error = status == WorkflowStatus.Failed ? payload : status == WorkflowStatus.Running ? null : h.Error,
                UpdatedAt = _time.GetUtcNow(),
            });
            if (status != WorkflowStatus.Running) _stepLogs.TryRemove(key, out _);
            return Task.CompletedTask;
        });

    // ---------------------------------------------------------------- leases

    public Task<Lease?> TryAcquireLeaseAsync(string key, string owner, TimeSpan ttl, CancellationToken ct = default) =>
        WithLock(key, ct, () =>
        {
            var h = ReadHeader(key) ?? throw new InstanceNotFoundException(key);
            var now = _time.GetUtcNow();
            if (h.IsLeasedAt(now) && h.LeaseOwner != owner && OwnerIsAlive(h.LeaseOwner!)) return Task.FromResult<Lease?>(null);
            var lease = new Lease(key, owner, Guid.NewGuid(), now + ttl);
            WriteHeader(HeaderPath(key), h with { LeaseOwner = owner, LeaseToken = lease.Token, LeaseExpires = lease.Expires, UpdatedAt = now });
            return Task.FromResult<Lease?>(lease);
        });

    /// <summary>
    /// The file store is single-host. When the lease owner has the default <c>machine:pid</c> shape and names
    /// this machine, a dead process means a dead lease, so a restart after a crash does not wait for the TTL.
    /// </summary>
    private static bool OwnerIsAlive(string owner)
    {
        var sep = owner.LastIndexOf(':');
        if (sep <= 0 || !int.TryParse(owner.AsSpan(sep + 1), out var pid)) return true;
        if (!string.Equals(owner[..sep], Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        if (pid == Environment.ProcessId) return true;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    public Task<bool> RenewLeaseAsync(Lease lease, TimeSpan ttl, CancellationToken ct = default) =>
        WithLock(lease.InstanceKey, ct, () =>
        {
            var h = ReadHeader(lease.InstanceKey);
            if (h is null || h.LeaseToken != lease.Token) return Task.FromResult(false);
            WriteHeader(HeaderPath(lease.InstanceKey), h with { LeaseExpires = _time.GetUtcNow() + ttl });
            return Task.FromResult(true);
        });

    public Task ReleaseLeaseAsync(Lease lease, CancellationToken ct = default) =>
        WithLock(lease.InstanceKey, ct, () =>
        {
            var h = ReadHeader(lease.InstanceKey);
            if (h is not null && h.LeaseToken == lease.Token)
                WriteHeader(HeaderPath(lease.InstanceKey), h with { LeaseOwner = null, LeaseToken = null, LeaseExpires = null, UpdatedAt = _time.GetUtcNow() });
            return Task.CompletedTask;
        });

    // ---------------------------------------------------------------- steps

    private sealed record StepDto(int Seq, string Name, byte[]? Payload, DateTimeOffset At, bool Failed = false);

    private sealed class StepLog
    {
        public readonly List<StepEntry> Entries = new();
        /// <summary>Byte length of the well-formed prefix of the file. Anything after it is a torn tail from a crash.</summary>
        public long ValidLength;
    }

    private readonly ConcurrentDictionary<string, StepLog> _stepLogs = new(StringComparer.Ordinal);

    private StepLog LoadSteps(string key)
    {
        return _stepLogs.GetOrAdd(key, k =>
        {
            var log = new StepLog();
            var path = StepsPath(k);
            if (!File.Exists(path)) return log;
            var bytes = File.ReadAllBytes(path);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var nl = Array.IndexOf(bytes, (byte)'\n', offset);
                if (nl < 0) break; // no terminating newline: torn tail
                var line = bytes.AsSpan(offset, nl - offset);
                if (line.Length > 0)
                {
                    StepDto? dto;
                    try { dto = JsonSerializer.Deserialize<StepDto>(line, JsonLine); }
                    catch (JsonException) { break; }
                    if (dto is null || dto.Seq != log.Entries.Count) break;
                    log.Entries.Add(new StepEntry(dto.Seq, dto.Name, dto.Payload, dto.At, dto.Failed));
                }
                offset = nl + 1;
                log.ValidLength = offset;
            }
            return log;
        });
    }

    public Task<StepEntry?> TryGetStepAsync(string key, int seq, CancellationToken ct = default) =>
        WithLock(key, ct, () =>
        {
            var steps = LoadSteps(key).Entries;
            return Task.FromResult(seq < steps.Count ? steps[seq] : null);
        });

    public Task AppendStepAsync(string key, StepEntry entry, CancellationToken ct = default) =>
        WithLock(key, ct, () =>
        {
            if (ReadHeader(key) is null) throw new InstanceNotFoundException(key);
            var log = LoadSteps(key);
            var steps = log.Entries;
            if (entry.Seq < steps.Count) throw new DuplicateStepException(key, entry.Seq);
            if (entry.Seq != steps.Count)
                throw new DurableException($"Instance '{key}': step #{entry.Seq} appended but the log has {steps.Count} entries. Sequence numbers must be contiguous.");

            var path = StepsPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var line = JsonSerializer.SerializeToUtf8Bytes(new StepDto(entry.Seq, entry.Name, entry.Payload, entry.RecordedAt, entry.Failed), JsonLine);
            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                if (fs.Length != log.ValidLength) fs.SetLength(log.ValidLength); // drop a torn tail from a crash
                fs.Seek(log.ValidLength, SeekOrigin.Begin);
                fs.Write(line);
                fs.WriteByte((byte)'\n');
                fs.Flush(true);
                log.ValidLength = fs.Position;
            }
            steps.Add(entry);
            return Task.CompletedTask;
        });

    public Task<IReadOnlyList<StepEntry>> ListStepsAsync(string key, CancellationToken ct = default) =>
        WithLock(key, ct, () =>
        {
            if (ReadHeader(key) is null) return Task.FromResult<IReadOnlyList<StepEntry>>(Array.Empty<StepEntry>());
            // read fresh from disk so the dashboard sees steps written by another process
            _stepLogs.TryRemove(key, out _);
            return Task.FromResult<IReadOnlyList<StepEntry>>(LoadSteps(key).Entries.ToArray());
        });

    // ---------------------------------------------------------------- signals

    public Task AppendSignalAsync(string key, string name, byte[] payload, CancellationToken ct = default) =>
        WithLock(key, ct, () =>
        {
            if (ReadHeader(key) is null) throw new InstanceNotFoundException(key);
            var dir = SignalDir(key, name);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{_time.GetUtcNow().UtcTicks:D20}-{Guid.NewGuid():N}.bin");
            var tmp = file + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(payload);
                fs.Flush(true);
            }
            File.Move(tmp, file);
            _signalArrived.Release();
            return Task.CompletedTask;
        });

    public Task<byte[]?> TryTakeSignalAsync(string key, string name, CancellationToken ct = default) =>
        WithLock(key, ct, () =>
        {
            var dir = SignalDir(key, name);
            if (!Directory.Exists(dir)) return Task.FromResult<byte[]?>(null);
            var first = Directory.EnumerateFiles(dir, "*.bin").Order(StringComparer.Ordinal).FirstOrDefault();
            if (first is null) return Task.FromResult<byte[]?>(null);
            var bytes = File.ReadAllBytes(first);
            File.Delete(first);
            return Task.FromResult<byte[]?>(bytes);
        });

    public async Task<byte[]> WaitForSignalAsync(string key, string name, CancellationToken ct = default)
    {
        while (true)
        {
            var p = await TryTakeSignalAsync(key, name, ct).ConfigureAwait(false);
            if (p is not null) return p;
            await _signalArrived.WaitAsync(500, ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- recovery and inspection

    private IEnumerable<InstanceHeader> EnumerateHeaders()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var path in Directory.EnumerateFiles(_root, "header.json", SearchOption.AllDirectories))
        {
            var h = ReadHeaderFile(path);
            if (h is not null) yield return h;
        }
    }

    public async IAsyncEnumerable<string> ListRunnableAsync(DateTimeOffset expiredBefore, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var h in EnumerateHeaders())
        {
            ct.ThrowIfCancellationRequested();
            if (h.Status == WorkflowStatus.Running && (h.LeaseExpires is null || h.LeaseExpires < expiredBefore))
                yield return h.Key;
        }
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<InstanceHeader>> ListInstancesAsync(InstanceQuery query, CancellationToken ct = default)
    {
        IReadOnlyList<InstanceHeader> result = EnumerateHeaders()
            .Where(query.Matches)
            .OrderByDescending(h => h.CreatedAt)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        WithLock(key, ct, () =>
        {
            _stepLogs.TryRemove(key, out _);
            var dir = DirFor(key);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return Task.CompletedTask;
        });
}
