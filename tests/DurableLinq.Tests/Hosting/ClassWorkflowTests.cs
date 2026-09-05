using DurableLinq.Hosting;
using DurableLinq.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DurableLinq.Tests.Hosting;

public sealed class ClassWorkflowTests
{
    public interface IClock { DateTimeOffset Now { get; } }
    private sealed class FixedClock : IClock { public DateTimeOffset Now => new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero); }

    /// <summary>Counts how many times it was constructed, to prove one instance per run.</summary>
    public sealed class ScopedProbe { public static int Created; public ScopedProbe() => Interlocked.Increment(ref Created); }

    public sealed class Stamp(IClock clock, ScopedProbe probe) : IWorkflow<string, string>
    {
        public static string Name => "stamp";
        public static int Version => 3;

        public Durable<string> Handle(string input) =>
            from at in Durable.Step("read-clock", () => clock.Now)
            from text in Durable.Step("compose", () => $"{input}@{at:yyyy-MM-dd} probe#{probe.GetHashCode() != 0}")
            select text;
    }

    [Fact]
    public async Task AddWorkflow_registers_the_definition_and_activates_the_class_per_run()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IClock, FixedClock>();
        builder.Services.AddScoped<ScopedProbe>();
        builder.Services.AddDurable(d =>
        {
            d.UseInMemoryStore();
            d.Namespace = "class-tests";
            d.AddWorkflow<Stamp, string, string>();
        });
        using var host = builder.Build();

        var runner = host.Services.GetRequiredService<IWorkflowRunner>();
        var def = host.Services.GetRequiredService<WorkflowDefinition<string, string>>();
        Assert.Equal("stamp", def.Name);
        Assert.Equal(3, def.Version);
        Assert.Equal("class-tests/stamp/v3/a", runner.KeyFor(def, "a"));

        var before = ScopedProbe.Created;
        Assert.Equal("hi@2026-01-02 probe#True", (await runner.RunAsync(def, "a", "hi")).Value);
        await runner.RunAsync(def, "b", "yo");
        Assert.Equal(before + 2, ScopedProbe.Created);
    }

    [Fact]
    public async Task For_builds_a_definition_from_an_instance_without_a_container()
    {
        var def = WorkflowDefinitionClassExtensions.For<Stamp, string, string>(new Stamp(new FixedClock(), new ScopedProbe()));
        var runner = new WorkflowRunner(new InMemoryWorkflowStore(), new WorkflowRunnerOptions { Namespace = "t", Events = new() });
        Assert.Equal("x@2026-01-02 probe#True", (await runner.RunAsync(def, "1", "x")).Value);
    }
}
