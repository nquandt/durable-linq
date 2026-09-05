namespace DurableLinq;

/// <summary>
/// Class-based workflow. Take dependencies through the constructor, build the query expression in
/// <see cref="Build"/>. The hosting package activates one instance per run inside that run's DI scope.
/// </summary>
public interface IWorkflow<TIn, TOut>
{
    static abstract string Name { get; }
    static abstract int Version { get; }

    /// <summary>
    /// Builds the run for the given input. Check the input inside the query or method body, with
    /// <c>Durable.Start(input).Ensure(...)</c> or a step's <c>.Ensure(...)</c>, the same as any other guard.
    /// A failing check ends the run as <see cref="RunStatus.Failed"/> with a recorded step.
    /// </summary>
    Durable<TOut> Handle(TIn input);
}

public static class WorkflowDefinitionClassExtensions
{
    /// <summary>
    /// Creates a definition for a workflow class. <paramref name="activate"/> builds the instance for a run;
    /// without a container, pass a lambda that news it up with the dependencies you want.
    /// </summary>
    public static WorkflowDefinition<TIn, TOut> For<TWorkflow, TIn, TOut>(Func<WorkflowServices, TWorkflow> activate)
        where TWorkflow : IWorkflow<TIn, TOut> =>
        new(TWorkflow.Name, TWorkflow.Version,
            build: (input, services) => activate(services).Handle(input));

    /// <summary>Creates a definition for a workflow class whose dependencies you already hold.</summary>
    public static WorkflowDefinition<TIn, TOut> For<TWorkflow, TIn, TOut>(TWorkflow instance)
        where TWorkflow : IWorkflow<TIn, TOut> =>
        For<TWorkflow, TIn, TOut>(_ => instance);
}
