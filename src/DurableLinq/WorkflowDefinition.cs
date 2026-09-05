namespace DurableLinq;

/// <summary>
/// Resolves dependencies for a workflow at build time. Wraps an optional <see cref="IServiceProvider"/>,
/// so workflow functions stay static and testable while still using a container when one exists.
/// </summary>
public sealed class WorkflowServices
{
    public static readonly WorkflowServices Empty = new(null);

    private readonly IServiceProvider? _provider;

    public WorkflowServices(IServiceProvider? provider) => _provider = provider;

    public IServiceProvider? Provider => _provider;

    /// <summary>Resolves a required service. Throws when no provider or no registration exists.</summary>
    public T Get<T>() where T : notnull
    {
        if (_provider is null)
            throw new InvalidOperationException($"No service provider is available to resolve {typeof(T).Name}. Pass one to the runner, or use the overload that takes no services.");
        return (T?)_provider.GetService(typeof(T))
               ?? throw new InvalidOperationException($"No service of type {typeof(T).FullName} is registered.");
    }

    public T? GetOptional<T>() where T : class => (T?)_provider?.GetService(typeof(T));
}

/// <summary>Non-generic view of a definition, used by registries and the recovery sweeper.</summary>
public abstract class WorkflowDefinition
{
    protected WorkflowDefinition(string name, int version)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        if (name.Contains('/')) throw new ArgumentException("Name must not contain '/'.", nameof(name));
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        Name = name;
        Version = version;
    }

    public string Name { get; }
    public int Version { get; }
    public abstract Type InputType { get; }
    public abstract Type OutputType { get; }

    /// <summary>Resumes an existing instance by key, using the stored input.</summary>
    public abstract Task<RunResult> ResumeAsync(IWorkflowRunner runner, string instanceKey, CancellationToken ct = default);

    public static WorkflowDefinition<TIn, TOut> Create<TIn, TOut>(string name, int version, Func<TIn, Durable<TOut>> build) =>
        new(name, version, (input, _) => build(input));

    public static WorkflowDefinition<TIn, TOut> Create<TIn, TOut>(string name, int version, Func<TIn, WorkflowServices, Durable<TOut>> build) =>
        new(name, version, build);

    public override string ToString() => $"{Name} v{Version}";
}

/// <summary>A named, versioned factory from input to a <see cref="Durable{T}"/>.</summary>
public sealed class WorkflowDefinition<TIn, TOut> : WorkflowDefinition
{
    private readonly Func<TIn, WorkflowServices, Durable<TOut>> _build;

    public WorkflowDefinition(string name, int version, Func<TIn, WorkflowServices, Durable<TOut>> build) : base(name, version)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
    }

    public override Type InputType => typeof(TIn);
    public override Type OutputType => typeof(TOut);

    public Durable<TOut> Handle(TIn input, WorkflowServices services) => _build(input, services);

    public override async Task<RunResult> ResumeAsync(IWorkflowRunner runner, string instanceKey, CancellationToken ct = default) =>
        await runner.ResumeAsync(this, instanceKey, ct).ConfigureAwait(false);
}
