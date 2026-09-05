using DurableLinq.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DurableLinq.Hosting;

/// <summary>Configuration surface for <see cref="ServiceCollectionExtensions.AddDurable"/>.</summary>
public sealed class DurableBuilder
{
    internal DurableBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>Prefix on every instance key. Defaults to the entry assembly name.</summary>
    public string? Namespace { get; set; }

    /// <summary>How long a lease lasts without renewal.</summary>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Identifies this host in leases. Defaults to machine name plus process id.</summary>
    public string? Owner { get; set; }

    /// <summary>Custom serializer. Defaults to System.Text.Json.</summary>
    public IWorkflowSerializer? Serializer { get; set; }

    /// <summary>
    /// Maps exceptions captured at step boundaries to errors. When null, an <see cref="IFailureClassifier"/>
    /// registered in the container is used, else the default classification.
    /// </summary>
    public IFailureClassifier? Classifier { get; set; }

    /// <summary>Whether the recovery sweeper runs. Defaults to <c>true</c>.</summary>
    public bool EnableRecovery { get; set; } = true;

    /// <summary>How often the sweeper looks for instances with an expired lease.</summary>
    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Whether runner events are written to <c>ILogger</c> and <c>System.Diagnostics.Metrics</c>. Defaults to <c>true</c>.</summary>
    public bool EnableObservability { get; set; } = true;

    internal Func<IServiceProvider, IWorkflowStore>? StoreFactory { get; private set; }

    /// <summary>Registers the store. Store packages call this from their <c>UseXxx</c> extension methods.</summary>
    public DurableBuilder UseStore(Func<IServiceProvider, IWorkflowStore> factory)
    {
        StoreFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public DurableBuilder UseStore(IWorkflowStore store) => UseStore(_ => store);

    public DurableBuilder UseInMemoryStore() => UseStore(sp => new InMemoryWorkflowStore(sp.GetService<TimeProvider>()));

    public DurableBuilder UseFileStore(string rootDirectory) => UseStore(sp => new FileWorkflowStore(rootDirectory, sp.GetService<TimeProvider>()));

    /// <summary>Registers a workflow. The definition is available by injection as <c>WorkflowDefinition&lt;TIn, TOut&gt;</c>.</summary>
    public DurableBuilder Define<TIn, TOut>(string name, int version, Func<TIn, WorkflowServices, Durable<TOut>> build) =>
        Define(WorkflowDefinition.Create(name, version, build));

    public DurableBuilder Define<TIn, TOut>(string name, int version, Func<TIn, Durable<TOut>> build) =>
        Define(WorkflowDefinition.Create(name, version, build));

    public DurableBuilder Define<TIn, TOut>(WorkflowDefinition<TIn, TOut> definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Services.AddSingleton(definition);
        Services.AddSingleton<WorkflowDefinition>(definition);
        return this;
    }
}

/// <summary>Looks up registered definitions by name and version. Used by the recovery sweeper and the dashboard.</summary>
public interface IWorkflowDefinitionRegistry
{
    IReadOnlyCollection<WorkflowDefinition> All { get; }
    WorkflowDefinition? Find(string name, int version);
}

internal sealed class WorkflowDefinitionRegistry : IWorkflowDefinitionRegistry
{
    private readonly Dictionary<(string, int), WorkflowDefinition> _byKey;

    public WorkflowDefinitionRegistry(IEnumerable<WorkflowDefinition> definitions)
    {
        _byKey = new Dictionary<(string, int), WorkflowDefinition>();
        foreach (var d in definitions)
        {
            if (!_byKey.TryAdd((d.Name, d.Version), d))
                throw new InvalidOperationException($"Workflow '{d}' is defined twice.");
        }
    }

    public IReadOnlyCollection<WorkflowDefinition> All => _byKey.Values;
    public WorkflowDefinition? Find(string name, int version) => _byKey.GetValueOrDefault((name, version));
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the store, the runner, all definitions, and the hosted services.
    /// Call <c>UseFileStore</c> or another <c>UseXxx</c> inside <paramref name="configure"/>.
    /// </summary>
    public static IServiceCollection AddDurable(this IServiceCollection services, Action<DurableBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DurableBuilder(services);
        configure(builder);

        var storeFactory = builder.StoreFactory
            ?? throw new InvalidOperationException("No store configured. Call UseFileStore(...), UseInMemoryStore(), or a store package's UseXxx(...) inside AddDurable.");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DurableEventSource>(_ => new DurableEventSource());
        services.TryAddSingleton(sp => storeFactory(sp));
        services.TryAddSingleton(sp => new WorkflowRunnerOptions
        {
            Namespace = builder.Namespace ?? WorkflowRunnerOptions.DefaultNamespace(),
            LeaseTtl = builder.LeaseTtl,
            Owner = builder.Owner ?? $"{Environment.MachineName}:{Environment.ProcessId}",
            Serializer = builder.Serializer ?? JsonWorkflowSerializer.Instance,
            Events = sp.GetRequiredService<DurableEventSource>(),
            Time = sp.GetRequiredService<TimeProvider>(),
            Classifier = builder.Classifier ?? sp.GetService<IFailureClassifier>() ?? DefaultFailureClassifier.Instance,
        });
        services.TryAddSingleton<IWorkflowDefinitionRegistry, WorkflowDefinitionRegistry>();
        services.TryAddSingleton<IWorkflowRunner, ScopedWorkflowRunner>();

        if (builder.EnableRecovery)
        {
            services.AddSingleton(new RecoverySweeperOptions { Interval = builder.RecoveryInterval });
            services.AddHostedService<RecoverySweeper>();
        }
        if (builder.EnableObservability)
        {
            services.AddHostedService<DurableObservability>();
        }
        return services;
    }
}
