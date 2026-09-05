using ConsoleFileStore.Commands;

namespace ConsoleFileStore.Services;

/// <summary>
/// Creates a shipping label. With <c>--crash</c> it kills the process instead, after the charge step
/// was recorded and before this one is, which is the case durable execution exists for.
/// </summary>
public sealed class FakeShipping(CommandLine options) : IShipping
{
    public async Task<string> CreateLabelAsync(string address)
    {
        await Task.Delay(100);
        if (options.CrashOnShip)
        {
            Console.WriteLine("    -> shipping: simulated process crash before the label was recorded");
            Environment.Exit(137);
        }
        Console.WriteLine($"    -> shipping: label created for {address}");
        return "1Z-" + Random.Shared.Next(100000, 999999);
    }
}
