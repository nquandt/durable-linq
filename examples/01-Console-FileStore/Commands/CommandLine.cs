namespace ConsoleFileStore.Commands;

public enum CommandKind
{
    Run,
    Retry,
    Reset,
}

/// <summary>What the user asked for on the command line.</summary>
public sealed record CommandLine(CommandKind Command, bool CrashOnShip)
{
    public static CommandLine Parse(string[] args)
    {
        var command = args.Contains("--reset") ? CommandKind.Reset
                    : args.Contains("--retry") ? CommandKind.Retry
                    : CommandKind.Run;
        return new CommandLine(command, CrashOnShip: args.Contains("--crash"));
    }
}
