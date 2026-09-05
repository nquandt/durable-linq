using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DurableLinq.Analyzers.Tests;

public sealed class DurableMethodAnalyzerTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using DurableLinq;
        public static class W
        {
            public static Task<int> Io() => Task.FromResult(1);
        """;

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string body)
    {
        var source = Prelude + body + "\n}";
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(Durable<>).Assembly.Location));
        var compilation = CSharpCompilation.Create("t", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(compileErrors);
        var withAnalyzers = compilation.WithAnalyzers([new DurableMethodAnalyzer()]);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    [Fact]
    public async Task Clean_async_durable_method_has_no_diagnostics()
    {
        var diags = await AnalyzeAsync("""
            public static async Durable<int> Flow()
            {
                var a = await Durable.Step("a", () => 1);
                var b = await Durable.Step("b", () => Io());
                await Durable.Sleep(TimeSpan.FromSeconds(1));
                return a + b;
            }
            """);
        Assert.Empty(diags);
    }

    [Fact]
    public async Task Await_on_a_task_is_DL0002()
    {
        var diags = await AnalyzeAsync("""
            public static async Durable<int> Flow()
            {
                var a = await Durable.Step("a", () => 1);
                var b = await Io();
                await Task.Delay(10);
                return a + b;
            }
            """);
        Assert.Equal(2, diags.Length);
        Assert.All(diags, d => Assert.Equal("DL0002", d.Id));
        Assert.Contains("Task<int>", diags[0].GetMessage());
    }

    [Fact]
    public async Task Await_on_a_task_inside_a_step_body_is_fine()
    {
        var diags = await AnalyzeAsync("""
            public static async Durable<int> Flow()
            {
                return await Durable.Step("a", async () => { await Task.Delay(1); return await Io(); });
            }
            """);
        Assert.Empty(diags);
    }

    [Fact]
    public async Task Ordinary_async_task_method_is_not_analyzed()
    {
        var diags = await AnalyzeAsync("""
            public static async Task<int> NotAWorkflow()
            {
                try { return await Io(); } catch (Exception) { return 0; }
            }
            """);
        Assert.Empty(diags);
    }

    [Theory]
    [InlineData("catch (StepFailedException)")]
    [InlineData("catch (DurableException)")]
    [InlineData("catch (Exception)")]
    [InlineData("catch")]
    public async Task Catch_that_can_swallow_a_step_failure_is_DL0001(string clause)
    {
        var diags = await AnalyzeAsync($$"""
            public static async Durable<int> Flow()
            {
                try { return await Durable.Step("a", () => 1); }
                {{clause}} { return 0; }
            }
            """);
        var d = Assert.Single(diags);
        Assert.Equal("DL0001", d.Id);
    }

    [Fact]
    public async Task Catch_of_an_unrelated_exception_type_is_fine()
    {
        var diags = await AnalyzeAsync("""
            public static async Durable<int> Flow()
            {
                try { return await Durable.Step("a", () => 1); }
                catch (InvalidOperationException) { return 0; }
            }
            """);
        Assert.Empty(diags);
    }

    [Fact]
    public async Task Catch_around_a_try_with_no_durable_await_is_fine()
    {
        var diags = await AnalyzeAsync("""
            public static async Durable<int> Flow()
            {
                int parsed;
                try { parsed = int.Parse("1"); } catch (Exception) { parsed = 0; }
                return await Durable.Step("a", () => parsed);
            }
            """);
        Assert.Empty(diags);
    }

    [Fact]
    public async Task Lambdas_and_local_functions_declared_async_durable_are_analyzed()
    {
        var diags = await AnalyzeAsync("""
            public static Durable<int> Flow()
            {
                async Durable<int> Local() { await Task.Delay(1); return 1; }
                Func<Durable<int>> lambda = async () => { await Task.Yield(); return 2; };
                return Local();
            }
            """);
        Assert.Equal(2, diags.Length);
        Assert.All(diags, d => Assert.Equal("DL0002", d.Id));
    }
}
