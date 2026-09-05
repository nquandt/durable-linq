using ConsoleFileStore.Models;
using ConsoleFileStore.Services;
using DurableLinq;

namespace ConsoleFileStore.Workflows;

/// <summary>
/// The same checkout as <see cref="CheckoutWorkflow"/>, written as an <c>async Durable&lt;T&gt;</c> method.
/// Each <c>await</c> on a durable is one recorded step. Loops and branches are plain C#. The method body
/// does not start when <c>Handle</c> is called; the runner starts it with the run's context, so the
/// signature, the registration, and the log are the same as the query version's.
/// </summary>
public sealed class CheckoutAsyncWorkflow(ICustomerDb customers, IWarehouse warehouse, IPayments payments, IShipping shipping)
    : IWorkflow<Order, Receipt>
{
    public static string Name => "checkout";
    public static int Version => 1;

    public async Durable<Receipt> Handle(Order order)
    {
        await Durable.Start(order)
            .Ensure(o => o.Lines.Length != 0, "order has no lines")
            .Ensure(o => o.Lines.All(l => l.Qty > 0), "every line needs a positive quantity");

        var key = await Durable.NewGuid("idempotency-key");

        var customer = await Durable.Step("load-customer", () => customers.LoadCustomerAsync(order.CustomerId))
            .Ensure(c => c.Card is not null, "customer has no card on file");

        var lines = new List<Line>();
        for (var i = 0; i < order.Lines.Length; i++)
            lines.Add(await Durable.Step($"reserve[{i}]", () => warehouse.ReserveAsync(order.Lines[i])));

        var total = lines.Sum(l => l.UnitPrice * l.Qty);

        var auth = await Durable.Step("charge", () => payments.ChargeAsync(customer.Card!, total, key));
        var tracking = await Durable.Step("ship", () => shipping.CreateLabelAsync(customer.Address));

        return new Receipt(auth, tracking, total);
    }
}
