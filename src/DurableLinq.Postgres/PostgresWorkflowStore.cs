using System.Reflection;
using System.Runtime.CompilerServices;
using Dapper;
using Npgsql;

namespace DurableLinq.Postgres;

/// <summary>Postgres-backed <see cref="IWorkflowStore"/> using Dapper and Npgsql.</summary>
public sealed class PostgresWorkflowStore : IWorkflowStore
{
    private const string PgUniqueViolation = "23505";

    private readonly string _connectionString;
    private readonly PostgresStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly string _schema;
    private readonly string _prefix;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaEnsured;

    private string Instances => $"{_schema}.{_prefix}workflow_instances";
    private string Steps => $"{_schema}.{_prefix}workflow_steps";
    private string Signals => $"{_schema}.{_prefix}workflow_signals";

    public PostgresWorkflowStore(string connectionString, PostgresStoreOptions? options = null, TimeProvider? time = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _options = options ?? new PostgresStoreOptions();
        _time = time ?? TimeProvider.System;
        _schema = _options.Schema;
        _prefix = _options.TablePrefix ?? string.Empty;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        if (_options.AutoCreateSchema) await EnsureSchemaAsync(ct).ConfigureAwait(false);
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>Creates the schema and tables if they do not exist. Safe to call repeatedly.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_schemaEnsured) return;
        await _schemaGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaEnsured) return;
            var sql = LoadSchemaSql();
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private string LoadSchemaSql()
    {
        var asm = typeof(PostgresWorkflowStore).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("schema.sql", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return text.Replace("{schema}", _schema).Replace("{prefix}", _prefix);
    }

    // ---------------------------------------------------------------- header

    public async Task<InstanceHeader?> GetHeaderAsync(string key, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<HeaderRow>(
            new CommandDefinition($"select * from {Instances} where instance_key = @key", new { key }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToHeader();
    }

    public async Task<bool> TryCreateAsync(InstanceHeader header, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = await conn.ExecuteAsync(new CommandDefinition($@"
insert into {Instances}
    (instance_key, namespace, workflow_name, version, instance_id, status, input, result, error,
     lease_owner, lease_token, lease_expires, created_at, updated_at)
values
    (@Key, @Namespace, @WorkflowName, @Version, @InstanceId, @Status, @Input, @Result, @Error,
     @LeaseOwner, @LeaseToken, @LeaseExpires, @CreatedAt, @UpdatedAt)
on conflict (instance_key) do nothing", HeaderRow.From(header), cancellationToken: ct)).ConfigureAwait(false);
            return rows == 1;
        }
        catch (PostgresException ex) when (ex.SqlState == PgUniqueViolation)
        {
            return false;
        }
    }

    public async Task UpdateStatusAsync(string key, WorkflowStatus status, byte[]? payload, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var now = Now;
        await conn.ExecuteAsync(new CommandDefinition($@"
update {Instances} set
    status = @status,
    result = case when @status = {(int)WorkflowStatus.Completed} then @payload else result end,
    error = case when @status = {(int)WorkflowStatus.Failed} then @payload
                 when @status = {(int)WorkflowStatus.Running} then null
                 else error end,
    updated_at = @now
where instance_key = @key",
            new { key, status = (int)status, payload, now }, cancellationToken: ct)).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- leases

    public async Task<Lease?> TryAcquireLeaseAsync(string key, string owner, TimeSpan ttl, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var now = Now;
        var expires = now + ttl;
        var token = Guid.NewGuid();
        var rows = await conn.ExecuteAsync(new CommandDefinition($@"
update {Instances} set lease_owner = @owner, lease_token = @token, lease_expires = @expires, updated_at = @now
where instance_key = @key and (lease_owner is null or lease_expires < @now or lease_owner = @owner)",
            new { key, owner, token, expires, now }, cancellationToken: ct)).ConfigureAwait(false);
        return rows == 1 ? new Lease(key, owner, token, expires) : null;
    }

    public async Task<bool> RenewLeaseAsync(Lease lease, TimeSpan ttl, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var expires = Now + ttl;
        var rows = await conn.ExecuteAsync(new CommandDefinition($@"
update {Instances} set lease_expires = @expires where instance_key = @key and lease_token = @token",
            new { key = lease.InstanceKey, token = lease.Token, expires }, cancellationToken: ct)).ConfigureAwait(false);
        return rows == 1;
    }

    public async Task ReleaseLeaseAsync(Lease lease, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var now = Now;
        await conn.ExecuteAsync(new CommandDefinition($@"
update {Instances} set lease_owner = null, lease_token = null, lease_expires = null, updated_at = @now
where instance_key = @key and lease_token = @token",
            new { key = lease.InstanceKey, token = lease.Token, now }, cancellationToken: ct)).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- steps

    public async Task<StepEntry?> TryGetStepAsync(string key, int seq, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<StepRow>(new CommandDefinition(
            $"select * from {Steps} where instance_key = @key and seq = @seq", new { key, seq }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToEntry();
    }

    public async Task AppendStepAsync(string key, StepEntry entry, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from {Steps} where instance_key = @key", new { key }, cancellationToken: ct)).ConfigureAwait(false);
        if (entry.Seq > count)
            throw new DurableException($"Instance '{key}': step #{entry.Seq} appended but the log has {count} entries. Sequence numbers must be contiguous.");
        try
        {
            await conn.ExecuteAsync(new CommandDefinition($@"
insert into {Steps} (instance_key, seq, name, payload, recorded_at, failed)
values (@key, @Seq, @Name, @Payload, @RecordedAt, @Failed)",
                new { key, entry.Seq, entry.Name, entry.Payload, entry.RecordedAt, entry.Failed }, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PgUniqueViolation)
        {
            throw new DuplicateStepException(key, entry.Seq);
        }
        await conn.ExecuteAsync(new CommandDefinition(
            $"update {Instances} set updated_at = @now where instance_key = @key", new { key, now = Now }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StepEntry>> ListStepsAsync(string key, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<StepRow>(new CommandDefinition(
            $"select * from {Steps} where instance_key = @key order by seq", new { key }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToEntry()).ToList();
    }

    // ---------------------------------------------------------------- signals

    public async Task AppendSignalAsync(string key, string name, byte[] payload, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition($@"
insert into {Signals} (instance_key, name, payload, received_at) values (@key, @name, @payload, @now)",
            new { key, name, payload, now = Now }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<byte[]?> TryTakeSignalAsync(string key, string name, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<byte[]?>(new CommandDefinition($@"
delete from {Signals}
where id = (
    select id from {Signals}
    where instance_key = @key and name = @name
    order by id
    limit 1
    for update skip locked
)
returning payload", new { key, name }, cancellationToken: ct)).ConfigureAwait(false);
    }

    // WaitForSignalAsync uses the default polling implementation from IWorkflowStore.

    // ---------------------------------------------------------------- recovery and inspection

    public async IAsyncEnumerable<string> ListRunnableAsync(DateTimeOffset expiredBefore, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<string>(new CommandDefinition($@"
select instance_key from {Instances}
where status = {(int)WorkflowStatus.Running} and (lease_expires is null or lease_expires < @before)",
            new { before = expiredBefore }, cancellationToken: ct)).ConfigureAwait(false);
        foreach (var key in rows)
        {
            ct.ThrowIfCancellationRequested();
            yield return key;
        }
    }

    public async Task<IReadOnlyList<InstanceHeader>> ListInstancesAsync(InstanceQuery query, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var where = new List<string>();
        var p = new DynamicParameters();
        if (query.Namespace is not null) { where.Add("namespace = @ns"); p.Add("ns", query.Namespace); }
        if (query.WorkflowName is not null) { where.Add("workflow_name = @wn"); p.Add("wn", query.WorkflowName); }
        if (query.Version is not null) { where.Add("version = @ver"); p.Add("ver", query.Version); }
        if (query.Status is not null) { where.Add("status = @st"); p.Add("st", (int)query.Status.Value); }
        if (query.InstanceIdContains is not null) { where.Add("instance_id ilike @ic"); p.Add("ic", $"%{query.InstanceIdContains}%"); }
        var whereSql = where.Count > 0 ? "where " + string.Join(" and ", where) : "";
        p.Add("limit", query.Limit);
        p.Add("offset", query.Offset);
        var rows = await conn.QueryAsync<HeaderRow>(new CommandDefinition(
            $"select * from {Instances} {whereSql} order by created_at desc offset @offset limit @limit", p, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToHeader()).ToList();
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct).ConfigureAwait(false);
        using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition($"delete from {Steps} where instance_key = @key", new { key }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition($"delete from {Signals} where instance_key = @key", new { key }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition($"delete from {Instances} where instance_key = @key", new { key }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- row DTOs

    private sealed class HeaderRow
    {
        public string instance_key { get; set; } = "";
        public string @namespace { get; set; } = "";
        public string workflow_name { get; set; } = "";
        public int version { get; set; }
        public string instance_id { get; set; } = "";
        public int status { get; set; }
        public byte[]? input { get; set; }
        public byte[]? result { get; set; }
        public byte[]? error { get; set; }
        public string? lease_owner { get; set; }
        public Guid? lease_token { get; set; }
        public DateTimeOffset? lease_expires { get; set; }
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset updated_at { get; set; }

        public InstanceHeader ToHeader() => new()
        {
            Key = instance_key,
            Namespace = @namespace,
            WorkflowName = workflow_name,
            Version = version,
            InstanceId = instance_id,
            Status = (WorkflowStatus)status,
            Input = input,
            Result = result,
            Error = error,
            LeaseOwner = lease_owner,
            LeaseToken = lease_token,
            LeaseExpires = lease_expires,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
        };

        public static object From(InstanceHeader h) => new
        {
            Key = h.Key,
            h.Namespace,
            h.WorkflowName,
            h.Version,
            h.InstanceId,
            Status = (int)h.Status,
            h.Input,
            h.Result,
            h.Error,
            h.LeaseOwner,
            h.LeaseToken,
            h.LeaseExpires,
            h.CreatedAt,
            h.UpdatedAt,
        };
    }

    private sealed class StepRow
    {
        public string instance_key { get; set; } = "";
        public int seq { get; set; }
        public string name { get; set; } = "";
        public byte[]? payload { get; set; }
        public DateTimeOffset recorded_at { get; set; }
        public bool failed { get; set; }

        public StepEntry ToEntry() => new(seq, name, payload, recorded_at, failed);
    }
}
