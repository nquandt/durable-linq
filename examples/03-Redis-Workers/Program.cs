// Run instructions: see README.md.

using DurableLinq.Hosting;
using RedisWorkers.Messaging;
using RedisWorkers.Models;
using RedisWorkers.Workflows;

var builder = Host.CreateApplicationBuilder(args);
var redis = Environment.GetEnvironmentVariable("REDIS") ?? "localhost:6379";
var workerName = Environment.GetEnvironmentVariable("WORKER_NAME") ?? Environment.MachineName;

builder.Services.AddSingleton(new WorkerOptions(workerName));

builder.Services.AddDurable(d =>
{
    d.UseRedis(redis);
    d.Namespace = "example-03";
    d.Owner = workerName;
    d.LeaseTtl = TimeSpan.FromSeconds(10);
    d.RecoveryInterval = TimeSpan.FromSeconds(5);
    // WORKFLOW_STYLE=async registers the same workflow written as an async method instead of a query.
    if (WorkflowStyle.IsAsync)
        d.AddWorkflow<ShipmentAsyncWorkflow, Shipment, string>();
    else
        d.AddWorkflow<ShipmentWorkflow, Shipment, string>();
});
builder.Services.AddHostedService<Producer>();

await builder.Build().RunAsync();
