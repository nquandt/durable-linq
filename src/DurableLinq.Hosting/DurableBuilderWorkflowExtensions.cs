using Microsoft.Extensions.DependencyInjection;

namespace DurableLinq.Hosting;

public static class DurableBuilderWorkflowExtensions
{
    /// <summary>
    /// Registers a class-based workflow. The class is activated once per run from that run's DI scope,
    /// so constructor dependencies can be scoped services.
    /// </summary>
    public static DurableBuilder AddWorkflow<TWorkflow, TIn, TOut>(this DurableBuilder durable)
        where TWorkflow : class, IWorkflow<TIn, TOut>
    {
        // scoped: Validate and Build in one run share the instance, and scoped dependencies resolve correctly
        durable.Services.AddScoped<TWorkflow>();
        return durable.Define(WorkflowDefinitionClassExtensions.For<TWorkflow, TIn, TOut>(services => services.Get<TWorkflow>()));
    }
}
