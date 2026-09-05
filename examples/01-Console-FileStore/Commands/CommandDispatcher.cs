using Microsoft.Extensions.DependencyInjection;

namespace ConsoleFileStore.Commands;

/// <summary>Picks the registered command that matches the parsed command line.</summary>
public sealed class CommandDispatcher(IEnumerable<ICommand> commands)
{
    public ICommand Resolve(CommandKind kind) =>
        commands.FirstOrDefault(c => c.Kind == kind)
        ?? throw new InvalidOperationException($"No command registered for {kind}.");
}

public static class CommandRegistration
{
    public static IServiceCollection AddCommands(this IServiceCollection services)
    {
        services.AddSingleton<RunCheckoutCommand>();
        services.AddSingleton<ICommand>(sp => sp.GetRequiredService<RunCheckoutCommand>());
        services.AddSingleton<ICommand, RetryCheckoutCommand>();
        services.AddSingleton<ICommand, ResetCheckoutCommand>();
        services.AddSingleton<CommandDispatcher>();
        return services;
    }
}
