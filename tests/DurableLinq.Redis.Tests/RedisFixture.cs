using Testcontainers.Redis;
using Xunit;

namespace DurableLinq.Redis.Tests;

/// <summary>Starts one Redis container for the whole test class. Skips tests with a clear message when Docker
/// isn't reachable, per CLAUDE.md.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string? ConnectionString { get; private set; }
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new RedisBuilder().WithImage("redis:7-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            SkipReason = $"Docker is not reachable, skipping Redis contract tests: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
