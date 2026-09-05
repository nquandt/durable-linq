using DurableLinq.SqlServer;
using DurableLinq.Tests.Contract;
using Xunit;

namespace DurableLinq.SqlServer.Tests;

[Collection("sqlserver")]
public sealed class SqlServerWorkflowStoreContractTests(SqlServerFixture fixture) : WorkflowStoreContractTests
{
    protected override async Task<IWorkflowStore> CreateStoreAsync()
    {
        if (fixture.SkipReason is not null)
        {
            Assert.Fail($"SKIPPED: {fixture.SkipReason}");
        }
        var options = new SqlServerStoreOptions { Schema = "test_" + Guid.NewGuid().ToString("N")[..8] };
        var store = new SqlServerWorkflowStore(fixture.ConnectionString!, options);
        await store.EnsureSchemaAsync();
        return store;
    }
}
