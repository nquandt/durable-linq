using DurableLinq;
using WebApiHosting.Models;
using WebApiHosting.Services;

namespace WebApiHosting.Workflows;

/// <summary>
/// The checkout workflow. Dependencies arrive through the constructor; the hosting package creates one
/// instance per run from that run's DI scope.
/// </summary>
public sealed class CheckoutWorkflow(IPayments payments, IMail mail) : IWorkflow<OrderRequest, Receipt>
{
    public static string Name => "checkout";
    public static int Version => 1;

    public Durable<Receipt> Handle(OrderRequest req) =>
        from key in Durable.NewGuid("idempotency-key")
        from at in Durable.Now("charged-at")
        from auth in Durable.Step("charge", () => payments.Charge(req.CustomerId, req.Amount, key), StepOptions.Retry(3))
        from _ in Durable.Sleep(TimeSpan.FromSeconds(5), "settle")
        from ok in Durable.WaitFor<bool>("delivery-confirmed")
        from sent in Durable.Step("notify", () => mail.Send(req.CustomerId, $"Order delivered, auth {auth}"))
        select new Receipt(auth, at, ok);
}
