// See README.md for run instructions and workflow behavior.

using DurableLinq.Dashboard;
using DurableLinq.Hosting;
using PostgresApprovals.Endpoints;
using PostgresApprovals.Models;
using PostgresApprovals.Services;
using PostgresApprovals.Workflows;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5004");
var cs = builder.Configuration["Durable:Postgres"]
         ?? Environment.GetEnvironmentVariable("POSTGRES")
         ?? "Host=localhost;Port=5432;Username=durable;Password=durable;Database=durable";

builder.Services.AddSingleton<INotifier, ConsoleNotifier>();

builder.Services.AddDurable(d =>
{
    d.UsePostgres(cs);
    d.Namespace = "example-04";
    // WORKFLOW_STYLE=async registers the same workflow written as an async method instead of a query.
    if (WorkflowStyle.IsAsync)
        d.AddWorkflow<ExpenseApprovalAsyncWorkflow, Expense, Decision>();
    else
        d.AddWorkflow<ExpenseApprovalWorkflow, Expense, Decision>();
});

var app = builder.Build();

app.MapExpenseEndpoints();
app.MapDurableDashboard("/durable");
app.Run();

// Makes the entry point reachable for WebApplicationFactory<Program> in tests.
public partial class Program { }
