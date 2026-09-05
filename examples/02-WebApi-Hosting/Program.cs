// Example 02: DurableLinq inside an ASP.NET Core app with dependency injection. See README.md.

using System.Threading.Channels;
using DurableLinq.Hosting;
using WebApiHosting.Endpoints;
using WebApiHosting.Messaging;
using WebApiHosting.Models;
using WebApiHosting.Services;
using WebApiHosting.Workflows;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5002");

builder.Services.AddScoped<IPayments, FakePayments>();
builder.Services.AddScoped<IMail, FakeMail>();

var stateDirectory = builder.Configuration["Durable:StateDirectory"] ?? "./workflow-state";

builder.Services.AddDurable(d =>
{
    d.UseFileStore(stateDirectory);
    d.Namespace = "example-02";
    d.LeaseTtl = TimeSpan.FromSeconds(10);
    d.RecoveryInterval = TimeSpan.FromSeconds(5);
    // WORKFLOW_STYLE=async registers the same workflow written as an async method instead of a query.
    if (WorkflowStyle.IsAsync)
        d.AddWorkflow<CheckoutAsyncWorkflow, OrderRequest, Receipt>();
    else
        d.AddWorkflow<CheckoutWorkflow, OrderRequest, Receipt>();
});

// A tiny in-process queue standing in for a real message broker.
builder.Services.AddSingleton(Channel.CreateUnbounded<OrderPlaced>());
builder.Services.AddHostedService<CheckoutConsumer>();

var app = builder.Build();

app.MapOrderEndpoints();

app.Run();

// Makes the entry point reachable for WebApplicationFactory<Program> in tests.
public partial class Program { }
