using System.Diagnostics;
using DurableLinq;
using DurableLinq.Examples.Tests.Fixtures;
using DurableLinq.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RedisWorkers.Models;
using RedisWorkers.Workflows;
using Xunit;

namespace DurableLinq.Examples.Tests;

/// <summary>
/// Drives example 03's shipment workflow directly (bypassing the Producer) against two hosts sharing one
/// Redis-backed store, to exercise leasing and recovery the way two worker processes would.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class Example03Tests : IDisposable
{
    private const string Namespace = "example-03-tests";
    private readonly RedisFixture _fixture;
    private readonly IHost? _w1;
    private readonly IHost? _w2;

    public Example03Tests(RedisFixture fixture)
    {
        _fixture = fixture;
        if (_fixture.SkipReason is not null) return;

        _w1 = BuildHost("w1");
        _w2 = BuildHost("w2");
    }

    public void Dispose()
    {
        _w1?.Dispose();
        _w2?.Dispose();
    }

    private IHost BuildHost(string workerName)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDurable(d =>
        {
            d.UseRedis(_fixture.ConnectionString!);
            d.Namespace = Namespace;
            d.Owner = workerName;
            d.LeaseTtl = TimeSpan.FromSeconds(5);
            d.EnableRecovery = false;
            d.Services.AddSingleton(new WorkerOptions(workerName));
            d.AddWorkflow<ShipmentWorkflow, Shipment, string>();
        });
        return builder.Build();
    }

    private static WorkflowDefinition<Shipment, string> Def(IHost host) =>
        host.Services.GetRequiredService<WorkflowDefinition<Shipment, string>>();

    [Fact]
    public async Task Concurrent_runs_for_the_same_shipment_execute_each_step_once()
    {
        if (_fixture.SkipReason is not null) { return; }

        var shipment = new Shipment("s-1", "Denver", 2);
        var r1 = _w1!.Services.GetRequiredService<IWorkflowRunner>();
        var r2 = _w2!.Services.GetRequiredService<IWorkflowRunner>();
        var def1 = Def(_w1);
        var def2 = Def(_w2);

        var task1 = r1.RunAsync(def1, shipment.Id, shipment);
        var task2 = r2.RunAsync(def2, shipment.Id, shipment);
        var results = await Task.WhenAll(task1, task2);

        var busy = results.Count(r => r.IsBusy);
        var succeeded = results.Where(r => r.IsCompleted).Select(r => r.Value).ToList();

        Assert.True(busy == 1 || succeeded.Count == 2 && succeeded[0] == succeeded[1],
            "Expected exactly one Busy result, or both calls to agree on the same result.");

        var store = _w1.Services.GetRequiredService<IWorkflowStore>();
        var key = r1.KeyFor(def1, shipment.Id);
        var steps = await store.ListStepsAsync(key);
        Assert.Equal(shipment.Parcels + 3, steps.Count);
        Assert.Equal(steps.Select(s => s.Seq).Distinct().Count(), steps.Count);
    }

    [Fact]
    public async Task Second_host_short_circuits_a_completed_shipment()
    {
        if (_fixture.SkipReason is not null) { return; }

        var shipment = new Shipment("s-2", "Austin", 1);
        var r1 = _w1!.Services.GetRequiredService<IWorkflowRunner>();
        var r2 = _w2!.Services.GetRequiredService<IWorkflowRunner>();

        await r1.RunAsync(Def(_w1), shipment.Id, shipment);

        var sw = Stopwatch.StartNew();
        var result = await r2.RunAsync(Def(_w2), shipment.Id, shipment);
        sw.Stop();

        Assert.NotNull(result);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"Short-circuit took {sw.Elapsed}.");
    }

    [Fact]
    public async Task Sweeper_recovers_an_instance_with_an_expired_lease()
    {
        if (_fixture.SkipReason is not null) { return; }

        var shipment = new Shipment("s-3", "Reno", 1);
        var store = _w1!.Services.GetRequiredService<IWorkflowStore>();
        var runner = _w1.Services.GetRequiredService<IWorkflowRunner>();
        var key = runner.KeyFor(Def(_w1), shipment.Id);
        var now = DateTimeOffset.UtcNow;

        var header = new InstanceHeader
        {
            Key = key,
            Namespace = Namespace,
            WorkflowName = "ship",
            Version = 1,
            InstanceId = shipment.Id,
            Status = WorkflowStatus.Running,
            Input = JsonWorkflowSerializer.Instance.Serialize(shipment),
            LeaseOwner = "some-dead-worker",
            LeaseToken = Guid.NewGuid(),
            LeaseExpires = now.AddMinutes(-1),
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-2),
        };
        Assert.True(await store.TryCreateAsync(header));
        await store.AppendStepAsync(key, new StepEntry(0, "pick[0]", JsonWorkflowSerializer.Instance.Serialize($"{shipment.Id}-0@w1"), now.AddMinutes(-2)));

        var registry = _w2!.Services.GetRequiredService<IWorkflowDefinitionRegistry>();
        var sweeperRunner = _w2.Services.GetRequiredService<IWorkflowRunner>();
        var loggerFactory = _w2.Services.GetRequiredService<ILoggerFactory>();
        var time = _w2.Services.GetRequiredService<TimeProvider>();
        var sweeper = new RecoverySweeper(store, sweeperRunner, registry, new RecoverySweeperOptions(), time, loggerFactory.CreateLogger<RecoverySweeper>());

        var resumed = await sweeper.SweepOnceAsync();
        Assert.Equal(1, resumed);

        var finalHeader = await store.GetHeaderAsync(key);
        Assert.NotNull(finalHeader);
        Assert.Equal(WorkflowStatus.Completed, finalHeader!.Status);
    }

}
