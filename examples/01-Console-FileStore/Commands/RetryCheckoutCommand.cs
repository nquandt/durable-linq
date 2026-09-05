using ConsoleFileStore.Models;
using DurableLinq;

namespace ConsoleFileStore.Commands;

/// <summary>Moves a Failed instance back to Running, then runs it. Completed steps replay; the failed one re-executes.</summary>
public sealed class RetryCheckoutCommand(IWorkflowRunner runner, WorkflowDefinition<Order, Receipt> checkout, RunCheckoutCommand run) : ICommand
{
    public CommandKind Kind => CommandKind.Retry;

    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        var key = runner.KeyFor(checkout, SampleOrders.Default.Id);
        if (await runner.GetStatusAsync(key, ct) == WorkflowStatus.Failed)
        {
            var reset = await runner.RetryFailedAsync(key, ct);
            Console.WriteLine(reset ? $"Reset {key} to Running" : $"Could not reset {key} (not failed, or the error is terminal)");
        }
        return await run.ExecuteAsync(ct);
    }
}
