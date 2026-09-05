using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DurableLinq.Hosting;

public sealed class RecoverySweeperOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Delay before the first sweep, so the host finishes starting first.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Maximum number of instances resumed concurrently by one sweep.</summary>
    public int MaxConcurrency { get; set; } = 4;
}

/// <summary>
/// Periodically finds instances that are Running with an expired lease (a host died mid-run)
/// and resumes them with the stored input. Instances whose definition is not registered are
/// logged as orphaned and left alone.
/// </summary>
public sealed class RecoverySweeper : BackgroundService
{
    private readonly IWorkflowStore _store;
    private readonly IWorkflowRunner _runner;
    private readonly IWorkflowDefinitionRegistry _registry;
    private readonly RecoverySweeperOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RecoverySweeper> _log;

    public RecoverySweeper(IWorkflowStore store, IWorkflowRunner runner, IWorkflowDefinitionRegistry registry,
        RecoverySweeperOptions options, TimeProvider time, ILogger<RecoverySweeper> log)
    {
        _store = store;
        _runner = runner;
        _registry = registry;
        _options = options;
        _time = time;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(_options.InitialDelay, _time, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_options.Interval, _time);
        do
        {
            try { await SweepOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogError(ex, "Recovery sweep failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Runs one sweep. Public so tests and admin endpoints can trigger it.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var resumed = 0;
        using var gate = new SemaphoreSlim(_options.MaxConcurrency);
        var tasks = new List<Task>();

        await foreach (var key in _store.ListRunnableAsync(now, ct).ConfigureAwait(false))
        {
            if (!InstanceKey.TryParse(key, out var parsed))
            {
                _log.LogWarning("Skipping instance with unparseable key {Key}", key);
                continue;
            }
            if (parsed.Namespace != _runner.Namespace) continue;

            var def = _registry.Find(parsed.WorkflowName, parsed.Version);
            if (def is null)
            {
                _log.LogWarning("Orphaned instance {Key}: no definition registered for {Workflow} v{Version}", key, parsed.WorkflowName, parsed.Version);
                continue;
            }

            await gate.WaitAsync(ct).ConfigureAwait(false);
            resumed++;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    _log.LogInformation("Resuming {Key} after lease expiry", key);
                    var result = await def.ResumeAsync(_runner, key, ct).ConfigureAwait(false);
                    if (result.IsFailed) _log.LogWarning("Resumed instance {Key} failed: {Error}", key, result.Error);
                    // Busy means another host got there first; nothing to do
                }
                catch (LeaseLostException) { /* another host took over mid-run */ }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { _log.LogError(ex, "Resume of {Key} threw", key); }
                finally { gate.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return resumed;
    }
}
