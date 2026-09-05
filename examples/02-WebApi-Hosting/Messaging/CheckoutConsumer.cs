using System.Threading.Channels;
using DurableLinq;
using DurableLinq.Hosting;
using WebApiHosting.Models;
using WebApiHosting.Workflows;

namespace WebApiHosting.Messaging;

/// <summary>Pulls messages from the queue and runs the workflow. This is the only place RunAsync is called.</summary>
public sealed class CheckoutConsumer(Channel<OrderPlaced> queue, IWorkflowRunner runner,
    WorkflowDefinition<OrderRequest, Receipt> checkout, ILogger<CheckoutConsumer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var msg in queue.Reader.ReadAllAsync(stoppingToken))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await runner.RunAsync(checkout, msg.OrderId, msg.Request, stoppingToken);
                    switch (result.Status)
                    {
                        case RunStatus.Completed:
                            log.LogInformation("Order {Id} done: {Auth}", msg.OrderId, result.Value.AuthCode);
                            break;
                        case RunStatus.Busy:
                            await queue.Writer.WriteAsync(msg, stoppingToken); // "nack": try later
                            break;
                        case RunStatus.Failed:
                            log.LogWarning("Order {Id} failed: {Error}", msg.OrderId, result.Error);
                            break;
                    }
                }
                catch (OperationCanceledException) { }
            }, stoppingToken);
        }
    }
}
