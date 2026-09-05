using ConsoleFileStore.Models;

namespace ConsoleFileStore.Services;

public sealed class FakeCustomerDb : ICustomerDb
{
    public async Task<Customer> LoadCustomerAsync(string id)
    {
        await Task.Delay(200);
        Console.WriteLine($"    -> db: load customer {id}");
        return new Customer(id, "card-****-4242", "1 Main St");
    }
}
