using DurableLinq.Tests.Contract;
using Xunit;

namespace DurableLinq.Redis.Tests;

[Collection(RedisCollection.Name)]
public sealed class RedisEngineTests(RedisFixture fixture) : EngineOverStoreContractTests
{
    protected override Task<IWorkflowStore> CreateStoreAsync()
    {
        if (fixture.SkipReason is not null)
            throw new SkipException(fixture.SkipReason);

        var options = new RedisStoreOptions { KeyPrefix = "durable-engine-" + Guid.NewGuid().ToString("N") };
        return Task.FromResult<IWorkflowStore>(new RedisWorkflowStore(fixture.ConnectionString!, options));
    }

    protected override async Task DisposeStoreAsync(IWorkflowStore store)
    {
        if (store is RedisWorkflowStore redisStore) await redisStore.DisposeAsync();
    }
}
