using System.Text.Json;

namespace DurableLinq;

/// <summary>
/// The result of a step or a workflow: a value, or a <see cref="WorkflowError"/>. Failures travel through the
/// query as values; the engine never uses exceptions for control flow.
/// </summary>
public readonly struct Outcome<T>
{
    private readonly T _value;

    private Outcome(T value, WorkflowError? error)
    {
        _value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;
    public bool IsFailure => Error is not null;
    public WorkflowError? Error { get; }

    /// <summary>The value. Throws when the outcome is a failure; check <see cref="IsSuccess"/> first.</summary>
    public T Value => Error is null ? _value : throw new InvalidOperationException($"Outcome is a failure: {Error.Code}: {Error.Message}");

    public static Outcome<T> Success(T value) => new(value, null);
    public static Outcome<T> Failure(WorkflowError error) => new(default!, error ?? throw new ArgumentNullException(nameof(error)));

    public Outcome<TResult> Map<TResult>(Func<T, TResult> f) => Error is null ? Outcome<TResult>.Success(f(_value)) : Outcome<TResult>.Failure(Error);

    /// <summary>Re-types a failure. Only valid when <see cref="IsFailure"/>.</summary>
    public Outcome<TResult> As<TResult>() => Outcome<TResult>.Failure(Error ?? throw new InvalidOperationException("Only a failure can be re-typed."));

    public static implicit operator Outcome<T>(T value) => Success(value);
    public static implicit operator Outcome<T>(WorkflowError error) => Failure(error);

    public override string ToString() => Error is null ? $"Success({_value})" : $"Failure({Error.Code}: {Error.Message})";
}

/// <summary>Factories for <see cref="Outcome{T}"/>, for step bodies that return a designed failure instead of throwing.</summary>
public static class Outcome
{
    public static Outcome<T> Ok<T>(T value) => Outcome<T>.Success(value);

    public static Outcome<T> Fail<T>(WorkflowError error) => Outcome<T>.Failure(error);

    /// <summary>A designed failure. <paramref name="terminal"/> means a retry cannot fix it.</summary>
    public static Outcome<T> Fail<T>(string code, string message, bool terminal = false, object? detail = null) =>
        Outcome<T>.Failure(WorkflowError.Create(code, message, terminal, detail));

    /// <summary>A failure to carry through a step, without the result type in hand.</summary>
    public static WorkflowError Error(string code, string message, bool terminal = false, object? detail = null) =>
        WorkflowError.Create(code, message, terminal, detail);
}

/// <summary>
/// Lets a user-defined exception tell the engine how to classify itself when it is captured at a step boundary.
/// Implement it on your own exception types; the hierarchy stays yours.
/// </summary>
public interface IWorkflowFailure
{
    /// <summary>Stable identifier such as <c>payments.declined</c>. Used by <c>Recover</c>, dashboards, and metrics.</summary>
    string Code { get; }

    /// <summary>True when a retry cannot fix it.</summary>
    bool IsTerminal { get; }

    /// <summary>Extra data serialized into the error for callers. May be null.</summary>
    object? Detail { get; }
}

/// <summary>
/// Maps exceptions captured at step boundaries to errors. Register one to classify exceptions from libraries
/// you do not control. Return null to fall back to the default classification.
/// </summary>
public interface IFailureClassifier
{
    WorkflowError? Classify(Exception exception);
}

/// <summary>Default classification: <see cref="IWorkflowFailure"/> when implemented, otherwise a retryable error keyed by type name.</summary>
public sealed class DefaultFailureClassifier : IFailureClassifier
{
    public static readonly DefaultFailureClassifier Instance = new();

    public WorkflowError? Classify(Exception exception) => null;
}

/// <summary>Result of input validation before an instance is created.</summary>
public sealed record ValidationResult
{
    public static readonly ValidationResult Valid = new(Array.Empty<string>());

    public ValidationResult(IReadOnlyList<string> errors) => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Invalid(params string[] errors) => new(errors);

    /// <summary>Collects the messages of every check whose condition is false.</summary>
    public static ValidationResult Check(params (bool Ok, string Message)[] checks) =>
        new(checks.Where(c => !c.Ok).Select(c => c.Message).ToArray());

    public WorkflowError ToError() => WorkflowError.Create(WorkflowError.ValidationCode, string.Join("; ", Errors), terminal: true, detail: Errors);

    public override string ToString() => IsValid ? "valid" : string.Join("; ", Errors);
}
