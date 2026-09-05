using System.Net;
using System.Net.Http.Json;
using DurableLinq.Examples.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DurableLinq.Examples.Tests;

/// <summary>Drives example 02 (ASP.NET Core minimal API, file store) in-process with WebApplicationFactory.</summary>
public sealed class Example02Tests : IDisposable
{
    private readonly string _stateDir = Directory.CreateTempSubdirectory("durablelinq-example02-").FullName;
    private readonly ExampleFactory _factory;
    private readonly HttpClient _client;

    public Example02Tests()
    {
        _factory = new ExampleFactory(_stateDir);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_stateDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Order_runs_through_settle_delivery_and_completion()
    {
        var placed = await _client.PostAsJsonAsync("/orders/o-1", new { customerId = "c-9", amount = 42.5 });
        Assert.Equal(HttpStatusCode.Accepted, placed.StatusCode);

        var settling = await Poll.UntilAsync(
            () => Poll.GetJsonOrDefaultAsync<OrderStatusResponse>(_client, "/orders/o-1"),
            r => r is not null && r.Steps.Contains("settle"),
            TimeSpan.FromSeconds(15));
        Assert.NotNull(settling);

        var delivered = await _client.PostAsync("/orders/o-1/delivered", content: null);
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);

        var completed = await Poll.UntilAsync(
            () => Poll.GetJsonOrDefaultAsync<OrderStatusResponse>(_client, "/orders/o-1"),
            r => r is not null && r.Status == "Completed",
            TimeSpan.FromSeconds(20));

        Assert.NotNull(completed);
        Assert.NotNull(completed!.Result);
        var result = completed.Result!.Value;
        var authCode = result.GetProperty("authCode").GetString();
        Assert.StartsWith("AUTH-", authCode, StringComparison.Ordinal);
        Assert.True(result.GetProperty("delivered").GetBoolean());
    }

    [Fact]
    public async Task Unknown_order_returns_not_found()
    {
        var get = await _client.GetAsync("/orders/unknown");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var deliver = await _client.PostAsync("/orders/unknown/delivered", content: null);
        Assert.Equal(HttpStatusCode.NotFound, deliver.StatusCode);
    }

    [Fact]
    public async Task Posting_the_same_order_twice_creates_one_instance()
    {
        var body = new { customerId = "c-1", amount = 10.0 };
        var first = await _client.PostAsJsonAsync("/orders/o-2", body);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var afterFirst = await Poll.UntilAsync(
            () => Poll.GetJsonOrDefaultAsync<OrderStatusResponse>(_client, "/orders/o-2"),
            r => r is not null && r.Steps.Length > 0,
            TimeSpan.FromSeconds(15));
        Assert.NotNull(afterFirst);

        var second = await _client.PostAsJsonAsync("/orders/o-2", body);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        // Give the second (short-circuiting) run a moment to be processed by the consumer, then confirm
        // the step count never grew past the six steps the workflow records once.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var afterSecond = await Poll.GetJsonOrDefaultAsync<OrderStatusResponse>(_client, "/orders/o-2");
        Assert.NotNull(afterSecond);
        Assert.True(afterSecond!.Steps.Length <= 6, $"Expected at most 6 recorded steps, got {afterSecond.Steps.Length}: {string.Join(',', afterSecond.Steps)}");
    }

    private sealed record OrderStatusResponse(string Key, string Status, string[] Steps, System.Text.Json.JsonElement? Result, System.Text.Json.JsonElement? Error);

    private sealed class ExampleFactory(string stateDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Durable:StateDirectory", stateDirectory);
        }
    }
}
