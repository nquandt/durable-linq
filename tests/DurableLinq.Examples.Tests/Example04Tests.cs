extern alias PgApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DurableLinq.Examples.Tests.Fixtures;
using DurableLinq.Examples.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DurableLinq.Examples.Tests;

/// <summary>Drives example 04 (Postgres approvals + in-process dashboard) with a real Postgres container.</summary>
[Collection(PostgresCollection.Name)]
public sealed class Example04Tests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly ExampleFactory? _factory;
    private readonly HttpClient? _client;

    public Example04Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
        if (_fixture.SkipReason is not null) return;

        _factory = new ExampleFactory(_fixture.ConnectionString!);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact]
    public async Task Small_expense_is_auto_approved_by_policy()
    {
        if (_fixture.SkipReason is not null) return;

        var submit = await _client!.PostAsJsonAsync("/expenses/e-small", new { employee = "kim", amount = 120 });
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);

        var completed = await Poll.UntilAsync(
            () => Poll.GetJsonOrDefaultAsync<ExpenseStatusResponse>(_client!, "/expenses/e-small"),
            r => r is not null && r.Status == "Completed",
            TimeSpan.FromSeconds(15));

        Assert.NotNull(completed);
        Assert.NotNull(completed!.Decision);
        Assert.True(completed.Decision!.Approved);
        Assert.Equal("policy", completed.Decision.DecidedBy);
    }

    [Fact]
    public async Task Large_expense_waits_for_manager_then_completes_on_approval()
    {
        if (_fixture.SkipReason is not null) return;

        var submit = await _client!.PostAsJsonAsync("/expenses/e-big", new { employee = "kim", amount = 1200 });
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);

        var running = await Poll.UntilAsync(
            () => Poll.GetJsonOrDefaultAsync<ExpenseStatusResponse>(_client!, "/expenses/e-big"),
            r => r is not null && r.Status == "Running",
            TimeSpan.FromSeconds(15));
        Assert.NotNull(running);

        var key = running!.Key;
        var detail = await Poll.UntilAsync(
            () => GetInstanceDetailAsync(key),
            d => d is not null && StepNames(d.Value).Contains("check-policy") && StepNames(d.Value).Contains("if"),
            TimeSpan.FromSeconds(15));
        Assert.NotNull(detail);
        Assert.DoesNotContain("wait:manager-decision", StepNames(detail!.Value));

        var approve = await _client!.PostAsJsonAsync("/expenses/e-big/approve", new { by = "lee", comment = "ok" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var completed = await Poll.UntilAsync(
            () => Poll.GetJsonOrDefaultAsync<ExpenseStatusResponse>(_client!, "/expenses/e-big"),
            r => r is not null && r.Status == "Completed",
            TimeSpan.FromSeconds(20));

        Assert.NotNull(completed);
        Assert.NotNull(completed!.Decision);
        Assert.True(completed.Decision!.Approved);
        Assert.Equal("lee", completed.Decision.DecidedBy);
    }

    [Fact]
    public async Task Dashboard_reports_summary_and_serves_html()
    {
        if (_fixture.SkipReason is not null) return;

        // Make sure at least a couple of instances exist for this namespace.
        await _client!.PostAsJsonAsync("/expenses/e-summary-1", new { employee = "sam", amount = 50 });
        await Poll.UntilAsync(
            () => Poll.GetJsonOrDefaultAsync<ExpenseStatusResponse>(_client!, "/expenses/e-summary-1"),
            r => r is not null && r.Status == "Completed",
            TimeSpan.FromSeconds(15));

        var summary = await _client!.GetFromJsonAsync<JsonElement>("/durable/api/summary?namespace=example-04");
        Assert.True(summary.GetProperty("total").GetInt32() >= 2);

        var html = await _client!.GetAsync("/durable/");
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.StartsWith("text/html", html.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        var body = await html.Content.ReadAsStringAsync();
        Assert.Contains("DurableLinq", body, StringComparison.Ordinal);
    }

    private async Task<JsonElement?> GetInstanceDetailAsync(string key)
    {
        var response = await _client!.GetAsync($"/durable/api/instances/{key}");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IEnumerable<string> StepNames(JsonElement detail) =>
        detail.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString() ?? "");

    private sealed record ExpenseStatusResponse(string Key, string Status, DecisionDto? Decision);
    private sealed record DecisionDto(bool Approved, string DecidedBy, string Reason);

    private sealed class ExampleFactory(string connectionString) : WebApplicationFactory<PgApp::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Durable:Postgres", connectionString);
        }
    }
}
