namespace DurableLinq;

// Only engine invariants throw. Business failures, validation, and busy instances are values:
// see Outcome<T>, WorkflowError, ValidationResult, and RunResult<T>.

public class DurableException : Exception
{
    public DurableException(string message) : base(message) { }
    public DurableException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>
/// The step at a given sequence number has a different name than the one recorded in the log.
/// The workflow code changed in a way the recorded history cannot support.
/// </summary>
public sealed class NondeterminismException(string instanceKey, int seq, string recordedName, string actualName)
    : DurableException($"Instance '{instanceKey}' step #{seq}: the log recorded '{recordedName}' but the code produced '{actualName}'. " +
                       "Step names must be stable. Increase the workflow version to run changed code.")
{
    public string InstanceKey { get; } = instanceKey;
    public int Seq { get; } = seq;
    public string RecordedName { get; } = recordedName;
    public string ActualName { get; } = actualName;
}

/// <summary>The lease was lost during a run. The run was aborted; another host may resume the instance.</summary>
public sealed class LeaseLostException(string instanceKey)
    : DurableException($"The lease for instance '{instanceKey}' was lost during the run.")
{
    public string InstanceKey { get; } = instanceKey;
}

/// <summary>The store found no instance with the given key.</summary>
public sealed class InstanceNotFoundException(string instanceKey)
    : DurableException($"No workflow instance with key '{instanceKey}'.")
{
    public string InstanceKey { get; } = instanceKey;
}

/// <summary>A definition needed to resume an instance is not registered.</summary>
public sealed class DefinitionNotFoundException(string workflowName, int version)
    : DurableException($"No workflow definition is registered for '{workflowName}' v{version}. Keep old versions registered until their instances drain.")
{
    public string WorkflowName { get; } = workflowName;
    public int Version { get; } = version;
}

/// <summary>
/// A step's work finished, but its result could not be serialized for the log. The work already ran and was
/// never recorded, so a naive retry could repeat it. Not captured as a business failure on purpose: fix the
/// step's result type (or the serializer), then decide by hand whether the work needs to be redone before
/// resuming the instance.
/// </summary>
public sealed class StepResultNotSerializableException(string stepName, int seq, Type resultType, Exception inner)
    : DurableException(
        $"Step '{stepName}' (#{seq}) finished but its result of type '{resultType}' could not be serialized: {inner.Message} " +
        "The work already ran and was not recorded, so a naive retry could repeat it. Fix the result type or serializer before resuming.",
        inner)
{
    public string StepName { get; } = stepName;
    public int Seq { get; } = seq;
    public Type ResultType { get; } = resultType;
}

// ---- thrown only by RunResult<T>.ValueOrThrow(), for callers who want a single-line happy path ----

/// <summary>The instance is Failed. The error is stored with the instance.</summary>
public sealed class WorkflowFailedException(string instanceKey, WorkflowError error)
    : DurableException($"Instance '{instanceKey}' failed: {error}")
{
    public string InstanceKey { get; } = instanceKey;
    public WorkflowError Error { get; } = error;
}

/// <summary>Another host holds the lease on this instance.</summary>
public sealed class WorkflowBusyException(string instanceKey, string? owner, DateTimeOffset? expires)
    : DurableException($"Instance '{instanceKey}' is leased by '{owner}' until {expires:O}.")
{
    public string InstanceKey { get; } = instanceKey;
    public string? Owner { get; } = owner;
    public DateTimeOffset? Expires { get; } = expires;
}

/// <summary>
/// Convenience base for user exceptions that a retry cannot fix. Implementing <see cref="IWorkflowFailure"/>
/// on your own types works the same; this class exists so the simple case is one line.
/// </summary>
public class TerminalException : Exception, IWorkflowFailure
{
    public TerminalException(string code, string message, object? detail = null) : base(message)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }
    public bool IsTerminal => true;
    public object? Detail { get; }
}
