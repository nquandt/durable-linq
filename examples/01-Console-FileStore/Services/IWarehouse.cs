using ConsoleFileStore.Models;

namespace ConsoleFileStore.Services;

public interface IWarehouse
{
    Task<Line> ReserveAsync(Line line);
}
