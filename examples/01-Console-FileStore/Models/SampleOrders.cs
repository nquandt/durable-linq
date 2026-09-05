namespace ConsoleFileStore.Models;

public static class SampleOrders
{
    /// <summary>The one order this demo runs. A fixed id makes every run target the same instance.</summary>
    public static readonly Order Default = new("order-42", "cust-7",
    [
        new Line("book", 2, 12.5m),
        new Line("pen", 5, 1.2m),
    ]);
}
