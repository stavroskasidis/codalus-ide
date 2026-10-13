using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using CodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Code Cleanup (M12b, Visual Studio's Ctrl+K Ctrl+E): for each diagnostic id of the profile, the code fix applied to
/// the whole document (its Fix All provider, or one fix at a time), then sorted usings and Format Document. The fixes
/// follow the project's .editorconfig preferences, because the IDE analyzers report what to change.
/// </summary>
internal sealed class CodeCleanupFeature(DiagnosticsService diagnostics, Action<string> log)
{
    /// <summary>The default profile: unused usings, simplifications, braces, accessibility modifiers, readonly fields, <c>new()</c>, file-scoped namespaces.</summary>
    public static readonly string[] DefaultFixes = ["IDE0005", "IDE0001", "IDE0002", "IDE0011", "IDE0040", "IDE0044", "IDE0090", "IDE0161"];

    public async Task<WorkspaceEdit?> CleanUpAsync(Document document, CodeCleanupParams parameters, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var original = document;
        foreach (var id in (parameters.Fixes ?? DefaultFixes).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            document = id.Equals("IDE0005", StringComparison.OrdinalIgnoreCase)
                ? await RemoveUnusedUsingsAsync(document, cancellationToken).ConfigureAwait(false)
                : await FixAllAsync(document, id, cancellationToken).ConfigureAwait(false);
        }

        if (parameters.SortUsings != false && await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is CompilationUnitSyntax root)
        {
            document = document.WithSyntaxRoot(SortUsings(root));
        }

        if (parameters.Format != false)
        {
            document = await Formatter.FormatAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return await WorkspaceEdits.ComputeAsync(original.Project.Solution, document.Project.Solution, versionOf, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// IDE0005: the usings the compiler reports as unnecessary (CS8019, hidden) or duplicated (CS0105). Roslyn's fixer
    /// for it needs services we can't create, so the directives are removed here.
    /// </summary>
    private static async Task<Document> RemoveUnusedUsingsAsync(Document document, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null)
        {
            return document;
        }

        var spans = model.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id is "CS8019" or "CS0105" && d.Location.IsInSource)
            .Select(d => d.Location.SourceSpan)
            .ToList();
        var unused = root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None) && spans.Any(s => u.Span.Contains(s) || s.Contains(u.Span)))
            .ToList();
        if (unused.Count == 0)
        {
            return document;
        }

        // The first using's leading trivia (a file header comment) moves to what follows.
        var first = root.DescendantNodes().OfType<UsingDirectiveSyntax>().FirstOrDefault();
        var header = first is not null && unused.Contains(first) ? first.GetLeadingTrivia() : default;
        var newRoot = root.RemoveNodes(unused, SyntaxRemoveOptions.KeepNoTrivia)!;
        if (header.Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)) && newRoot.GetFirstToken() is var token && token != default)
        {
            newRoot = newRoot.ReplaceToken(token, token.WithLeadingTrivia(header.AddRange(token.LeadingTrivia)));
        }

        return document.WithSyntaxRoot(newRoot);
    }

    private async Task<Document> FixAllAsync(Document document, string id, CancellationToken cancellationToken)
    {
        var found = (await diagnostics.DiagnosticsInSpanAsync(document, null, cancellationToken).ConfigureAwait(false))
            .Where(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && !d.IsSuppressed && d.Location.IsInSource)
            .ToList();
        if (found.Count == 0)
        {
            return document;
        }

        foreach (var provider in ProviderCatalog.CodeFixes(document.Project).Where(p => p.FixableDiagnosticIds.Contains(found[0].Id)))
        {
            try
            {
                // The first fix's equivalence key tells the Fix All provider which fix to apply everywhere.
                CodeAction? first = null;
                await provider.RegisterCodeFixesAsync(new CodeFixContext(document, found[0], (action, _) => first ??= action, cancellationToken)).ConfigureAwait(false);
                if (first is null)
                {
                    continue;
                }

                if (provider.GetFixAllProvider() is { } fixAll)
                {
                    var context = new FixAllContext(document, provider, FixAllScope.Document, first.EquivalenceKey, [found[0].Id], new Diagnostics(found), cancellationToken);
                    if (await fixAll.GetFixAsync(context).ConfigureAwait(false) is { } action && await ChangedAsync(action, document, cancellationToken).ConfigureAwait(false) is { } changed)
                    {
                        return changed;
                    }
                }
                else if (await ChangedAsync(first, document, cancellationToken).ConfigureAwait(false) is { } single)
                {
                    return single;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"Code Cleanup: {provider.GetType().Name} for {id}: {ex.Message}");
            }
        }

        return document;
    }

    private static async Task<Document?> ChangedAsync(CodeAction action, Document document, CancellationToken cancellationToken)
    {
        var operations = await action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
        return operations.OfType<ApplyChangesOperation>().LastOrDefault()?.ChangedSolution.GetDocument(document.Id);
    }

    /// <summary>Usings sorted alphabetically with <c>System</c> namespaces first, per block (file and namespaces).</summary>
    internal static CompilationUnitSyntax SortUsings(CompilationUnitSyntax root)
    {
        static SyntaxList<UsingDirectiveSyntax> Sort(SyntaxList<UsingDirectiveSyntax> usings)
        {
            if (usings.Count < 2)
            {
                return usings;
            }

            // Global usings, then plain ones, then static ones, then aliases; System first in each group.
            static int Group(UsingDirectiveSyntax u) => (u.GlobalKeyword.IsKind(SyntaxKind.None) ? 1 : 0) * 10 + (u.Alias is not null ? 3 : u.StaticKeyword.IsKind(SyntaxKind.None) ? 1 : 2);
            static string Name(UsingDirectiveSyntax u) => u.Name?.ToString() ?? u.NamespaceOrType.ToString();
            var leading = usings[0].GetLeadingTrivia();
            var sorted = usings
                .Select(u => u.WithLeadingTrivia(u.GetLeadingTrivia().Where(t => !t.IsKind(SyntaxKind.EndOfLineTrivia) && !t.IsKind(SyntaxKind.WhitespaceTrivia))))
                .OrderBy(Group)
                .ThenBy(u => Name(u) is var n && (n == "System" || n.StartsWith("System.", StringComparison.Ordinal)) ? 0 : 1)
                .ThenBy(Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sorted.SequenceEqual(usings, (a, b) => a.IsEquivalentTo(b)))
            {
                return usings;
            }

            sorted[0] = sorted[0].WithLeadingTrivia(leading);
            return SyntaxFactory.List(sorted);
        }

        root = root.WithUsings(Sort(root.Usings));
        return root.ReplaceNodes(root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>(), (_, ns) => ns.WithUsings(Sort(ns.Usings)));
    }

    private sealed class Diagnostics(IReadOnlyList<RoslynDiagnostic> found) : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<RoslynDiagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<RoslynDiagnostic>>(found);

        public override Task<IEnumerable<RoslynDiagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<RoslynDiagnostic>>([]);

        public override Task<IEnumerable<RoslynDiagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<RoslynDiagnostic>>(found);
    }
}

file static class SequenceExtensions
{
    public static bool SequenceEqual<T>(this IReadOnlyList<T> first, SyntaxList<T> second, Func<T, T, bool> equal)
        where T : SyntaxNode =>
        first.Count == second.Count && first.Select((item, i) => equal(item, second[i])).All(x => x);
}
