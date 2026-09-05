using Microsoft.Extensions.DependencyInjection;

namespace DurableLinq.Hosting;

/// <summary>
/// The runner registered by <c>AddDurable</c>. Each run gets its own DI scope, so workflow
/// factories can resolve scoped services (a DbContext, for example) through <see cref="WorkflowServices"/>.
/// </summary>
public sealed class ScopedWorkflowRunner : IWorkflowRunner
{
    private readonly IWorkflowStore _store;
    private readonly WorkflowRunnerOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly WorkflowRunner _unscoped;

    public ScopedWorkflowRunner(IWorkflowStore store, WorkflowRunnerOptions options, IServiceScopeFactory scopes)
    {
        _store = store;
        _options = options;
        _scopes = scopes;
        _unscoped = new WorkflowRunner(store, options);
    }

    public string Namespace => _options.Namespace;
    public IWorkflowStore Store => _store;
    public WorkflowRunnerOptions Options => _options;

    public string KeyFor(WorkflowDefinition definition, string instanceId) => _unscoped.KeyFor(definition, instanceId);

    public async Task<RunResult<TOut>> RunAsync<TIn, TOut>(WorkflowDefinition<TIn, TOut> definition, string instanceId, TIn input, CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await new WorkflowRunner(_store, _options, scope.ServiceProvider).RunAsync(definition, instanceId, input, ct).ConfigureAwait(false);
    }

    public async Task<RunResult<TOut>> ResumeAsync<TIn, TOut>(WorkflowDefinition<TIn, TOut> definition, string instanceKey, CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await new WorkflowRunner(_store, _options, scope.ServiceProvider).ResumeAsync(definition, instanceKey, ct).ConfigureAwait(false);
    }

    public Task SignalAsync<T>(string instanceKey, string signalName, T payload, CancellationToken ct = default) =>
        _unscoped.SignalAsync(instanceKey, signalName, payload, ct);

    public Task<InstanceHeader?> GetHeaderAsync(string instanceKey, CancellationToken ct = default) => _unscoped.GetHeaderAsync(instanceKey, ct);
    public Task<WorkflowStatus?> GetStatusAsync(string instanceKey, CancellationToken ct = default) => _unscoped.GetStatusAsync(instanceKey, ct);
    public Task<TOut?> GetResultAsync<TOut>(string instanceKey, CancellationToken ct = default) => _unscoped.GetResultAsync<TOut>(instanceKey, ct);
    public Task<WorkflowError?> GetErrorAsync(string instanceKey, CancellationToken ct = default) => _unscoped.GetErrorAsync(instanceKey, ct);
    public Task<bool> RetryFailedAsync(string instanceKey, CancellationToken ct = default) => _unscoped.RetryFailedAsync(instanceKey, ct);
}
