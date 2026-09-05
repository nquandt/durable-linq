using DurableLinq;
using DurableLinq.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RedisWorkers.Models;

namespace RedisWorkers.Messaging;

/// <summary>Creates a new shipment every few seconds and runs it. In production this is your queue consumer.</summary>
public sealed class Producer(IWorkflowRunner runner, WorkflowDefinition<Shipment, string> ship, WorkerOptions worker, ILogger<Producer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var n = 0;
        while (!ct.IsCancellationRequested)
        {
            var shipment = new Shipment($"{worker.Name}-{DateTime.UtcNow:HHmmss}-{n++}", "Oslo", Random.Shared.Next(1, 4));
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await runner.RunAsync(ship, shipment.Id, shipment, ct);
                    switch (result.Status)
                    {
                        case RunStatus.Completed:
                            log.LogInformation("{Id} => {Result}", shipment.Id, result.Value);
                            break;
                        case RunStatus.Busy:
                            log.LogInformation("{Id} is leased by {Owner}", shipment.Id, result.LeaseOwner);
                            break;
                        case RunStatus.Failed:
                            log.LogError("{Id} failed: {Error}", shipment.Id, result.Error);
                            break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { log.LogError(e, "{Id} failed", shipment.Id); }
            }, ct);
            try { await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch (OperationCanceledException) { }
        }
    }
}
