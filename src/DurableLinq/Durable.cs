using System.Runtime.CompilerServices;

namespace DurableLinq;

/// <summary>
/// A workflow that produces a <typeparamref name="T"/> when run against a <see cref="WorkflowContext"/>.
/// Nothing runs until <see cref="RunAsync"/> is called. Failures travel through the chain as
/// <see cref="Outcome{T}"/> values; the query never throws for a business failure.
/// <c>Select</c>, <c>SelectMany</c>, and <c>Where</c> exist so the C# compiler can translate query syntax.
/// The <see cref="AsyncMethodBuilderAttribute"/> lets a method be declared <c>async Durable&lt;T&gt;</c> and
/// <c>await</c> durables in a plain method body; see <see cref="DurableMethodBuilder{T}"/>.
/// </summary>
[AsyncMethodBuilder(typeof(DurableMethodBuilder<>))]
public sealed class Durable<T>
{
    private readonly Func<WorkflowContext, Task<Outcome<T>>> _run;

    /// <summary>Wraps a function that yields a value. An exception it throws is captured as a failure.</summary>
    public Durable(Func<WorkflowContext, Task<T>> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = async ctx =>
        {
            try { return Outcome<T>.Success(await run(ctx).ConfigureAwait(false)); }
            catch (Exception ex) when (ctx.IsCapturable(ex)) { return ctx.Capture<T>(ex); }
        };
    }

    private Durable(Func<WorkflowContext, Task<Outcome<T>>> run, bool _) => _run = run;

    /// <summary>Wraps a function that yields an outcome directly.</summary>
    public static Durable<T> FromOutcome(Func<WorkflowContext, Task<Outcome<T>>> run) => new(run, true);

    /// <summary>Runs the workflow against the given context. Called by the runner, not by workflow authors.</summary>
    public Task<Outcome<T>> RunAsync(WorkflowContext ctx) => _run(ctx);

    /// <summary>Enables <c>await</c> inside an <c>async Durable&lt;T&gt;</c> method. See <see cref="DurableAwaiter{T}"/>.</summary>
    public DurableAwaiter<T> GetAwaiter() => new(this);

    // ---------------------------------------------------------------- query operators

    /// <summary>Pure projection. Enables <c>select</c> and <c>let</c>.</summary>
    public Durable<TResult> Select<TResult>(Func<T, TResult> selector) => FromOutcome<TResult>(async ctx =>
    {
        var t = await _run(ctx).ConfigureAwait(false);
        if (t.IsFailure) return t.As<TResult>();
        try { return Outcome<TResult>.Success(selector(t.Value)); }
        catch (Exception ex) when (ctx.IsCapturable(ex)) { return ctx.Capture<TResult>(ex); }
    });

    /// <summary>Sequential composition. Enables a second <c>from</c> clause.</summary>
    public Durable<TResult> SelectMany<TNext, TResult>(Func<T, Durable<TNext>> bind, Func<T, TNext, TResult> project) => FromOutcome<TResult>(async ctx =>
    {
        var t = await _run(ctx).ConfigureAwait(false);
        if (t.IsFailure) return t.As<TResult>();
        Durable<TNext> next;
        try { next = bind(t.Value); }
        catch (Exception ex) when (ctx.IsCapturable(ex)) { return ctx.Capture<TResult>(ex); }
        var u = await next.RunAsync(ctx).ConfigureAwait(false);
        if (u.IsFailure) return u.As<TResult>();
        try { return Outcome<TResult>.Success(project(t.Value, u.Value)); }
        catch (Exception ex) when (ctx.IsCapturable(ex)) { return ctx.Capture<TResult>(ex); }
    });

    /// <summary>Sequential composition without a projection.</summary>
    public Durable<TNext> SelectMany<TNext>(Func<T, Durable<TNext>> bind) => SelectMany(bind, (_, u) => u);

    /// <summary>
    /// Enables a <c>where</c> clause as a guard: when the predicate is false the run fails with a terminal
    /// validation error. Prefer <see cref="Ensure(Func{T, bool}, string)"/> when you want a specific message.
    /// </summary>
    public Durable<T> Where(Func<T, bool> predicate) =>
        Ensure(predicate, _ => $"guard failed");

    // ---------------------------------------------------------------- chaining

    /// <summary>Method-chain alias for <see cref="SelectMany{TNext}(Func{T, Durable{TNext}})"/>.</summary>
    public Durable<TNext> Then<TNext>(Func<T, Durable<TNext>> next) => SelectMany(next);

    /// <summary>Runs <paramref name="next"/> after this workflow and discards this workflow's result.</summary>
    public Durable<TNext> Then<TNext>(Durable<TNext> next) => SelectMany(_ => next);

    /// <summary>
    /// Runs a recorded step that uses this value and yields the value again, so no <c>from _ in</c> discard
    /// is needed for notify-style steps.
    /// </summary>
    public Durable<T> Then(string name, Func<T, CancellationToken, Task> work, StepOptions? options = null) =>
        SelectMany(value => Durable.Step(name, ct => work(value, ct), options), (value, _) => value);

    public Durable<T> Then(string name, Func<T, Task> work, StepOptions? options = null) =>
        Then(name, (value, _) => work(value), options);

    public Durable<T> Then(string name, Action<T> work, StepOptions? options = null) =>
        Then(name, (value, _) => { work(value); return Task.CompletedTask; }, options);

    // ---------------------------------------------------------------- validation

    /// <summary>
    /// Checks the value after the step ran or replayed. When the predicate is false the run fails with a
    /// terminal validation error that names the step. Nothing is written to the log, because the check
    /// re-evaluates deterministically on replayed values.
    /// </summary>
    public Durable<T> Ensure(Func<T, bool> predicate, string message) => Ensure(predicate, _ => message);

    /// <summary>Like <see cref="Ensure(Func{T, bool}, string)"/>, with a message built from the failing value.</summary>
    public Durable<T> Ensure(Func<T, bool> predicate, Func<T, string> message) => FromOutcome<T>(async ctx =>
    {
        var t = await _run(ctx).ConfigureAwait(false);
        if (t.IsFailure) return t;
        bool ok;
        try { ok = predicate(t.Value); }
        catch (Exception ex) when (ctx.IsCapturable(ex)) { return ctx.Capture<T>(ex); }
        if (ok) return t;
        var error = WorkflowError.Create(WorkflowError.ValidationCode, message(t.Value), terminal: true, stepName: ctx.LastStepName);
        ctx.Events.Raise(DurableEventKind.StepFailed, ctx.InstanceKey, ctx.LastStepName, detail: error.ToString());
        return Outcome<T>.Failure(error);
    });

    /// <summary>Checks the value with a function that returns a <see cref="ValidationResult"/>.</summary>
    public Durable<T> Ensure(Func<T, ValidationResult> validate) => FromOutcome<T>(async ctx =>
    {
        var t = await _run(ctx).ConfigureAwait(false);
        if (t.IsFailure) return t;
        ValidationResult result;
        try { result = validate(t.Value); }
        catch (Exception ex) when (ctx.IsCapturable(ex)) { return ctx.Capture<T>(ex); }
        if (result.IsValid) return t;
        return Outcome<T>.Failure(result.ToError() with { StepName = ctx.LastStepName });
    });

    // ---------------------------------------------------------------- failure handling

    /// <summary>
    /// When this workflow fails, runs <paramref name="handler"/> with the error and yields its result instead.
    /// The failure is recorded at its position, so a replay follows the same recovery path.
    /// </summary>
    public Durable<T> Recover(Func<WorkflowError, Durable<T>> handler) => FromOutcome<T>(async ctx =>
    {
        var t = await _run(ctx).ConfigureAwait(false);
        if (t.IsSuccess) return t;
        await ctx.RecordFailureAsync(t.Error!).ConfigureAwait(false);
        using (ctx.PushScope("recover"))
            return await handler(t.Error!).RunAsync(ctx).ConfigureAwait(false);
    });

    /// <summary>Recovers only failures with the given code; other failures pass through.</summary>
    public Durable<T> Recover(string code, Func<WorkflowError, Durable<T>> handler) =>
        Recover(error => error.Code == code ? handler(error) : Durable.Fail<T>(error));

    /// <summary>
    /// When this workflow fails, runs <paramref name="compensation"/> and then fails with the original error.
    /// Whether compensation ran is recorded, so a replay does not compensate twice.
    /// </summary>
    public Durable<T> Compensate(Durable<Unit> compensation) => FromOutcome<T>(async ctx =>
    {
        var t = await _run(ctx).ConfigureAwait(false);
        if (t.IsSuccess) return t;
        await ctx.RecordFailureAsync(t.Error!).ConfigureAwait(false);
        Outcome<Unit> c;
        using (ctx.PushScope("compensate"))
            c = await compensation.RunAsync(ctx).ConfigureAwait(false);
        return c.IsFailure ? c.As<T>() : t;
    });

    private static Durable<TResult> FromOutcome<TResult>(Func<WorkflowContext, Task<Outcome<TResult>>> run) => Durable<TResult>.FromOutcome(run);
}
