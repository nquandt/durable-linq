using DurableLinq;
using DurableLinq.Hosting;
using PostgresApprovals.Models;

namespace PostgresApprovals.Endpoints;

public static class ExpenseEndpoints
{
    public static IEndpointRouteBuilder MapExpenseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/expenses/{id}", SubmitExpense);
        app.MapPost("/expenses/{id}/approve", Approve);
        app.MapPost("/expenses/{id}/reject", Reject);
        app.MapGet("/expenses/{id}", GetExpense);
        return app;
    }

    private static IResult SubmitExpense(string id, Expense expense, IWorkflowRunner runner, WorkflowDefinition<Expense, Decision> def, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ExpenseEndpoints");
        // Fire and forget: the instance lives in Postgres, and the sweeper resumes it if this process dies.
        _ = Task.Run(async () =>
        {
            var result = await runner.RunAsync(def, id, expense);
            if (result.IsBusy) logger.LogWarning("Expense {Id} is busy: leased by {Owner}", id, result.LeaseOwner);
            else if (result.IsFailed) logger.LogWarning("Expense {Id} failed: {Error}", id, result.Error);
        });
        return Results.Accepted($"/expenses/{id}");
    }

    private static Task<IResult> Approve(string id, Review review, IWorkflowRunner runner, WorkflowDefinition<Expense, Decision> def) =>
        SignalAsync(runner, def, id, review with { Approved = true });

    private static Task<IResult> Reject(string id, Review review, IWorkflowRunner runner, WorkflowDefinition<Expense, Decision> def) =>
        SignalAsync(runner, def, id, review with { Approved = false });

    private static async Task<IResult> GetExpense(string id, IWorkflowRunner runner, WorkflowDefinition<Expense, Decision> def)
    {
        var key = runner.KeyFor(def, id);
        var header = await runner.GetHeaderAsync(key);
        if (header is null) return Results.NotFound();
        var decision = header.Status == WorkflowStatus.Completed ? await runner.GetResultAsync<Decision>(key) : null;
        return Results.Ok(new ExpenseStatusResponse(key, header.Status.ToString(), decision));
    }

    private static async Task<IResult> SignalAsync(IWorkflowRunner runner, WorkflowDefinition<Expense, Decision> def, string id, Review review)
    {
        try { await runner.SignalAsync(def, id, "manager-decision", review); return Results.Ok(); }
        catch (InstanceNotFoundException) { return Results.NotFound(); }
    }
}
