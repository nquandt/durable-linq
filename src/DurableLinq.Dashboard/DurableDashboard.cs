using System.Reflection;
using System.Text;
using System.Text.Json;
using DurableLinq.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DurableLinq.Dashboard;

/// <summary>Maps the small, self-contained web dashboard for inspecting DurableLinq workflow instances.</summary>
public static class DurableDashboard
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string? _html;

    /// <summary>
    /// Maps the dashboard UI and its JSON API under <paramref name="pathPrefix"/>.
    /// Resolves <see cref="IWorkflowStore"/> (required), <see cref="IWorkflowRunner"/> (optional),
    /// and <see cref="IWorkflowDefinitionRegistry"/> (optional) from DI.
    /// </summary>
    public static IEndpointConventionBuilder MapDurableDashboard(this IEndpointRouteBuilder endpoints, string pathPrefix = "/durable")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        pathPrefix = pathPrefix.TrimEnd('/');

        var group = endpoints.MapGroup(pathPrefix);

        group.MapGet("/", GetIndex);
        group.MapGet("/api/instances", GetInstances);
        group.MapGet("/api/instances/{**key}", GetInstance);
        group.MapGet("/api/summary", GetSummary);
        group.MapGet("/api/definitions", GetDefinitions);
        group.MapPost("/api/instances/{**suffix}", PostInstanceAction);
        group.MapDelete("/api/instances/{**key}", DeleteInstance);

        return group;
    }

    private static string LoadHtml()
    {
        if (_html is not null) return _html;
        var asm = typeof(DurableDashboard).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("dashboard.html", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return _html = reader.ReadToEnd();
    }

    private static IResult GetIndex() => Results.Text(LoadHtml(), "text/html");

    private static string? AsJsonString(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            try
            {
                var s = Encoding.UTF8.GetString(bytes);
                // Only treat as text if it round-trips cleanly (valid UTF-8).
                return JsonSerializer.Serialize(s);
            }
            catch
            {
                return null;
            }
        }
    }

    private static object HeaderToDto(InstanceHeader h, DateTimeOffset now) => new
    {
        h.Key,
        h.Namespace,
        h.WorkflowName,
        h.Version,
        h.InstanceId,
        Status = h.Status.ToString(),
        h.LeaseOwner,
        h.LeaseExpires,
        h.CreatedAt,
        h.UpdatedAt,
        IsLeased = h.IsLeasedAt(now),
        InputJson = AsJsonString(h.Input),
        ResultJson = AsJsonString(h.Result),
        ErrorJson = AsJsonString(h.Error),
    };

    private static async Task<IResult> GetInstances(HttpContext ctx, IWorkflowStore store)
    {
        var q = ctx.Request.Query;
        var query = new InstanceQuery
        {
            Namespace = q["namespace"].FirstOrDefault() is { Length: > 0 } ns ? ns : null,
            WorkflowName = q["workflow"].FirstOrDefault() is { Length: > 0 } wf ? wf : null,
            Version = int.TryParse(q["version"], out var v) ? v : null,
            Status = Enum.TryParse<WorkflowStatus>(q["status"], true, out var st) ? st : null,
            InstanceIdContains = q["q"].FirstOrDefault() is { Length: > 0 } qs ? qs : null,
            Limit = int.TryParse(q["limit"], out var lim) ? lim : 100,
            Offset = int.TryParse(q["offset"], out var off) ? off : 0,
        };

        var headers = await store.ListInstancesAsync(query, ctx.RequestAborted).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return Results.Json(headers.Select(h => HeaderToDto(h, now)), JsonOptions);
    }

    private static async Task<IResult> GetInstance(string key, HttpContext ctx, IWorkflowStore store)
    {
        var header = await store.GetHeaderAsync(key, ctx.RequestAborted).ConfigureAwait(false);
        if (header is null) return Results.NotFound();

        var steps = await store.ListStepsAsync(key, ctx.RequestAborted).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var dto = new
        {
            Header = HeaderToDto(header, now),
            Steps = steps.Select(s => new
            {
                s.Seq,
                s.Name,
                s.RecordedAt,
                s.Failed,
                PayloadJson = AsJsonString(s.Payload),
            }),
        };
        return Results.Json(dto, JsonOptions);
    }

    private static async Task<IResult> GetSummary(HttpContext ctx, IWorkflowStore store)
    {
        var ns = ctx.Request.Query["namespace"].FirstOrDefault();
        var query = new InstanceQuery { Namespace = string.IsNullOrEmpty(ns) ? null : ns, Limit = 100_000 };
        var headers = await store.ListInstancesAsync(query, ctx.RequestAborted).ConfigureAwait(false);

        var byStatus = headers.GroupBy(h => h.Status.ToString())
            .ToDictionary(g => g.Key, g => g.Count());
        var byWorkflow = headers.GroupBy(h => h.WorkflowName)
            .Select(g => new { workflow = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();

        return Results.Json(new { total = headers.Count, byStatus, byWorkflow }, JsonOptions);
    }

    private static IResult GetDefinitions(IServiceProvider services)
    {
        var registry = services.GetService<IWorkflowDefinitionRegistry>();
        if (registry is null) return Results.Json(Array.Empty<object>(), JsonOptions);

        var defs = registry.All.Select(d => new
        {
            d.Name,
            d.Version,
            InputType = d.InputType.Name,
            OutputType = d.OutputType.Name,
        });
        return Results.Json(defs, JsonOptions);
    }

    private sealed record SignalRequest(string Name, string? PayloadJson);

    /// <summary>
    /// Dispatches POST requests under /api/instances/{key}/signal or /api/instances/{key}/retry.
    /// The instance key itself may contain slashes, so the action suffix is split off manually
    /// rather than routed with two catch-all segments.
    /// </summary>
    private static Task<IResult> PostInstanceAction(string suffix, HttpContext ctx, IServiceProvider services)
    {
        const string signalSuffix = "/signal";
        const string retrySuffix = "/retry";

        if (suffix.EndsWith(signalSuffix, StringComparison.Ordinal))
        {
            var key = suffix[..^signalSuffix.Length];
            return PostSignalCore(key, ctx, services);
        }
        if (suffix.EndsWith(retrySuffix, StringComparison.Ordinal))
        {
            var key = suffix[..^retrySuffix.Length];
            return PostRetry(key, ctx, services);
        }
        return Task.FromResult(Results.NotFound());
    }

    private static async Task<IResult> PostSignalCore(string key, HttpContext ctx, IServiceProvider services)
    {
        SignalRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<SignalRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest("Invalid request body.");
        }
        if (body is null) return Results.BadRequest("Request body is required.");
        return await PostSignal(key, body, ctx, services).ConfigureAwait(false);
    }

    private static async Task<IResult> PostSignal(string key, SignalRequest body, HttpContext ctx, IServiceProvider services)
    {
        if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest("Signal name is required.");

        var raw = body.PayloadJson ?? "null";
        JsonElement element;
        try
        {
            element = JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not valid JSON: treat the raw text as a JSON string literal.
            element = JsonDocument.Parse(JsonSerializer.Serialize(raw)).RootElement.Clone();
        }

        var runner = services.GetService<IWorkflowRunner>();
        if (runner is not null)
        {
            await runner.SignalAsync(key, body.Name, element, ctx.RequestAborted).ConfigureAwait(false);
            return Results.Ok();
        }

        var store = services.GetService<IWorkflowStore>();
        if (store is not null)
        {
            var payload = Encoding.UTF8.GetBytes(element.GetRawText());
            await store.AppendSignalAsync(key, body.Name, payload, ctx.RequestAborted).ConfigureAwait(false);
            return Results.Ok();
        }

        return Results.Problem("No IWorkflowRunner or IWorkflowStore is registered.", statusCode: 500);
    }

    private static async Task<IResult> PostRetry(string key, HttpContext ctx, IServiceProvider services)
    {
        var runner = services.GetService<IWorkflowRunner>();
        if (runner is null) return Results.Problem("Retry requires an IWorkflowRunner to be registered.", statusCode: 400);
        await runner.RetryFailedAsync(key, ctx.RequestAborted).ConfigureAwait(false);
        return Results.Ok();
    }

    private static async Task<IResult> DeleteInstance(string key, HttpContext ctx, IWorkflowStore store)
    {
        await store.DeleteAsync(key, ctx.RequestAborted).ConfigureAwait(false);
        return Results.Ok();
    }
}
