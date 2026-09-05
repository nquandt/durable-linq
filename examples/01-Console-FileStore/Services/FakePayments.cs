namespace ConsoleFileStore.Services;

public sealed class FakePayments : IPayments
{
    public async Task<string> ChargeAsync(string card, decimal total, Guid idempotencyKey)
    {
        await Task.Delay(300);
        Console.WriteLine($"    -> payments: charge {total:C} to {card} (key {idempotencyKey:N})");
        return "AUTH-" + Random.Shared.Next(1000, 9999);
    }
}
