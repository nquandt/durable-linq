namespace DurableLinq;

/// <summary>
/// Per-run state handed to every <see cref="Durable{T}"/>. Tracks the step sequence number,
/// the name scope, and gives primitives access to the store, serializer, clock, and events.
/// </summary>
public sealed class WorkflowContext
{
    private int _seq;
    private readonly Stack<string> _scopes = new();
    private readonly HashSet<int> _recordedFailures = new();
    private string _prefix = "";

    public WorkflowContext(
        string instanceKey,
        IWorkflowStore store,
        IWorkflowSerializer? serializer = null,
        DurableEventSource? events = null,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default,
        IFailureClassifier? classifier = null)
    {
        InstanceKey = instanceKey ?? throw new ArgumentNullException(nameof(instanceKey));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Serializer = serializer ?? JsonWorkflowSerializer.Instance;
        Events = events ?? DurableEventSource.Default;
        Time = time ?? TimeProvider.System;
        CancellationToken = cancellationToken;
        Classifier = classifier ?? DefaultFailureClassifier.Instance;
    }

    public string InstanceKey { get; }
    public IWorkflowStore Store { get; }
    public IWorkflowSerializer Serializer { get; }
    public DurableEventSource Events { get; }
    public TimeProvider Time { get; }
    public CancellationToken CancellationToken { get; }
    public IFailureClassifier Classifier { get; }

    /// <summary>Number of steps visited so far in this run (executed or replayed).</summary>
    public int StepCount => _seq;

    /// <summary>Number of steps in this run that were served from the log.</summary>
    public int ReplayedCount { get; internal set; }

    /// <summary>Number of steps in this run that actually executed.</summary>
    public int ExecutedCount { get; internal set; }

    /// <summary>Qualified name of the most recent step, executed or replayed. Used to label guards.</summary>
    public string? LastStepName { get; internal set; }

    /// <summary>Reserves the next sequence number.</summary>
    public int NextSeq() => _seq++;

    /// <summary>Applies the current <see cref="Durable.Sub{T}"/> scope to a step name.</summary>
    public string Qualify(string name) => _prefix.Length == 0 ? name : _prefix + name;

    // ---------------------------------------------------------------- failures

    /// <summary>
    /// Whether an exception is a business failure to capture as a value. Engine invariants and cancellation
    /// are not: they propagate as exceptions.
    /// </summary>
    public bool IsCapturable(Exception ex) =>
        ex is not DurableException
        && !(ex is OperationCanceledException && CancellationToken.IsCancellationRequested);

    /// <summary>Turns a captured exception into a failed outcome, classified by the exception itself or the classifier.</summary>
    public Outcome<T> Capture<T>(Exception ex, string? stepName = null, int? seq = null) =>
        Outcome<T>.Failure(WorkflowError.FromException(ex, Classifier, stepName ?? LastStepName, seq, Time.GetUtcNow()));

    /// <summary>
    /// Records a failure in the log at its step's sequence number, so a replay produces the same failure at
    /// the same position. Called by <c>Recover</c> and <c>Compensate</c> before they continue. Failures
    /// without a sequence number (guards) re-evaluate deterministically and need no entry.
    /// </summary>
    public async Task RecordFailureAsync(WorkflowError error)
    {
        if (error.Seq is not { } seq || !_recordedFailures.Add(seq)) return;
        var existing = await Store.TryGetStepAsync(InstanceKey, seq, CancellationToken).ConfigureAwait(false);
        if (existing is not null) return;
        var entry = new StepEntry(seq, error.StepName ?? "?", Serializer.Serialize(error), Time.GetUtcNow(), Failed: true);
        // The failure is already decided; persist it even if cancellation was requested while getting here.
        await Store.AppendStepAsync(InstanceKey, entry, CancellationToken.None).ConfigureAwait(false);
    }

    internal IDisposable PushScope(string name)
    {
        _scopes.Push(_prefix);
        _prefix = _prefix + name + "/";
        return new ScopePopper(this);
    }

    private void PopScope() => _prefix = _scopes.Pop();

    private sealed class ScopePopper(WorkflowContext ctx) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            ctx.PopScope();
        }
    }
}
