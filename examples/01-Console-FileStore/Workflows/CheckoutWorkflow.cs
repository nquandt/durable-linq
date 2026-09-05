using ConsoleFileStore.Models;
using ConsoleFileStore.Services;
using DurableLinq;

namespace ConsoleFileStore.Workflows;

/// <summary>
/// The checkout workflow. Dependencies arrive through the constructor; the hosting package creates one
/// instance per run from that run's DI scope. Every external call is a step, so a restart replays what
/// already happened and continues from the first step without a recorded result.
/// </summary>
public sealed class CheckoutWorkflow(ICustomerDb customers, IWarehouse warehouse, IPayments payments, IShipping shipping)
    : IWorkflow<Order, Receipt>
{
    public static string Name => "checkout";
    public static int Version => 1;

    public Durable<Receipt> Handle(Order order) =>
        from _ in Durable.Start(order)
            .Ensure(o => o.Lines.Length != 0, "order has no lines")
            .Ensure(o => o.Lines.All(l => l.Qty > 0), "every line needs a positive quantity")
        from key in Durable.NewGuid("idempotency-key")
        from customer in Durable.Step("load-customer", () => customers.LoadCustomerAsync(order.CustomerId))
            .Ensure(c => c.Card is not null, "customer has no card on file")
        from lines in Durable.ForEach(order.Lines, (line, i) => Durable.Step($"reserve[{i}]", () => warehouse.ReserveAsync(line)))
        let total = lines.Sum(l => l.UnitPrice * l.Qty)
        from auth in Durable.Step("charge", () => payments.ChargeAsync(customer.Card!, total, key))
        from tracking in Durable.Step("ship", () => shipping.CreateLabelAsync(customer.Address))
        select new Receipt(auth, tracking, total);
}
