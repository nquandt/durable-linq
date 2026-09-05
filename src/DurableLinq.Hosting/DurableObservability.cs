using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DurableLinq.Hosting;

/// <summary>
/// Forwards <see cref="DurableEvent"/>s to <c>ILogger</c> and to a <c>System.Diagnostics.Metrics</c> meter
/// named <c>DurableLinq</c>. Whatever the host already exports (OpenTelemetry, dotnet-counters) picks these up.
/// </summary>
public sealed class DurableObservability : IHostedService, IDisposable
{
    public const string MeterName = "DurableLinq";

    private readonly DurableEventSource _events;
    private readonly ILogger<DurableObservability> _log;
    private readonly Meter _meter;
    private readonly Counter<long> _stepsExecuted;
    private readonly Counter<long> _stepsReplayed;
    private readonly Counter<long> _stepsFailed;
    private readonly Counter<long> _instancesStarted;
    private readonly Counter<long> _instancesCompleted;
    private readonly Counter<long> _instancesFailed;
    private readonly Counter<long> _signals;
    private readonly Histogram<double> _stepDuration;
    private readonly Histogram<double> _instanceDuration;
    private readonly UpDownCounter<long> _running;

    public DurableObservability(DurableEventSource events, ILogger<DurableObservability> log)
    {
        _events = events;
        _log = log;
        _meter = new Meter(MeterName);
        _stepsExecuted = _meter.CreateCounter<long>("durable.steps.executed");
        _stepsReplayed = _meter.CreateCounter<long>("durable.steps.replayed");
        _stepsFailed = _meter.CreateCounter<long>("durable.steps.failed");
        _instancesStarted = _meter.CreateCounter<long>("durable.instances.started");
        _instancesCompleted = _meter.CreateCounter<long>("durable.instances.completed");
        _instancesFailed = _meter.CreateCounter<long>("durable.instances.failed");
        _signals = _meter.CreateCounter<long>("durable.signals.received");
        _stepDuration = _meter.CreateHistogram<double>("durable.step.duration", unit: "ms");
        _instanceDuration = _meter.CreateHistogram<double>("durable.instance.duration", unit: "ms");
        _running = _meter.CreateUpDownCounter<long>("durable.instances.running");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _events.Raised += OnEvent;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _events.Raised -= OnEvent;
        return Task.CompletedTask;
    }

    private void OnEvent(DurableEvent e)
    {
        var workflow = InstanceKey.TryParse(e.InstanceKey, out var k) ? k.WorkflowName : "?";
        var tags = new TagList { { "workflow", workflow } };

        switch (e.Kind)
        {
            case DurableEventKind.InstanceStarted:
            case DurableEventKind.InstanceResumed:
                _instancesStarted.Add(1, tags);
                _running.Add(1, tags);
                _log.LogInformation("{Kind} {Key}", e.Kind, e.InstanceKey);
                break;
            case DurableEventKind.InstanceCompleted:
                _instancesCompleted.Add(1, tags);
                _running.Add(-1, tags);
                if (e.Duration is { } d1) _instanceDuration.Record(d1.TotalMilliseconds, tags);
                _log.LogInformation("Completed {Key} in {Ms:F0}ms ({Detail})", e.InstanceKey, e.Duration?.TotalMilliseconds, e.Detail);
                break;
            case DurableEventKind.InstanceFailed:
                _instancesFailed.Add(1, tags);
                _running.Add(-1, tags);
                if (e.Duration is { } d2) _instanceDuration.Record(d2.TotalMilliseconds, tags);
                _log.LogError("Failed {Key} at step {Step}: {Detail}", e.InstanceKey, e.StepName, e.Detail);
                break;
            case DurableEventKind.LeaseLost:
                _running.Add(-1, tags);
                _log.LogWarning("Lease lost {Key}", e.InstanceKey);
                break;
            case DurableEventKind.StepExecuted:
                tags.Add("step", e.StepName);
                _stepsExecuted.Add(1, tags);
                if (e.Duration is { } d3) _stepDuration.Record(d3.TotalMilliseconds, tags);
                _log.LogDebug("Step #{Seq} {Step} executed in {Ms:F0}ms [{Key}]", e.Seq, e.StepName, e.Duration?.TotalMilliseconds, e.InstanceKey);
                break;
            case DurableEventKind.StepReplayed:
                tags.Add("step", e.StepName);
                _stepsReplayed.Add(1, tags);
                _log.LogTrace("Step #{Seq} {Step} replayed [{Key}]", e.Seq, e.StepName, e.InstanceKey);
                break;
            case DurableEventKind.StepFailed:
                tags.Add("step", e.StepName);
                _stepsFailed.Add(1, tags);
                _log.LogWarning("Step #{Seq} {Step} failed: {Detail} [{Key}]", e.Seq, e.StepName, e.Detail, e.InstanceKey);
                break;
            case DurableEventKind.StepRetrying:
                _log.LogWarning("Step #{Seq} {Step} retrying ({Detail}) [{Key}]", e.Seq, e.StepName, e.Detail, e.InstanceKey);
                break;
            case DurableEventKind.Sleeping:
                _log.LogInformation("Sleeping {Key} until {WakeAt}", e.InstanceKey, e.Detail);
                break;
            case DurableEventKind.WaitingForSignal:
                _log.LogInformation("Waiting {Key} for signal {Signal}", e.InstanceKey, e.Detail);
                break;
            case DurableEventKind.SignalReceived:
                _signals.Add(1, tags);
                _log.LogInformation("Signal {Signal} received by {Key}", e.Detail, e.InstanceKey);
                break;
            case DurableEventKind.SignalSent:
                _log.LogDebug("Signal {Signal} sent to {Key}", e.Detail, e.InstanceKey);
                break;
        }
    }

    public void Dispose() => _meter.Dispose();
}
