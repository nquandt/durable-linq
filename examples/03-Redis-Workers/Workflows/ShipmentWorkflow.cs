using DurableLinq;
using RedisWorkers.Models;

namespace RedisWorkers.Workflows;

/// <summary>
/// The shipment workflow. Dependencies arrive through the constructor; the hosting package creates one
/// instance per run from that run's DI scope.
/// </summary>
public sealed class ShipmentWorkflow(WorkerOptions worker) : IWorkflow<Shipment, string>
{
    public static string Name => "ship";
    public static int Version => 1;

    public Durable<string> Handle(Shipment s) =>
        from picked in Durable.ForEach(Enumerable.Range(0, s.Parcels), i =>
                          Durable.Step($"pick[{i}]", async () => { await Task.Delay(1500); return $"{s.Id}-{i}@{worker.Name}"; }))
        from label in Durable.Step("label", async () => { await Task.Delay(1000); return $"LBL-{s.Id}@{worker.Name}"; })
        from _ in Durable.Sleep(TimeSpan.FromSeconds(3), "await-carrier")
        from done in Durable.Step("handoff", async () => { await Task.Delay(500); return true; })
        select $"{label} parcels={string.Join(",", picked)}";
}
