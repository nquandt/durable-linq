using ConsoleFileStore.Models;

namespace ConsoleFileStore.Services;

public interface ICustomerDb
{
    Task<Customer> LoadCustomerAsync(string id);
}
