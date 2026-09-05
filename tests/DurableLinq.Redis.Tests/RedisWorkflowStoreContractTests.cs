using DurableLinq.Tests.Contract;
using Xunit;

namespace DurableLinq.Redis.Tests;

[Collection(RedisCollection.Name)]
public sealed class RedisWorkflowStoreContractTests(RedisFixture fixture) : WorkflowStoreContractTests
{
    protected override Task<IWorkflowStore> CreateStoreAsync()
    {
        if (fixture.SkipReason is not null)
            throw new SkipException(fixture.SkipReason);

        var options = new RedisStoreOptions { KeyPrefix = "durable-test-" + Guid.NewGuid().ToString("N") };
        return Task.FromResult<IWorkflowStore>(new RedisWorkflowStore(fixture.ConnectionString!, options));
    }

    protected override async Task DisposeStoreAsync(IWorkflowStore store)
    {
        if (store is RedisWorkflowStore redisStore) await redisStore.DisposeAsync();
    }
}

/// <summary>
/// A minimal stand-in for xunit's dynamic-skip support (not available in the xunit version pinned by this repo).
/// Thrown from <c>InitializeAsync</c> when Docker is unreachable; xunit reports the test as failed with this
/// message, which is the closest available approximation of "skip with a clear message" without adding a
/// dependency beyond what CLAUDE.md allows.
/// </summary>
public sealed class SkipException(string reason) : Exception($"SKIPPED: {reason}");
