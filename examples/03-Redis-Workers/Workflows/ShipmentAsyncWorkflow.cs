using DurableLinq;
using RedisWorkers.Models;

namespace RedisWorkers.Workflows;

/// <summary>
/// The same shipment as <see cref="ShipmentWorkflow"/>, written as an <c>async Durable&lt;T&gt;</c> method.
/// The pick loop is a plain <c>for</c>. The log is identical to the query version's.
/// </summary>
public sealed class ShipmentAsyncWorkflow(WorkerOptions worker) : IWorkflow<Shipment, string>
{
    public static string Name => "ship";
    public static int Version => 1;

    public async Durable<string> Handle(Shipment s)
    {
        var picked = new List<string>();
        for (var i = 0; i < s.Parcels; i++)
            picked.Add(await Durable.Step($"pick[{i}]", async () => { await Task.Delay(1500); return $"{s.Id}-{i}@{worker.Name}"; }));

        var label = await Durable.Step("label", async () => { await Task.Delay(1000); return $"LBL-{s.Id}@{worker.Name}"; });

        await Durable.Sleep(TimeSpan.FromSeconds(3), "await-carrier");

        await Durable.Step("handoff", async () => { await Task.Delay(500); return true; });

        return $"{label} parcels={string.Join(",", picked)}";
    }
}
