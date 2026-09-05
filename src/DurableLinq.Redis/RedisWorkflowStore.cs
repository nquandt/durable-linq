using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace DurableLinq.Redis;

/// <summary>
/// Redis-backed <see cref="IWorkflowStore"/>. Key layout (all prefixed with <see cref="RedisStoreOptions.KeyPrefix"/>):
/// <list type="bullet">
/// <item><c>{prefix}:h:{key}</c> — hash holding the full <see cref="InstanceHeader"/>.</item>
/// <item><c>{prefix}:steps:{key}</c> — list of JSON step entries in sequence order.</item>
/// <item><c>{prefix}:sig:{key}:{name}</c> — list of pending signal payloads (base64) for one signal name.</item>
/// <item><c>{prefix}:signames:{key}</c> — set of signal names ever used for an instance, so <see cref="DeleteAsync"/> can clean them up.</item>
/// <item><c>{prefix}:instances</c> — sorted set of every instance key, scored by <c>CreatedAt</c> ticks.</item>
/// <item><c>{prefix}:running</c> — sorted set of instance keys with <see cref="WorkflowStatus.Running"/>, scored by lease-expiry
/// ticks (or 0 when there is no lease), used by <see cref="ListRunnableAsync"/>.</item>
/// </list>
/// Step append is atomic via a Lua script that checks <c>LLEN == seq</c>. Lease acquire/renew/release are atomic
/// compare-and-set Lua scripts keyed off a lease token stored in the header hash.
/// </summary>
public sealed class RedisWorkflowStore : IWorkflowStore, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _mux;
    private readonly bool _ownsMux;
    private readonly RedisStoreOptions _options;
    private readonly string _prefix;

    public RedisWorkflowStore(string connectionString, RedisStoreOptions? options = null)
        : this(ConnectionMultiplexer.Connect(connectionString), options, ownsMux: true)
    {
    }

    public RedisWorkflowStore(IConnectionMultiplexer multiplexer, RedisStoreOptions? options = null)
        : this(multiplexer, options, ownsMux: false)
    {
    }

    private RedisWorkflowStore(IConnectionMultiplexer multiplexer, RedisStoreOptions? options, bool ownsMux)
    {
        _mux = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _options = options ?? new RedisStoreOptions();
        _prefix = string.IsNullOrWhiteSpace(_options.KeyPrefix) ? "durable" : _options.KeyPrefix;
        _ownsMux = ownsMux;
    }

    private IDatabase Db => _mux.GetDatabase(_options.Database);

    // ---- key helpers ----

    private string HeaderKey(string key) => $"{_prefix}:h:{key}";
    private string StepsKey(string key) => $"{_prefix}:steps:{key}";
    private string SignalKey(string key, string name) => $"{_prefix}:sig:{key}:{name}";
    private string SignalNamesKey(string key) => $"{_prefix}:signames:{key}";
    private string InstancesKey => $"{_prefix}:instances";
    private string RunningKey => $"{_prefix}:running";

    // ---- header ----

    public async Task<InstanceHeader?> GetHeaderAsync(string key, CancellationToken ct = default)
    {
        var entries = await Db.HashGetAllAsync(HeaderKey(key)).ConfigureAwait(false);
        return entries.Length == 0 ? null : FromHash(key, entries);
    }

    public async Task<bool> TryCreateAsync(InstanceHeader header, CancellationToken ct = default)
    {
        var hk = HeaderKey(header.Key);
        var fields = ToHash(header);

        const string script = """
            if redis.call('EXISTS', KEYS[1]) == 1 then
                return 0
            end
            redis.call('HSET', KEYS[1], unpack(ARGV))
            return 1
            """;

        var argv = new RedisValue[fields.Length * 2];
        for (var i = 0; i < fields.Length; i++)
        {
            argv[i * 2] = fields[i].Name;
            argv[i * 2 + 1] = fields[i].Value;
        }

        var created = (int)await Db.ScriptEvaluateAsync(script, new RedisKey[] { hk }, argv).ConfigureAwait(false) == 1;
        if (!created) return false;

        var tran = Db.CreateTransaction();
        _ = tran.SortedSetAddAsync(InstancesKey, header.Key, header.CreatedAt.UtcTicks);
        if (header.Status == WorkflowStatus.Running)
            _ = tran.SortedSetAddAsync(RunningKey, header.Key, header.LeaseExpires?.UtcTicks ?? 0);
        await tran.ExecuteAsync().ConfigureAwait(false);
        return true;
    }

    public async Task UpdateStatusAsync(string key, WorkflowStatus status, byte[]? payload, CancellationToken ct = default)
    {
        var hk = HeaderKey(key);
        var now = DateTimeOffset.UtcNow;

        const string script = """
            if redis.call('EXISTS', KEYS[1]) == 0 then return redis.error_reply('NOTFOUND') end
            redis.call('HSET', KEYS[1], 'status', ARGV[1], 'updatedAt', ARGV[4])
            if ARGV[1] == '1' then
                redis.call('HSET', KEYS[1], 'result', ARGV[2])
            elseif ARGV[1] == '2' then
                redis.call('HSET', KEYS[1], 'error', ARGV[2])
            elseif ARGV[1] == '0' then
                redis.call('HDEL', KEYS[1], 'error')
            end
            if ARGV[1] == '0' then
                redis.call('ZADD', KEYS[2], ARGV[3], KEYS[3])
            else
                redis.call('ZREM', KEYS[2], KEYS[3])
            end
            return 1
            """;

        try
        {
            await Db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { hk, RunningKey, key },
                new RedisValue[] { (int)status, payload is null ? "" : Convert.ToBase64String(payload), now.UtcTicks, now.UtcTicks })
                .ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("NOTFOUND", StringComparison.Ordinal))
        {
            throw new InstanceNotFoundException(key);
        }
    }

    // ---- leases ----

    public async Task<Lease?> TryAcquireLeaseAsync(string key, string owner, TimeSpan ttl, CancellationToken ct = default)
    {
        var hk = HeaderKey(key);
        var now = DateTimeOffset.UtcNow;
        var expires = now + ttl;
        var token = Guid.NewGuid();

        const string script = """
            if redis.call('EXISTS', KEYS[1]) == 0 then return redis.error_reply('NOTFOUND') end
            local currentOwner = redis.call('HGET', KEYS[1], 'leaseOwner')
            local currentExpires = redis.call('HGET', KEYS[1], 'leaseExpires')
            local now = tonumber(ARGV[4])
            if currentOwner and currentOwner ~= '' and currentOwner ~= ARGV[1]
               and currentExpires and currentExpires ~= '' and tonumber(currentExpires) > now then
                return 0
            end
            redis.call('HSET', KEYS[1], 'leaseOwner', ARGV[1], 'leaseToken', ARGV[2], 'leaseExpires', ARGV[3], 'updatedAt', ARGV[4])
            redis.call('ZADD', KEYS[2], ARGV[3], KEYS[3])
            return 1
            """;

        RedisResult result;
        try
        {
            result = await Db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { hk, RunningKey, key },
                new RedisValue[] { owner, token.ToString("N"), expires.UtcTicks, now.UtcTicks })
                .ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("NOTFOUND", StringComparison.Ordinal))
        {
            throw new InstanceNotFoundException(key);
        }

        return (int)result == 1 ? new Lease(key, owner, token, expires) : null;
    }

    public async Task<bool> RenewLeaseAsync(Lease lease, TimeSpan ttl, CancellationToken ct = default)
    {
        var hk = HeaderKey(lease.InstanceKey);
        var expires = DateTimeOffset.UtcNow + ttl;

        const string script = """
            if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
            local token = redis.call('HGET', KEYS[1], 'leaseToken')
            if token ~= ARGV[1] then return 0 end
            redis.call('HSET', KEYS[1], 'leaseExpires', ARGV[2])
            redis.call('ZADD', KEYS[2], ARGV[2], KEYS[3])
            return 1
            """;

        var result = await Db.ScriptEvaluateAsync(
            script,
            new RedisKey[] { hk, RunningKey, lease.InstanceKey },
            new RedisValue[] { lease.Token.ToString("N"), expires.UtcTicks })
            .ConfigureAwait(false);

        return (int)result == 1;
    }

    public async Task ReleaseLeaseAsync(Lease lease, CancellationToken ct = default)
    {
        var hk = HeaderKey(lease.InstanceKey);

        const string script = """
            if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
            local token = redis.call('HGET', KEYS[1], 'leaseToken')
            if token ~= ARGV[1] then return 0 end
            redis.call('HDEL', KEYS[1], 'leaseOwner', 'leaseToken', 'leaseExpires')
            local status = redis.call('HGET', KEYS[1], 'status')
            if status == '0' then
                redis.call('ZADD', KEYS[2], 0, KEYS[3])
            end
            return 1
            """;

        await Db.ScriptEvaluateAsync(
            script,
            new RedisKey[] { hk, RunningKey, lease.InstanceKey },
            new RedisValue[] { lease.Token.ToString("N") })
            .ConfigureAwait(false);
    }

    // ---- step log ----

    public async Task<StepEntry?> TryGetStepAsync(string key, int seq, CancellationToken ct = default)
    {
        var v = await Db.ListGetByIndexAsync(StepsKey(key), seq).ConfigureAwait(false);
        return v.IsNullOrEmpty ? null : DeserializeStep(v!);
    }

    public async Task AppendStepAsync(string key, StepEntry entry, CancellationToken ct = default)
    {
        var sk = StepsKey(key);
        var json = SerializeStep(entry);

        const string script = """
            local len = redis.call('LLEN', KEYS[1])
            local seq = tonumber(ARGV[1])
            if seq < len then return redis.error_reply('DUP') end
            if seq > len then return redis.error_reply('GAP') end
            redis.call('RPUSH', KEYS[1], ARGV[2])
            return len
            """;

        try
        {
            await Db.ScriptEvaluateAsync(script, new RedisKey[] { sk }, new RedisValue[] { entry.Seq, json }).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("DUP", StringComparison.Ordinal))
        {
            throw new DuplicateStepException(key, entry.Seq);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("GAP", StringComparison.Ordinal))
        {
            var len = await Db.ListLengthAsync(sk).ConfigureAwait(false);
            throw new DurableException($"Instance '{key}': step #{entry.Seq} appended but the log has {len} entries. Sequence numbers must be contiguous.");
        }

        await Db.HashSetAsync(HeaderKey(key), "updatedAt", DateTimeOffset.UtcNow.UtcTicks).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StepEntry>> ListStepsAsync(string key, CancellationToken ct = default)
    {
        var values = await Db.ListRangeAsync(StepsKey(key)).ConfigureAwait(false);
        return values.Select(v => DeserializeStep(v!)).ToArray();
    }

    // ---- signals ----

    public async Task AppendSignalAsync(string key, string name, byte[] payload, CancellationToken ct = default)
    {
        var tran = Db.CreateTransaction();
        _ = tran.ListRightPushAsync(SignalKey(key, name), Encode(payload));
        _ = tran.SetAddAsync(SignalNamesKey(key), name);
        await tran.ExecuteAsync().ConfigureAwait(false);
    }

    public async Task<byte[]?> TryTakeSignalAsync(string key, string name, CancellationToken ct = default)
    {
        var v = await Db.ListLeftPopAsync(SignalKey(key, name)).ConfigureAwait(false);
        return v.IsNullOrEmpty ? null : Decode(v!);
    }

    /// <summary>Polls <see cref="TryTakeSignalAsync"/> with a short, capped backoff. Redis has no server-side BLPOP
    /// exposed through StackExchange.Redis that composes with the multi-tenant key layout here, so a poll loop is used.</summary>
    public async Task<byte[]> WaitForSignalAsync(string key, string name, CancellationToken ct = default)
    {
        var delay = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            var payload = await TryTakeSignalAsync(key, name, ct).ConfigureAwait(false);
            if (payload is not null) return payload;
            await Task.Delay(delay, ct).ConfigureAwait(false);
            if (delay < TimeSpan.FromMilliseconds(500)) delay += TimeSpan.FromMilliseconds(100);
        }
    }

    // ---- recovery and inspection ----

    public async IAsyncEnumerable<string> ListRunnableAsync(DateTimeOffset expiredBefore, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var members = await Db.SortedSetRangeByScoreAsync(RunningKey, double.NegativeInfinity, expiredBefore.UtcTicks).ConfigureAwait(false);
        foreach (var m in members)
        {
            ct.ThrowIfCancellationRequested();
            yield return m!;
        }
    }

    public async Task<IReadOnlyList<InstanceHeader>> ListInstancesAsync(InstanceQuery query, CancellationToken ct = default)
    {
        var keys = await Db.SortedSetRangeByScoreAsync(InstancesKey, order: Order.Descending).ConfigureAwait(false);
        var result = new List<InstanceHeader>();
        foreach (var k in keys)
        {
            var header = await GetHeaderAsync(k!, ct).ConfigureAwait(false);
            if (header is not null && query.Matches(header)) result.Add(header);
        }
        return result.Skip(query.Offset).Take(query.Limit).ToArray();
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var names = await Db.SetMembersAsync(SignalNamesKey(key)).ConfigureAwait(false);
        var batch = Db.CreateBatch();
        var tasks = new List<Task>
        {
            batch.KeyDeleteAsync(HeaderKey(key)),
            batch.KeyDeleteAsync(StepsKey(key)),
            batch.KeyDeleteAsync(SignalNamesKey(key)),
            batch.SortedSetRemoveAsync(InstancesKey, key),
            batch.SortedSetRemoveAsync(RunningKey, key),
        };
        foreach (var n in names)
            tasks.Add(batch.KeyDeleteAsync(SignalKey(key, n!)));
        batch.Execute();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsMux) await _mux.CloseAsync().ConfigureAwait(false);
    }

    // ---- header hash (de)serialization ----

    private static (string Name, RedisValue Value)[] ToHash(InstanceHeader h) =>
    [
        ("ns", h.Namespace),
        ("wf", h.WorkflowName),
        ("ver", h.Version),
        ("iid", h.InstanceId),
        ("status", (int)h.Status),
        ("input", h.Input is null ? "" : Convert.ToBase64String(h.Input)),
        ("result", h.Result is null ? "" : Convert.ToBase64String(h.Result)),
        ("error", h.Error is null ? "" : Convert.ToBase64String(h.Error)),
        ("leaseOwner", h.LeaseOwner ?? ""),
        ("leaseToken", h.LeaseToken?.ToString("N") ?? ""),
        ("leaseExpires", h.LeaseExpires.HasValue ? h.LeaseExpires.Value.UtcTicks.ToString() : ""),
        ("createdAt", h.CreatedAt.UtcTicks),
        ("updatedAt", h.UpdatedAt.UtcTicks),
    ];

    private InstanceHeader FromHash(string key, HashEntry[] entries)
    {
        var map = entries.ToDictionary(e => e.Name.ToString(), e => e.Value, StringComparer.Ordinal);

        string Str(string field) => map.TryGetValue(field, out var v) && !v.IsNullOrEmpty ? v.ToString() : "";
        string? StrOrNull(string field) => map.TryGetValue(field, out var v) && !v.IsNullOrEmpty ? v.ToString() : null;
        byte[]? Bytes(string field) => map.TryGetValue(field, out var v) ? Decode(v) : null;
        long? Ticks(string field) => map.TryGetValue(field, out var v) && !v.IsNullOrEmpty ? (long)v : null;

        return new InstanceHeader
        {
            Key = key,
            Namespace = Str("ns"),
            WorkflowName = Str("wf"),
            Version = (int)Ticks("ver")!,
            InstanceId = Str("iid"),
            Status = (WorkflowStatus)(int)Ticks("status")!,
            Input = Bytes("input"),
            Result = Bytes("result"),
            Error = Bytes("error"),
            LeaseOwner = StrOrNull("leaseOwner"),
            LeaseToken = StrOrNull("leaseToken") is { } tok ? Guid.ParseExact(tok, "N") : null,
            LeaseExpires = Ticks("leaseExpires") is { } le ? new DateTimeOffset(le, TimeSpan.Zero) : null,
            CreatedAt = new DateTimeOffset(Ticks("createdAt")!.Value, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(Ticks("updatedAt")!.Value, TimeSpan.Zero),
        };
    }

    private static RedisValue Encode(byte[]? bytes) => bytes is null ? RedisValue.Null : Convert.ToBase64String(bytes);
    private static byte[]? Decode(RedisValue v) => v.IsNullOrEmpty ? null : Convert.FromBase64String(v!);

    private sealed record StepDto(int Seq, string Name, string? Payload, long RecordedAt, bool Failed);

    private static readonly JsonSerializerOptions StepJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string SerializeStep(StepEntry entry) =>
        JsonSerializer.Serialize(new StepDto(entry.Seq, entry.Name, entry.Payload is null ? null : Convert.ToBase64String(entry.Payload), entry.RecordedAt.UtcTicks, entry.Failed), StepJsonOptions);

    private static StepEntry DeserializeStep(string json)
    {
        var dto = JsonSerializer.Deserialize<StepDto>(json, StepJsonOptions)!;
        return new StepEntry(dto.Seq, dto.Name, dto.Payload is null ? null : Convert.FromBase64String(dto.Payload), new DateTimeOffset(dto.RecordedAt, TimeSpan.Zero), dto.Failed);
    }
}
