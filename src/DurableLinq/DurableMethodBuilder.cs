using System.Runtime.CompilerServices;

namespace DurableLinq;

/// <summary>
/// Lets <c>async Durable&lt;T&gt;</c> be a method return type. The compiler turns the method into a state
/// machine and hands it to this builder. The builder does not start the machine when the method is called:
/// it wraps the machine in a <see cref="Durable{T}"/> and starts it when the runner calls <c>RunAsync(ctx)</c>.
/// Every <c>await</c> on a durable inside the method then runs that durable against the same context, so the
/// context flows exactly as it does through a query expression.
/// </summary>
public sealed class DurableMethodBuilder<T>
{
    private IAsyncStateMachine? _stateMachine;
    private WorkflowContext? _context;
    private TaskCompletionSource<Outcome<T>>? _completion;
    private bool _started;

    private DurableMethodBuilder() => Task = Durable<T>.FromOutcome(Run);

    public static DurableMethodBuilder<T> Create() => new();

    /// <summary>The durable the method returns. Named <c>Task</c> because the compiler requires that name.</summary>
    public Durable<T> Task { get; }

    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
    {
        // Boxing copies the struct. The copy holds a reference to this builder (a class), so the copy and this
        // builder stay linked. MoveNext runs on the box later, from Run.
        _stateMachine = stateMachine;
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine) => _stateMachine = stateMachine;

    private System.Threading.Tasks.Task<Outcome<T>> Run(WorkflowContext context)
    {
        if (_started)
            throw new DurableException("An async Durable<T> method body can run once per instance. To run the same body again, call the method again or wrap it in Durable.Sub(name, () => Method()).");
        _started = true;
        _context = context;
        _completion = new TaskCompletionSource<Outcome<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stateMachine!.MoveNext();
        return _completion.Task;
    }

    public void SetResult(T result) => _completion!.TrySetResult(Outcome<T>.Success(result));

    public void SetException(Exception exception)
    {
        var ctx = _context!;
        if (exception is StepFailedException failed)
            _completion!.TrySetResult(Outcome<T>.Failure(failed.Error));
        else if (ctx.IsCapturable(exception))
            _completion!.TrySetResult(ctx.Capture<T>(exception));
        else
            _completion!.TrySetException(exception);
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        if (awaiter is IDurableAwaiter durable)
            durable.Start(_context!, _stateMachine!.MoveNext);
        else
            awaiter.OnCompleted(_stateMachine!.MoveNext);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        if (awaiter is IDurableAwaiter durable)
            durable.Start(_context!, _stateMachine!.MoveNext);
        else
            awaiter.UnsafeOnCompleted(_stateMachine!.MoveNext);
    }
}

/// <summary>Marks an awaiter the builder must start with the run's context.</summary>
internal interface IDurableAwaiter
{
    void Start(WorkflowContext context, Action continuation);
}

/// <summary>
/// Awaiter for <c>await someDurable</c> inside an <c>async Durable&lt;T&gt;</c> method. Never completed up front,
/// because it needs the run's context; the builder supplies it in <see cref="Start"/>. A failed outcome throws
/// <see cref="StepFailedException"/> at the <c>await</c>, which the builder turns back into a failed outcome.
/// </summary>
public sealed class DurableAwaiter<T> : ICriticalNotifyCompletion, IDurableAwaiter
{
    private readonly Durable<T> _durable;
    private Outcome<T> _outcome;
    private Exception? _exception;

    internal DurableAwaiter(Durable<T> durable) => _durable = durable;

    public bool IsCompleted => false;

    public T GetResult()
    {
        if (_exception is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_exception);
        return _outcome.IsSuccess ? _outcome.Value : throw new StepFailedException(_outcome.Error!);
    }

    void IDurableAwaiter.Start(WorkflowContext context, Action continuation)
    {
        var task = _durable.RunAsync(context);
        if (task.IsCompleted)
        {
            Complete(task);
            continuation();
            return;
        }
        task.ContinueWith(t => { Complete(t); continuation(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Complete(Task<Outcome<T>> task)
    {
        if (task.IsCompletedSuccessfully) _outcome = task.Result;
        else if (task.Exception is { } ex) _exception = ex.InnerException ?? ex;
        else _exception = new TaskCanceledException(task);
    }

    /// <summary>Only reached when the durable is awaited outside an <c>async Durable&lt;T&gt;</c> method.</summary>
    public void OnCompleted(Action continuation) => throw NotInDurableMethod();

    public void UnsafeOnCompleted(Action continuation) => throw NotInDurableMethod();

    private static DurableException NotInDurableMethod() =>
        new("A Durable<T> can only be awaited inside a method declared as 'async Durable<T>'. From ordinary async code, run it with IWorkflowRunner.");
}

/// <summary>
/// Thrown at an <c>await</c> when the awaited durable failed. Leave it uncaught: the builder converts it into
/// the workflow's failed outcome. To handle a failure, use <c>.Recover(...)</c> or <c>Durable.Try(...)</c> on the
/// durable before awaiting it, so the failure is recorded and a replay takes the same path.
/// </summary>
public sealed class StepFailedException(WorkflowError error) : DurableException($"Step failed: {error}")
{
    public WorkflowError Error { get; } = error;
}
