using DurableLinq.Stores;
using DurableLinq.Tests.Contract;

namespace DurableLinq.Tests.Stores;

public sealed class InMemoryEngineTests : EngineOverStoreContractTests
{
    protected override Task<IWorkflowStore> CreateStoreAsync() => Task.FromResult<IWorkflowStore>(new InMemoryWorkflowStore());
}

public sealed class FileEngineTests : EngineOverStoreContractTests
{
    private string _dir = null!;

    protected override Task<IWorkflowStore> CreateStoreAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "durablelinq-tests", Guid.NewGuid().ToString("N"));
        return Task.FromResult<IWorkflowStore>(new FileWorkflowStore(_dir));
    }

    protected override Task DisposeStoreAsync(IWorkflowStore store)
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        return Task.CompletedTask;
    }
}
