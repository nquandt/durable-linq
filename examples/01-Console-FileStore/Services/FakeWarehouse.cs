using ConsoleFileStore.Models;

namespace ConsoleFileStore.Services;

public sealed class FakeWarehouse : IWarehouse
{
    public async Task<Line> ReserveAsync(Line line)
    {
        await Task.Delay(150);
        Console.WriteLine($"    -> warehouse: reserve {line.Qty} x {line.Sku}");
        return line;
    }
}
