namespace PostgresApprovals.Services;

public interface INotifier
{
    Task NotifyAsync(string message);
}
