using DurableLinq;
using WebApiHosting.Models;
using WebApiHosting.Services;

namespace WebApiHosting.Workflows;

/// <summary>
/// The same checkout as <see cref="CheckoutWorkflow"/>, written as an <c>async Durable&lt;T&gt;</c> method.
/// The timer and the signal are awaited like any other step. The log is identical to the query version's.
/// </summary>
public sealed class CheckoutAsyncWorkflow(IPayments payments, IMail mail) : IWorkflow<OrderRequest, Receipt>
{
    public static string Name => "checkout";
    public static int Version => 1;

    public async Durable<Receipt> Handle(OrderRequest req)
    {
        var key = await Durable.NewGuid("idempotency-key");
        var chargedAt = await Durable.Now("charged-at");
        var auth = await Durable.Step("charge", () => payments.Charge(req.CustomerId, req.Amount, key), StepOptions.Retry(3));

        await Durable.Sleep(TimeSpan.FromSeconds(5), "settle");
        var delivered = await Durable.WaitFor<bool>("delivery-confirmed");

        await Durable.Step("notify", () => mail.Send(req.CustomerId, $"Order delivered, auth {auth}"));

        return new Receipt(auth, chargedAt, delivered);
    }
}
