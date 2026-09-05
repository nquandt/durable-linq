using System.Diagnostics;

namespace DurableLinq;

/// <summary>Retry settings for a step. Attempts happen inside one run; nothing is logged until one succeeds.</summary>
public sealed record StepOptions
{
    public static readonly StepOptions Default = new();

    /// <summary>Total attempts, including the first. 1 means no retry.</summary>
    public int MaxAttempts { get; init; } = 1;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public double BackoffMultiplier { get; init; } = 2.0;
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Returns <c>true</c> when the error should be retried. Terminal errors are never retried. Defaults to retry everything else.</summary>
    public Func<WorkflowError, bool>? ShouldRetry { get; init; }

    public static StepOptions Retry(int maxAttempts, TimeSpan? initialDelay = null) =>
        new() { MaxAttempts = maxAttempts, InitialDelay = initialDelay ?? TimeSpan.FromSeconds(1) };
}

/// <summary>
/// The primitives a workflow is built from. Every one of them records to the log, so the code
/// between <c>from</c> clauses stays deterministic by construction.
/// </summary>
public static class Durable
{
    // ---------------------------------------------------------------- lifting

    /// <summary>A workflow that yields a value without touching the log.</summary>
    public static Durable<T> Return<T>(T value) => Durable<T>.FromOutcome(_ => Task.FromResult(Outcome<T>.Success(value)));

    /// <summary>
    /// Seeds a query with the input so checks can hang off it: <c>from o in Durable.Start(order).Ensure(...)</c>.
    /// Unlike <c>Validate</c> on the workflow class, a failure here happens inside the run and is stored as a Failed instance.
    /// </summary>
    public static Durable<T> Start<T>(T value) => Return(value);

    /// <summary>A workflow that fails with the given error.</summary>
    public static Durable<T> Fail<T>(WorkflowError error) => Durable<T>.FromOutcome(_ => Task.FromResult(Outcome<T>.Failure(error)));

    public static Durable<T> Fail<T>(string code, string message, bool terminal = false, object? detail = null) =>
        Fail<T>(WorkflowError.Create(code, message, terminal, detail));

    /// <summary>A workflow that yields <see cref="Unit.Value"/>.</summary>
    public static Durable<Unit> Nothing { get; } = Return(Unit.Value);

    /// <summary>Exposes the context. Useful for reading the instance key inside a workflow.</summary>
    public static Durable<WorkflowContext> Context { get; } = new(ctx => Task.FromResult(ctx));

    /// <summary>The instance key of the running workflow.</summary>
    public static Durable<string> InstanceKey { get; } = new(ctx => Task.FromResult(ctx.InstanceKey));

    // ---------------------------------------------------------------- step

    /// <summary>Runs <paramref name="work"/> once and records its result. On replay the recorded result is returned.</summary>
    public static Durable<T> Step<T>(string name, Func<CancellationToken, Task<T>> work, StepOptions? options = null) =>
        Try(name, async ct => Outcome<T>.Success(await work(ct).ConfigureAwait(false)), options);

    public static Durable<T> Step<T>(string name, Func<Task<T>> work, StepOptions? options = null) =>
        Step(name, _ => work(), options);

    public static Durable<T> Step<T>(string name, Func<T> work, StepOptions? options = null) =>
        Step(name, _ => Task.FromResult(work()), options);

    public static Durable<Unit> Step(string name, Func<CancellationToken, Task> work, StepOptions? options = null) =>
        Step(name, async ct => { await work(ct).ConfigureAwait(false); return Unit.Value; }, options);

    public static Durable<Unit> Step(string name, Func<Task> work, StepOptions? options = null) =>
        Step(name, async _ => { await work().ConfigureAwait(false); return Unit.Value; }, options);

    public static Durable<Unit> Step(string name, Action work, StepOptions? options = null) =>
        Step(name, _ => { work(); return Task.FromResult(Unit.Value); }, options);

    /// <summary>
    /// A step whose body returns an <see cref="Outcome{T}"/>, for failures that are part of the design rather
    /// than surprises. A terminal failure is recorded so a replay yields it again; a retryable one is not,
    /// so <c>RetryFailedAsync</c> re-executes the step. Exceptions are captured like in <c>Step</c>.
    /// </summary>
    public static Durable<T> Try<T>(string name, Func<CancellationToken, Task<Outcome<T>>> work, StepOptions? options = null) =>
        Recorded(name, async (ctx, seq) =>
        {
            var opts = options ?? StepOptions.Default;
            var delay = opts.InitialDelay;
            var fullName = ctx.Qualify(name);
            for (var attempt = 1; ; attempt++)
            {
                Outcome<T> outcome;
                try
                {
                    outcome = await work(ctx.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ctx.IsCapturable(ex))
                {
                    outcome = ctx.Capture<T>(ex, fullName, seq);
                }
                if (outcome.IsSuccess) return outcome;

                var error = outcome.Error! with { StepName = fullName, Seq = seq };
                var retry = attempt < opts.MaxAttempts && !error.IsTerminal && (opts.ShouldRetry?.Invoke(error) ?? true);
                if (!retry) return Outcome<T>.Failure(error);

                ctx.Events.Raise(DurableEventKind.StepRetrying, ctx.InstanceKey, fullName, seq, detail: $"attempt {attempt} of {opts.MaxAttempts}: {error}");
                await Task.Delay(delay, ctx.Time, ctx.CancellationToken).ConfigureAwait(false);
                var next = TimeSpan.FromTicks((long)(delay.Ticks * opts.BackoffMultiplier));
                delay = next > opts.MaxDelay ? opts.MaxDelay : next;
            }
        });

    public static Durable<T> Try<T>(string name, Func<Task<Outcome<T>>> work, StepOptions? options = null) =>
        Try(name, _ => work(), options);

    public static Durable<T> Try<T>(string name, Func<Outcome<T>> work, StepOptions? options = null) =>
        Try(name, _ => Task.FromResult(work()), options);

    // ---------------------------------------------------------------- time and randomness

    /// <summary>The current time, recorded so a replay sees the same value.</summary>
    public static Durable<DateTimeOffset> Now(string name = "now") =>
        Recorded(name, (ctx, _) => Task.FromResult(Outcome<DateTimeOffset>.Success(ctx.Time.GetUtcNow())));

    /// <summary>A new guid, recorded so a replay sees the same value.</summary>
    public static Durable<Guid> NewGuid(string name = "guid") =>
        Recorded(name, (_, _) => Task.FromResult(Outcome<Guid>.Success(Guid.NewGuid())));

    /// <summary>A random integer in <c>[min, max)</c>, recorded so a replay sees the same value.</summary>
    public static Durable<int> RandomInt(int min, int max, string name = "random") =>
        Recorded(name, (_, _) => Task.FromResult(Outcome<int>.Success(Random.Shared.Next(min, max))));

    /// <summary>
    /// Durable timer. The wake-up time is recorded on first execution. A restart waits only for the remainder.
    /// The run blocks in-process until the time arrives.
    /// </summary>
    public static Durable<Unit> Sleep(TimeSpan duration, string name = "sleep") => Durable<Unit>.FromOutcome(async ctx =>
    {
        var seq = ctx.NextSeq();
        var fullName = ctx.Qualify(name);
        ctx.LastStepName = fullName;
        var ct = ctx.CancellationToken;
        var prior = await ctx.Store.TryGetStepAsync(ctx.InstanceKey, seq, ct).ConfigureAwait(false);
        DateTimeOffset wakeAt;
        if (prior is not null)
        {
            CheckName(ctx, seq, prior, fullName);
            wakeAt = ctx.Serializer.Deserialize<DateTimeOffset>(prior.Payload!);
            ctx.ReplayedCount++;
            ctx.Events.Raise(DurableEventKind.StepReplayed, ctx.InstanceKey, fullName, seq);
        }
        else
        {
            wakeAt = ctx.Time.GetUtcNow() + duration;
            // The wake time is already decided; persist it even if cancellation was requested while getting here.
            await ctx.Store.AppendStepAsync(ctx.InstanceKey, new StepEntry(seq, fullName, ctx.Serializer.Serialize(wakeAt), ctx.Time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
            ctx.ExecutedCount++;
            ctx.Events.Raise(DurableEventKind.StepExecuted, ctx.InstanceKey, fullName, seq);
        }

        var remaining = wakeAt - ctx.Time.GetUtcNow();
        if (remaining > TimeSpan.Zero)
            ctx.Events.Raise(DurableEventKind.Sleeping, ctx.InstanceKey, fullName, seq, duration: remaining, detail: wakeAt.ToString("O"));
        // timers can fire a few milliseconds early relative to the wall clock; loop until the clock agrees
        while (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, ctx.Time, ct).ConfigureAwait(false);
            remaining = wakeAt - ctx.Time.GetUtcNow();
        }
        return Outcome<Unit>.Success(Unit.Value);
    });

    // ---------------------------------------------------------------- signals

    /// <summary>
    /// Waits for an external caller to send a signal with this name to the instance. The payload is recorded
    /// as the step result. The run blocks in-process until the signal arrives. With a timeout, the step fails
    /// with code <see cref="WorkflowError.TimeoutCode"/> when nothing arrives in time.
    /// </summary>
    public static Durable<T> WaitFor<T>(string signalName, TimeSpan? timeout = null) => Durable<T>.FromOutcome(async ctx =>
    {
        var seq = ctx.NextSeq();
        var fullName = ctx.Qualify("wait:" + signalName);
        ctx.LastStepName = fullName;
        var ct = ctx.CancellationToken;
        var prior = await ctx.Store.TryGetStepAsync(ctx.InstanceKey, seq, ct).ConfigureAwait(false);
        if (prior is not null)
        {
            CheckName(ctx, seq, prior, fullName);
            ctx.ReplayedCount++;
            ctx.Events.Raise(DurableEventKind.StepReplayed, ctx.InstanceKey, fullName, seq);
            if (prior.Failed) return ReplayFailure<T>(ctx, prior);
            return Outcome<T>.Success(ctx.Serializer.Deserialize<T>(prior.Payload!));
        }

        ctx.Events.Raise(DurableEventKind.WaitingForSignal, ctx.InstanceKey, fullName, seq, detail: signalName);
        var sw = Stopwatch.StartNew();
        byte[] payload;
        if (timeout is { } t)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(t);
            try
            {
                payload = await ctx.Store.WaitForSignalAsync(ctx.InstanceKey, signalName, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var error = WorkflowError.Create(WorkflowError.TimeoutCode, $"waited {t} for signal '{signalName}' and got nothing", stepName: fullName, seq: seq);
                ctx.Events.Raise(DurableEventKind.StepFailed, ctx.InstanceKey, fullName, seq, sw.Elapsed, detail: error.ToString());
                return Outcome<T>.Failure(error);
            }
        }
        else
        {
            payload = await ctx.Store.WaitForSignalAsync(ctx.InstanceKey, signalName, ct).ConfigureAwait(false);
        }

        // The signal has already been taken from the store; persist it even if cancellation was requested
        // while getting here. Otherwise the signal is gone and a replay would wait for it forever.
        await ctx.Store.AppendStepAsync(ctx.InstanceKey, new StepEntry(seq, fullName, payload, ctx.Time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        ctx.ExecutedCount++;
        ctx.Events.Raise(DurableEventKind.SignalReceived, ctx.InstanceKey, fullName, seq, duration: sw.Elapsed, detail: signalName);
        return Outcome<T>.Success(ctx.Serializer.Deserialize<T>(payload));
    });

    // ---------------------------------------------------------------- control flow

    /// <summary>Runs <paramref name="body"/> for each item in order. Step names inside the body should include the index.</summary>
    public static Durable<IReadOnlyList<TResult>> ForEach<TItem, TResult>(IEnumerable<TItem> items, Func<TItem, int, Durable<TResult>> body) =>
        Durable<IReadOnlyList<TResult>>.FromOutcome(async ctx =>
        {
            var results = new List<TResult>();
            var i = 0;
            foreach (var item in items)
            {
                var r = await body(item, i).RunAsync(ctx).ConfigureAwait(false);
                if (r.IsFailure) return r.As<IReadOnlyList<TResult>>();
                results.Add(r.Value);
                i++;
            }
            return Outcome<IReadOnlyList<TResult>>.Success(results);
        });

    public static Durable<IReadOnlyList<TResult>> ForEach<TItem, TResult>(IEnumerable<TItem> items, Func<TItem, Durable<TResult>> body) =>
        ForEach(items, (item, _) => body(item));

    /// <summary>Runs one of two branches. Which branch ran is recorded, so a replay follows the same path.</summary>
    public static Durable<T> If<T>(bool condition, Durable<T> then, Durable<T> @else) =>
        Recorded("if", (_, _) => Task.FromResult(Outcome<bool>.Success(condition)))
            .SelectMany(took => took ? then : @else);

    public static Durable<T> If<T>(bool condition, Func<Durable<T>> then, Func<Durable<T>> @else) =>
        Recorded("if", (_, _) => Task.FromResult(Outcome<bool>.Success(condition)))
            .SelectMany(took => took ? then() : @else());

    public static Durable<Unit> If(bool condition, Durable<Unit> then) => If(condition, then, Nothing);

    /// <summary>Repeats <paramref name="body"/> until it returns <c>false</c>. Each iteration's steps are scoped by index.</summary>
    public static Durable<int> While(Func<int, Durable<bool>> body, string name = "loop") => Durable<int>.FromOutcome(async ctx =>
    {
        var i = 0;
        while (true)
        {
            Outcome<bool> again;
            using (ctx.PushScope($"{name}[{i}]"))
                again = await body(i).RunAsync(ctx).ConfigureAwait(false);
            if (again.IsFailure) return again.As<int>();
            if (!again.Value) return Outcome<int>.Success(i);
            i++;
        }
    });

    /// <summary>
    /// Runs a child workflow inline. The child's step names are prefixed with <paramref name="name"/> and a slash,
    /// so the same child can be used several times in one parent.
    /// </summary>
    public static Durable<T> Sub<T>(string name, Durable<T> child) => Durable<T>.FromOutcome(async ctx =>
    {
        using (ctx.PushScope(name))
            return await child.RunAsync(ctx).ConfigureAwait(false);
    });

    public static Durable<T> Sub<T>(string name, Func<Durable<T>> child) => Durable<T>.FromOutcome(async ctx =>
    {
        using (ctx.PushScope(name))
            return await child().RunAsync(ctx).ConfigureAwait(false);
    });

    /// <summary>Static form of <see cref="Durable{T}.Recover(Func{WorkflowError, Durable{T}})"/>.</summary>
    public static Durable<T> Recover<T>(Durable<T> body, Func<WorkflowError, Durable<T>> handler) => body.Recover(handler);

    /// <summary>Static form of <see cref="Durable{T}.Compensate(Durable{Unit})"/>.</summary>
    public static Durable<T> Compensate<T>(Durable<T> body, Durable<Unit> compensation) => body.Compensate(compensation);

    // ---------------------------------------------------------------- internals

    /// <summary>
    /// The one place that touches the log for value steps. Reserves a sequence number, replays when an entry
    /// exists, otherwise executes and appends. A terminal failure is appended too, so a replay yields it again.
    /// Throws <see cref="NondeterminismException"/> when the recorded name differs.
    /// </summary>
    public static Durable<T> Recorded<T>(string name, Func<WorkflowContext, int, Task<Outcome<T>>> execute) => Durable<T>.FromOutcome(async ctx =>
    {
        var seq = ctx.NextSeq();
        var fullName = ctx.Qualify(name);
        ctx.LastStepName = fullName;
        var ct = ctx.CancellationToken;
        var prior = await ctx.Store.TryGetStepAsync(ctx.InstanceKey, seq, ct).ConfigureAwait(false);
        if (prior is not null)
        {
            CheckName(ctx, seq, prior, fullName);
            ctx.ReplayedCount++;
            ctx.Events.Raise(DurableEventKind.StepReplayed, ctx.InstanceKey, fullName, seq);
            if (prior.Failed) return ReplayFailure<T>(ctx, prior);
            return Outcome<T>.Success(ctx.Serializer.Deserialize<T>(prior.Payload!));
        }

        var sw = Stopwatch.StartNew();
        var outcome = await execute(ctx, seq).ConfigureAwait(false);

        if (outcome.IsFailure)
        {
            var error = outcome.Error! with { StepName = fullName, Seq = seq };
            ctx.Events.Raise(DurableEventKind.StepFailed, ctx.InstanceKey, fullName, seq, sw.Elapsed, detail: error.ToString());
            if (error.IsTerminal)
                await ctx.RecordFailureAsync(error).ConfigureAwait(false);
            return Outcome<T>.Failure(error);
        }

        byte[] payload;
        try
        {
            payload = ctx.Serializer.Serialize(outcome.Value);
        }
        catch (Exception ex)
        {
            // The work already ran; a serialization bug must not look like a retryable business failure.
            throw new StepResultNotSerializableException(fullName, seq, typeof(T), ex);
        }
        // The step's work already ran to completion; persist its result even if cancellation was requested
        // while getting here, so a resume never re-runs work that already happened.
        await ctx.Store.AppendStepAsync(ctx.InstanceKey, new StepEntry(seq, fullName, payload, ctx.Time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        ctx.ExecutedCount++;
        ctx.Events.Raise(DurableEventKind.StepExecuted, ctx.InstanceKey, fullName, seq, sw.Elapsed);
        return outcome;
    });

    private static Outcome<T> ReplayFailure<T>(WorkflowContext ctx, StepEntry prior)
    {
        var error = prior.Payload is null
            ? WorkflowError.Create("unknown", "recorded failure", stepName: prior.Name, seq: prior.Seq)
            : ctx.Serializer.Deserialize<WorkflowError>(prior.Payload);
        return Outcome<T>.Failure(error);
    }

    private static void CheckName(WorkflowContext ctx, int seq, StepEntry prior, string expected)
    {
        if (!string.Equals(prior.Name, expected, StringComparison.Ordinal))
            throw new NondeterminismException(ctx.InstanceKey, seq, prior.Name, expected);
    }
}
