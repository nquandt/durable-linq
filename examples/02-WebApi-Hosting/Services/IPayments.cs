namespace WebApiHosting.Services;

public interface IPayments
{
    Task<string> Charge(string customerId, decimal amount, Guid idempotencyKey);
}
