namespace DurableLinq;

public enum DurableEventKind
{
    InstanceStarted,
    InstanceResumed,
    StepExecuted,
    StepReplayed,
    StepFailed,
    StepRetrying,
    Sleeping,
    WaitingForSignal,
    SignalReceived,
    SignalSent,
    LeaseLost,
    InstanceCompleted,
    InstanceFailed,
}

/// <summary>A notification raised by the runner. Hosting packages forward these to logging and metrics.</summary>
public sealed record DurableEvent(
    DurableEventKind Kind,
    string InstanceKey,
    DateTimeOffset Timestamp,
    string? StepName = null,
    int? Seq = null,
    TimeSpan? Duration = null,
    Exception? Error = null,
    string? Detail = null);

/// <summary>Fan-out point for <see cref="DurableEvent"/>s. Subscribers must not throw.</summary>
public sealed class DurableEventSource
{
    public static DurableEventSource Default { get; } = new();

    public event Action<DurableEvent>? Raised;

    public void Raise(DurableEvent e)
    {
        var handlers = Raised;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((Action<DurableEvent>)h)(e); }
            catch { /* observers must not break the run */ }
        }
    }

    public void Raise(DurableEventKind kind, string instanceKey, string? stepName = null, int? seq = null,
        TimeSpan? duration = null, Exception? error = null, string? detail = null) =>
        Raise(new DurableEvent(kind, instanceKey, DateTimeOffset.UtcNow, stepName, seq, duration, error, detail));
}
