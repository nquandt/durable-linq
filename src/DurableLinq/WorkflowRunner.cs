using System.Diagnostics;
using System.Reflection;

namespace DurableLinq;

public sealed class WorkflowRunnerOptions
{
    /// <summary>Prefix on every instance key. Defaults to the entry assembly name.</summary>
    public string Namespace { get; set; } = DefaultNamespace();

    /// <summary>How long a lease lasts without renewal. The runner renews at half this interval.</summary>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Identifies this host in leases. Defaults to machine name plus process id.</summary>
    public string Owner { get; set; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    public IWorkflowSerializer Serializer { get; set; } = JsonWorkflowSerializer.Instance;
    public DurableEventSource Events { get; set; } = DurableEventSource.Default;
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Maps exceptions captured at step boundaries to errors. See <see cref="IFailureClassifier"/>.</summary>
    public IFailureClassifier Classifier { get; set; } = DefaultFailureClassifier.Instance;

    public static string DefaultNamespace() =>
        Assembly.GetEntryAssembly()?.GetName().Name ?? "default";
}

/// <summary>
/// Runs workflow definitions against a store. Handles validation, instance creation, short-circuit of
/// finished instances, leases with background renewal, and persistence of the outcome.
/// Business outcomes come back as <see cref="RunResult{TOut}"/>; only engine invariants throw.
/// </summary>
public sealed class WorkflowRunner : IWorkflowRunner
{
    private readonly IWorkflowStore _store;
    private readonly WorkflowRunnerOptions _options;
    private readonly WorkflowServices _services;

    public WorkflowRunner(IWorkflowStore store, WorkflowRunnerOptions? options = null, IServiceProvider? services = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new WorkflowRunnerOptions();
        _services = new WorkflowServices(services);
    }

    public string Namespace => _options.Namespace;
    public IWorkflowStore Store => _store;
    public WorkflowRunnerOptions Options => _options;

    public string KeyFor(WorkflowDefinition definition, string instanceId) =>
        InstanceKey.Format(_options.Namespace, definition.Name, definition.Version, instanceId);

    public Task<RunResult<TOut>> RunAsync<TIn, TOut>(WorkflowDefinition<TIn, TOut> definition, string instanceId, TIn input, CancellationToken ct = default) =>
        RunCoreAsync(definition, KeyFor(definition, instanceId), input, hasInput: true, ct);

    public Task<RunResult<TOut>> ResumeAsync<TIn, TOut>(WorkflowDefinition<TIn, TOut> definition, string instanceKey, CancellationToken ct = default) =>
        RunCoreAsync(definition, instanceKey, default!, hasInput: false, ct);

    private async Task<RunResult<TOut>> RunCoreAsync<TIn, TOut>(WorkflowDefinition<TIn, TOut> definition, string key, TIn input, bool hasInput, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var parsed = InstanceKey.Parse(key);
        if (parsed.WorkflowName != definition.Name || parsed.Version != definition.Version)
            throw new ArgumentException($"Key '{key}' does not belong to definition '{definition}'.", nameof(key));

        var now = _options.Time.GetUtcNow();

        // 1. create or load
        var header = await _store.GetHeaderAsync(key, ct).ConfigureAwait(false);
        var created = false;
        if (header is null)
        {
            if (!hasInput) throw new InstanceNotFoundException(key);
            var fresh = new InstanceHeader
            {
                Key = key,
                Namespace = parsed.Namespace,
                WorkflowName = parsed.WorkflowName,
                Version = parsed.Version,
                InstanceId = parsed.InstanceId,
                Status = WorkflowStatus.Running,
                Input = _options.Serializer.Serialize(input),
                CreatedAt = now,
                UpdatedAt = now,
            };
            created = await _store.TryCreateAsync(fresh, ct).ConfigureAwait(false);
            header = created ? fresh : await _store.GetHeaderAsync(key, ct).ConfigureAwait(false)
                                       ?? throw new InstanceNotFoundException(key);
        }

        // 2. short-circuit finished instances
        switch (header.Status)
        {
            case WorkflowStatus.Completed:
                return RunResult<TOut>.Completed(key, DeserializeResult<TOut>(header.Result));
            case WorkflowStatus.Failed:
                return RunResult<TOut>.Failed(key, DeserializeError(header.Error));
        }

        // 3. lease
        var lease = await _store.TryAcquireLeaseAsync(key, _options.Owner, _options.LeaseTtl, ct).ConfigureAwait(false);
        if (lease is null) return RunResult<TOut>.Busy(key, header.LeaseOwner, header.LeaseExpires);

        _options.Events.Raise(created ? DurableEventKind.InstanceStarted : DurableEventKind.InstanceResumed, key);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = RenewLoopAsync(lease, runCts);
        var sw = Stopwatch.StartNew();
        var ctx = new WorkflowContext(key, _store, _options.Serializer, _options.Events, _options.Time, runCts.Token, _options.Classifier);

        try
        {
            // 4. build and run
            var storedInput = header.Input is null ? default! : _options.Serializer.Deserialize<TIn>(header.Input);
            Outcome<TOut> outcome;
            try
            {
                var workflow = definition.Handle(storedInput, _services);
                outcome = await workflow.RunAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (ctx.IsCapturable(ex))
            {
                outcome = ctx.Capture<TOut>(ex); // thrown from Handle itself, outside any step
            }

            // 5. persist the outcome
            if (outcome.IsSuccess)
            {
                await _store.UpdateStatusAsync(key, WorkflowStatus.Completed, _options.Serializer.Serialize(outcome.Value), CancellationToken.None).ConfigureAwait(false);
                _options.Events.Raise(DurableEventKind.InstanceCompleted, key, duration: sw.Elapsed,
                    detail: $"executed={ctx.ExecutedCount} replayed={ctx.ReplayedCount}");
                return RunResult<TOut>.Completed(key, outcome.Value);
            }

            var error = outcome.Error! with { FailedAt = _options.Time.GetUtcNow() };
            await _store.UpdateStatusAsync(key, WorkflowStatus.Failed, _options.Serializer.Serialize(error), CancellationToken.None).ConfigureAwait(false);
            _options.Events.Raise(DurableEventKind.InstanceFailed, key, error.StepName, error.Seq, sw.Elapsed, detail: error.ToString());
            return RunResult<TOut>.Failed(key, error);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // renewal failed: someone else owns the instance now
            _options.Events.Raise(DurableEventKind.LeaseLost, key, duration: sw.Elapsed);
            throw new LeaseLostException(key);
        }
        finally
        {
            runCts.Cancel();
            try { await renewal.ConfigureAwait(false); } catch { /* renewal loop ends by cancellation */ }
            try { await _store.ReleaseLeaseAsync(lease, CancellationToken.None).ConfigureAwait(false); } catch { /* best effort */ }
        }
    }

    private async Task RenewLoopAsync(Lease lease, CancellationTokenSource runCts)
    {
        var interval = TimeSpan.FromTicks(Math.Max(_options.LeaseTtl.Ticks / 2, TimeSpan.FromMilliseconds(50).Ticks));
        try
        {
            while (!runCts.IsCancellationRequested)
            {
                await Task.Delay(interval, _options.Time, runCts.Token).ConfigureAwait(false);
                var ok = await _store.RenewLeaseAsync(lease, _options.LeaseTtl, runCts.Token).ConfigureAwait(false);
                if (!ok)
                {
                    runCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task SignalAsync<T>(string instanceKey, string signalName, T payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signalName)) throw new ArgumentException("Signal name is required.", nameof(signalName));
        _ = await _store.GetHeaderAsync(instanceKey, ct).ConfigureAwait(false) ?? throw new InstanceNotFoundException(instanceKey);
        await _store.AppendSignalAsync(instanceKey, signalName, _options.Serializer.Serialize(payload), ct).ConfigureAwait(false);
        _options.Events.Raise(DurableEventKind.SignalSent, instanceKey, detail: signalName);
    }

    public Task<InstanceHeader?> GetHeaderAsync(string instanceKey, CancellationToken ct = default) =>
        _store.GetHeaderAsync(instanceKey, ct);

    public async Task<WorkflowStatus?> GetStatusAsync(string instanceKey, CancellationToken ct = default) =>
        (await _store.GetHeaderAsync(instanceKey, ct).ConfigureAwait(false))?.Status;

    public async Task<TOut?> GetResultAsync<TOut>(string instanceKey, CancellationToken ct = default)
    {
        var header = await _store.GetHeaderAsync(instanceKey, ct).ConfigureAwait(false) ?? throw new InstanceNotFoundException(instanceKey);
        return header.Status == WorkflowStatus.Completed ? DeserializeResult<TOut>(header.Result) : default;
    }

    public async Task<WorkflowError?> GetErrorAsync(string instanceKey, CancellationToken ct = default)
    {
        var header = await _store.GetHeaderAsync(instanceKey, ct).ConfigureAwait(false) ?? throw new InstanceNotFoundException(instanceKey);
        return header.Status == WorkflowStatus.Failed ? DeserializeError(header.Error) : null;
    }

    public async Task<bool> RetryFailedAsync(string instanceKey, CancellationToken ct = default)
    {
        var header = await _store.GetHeaderAsync(instanceKey, ct).ConfigureAwait(false) ?? throw new InstanceNotFoundException(instanceKey);
        if (header.Status != WorkflowStatus.Failed) return false;
        if (DeserializeError(header.Error).IsTerminal) return false;
        await _store.UpdateStatusAsync(instanceKey, WorkflowStatus.Running, null, ct).ConfigureAwait(false);
        return true;
    }

    private TOut DeserializeResult<TOut>(byte[]? bytes) =>
        bytes is null ? default! : _options.Serializer.Deserialize<TOut>(bytes);

    private WorkflowError DeserializeError(byte[]? bytes) =>
        bytes is null
            ? WorkflowError.Create("unknown", "The instance failed but no error was stored.")
            : _options.Serializer.Deserialize<WorkflowError>(bytes);
}
