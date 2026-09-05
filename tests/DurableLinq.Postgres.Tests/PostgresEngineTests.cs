using DurableLinq.Tests.Contract;
using Xunit;

namespace DurableLinq.Postgres.Tests;

[Collection("postgres")]
public sealed class PostgresEngineTests(PostgresFixture fixture) : EngineOverStoreContractTests
{
    protected override async Task<IWorkflowStore> CreateStoreAsync()
    {
        if (fixture.SkipReason is not null)
        {
            Assert.Fail($"SKIPPED: {fixture.SkipReason}");
        }
        var options = new PostgresStoreOptions { Schema = "durable_engine_" + Guid.NewGuid().ToString("N")[..8] };
        var store = new PostgresWorkflowStore(fixture.ConnectionString!, options);
        await store.EnsureSchemaAsync();
        return store;
    }
}
