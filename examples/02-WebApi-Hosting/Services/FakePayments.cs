namespace WebApiHosting.Services;

public sealed class FakePayments(ILogger<FakePayments> log) : IPayments
{
    public async Task<string> Charge(string customerId, decimal amount, Guid idempotencyKey)
    {
        await Task.Delay(300);
        log.LogInformation("charging {Amount} to {Customer} (key {Key})", amount, customerId, idempotencyKey);
        return "AUTH-" + Random.Shared.Next(1000, 9999);
    }
}
