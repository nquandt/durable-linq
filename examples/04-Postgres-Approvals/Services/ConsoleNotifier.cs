namespace PostgresApprovals.Services;

public sealed class ConsoleNotifier : INotifier
{
    public Task NotifyAsync(string message) => Console.Out.WriteLineAsync(message);
}
