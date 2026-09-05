namespace ConsoleFileStore.Services;

public interface IPayments
{
    Task<string> ChargeAsync(string card, decimal total, Guid idempotencyKey);
}
