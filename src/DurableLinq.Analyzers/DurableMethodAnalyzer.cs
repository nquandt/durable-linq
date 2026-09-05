using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DurableLinq.Analyzers;

/// <summary>
/// Rules for methods, lambdas, and local functions declared <c>async Durable&lt;T&gt;</c>:
/// DL0001 flags a <c>catch</c> that can swallow a failed step, and DL0002 flags an <c>await</c> on
/// anything that is not a <c>Durable&lt;T&gt;</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DurableMethodAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "DurableLinq";
    private const string DurableTypeName = "DurableLinq.Durable`1";
    private const string StepFailedTypeName = "DurableLinq.StepFailedException";

    public static readonly DiagnosticDescriptor CatchSwallowsStepFailure = new(
        id: "DL0001",
        title: "Do not catch a failed step in an async Durable<T> method",
        messageFormat: "This catch can swallow StepFailedException; a plain catch records nothing, so a retry may take a different path. Handle the failure with .Recover(...), .Compensate(...), or Durable.Try(...) on the durable before awaiting it.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A failed step throws StepFailedException at the await. Leave it uncaught so the run fails as a value, or handle it with an operator that records the failure in the log.");

    public static readonly DiagnosticDescriptor NonDurableAwait = new(
        id: "DL0002",
        title: "Await only durables inside an async Durable<T> method",
        messageFormat: "'{0}' is not a Durable<T>; this await is not recorded, so it runs again on every replay. Wrap the call in Durable.Step(name, ...) or another primitive.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every side effect in a workflow must go through a primitive so it is memoized to the log. An await on a Task or ValueTask bypasses the log.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CatchSwallowsStepFailure, NonDurableAwait);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var durable = start.Compilation.GetTypeByMetadataName(DurableTypeName);
            if (durable is null) return;
            var stepFailed = start.Compilation.GetTypeByMetadataName(StepFailedTypeName);
            var exception = start.Compilation.GetTypeByMetadataName("System.Exception");

            start.RegisterSyntaxNodeAction(c => AnalyzeAwait(c, durable), SyntaxKind.AwaitExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeCatch(c, durable, stepFailed, exception), SyntaxKind.CatchClause);
        });
    }

    private static void AnalyzeAwait(SyntaxNodeAnalysisContext context, INamedTypeSymbol durable)
    {
        if (!IsInsideAsyncDurable(context, durable)) return;
        var await = (AwaitExpressionSyntax)context.Node;
        var type = context.SemanticModel.GetTypeInfo(await.Expression, context.CancellationToken).Type;
        if (type is null || type.TypeKind == TypeKind.Error) return;
        if (IsDurable(type, durable)) return;
        context.ReportDiagnostic(Diagnostic.Create(NonDurableAwait, await.Expression.GetLocation(), type.ToMinimalDisplayString(context.SemanticModel, await.SpanStart)));
    }

    private static void AnalyzeCatch(SyntaxNodeAnalysisContext context, INamedTypeSymbol durable, INamedTypeSymbol? stepFailed, INamedTypeSymbol? exception)
    {
        if (!IsInsideAsyncDurable(context, durable)) return;
        var @catch = (CatchClauseSyntax)context.Node;
        var tryBlock = ((TryStatementSyntax)@catch.Parent!).Block;

        // Only a try block that awaits a durable can see a StepFailedException.
        var awaitsDurable = tryBlock.DescendantNodes(n => n is not LambdaExpressionSyntax && n is not LocalFunctionStatementSyntax)
            .OfType<AwaitExpressionSyntax>()
            .Any(a => IsDurable(context.SemanticModel.GetTypeInfo(a.Expression, context.CancellationToken).Type, durable));
        if (!awaitsDurable) return;

        if (@catch.Declaration is null)
        {
            Report(context, @catch);
            return;
        }

        var caught = context.SemanticModel.GetTypeInfo(@catch.Declaration.Type, context.CancellationToken).Type;
        if (caught is null || caught.TypeKind == TypeKind.Error) return;

        // Flag StepFailedException and every base type up to and including System.Exception.
        var catchesIt = stepFailed is not null
            ? IsSameOrBaseOf(caught, stepFailed)
            : SymbolEqualityComparer.Default.Equals(caught, exception);
        if (catchesIt) Report(context, @catch);
    }

    private static void Report(SyntaxNodeAnalysisContext context, CatchClauseSyntax @catch) =>
        context.ReportDiagnostic(Diagnostic.Create(CatchSwallowsStepFailure, @catch.CatchKeyword.GetLocation()));

    private static bool IsSameOrBaseOf(ITypeSymbol candidate, ITypeSymbol derived)
    {
        for (var t = derived; t is not null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, candidate)) return true;
        return false;
    }

    private static bool IsDurable(ITypeSymbol? type, INamedTypeSymbol durable) =>
        type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, durable);

    /// <summary>True when the nearest enclosing method, lambda, or local function is <c>async</c> and returns <c>Durable&lt;T&gt;</c>.</summary>
    private static bool IsInsideAsyncDurable(SyntaxNodeAnalysisContext context, INamedTypeSymbol durable)
    {
        for (var node = context.Node.Parent; node is not null; node = node.Parent)
        {
            IMethodSymbol? method = node switch
            {
                AnonymousFunctionExpressionSyntax lambda => context.SemanticModel.GetSymbolInfo(lambda, context.CancellationToken).Symbol as IMethodSymbol,
                LocalFunctionStatementSyntax local => context.SemanticModel.GetDeclaredSymbol(local, context.CancellationToken),
                BaseMethodDeclarationSyntax decl => context.SemanticModel.GetDeclaredSymbol(decl, context.CancellationToken),
                AccessorDeclarationSyntax accessor => context.SemanticModel.GetDeclaredSymbol(accessor, context.CancellationToken),
                _ => null,
            };
            if (node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)
                return method is { IsAsync: true } && IsDurable(method.ReturnType, durable);
        }
        return false;
    }
}
