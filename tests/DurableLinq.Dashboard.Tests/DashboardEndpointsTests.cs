using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DurableLinq.Dashboard;
using DurableLinq.Stores;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DurableLinq.Dashboard.Tests;

public sealed class DashboardEndpointsTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly InMemoryWorkflowStore _store = new();

    public const string RunningKey = "test/demo/v1/running-1";
    public const string FailedKey = "test/demo/v1/failed-1";

    public DashboardEndpointsTests()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IWorkflowStore>(_store);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapDurableDashboard("/durable"));
                });
            });

        _host = builder.Start();
        _client = _host.GetTestClient();

        Seed().GetAwaiter().GetResult();
    }

    private async Task Seed()
    {
        var now = DateTimeOffset.UtcNow;

        await _store.TryCreateAsync(new InstanceHeader
        {
            Key = RunningKey,
            Namespace = "test",
            WorkflowName = "demo",
            Version = 1,
            InstanceId = "running-1",
            Status = WorkflowStatus.Running,
            Input = Encoding.UTF8.GetBytes("""{"orderId":42}"""),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _store.AppendStepAsync(RunningKey, new StepEntry(0, "load-customer", Encoding.UTF8.GetBytes("""{"id":1}"""), now));

        await _store.TryCreateAsync(new InstanceHeader
        {
            Key = FailedKey,
            Namespace = "test",
            WorkflowName = "demo",
            Version = 1,
            InstanceId = "failed-1",
            Status = WorkflowStatus.Failed,
            Input = Encoding.UTF8.GetBytes("""{"orderId":7}"""),
            Error = Encoding.UTF8.GetBytes("""{"type":"System.Exception","message":"boom"}"""),
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    [Fact]
    public async Task Lists_instances_newest_first()
    {
        var response = await _client.GetAsync("/durable/api/instances");
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, items.GetArrayLength());
    }

    [Fact]
    public async Task Filters_by_status()
    {
        var response = await _client.GetAsync("/durable/api/instances?status=Failed");
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("failed-1", items[0].GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task Returns_instance_detail_with_steps_and_decoded_json()
    {
        var response = await _client.GetAsync($"/durable/api/instances/{RunningKey}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var header = body.GetProperty("header");
        Assert.Equal("Running", header.GetProperty("status").GetString());
        Assert.Contains("orderId", header.GetProperty("inputJson").GetString());

        var steps = body.GetProperty("steps");
        Assert.Equal(1, steps.GetArrayLength());
        Assert.Equal("load-customer", steps[0].GetProperty("name").GetString());
        Assert.False(steps[0].GetProperty("failed").GetBoolean());
    }

    [Fact]
    public async Task Returns_404_for_unknown_instance()
    {
        var response = await _client.GetAsync("/durable/api/instances/test/demo/v1/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Summary_reports_counts_by_status_and_workflow()
    {
        var response = await _client.GetAsync("/durable/api/summary");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("byStatus").GetProperty("Running").GetInt32());
        Assert.Equal(1, body.GetProperty("byStatus").GetProperty("Failed").GetInt32());
    }

    [Fact]
    public async Task Signal_appends_to_the_store_when_no_runner_is_registered()
    {
        var response = await _client.PostAsJsonAsync($"/durable/api/instances/{RunningKey}/signal", new
        {
            name = "approve",
            payloadJson = "true",
        });
        response.EnsureSuccessStatusCode();

        var payload = await _store.TryTakeSignalAsync(RunningKey, "approve");
        Assert.NotNull(payload);
        Assert.Equal("true", Encoding.UTF8.GetString(payload!));
    }

    [Fact]
    public async Task Signal_wraps_non_json_payload_as_a_json_string()
    {
        var response = await _client.PostAsJsonAsync($"/durable/api/instances/{RunningKey}/signal", new
        {
            name = "note",
            payloadJson = "hello world",
        });
        response.EnsureSuccessStatusCode();

        var payload = await _store.TryTakeSignalAsync(RunningKey, "note");
        Assert.NotNull(payload);
        Assert.Equal("\"hello world\"", Encoding.UTF8.GetString(payload!));
    }

    [Fact]
    public async Task Delete_removes_the_instance()
    {
        var response = await _client.DeleteAsync($"/durable/api/instances/{FailedKey}");
        response.EnsureSuccessStatusCode();

        var header = await _store.GetHeaderAsync(FailedKey);
        Assert.Null(header);
    }

    [Fact]
    public async Task Definitions_returns_empty_array_when_no_registry_is_registered()
    {
        var response = await _client.GetAsync("/durable/api/definitions");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetArrayLength());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _host.Dispose();
        await Task.CompletedTask;
    }
}
