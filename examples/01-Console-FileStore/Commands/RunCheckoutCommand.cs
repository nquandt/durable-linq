using ConsoleFileStore.Models;
using DurableLinq;

namespace ConsoleFileStore.Commands;

/// <summary>Runs the checkout workflow for the sample order. Re-running resumes or short-circuits.</summary>
public sealed class RunCheckoutCommand(IWorkflowRunner runner, WorkflowDefinition<Order, Receipt> checkout) : ICommand
{
    public CommandKind Kind => CommandKind.Run;

    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        var order = SampleOrders.Default;
        Console.WriteLine($"Running {runner.KeyFor(checkout, order.Id)}");
        var result = await runner.RunAsync(checkout, order.Id, order, ct);
        return Report(result);
    }

    internal static int Report<TOut>(RunResult<TOut> result)
    {
        switch (result.Status)
        {
            case RunStatus.Completed when result.Value is Receipt receipt:
                Console.WriteLine($"Receipt: auth={receipt.AuthCode} tracking={receipt.Tracking} total={receipt.Total}");
                return 0;
            case RunStatus.Completed:
                Console.WriteLine($"Completed: {result.Value}");
                return 0;
            case RunStatus.Failed:
                Console.WriteLine($"Failed at step '{result.Error!.StepName}': {result.Error.Message}");
                if (!result.Error.IsTerminal)
                    Console.WriteLine("Run `dotnet run -- --retry` to put it back to Running and resume.");
                return 1;
            case RunStatus.Busy:
                // Another live process holds this instance. A queue consumer would nack and let the message redeliver.
                Console.WriteLine($"Busy: {result.LeaseOwner} holds the lease until {result.LeaseExpires:T}. Try again shortly.");
                return 2;
            default:
                return 1;
        }
    }
}
