using Testcontainers.MsSql;
using Xunit;

namespace DurableLinq.SqlServer.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string? ConnectionString { get; private set; }
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder()
                .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                .Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            SkipReason = $"Docker is not reachable: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
