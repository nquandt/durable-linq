// Example 01: a durable checkout in a console app. See README.md for the run instructions.

using ConsoleFileStore.Commands;
using ConsoleFileStore.Diagnostics;
using ConsoleFileStore.Models;
using ConsoleFileStore.Services;
using ConsoleFileStore.Workflows;
using DurableLinq.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var options = CommandLine.Parse(args);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ICustomerDb, FakeCustomerDb>();
builder.Services.AddSingleton<IWarehouse, FakeWarehouse>();
builder.Services.AddSingleton<IPayments, FakePayments>();
builder.Services.AddSingleton<IShipping, FakeShipping>();

builder.Services.AddDurable(durable =>
{
    durable.UseFileStore("./workflow-state");
    durable.Namespace = "example-01";
    durable.EnableRecovery = false;
    durable.EnableObservability = false;
    // WORKFLOW_STYLE=async registers the same workflow written as an async method instead of a query.
    // Both classes share a name and a version, so they read and write the same instances.
    if (WorkflowStyle.IsAsync)
        durable.AddWorkflow<CheckoutAsyncWorkflow, Order, Receipt>();
    else
        durable.AddWorkflow<CheckoutWorkflow, Order, Receipt>();
});

builder.Services.AddSingleton<ConsoleEventPrinter>();
builder.Services.AddCommands();

using var host = builder.Build();
host.Services.GetRequiredService<ConsoleEventPrinter>().Attach();

var command = host.Services.GetRequiredService<CommandDispatcher>().Resolve(options.Command);
return await command.ExecuteAsync();
