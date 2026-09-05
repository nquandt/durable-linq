using DurableLinq;

namespace ConsoleFileStore.Diagnostics;

/// <summary>Prints engine events so the difference between an executed and a replayed step is visible.</summary>
public sealed class ConsoleEventPrinter(DurableEventSource events)
{
    public void Attach() => events.Raised += Print;

    private static void Print(DurableEvent e) =>
        Console.WriteLine($"  [{e.Kind}] {e.StepName ?? ""} {e.Detail ?? ""}".TrimEnd());
}
