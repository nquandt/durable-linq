using ConsoleFileStore.Models;
using DurableLinq;

namespace ConsoleFileStore.Commands;

/// <summary>Deletes the stored instance so the demo can start from scratch.</summary>
public sealed class ResetCheckoutCommand(IWorkflowRunner runner, IWorkflowStore store, WorkflowDefinition<Order, Receipt> checkout) : ICommand
{
    public CommandKind Kind => CommandKind.Reset;

    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        var key = runner.KeyFor(checkout, SampleOrders.Default.Id);
        await store.DeleteAsync(key, ct);
        Console.WriteLine($"Deleted {key}");
        return 0;
    }
}
