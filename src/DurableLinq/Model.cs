using System.Text.Json;

namespace DurableLinq;

public enum WorkflowStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
}

/// <summary>Per-instance metadata. The store persists it under the instance key.</summary>
public sealed record InstanceHeader
{
    public required string Key { get; init; }
    public required string Namespace { get; init; }
    public required string WorkflowName { get; init; }
    public required int Version { get; init; }
    public required string InstanceId { get; init; }
    public WorkflowStatus Status { get; init; } = WorkflowStatus.Running;
    public byte[]? Input { get; init; }
    /// <summary>Serialized result when <see cref="Status"/> is Completed.</summary>
    public byte[]? Result { get; init; }
    /// <summary>Serialized <see cref="WorkflowError"/> when <see cref="Status"/> is Failed.</summary>
    public byte[]? Error { get; init; }
    public string? LeaseOwner { get; init; }
    public Guid? LeaseToken { get; init; }
    public DateTimeOffset? LeaseExpires { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public bool IsLeasedAt(DateTimeOffset now) => LeaseOwner is not null && LeaseExpires is not null && LeaseExpires > now;
}

/// <summary>Proof of exclusive access to an instance for a limited time.</summary>
public sealed record Lease(string InstanceKey, string Owner, Guid Token, DateTimeOffset Expires);

/// <summary>
/// One recorded step. Sequence numbers are contiguous from zero within an instance.
/// When <paramref name="Failed"/> is <c>true</c> the payload is a serialized <see cref="WorkflowError"/>
/// and a replay rethrows it; this is how a failure caught by <c>Durable.Catch</c> stays deterministic.
/// </summary>
public sealed record StepEntry(int Seq, string Name, byte[]? Payload, DateTimeOffset RecordedAt, bool Failed = false);

/// <summary>A pending signal in an instance inbox.</summary>
public sealed record SignalEntry(string Name, byte[] Payload, DateTimeOffset ReceivedAt);

/// <summary>
/// A failure, stored with the instance and carried through the query as a value. Serializable, so it survives
/// a process restart. <see cref="Code"/> is the stable handle for <c>Recover</c>, dashboards, and metrics.
/// </summary>
public sealed record WorkflowError
{
    public const string ValidationCode = "validation";
    public const string TimeoutCode = "timeout";

    /// <summary>Stable identifier. Defaults to the exception type name for captured exceptions.</summary>
    public required string Code { get; init; }
    public required string Message { get; init; }
    /// <summary>True when a retry cannot fix it.</summary>
    public bool IsTerminal { get; init; }
    /// <summary>Exception type name when the error was captured from a throw.</summary>
    public string? ExceptionType { get; init; }
    public string? StackTrace { get; init; }
    /// <summary>Extra data supplied by the failure, as JSON.</summary>
    public JsonElement? Detail { get; init; }
    public string? StepName { get; init; }
    public int? Seq { get; init; }
    public DateTimeOffset FailedAt { get; init; }

    public static WorkflowError Create(string code, string message, bool terminal = false, object? detail = null, string? stepName = null, int? seq = null) =>
        new()
        {
            Code = code,
            Message = message,
            IsTerminal = terminal,
            Detail = detail is null ? null : JsonSerializer.SerializeToElement(detail, JsonWorkflowSerializer.DefaultOptions),
            StepName = stepName,
            Seq = seq,
            FailedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Captures a thrown exception. <see cref="IWorkflowFailure"/> on the exception wins, then the classifier,
    /// then a retryable error keyed by the exception type name.
    /// </summary>
    public static WorkflowError FromException(Exception ex, IFailureClassifier? classifier = null, string? stepName = null, int? seq = null, DateTimeOffset? at = null)
    {
        WorkflowError? error = null;
        if (ex is IWorkflowFailure f)
            error = Create(f.Code, ex.Message, f.IsTerminal, f.Detail);
        error ??= classifier?.Classify(ex);
        error ??= Create(ex.GetType().Name, ex.Message);
        return error with
        {
            ExceptionType = ex.GetType().FullName,
            StackTrace = ex.StackTrace,
            StepName = error.StepName ?? stepName,
            Seq = error.Seq ?? seq,
            FailedAt = at ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Reads typed detail back. Returns default when there is none.</summary>
    public T? DetailAs<T>() => Detail is { } d ? d.Deserialize<T>(JsonWorkflowSerializer.DefaultOptions) : default;

    public override string ToString() => $"{Code}: {Message}" + (StepName is null ? "" : $" (step '{StepName}')") + (IsTerminal ? " [terminal]" : "");
}

/// <summary>Filter for <see cref="IWorkflowStore.ListInstancesAsync"/>.</summary>
public sealed record InstanceQuery
{
    public string? Namespace { get; init; }
    public string? WorkflowName { get; init; }
    public int? Version { get; init; }
    public WorkflowStatus? Status { get; init; }
    /// <summary>Case-insensitive substring match on the instance id.</summary>
    public string? InstanceIdContains { get; init; }
    public int Limit { get; init; } = 100;
    public int Offset { get; init; } = 0;

    public bool Matches(InstanceHeader h) =>
        (Namespace is null || string.Equals(Namespace, h.Namespace, StringComparison.Ordinal)) &&
        (WorkflowName is null || string.Equals(WorkflowName, h.WorkflowName, StringComparison.Ordinal)) &&
        (Version is null || Version == h.Version) &&
        (Status is null || Status == h.Status) &&
        (InstanceIdContains is null || h.InstanceId.Contains(InstanceIdContains, StringComparison.OrdinalIgnoreCase));
}
