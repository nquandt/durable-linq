using DurableLinq.Stores;
using DurableLinq.Tests.Contract;

namespace DurableLinq.Tests.Stores;

public sealed class InMemoryStoreTests : WorkflowStoreContractTests
{
    protected override Task<IWorkflowStore> CreateStoreAsync() => Task.FromResult<IWorkflowStore>(new InMemoryWorkflowStore());
}

public sealed class FileStoreTests : WorkflowStoreContractTests
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

    [Xunit.Fact]
    public async Task A_new_store_over_the_same_directory_sees_the_old_state()
    {
        var key = await CreateInstanceAsync("persisted");
        await Store.AppendStepAsync(key, new StepEntry(0, "a", [1], DateTimeOffset.UtcNow));
        await Store.AppendSignalAsync(key, "s", [5]);

        var reopened = new FileWorkflowStore(_dir);
        Xunit.Assert.NotNull(await reopened.GetHeaderAsync(key));
        Xunit.Assert.Equal("a", (await reopened.TryGetStepAsync(key, 0))!.Name);
        Xunit.Assert.Equal(new byte[] { 5 }, await reopened.TryTakeSignalAsync(key, "s"));
    }

    [Xunit.Fact]
    public async Task A_torn_last_line_in_the_step_log_is_ignored()
    {
        var key = await CreateInstanceAsync("torn");
        await Store.AppendStepAsync(key, new StepEntry(0, "a", [1], DateTimeOffset.UtcNow));
        var stepsFile = Directory.EnumerateFiles(_dir, "steps.jsonl", SearchOption.AllDirectories).Single();
        File.AppendAllText(stepsFile, "{\"seq\":1,\"name\":\"b\",\"pay");

        var reopened = new FileWorkflowStore(_dir);
        Xunit.Assert.Null(await reopened.TryGetStepAsync(key, 1));
        await reopened.AppendStepAsync(key, new StepEntry(1, "b", [2], DateTimeOffset.UtcNow));
        Xunit.Assert.Equal("b", (await reopened.TryGetStepAsync(key, 1))!.Name);
    }
}
