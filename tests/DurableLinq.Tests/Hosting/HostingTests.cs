using DurableLinq.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DurableLinq.Tests.Hosting;

public sealed class HostingTests
{
    public interface IGreeter { string Greet(string name); }
    private sealed class Greeter : IGreeter { public string Greet(string name) => $"hi {name}"; }

    private static IHost BuildHost(Action<DurableBuilder>? extra = null, Action<IServiceCollection>? services = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped<IGreeter, Greeter>();
        services?.Invoke(builder.Services);
        builder.Services.AddDurable(d =>
        {
            d.UseInMemoryStore();
            d.Namespace = "host-tests";
            d.RecoveryInterval = TimeSpan.FromMilliseconds(200);
            d.LeaseTtl = TimeSpan.FromMilliseconds(300);
            d.Define<string, string>("greet", 1, (name, svc) =>
                Durable.Step("greet", () => svc.Get<IGreeter>().Greet(name)));
            extra?.Invoke(d);
        });
        return builder.Build();
    }

    [Fact]
    public async Task AddDurable_registers_runner_store_and_definitions()
    {
        using var host = BuildHost();
        var runner = host.Services.GetRequiredService<IWorkflowRunner>();
        var def = host.Services.GetRequiredService<WorkflowDefinition<string, string>>();
        var registry = host.Services.GetRequiredService<IWorkflowDefinitionRegistry>();

        Assert.Equal("host-tests", runner.Namespace);
        Assert.Same(def, registry.Find("greet", 1));
        Assert.Equal("hi ada", (await runner.RunAsync(def, "1", "ada")).Value);
    }

    [Fact]
    public void AddDurable_without_a_store_throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddDurable(d => d.Define<int, int>("x", 1, i => Durable.Return(i))));
    }

    [Fact]
    public async Task Sweeper_resumes_an_instance_whose_lease_expired()
    {
        var gate = new TaskCompletionSource();
        var resumedRuns = 0;
        using var host = BuildHost(d => d.Define<Unit, string>("stuck", 1, _ =>
            from a in Durable.Step("a", () => "a")
            from b in Durable.Step("b", async () =>
            {
                resumedRuns++;
                if (resumedRuns == 1) await gate.Task; // first run hangs forever
                return "b";
            })
            select a + b));

        await host.StartAsync();
        var runner = host.Services.GetRequiredService<IWorkflowRunner>();
        var store = host.Services.GetRequiredService<IWorkflowStore>();
        var def = host.Services.GetRequiredService<WorkflowDefinition<Unit, string>>();
        var key = runner.KeyFor(def, "s1");

        // simulate a dead host: create the instance with an already-expired lease and a recorded first step
        var now = DateTimeOffset.UtcNow;
        await store.TryCreateAsync(new InstanceHeader
        {
            Key = key,
            Namespace = "host-tests",
            WorkflowName = "stuck",
            Version = 1,
            InstanceId = "s1",
            Input = JsonWorkflowSerializer.Instance.Serialize(Unit.Value),
            LeaseOwner = "dead-host",
            LeaseToken = Guid.NewGuid(),
            LeaseExpires = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await store.AppendStepAsync(key, new StepEntry(0, "a", JsonWorkflowSerializer.Instance.Serialize("a"), now));
        resumedRuns = 1; // pretend the dead host executed attempt one

        var sweeper = host.Services.GetServices<IHostedService>().OfType<RecoverySweeper>().Single();
        var count = await sweeper.SweepOnceAsync();
        Assert.Equal(1, count);
        Assert.Equal(WorkflowStatus.Completed, await runner.GetStatusAsync(key));
        Assert.Equal("ab", await runner.GetResultAsync<string>(key));

        await host.StopAsync();
    }

    [Fact]
    public async Task Sweeper_skips_instances_without_a_registered_definition()
    {
        using var host = BuildHost();
        var store = host.Services.GetRequiredService<IWorkflowStore>();
        var now = DateTimeOffset.UtcNow;
        var key = InstanceKey.Format("host-tests", "gone", 7, "z");
        await store.TryCreateAsync(new InstanceHeader
        {
            Key = key,
            Namespace = "host-tests",
            WorkflowName = "gone",
            Version = 7,
            InstanceId = "z",
            CreatedAt = now,
            UpdatedAt = now,
        });
        var sweeper = host.Services.GetServices<IHostedService>().OfType<RecoverySweeper>().Single();
        Assert.Equal(0, await sweeper.SweepOnceAsync());
        Assert.Equal(WorkflowStatus.Running, (await store.GetHeaderAsync(key))!.Status);
    }
}
