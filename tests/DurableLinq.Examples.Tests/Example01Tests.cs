using System.Diagnostics;
using System.Text;
using Xunit;

namespace DurableLinq.Examples.Tests;

/// <summary>
/// Drives example 01 (console app, file store) as a real subprocess. Each run happens in a fresh temp
/// working directory so "./workflow-state" is isolated per test.
/// </summary>
public sealed class Example01Tests : IDisposable
{
    private readonly string _workDir = Directory.CreateTempSubdirectory("durablelinq-example01-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task Crash_then_replay_then_short_circuit()
    {
        var crashRun = await RunAsync("--crash");
        Assert.Equal(137, crashRun.ExitCode);
        Assert.Contains("[StepExecuted] charge", crashRun.StdOut, StringComparison.Ordinal);

        var replayRun = await RunAsync();
        Assert.Equal(0, replayRun.ExitCode);
        Assert.Contains("replayed=5", replayRun.StdOut, StringComparison.Ordinal);
        Assert.Contains("executed=1", replayRun.StdOut, StringComparison.Ordinal);
        Assert.Contains("Receipt:", replayRun.StdOut, StringComparison.Ordinal);

        var shortCircuitRun = await RunAsync();
        Assert.Equal(0, shortCircuitRun.ExitCode);
        Assert.DoesNotContain("StepExecuted", shortCircuitRun.StdOut, StringComparison.Ordinal);
        Assert.Contains("Receipt:", shortCircuitRun.StdOut, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(params string[] args)
    {
        // Launch from the example's own build output. The test output folder resolves Microsoft.Extensions.Hosting
        // from the ASP.NET Core shared framework and does not carry the package DLL the example needs.
        var dllPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "examples", "01-Console-FileStore", "bin", BuildConfiguration, "net10.0", "ConsoleFileStore.dll"));
        Assert.True(File.Exists(dllPath), $"Expected build output at '{dllPath}'. Is the ProjectReference to example 01 present?");

        var psi = new ProcessStartInfo("dotnet", BuildArguments(dllPath, args))
        {
            WorkingDirectory = _workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = new Process { StartInfo = psi };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdErr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException(
                $"'dotnet ConsoleFileStore.dll {string.Join(' ', args)}' did not exit within 60s.\n" +
                $"stdout:\n{stdOut}\nstderr:\n{stdErr}");
        }

        if (process.ExitCode != 0 && process.ExitCode != 137)
        {
            // Not necessarily a test failure by itself, but surface the output for the assertion message.
        }

        return (process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    private static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static string BuildArguments(string dllPath, string[] args)
    {
        var quoted = new[] { $"\"{dllPath}\"" }.Concat(args.Select(a => $"\"{a}\""));
        return string.Join(' ', quoted);
    }
}
