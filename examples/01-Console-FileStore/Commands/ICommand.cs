namespace ConsoleFileStore.Commands;

public interface ICommand
{
    CommandKind Kind { get; }

    /// <summary>Runs the command and returns the process exit code.</summary>
    Task<int> ExecuteAsync(CancellationToken ct = default);
}
