using System.Threading.Channels;
using DurableLinq;
using DurableLinq.Hosting;
using WebApiHosting.Models;

namespace WebApiHosting.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/orders/{id}", PlaceOrder);
        app.MapPost("/orders/{id}/delivered", MarkDelivered);
        app.MapGet("/orders/{id}", GetOrder);

        // using DurableLinq.Dashboard;      // add a ProjectReference to src/DurableLinq.Dashboard
        // app.MapDurableDashboard("/durable");

        return app;
    }

    private static async Task<IResult> PlaceOrder(string id, OrderRequest req, Channel<OrderPlaced> queue)
    {
        await queue.Writer.WriteAsync(new OrderPlaced(id, req));
        return Results.Accepted($"/orders/{id}");
    }

    private static async Task<IResult> MarkDelivered(string id, IWorkflowRunner runner, WorkflowDefinition<OrderRequest, Receipt> checkout)
    {
        try
        {
            await runner.SignalAsync(checkout, id, "delivery-confirmed", true);
            return Results.Ok();
        }
        catch (InstanceNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> GetOrder(string id, IWorkflowRunner runner, IWorkflowStore store, WorkflowDefinition<OrderRequest, Receipt> checkout)
    {
        var key = runner.KeyFor(checkout, id);
        var header = await runner.GetHeaderAsync(key);
        if (header is null) return Results.NotFound();

        var steps =
            from s in await store.ListStepsAsync(key)
            select s.Name;

        object? result = header.Status == WorkflowStatus.Completed ? await runner.GetResultAsync<Receipt>(key) : null;
        var error = header.Status == WorkflowStatus.Failed ? await runner.GetErrorAsync(key) : null;

        return Results.Ok(new OrderStatusResponse(key, header.Status.ToString(), steps.ToArray(), result, error));
    }
}
