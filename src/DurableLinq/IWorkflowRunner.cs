namespace DurableLinq;

public enum RunStatus
{
    /// <summary>The workflow finished and <see cref="RunResult{TOut}.Value"/> holds the result.</summary>
    Completed,
    /// <summary>A step or guard failed. The instance is stored as Failed with <see cref="RunResult.Error"/>.</summary>
    Failed,
    /// <summary>Another host holds the lease. Requeue with a delay.</summary>
    Busy,
}

/// <summary>Non-generic view of a run's outcome.</summary>
public abstract class RunResult
{
    protected RunResult(string instanceKey, RunStatus status)
    {
        InstanceKey = instanceKey;
        Status = status;
    }

    public string InstanceKey { get; }
    public RunStatus Status { get; }

    public bool IsCompleted => Status == RunStatus.Completed;
    public bool IsFailed => Status == RunStatus.Failed;
    public bool IsBusy => Status == RunStatus.Busy;

    /// <summary>The stored error when <see cref="IsFailed"/>.</summary>
    public WorkflowError? Error { get; init; }

    /// <summary>Lease holder when <see cref="IsBusy"/>.</summary>
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpires { get; init; }

    /// <summary>Throws the matching exception for anything but Completed. For callers who want a one-line happy path.</summary>
    protected void ThrowIfNotCompleted()
    {
        switch (Status)
        {
            case RunStatus.Failed: throw new WorkflowFailedException(InstanceKey, Error ?? WorkflowError.Create("unknown", "failed"));
            case RunStatus.Busy: throw new WorkflowBusyException(InstanceKey, LeaseOwner, LeaseExpires);
        }
    }

    public override string ToString() => Status switch
    {
        RunStatus.Failed => $"Failed {InstanceKey}: {Error}",
        RunStatus.Busy => $"Busy {InstanceKey}: leased by {LeaseOwner} until {LeaseExpires:O}",
        _ => $"Completed {InstanceKey}",
    };
}

/// <summary>The outcome of <see cref="IWorkflowRunner.RunAsync{TIn, TOut}"/>.</summary>
public sealed class RunResult<TOut> : RunResult
{
    private readonly TOut _value;

    private RunResult(string instanceKey, RunStatus status, TOut value) : base(instanceKey, status) => _value = value;

    /// <summary>The result. Throws unless <see cref="RunResult.IsCompleted"/>.</summary>
    public TOut Value => IsCompleted ? _value : throw new InvalidOperationException($"Run is {Status}, not Completed: {this}");

    /// <summary>The result, or the matching exception when the run did not complete.</summary>
    public TOut ValueOrThrow()
    {
        ThrowIfNotCompleted();
        return _value;
    }

    public static RunResult<TOut> Completed(string key, TOut value) => new(key, RunStatus.Completed, value);
    public static RunResult<TOut> Failed(string key, WorkflowError error) => new(key, RunStatus.Failed, default!) { Error = error };
    public static RunResult<TOut> Busy(string key, string? owner, DateTimeOffset? expires) => new(key, RunStatus.Busy, default!) { LeaseOwner = owner, LeaseExpires = expires };
}

/// <summary>The single entry point workflow hosts call.</summary>
public interface IWorkflowRunner
{
    /// <summary>The namespace this runner prefixes on every instance key.</summary>
    string Namespace { get; }

    /// <summary>
    /// Creates the instance when it does not exist, then runs it to completion. A second call with the same
    /// key resumes or returns the stored result. The passed input is ignored when the instance already exists.
    /// Never throws for a business outcome; see <see cref="RunResult{TOut}"/>.
    /// </summary>
    Task<RunResult<TOut>> RunAsync<TIn, TOut>(WorkflowDefinition<TIn, TOut> definition, string instanceId, TIn input, CancellationToken ct = default);

    /// <summary>Resumes an existing instance by full key, using the stored input.</summary>
    Task<RunResult<TOut>> ResumeAsync<TIn, TOut>(WorkflowDefinition<TIn, TOut> definition, string instanceKey, CancellationToken ct = default);

    /// <summary>Delivers a signal to an instance. A <see cref="Durable.WaitFor{T}"/> with that name receives it.</summary>
    Task SignalAsync<T>(string instanceKey, string signalName, T payload, CancellationToken ct = default);

    /// <summary>Formats the instance key for a definition and id under this runner's namespace.</summary>
    string KeyFor(WorkflowDefinition definition, string instanceId);

    Task<InstanceHeader?> GetHeaderAsync(string instanceKey, CancellationToken ct = default);

    Task<WorkflowStatus?> GetStatusAsync(string instanceKey, CancellationToken ct = default);

    /// <summary>Reads the stored result of a completed instance; default when it is not completed.</summary>
    Task<TOut?> GetResultAsync<TOut>(string instanceKey, CancellationToken ct = default);

    /// <summary>Reads the stored error of a failed instance; null otherwise.</summary>
    Task<WorkflowError?> GetErrorAsync(string instanceKey, CancellationToken ct = default);

    /// <summary>
    /// Puts a Failed instance back to Running so the next <c>RunAsync</c> continues from the last recorded step.
    /// Returns false when the instance is not Failed or its error is terminal.
    /// </summary>
    Task<bool> RetryFailedAsync(string instanceKey, CancellationToken ct = default);
}

/// <summary>Convenience overloads.</summary>
public static class WorkflowRunnerExtensions
{
    public static Task SignalAsync<T>(this IWorkflowRunner runner, WorkflowDefinition definition, string instanceId, string signalName, T payload, CancellationToken ct = default) =>
        runner.SignalAsync(runner.KeyFor(definition, instanceId), signalName, payload, ct);

    public static Task SignalAsync(this IWorkflowRunner runner, string instanceKey, string signalName, CancellationToken ct = default) =>
        runner.SignalAsync(instanceKey, signalName, Unit.Value, ct);

    public static Task<WorkflowStatus?> GetStatusAsync(this IWorkflowRunner runner, WorkflowDefinition definition, string instanceId, CancellationToken ct = default) =>
        runner.GetStatusAsync(runner.KeyFor(definition, instanceId), ct);
}
