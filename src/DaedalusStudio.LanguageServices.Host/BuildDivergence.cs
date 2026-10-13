using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DaedalusStudio.LanguageServices.Host;

/// <summary>
/// Diagnostics the build never reports, because the build compiles a project against the assemblies of the projects
/// it references while the workspace compiles it against their compilations (ADR 0049).
/// </summary>
internal static class BuildDivergence
{
    /// <summary>
    /// CS4014 ("this call is not awaited") in a method that isn't <c>async</c>: the compiler reports it there only for a
    /// call to a method it sees as <c>async</c>, which a method of another project is only in source. The build sees that
    /// project's assembly, where no method is <c>async</c>, and reports nothing; the workspace would report it.
    /// </summary>
    public static bool IsCompilationReferenceArtifact(Diagnostic diagnostic, Compilation compilation, CancellationToken cancellationToken)
    {
        if (diagnostic.Id != "CS4014" || diagnostic.Location.SourceTree is not { } tree || !compilation.ContainsSyntaxTree(tree))
        {
            return false;
        }

        var node = tree.GetRoot(cancellationToken).FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        if (node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } invocation)
        {
            return false;
        }

        var model = compilation.GetSemanticModel(tree);
        if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method
            || !(method.ReducedFrom ?? method).IsAsync
            || SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, compilation.Assembly))
        {
            return false;
        }

        // In an async method or lambda the build reports it too (any awaitable call there).
        return model.GetEnclosingSymbol(invocation.SpanStart, cancellationToken) is not IMethodSymbol { IsAsync: true };
    }
}
