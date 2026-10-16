using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using CodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Paste with automatic usings (deferred from M12b): after code is pasted, the names in it that don't bind because a
/// <c>using</c> (VB: <c>Imports</c>) is missing get one, through Roslyn's own Add Import fix. Only unambiguous fixes are
/// applied: a name with one candidate namespace, or one of the namespaces the code was copied under (the source
/// file's usings, which the client sends). A name with several candidates and none preferred is left for the lightbulb.
/// </summary>
internal static class PasteImportsFeature
{
    /// <summary>The compiler's "name not found" errors that Add Import fixes: types, names, members (extension methods), LINQ.</summary>
    private static readonly HashSet<string> s_ids = new(StringComparer.Ordinal)
    {
        "CS0246", "CS0103", "CS1061", "CS1935", "CS0308",
        "BC30002", "BC30451", "BC30456", "BC36593", "BC30182",
    };

    private const int MaxImports = 20;

    public static async Task<WorkspaceEdit?> AddAsync(Document document, TextSpan pasted, IReadOnlyCollection<string> preferred, Func<string, int?> versionOf, Action<string> log, CancellationToken cancellationToken)
    {
        var original = document;
        var providers = ProviderCatalog.CodeFixes(document.Project).Where(IsAddImport).ToList();
        if (providers.Count == 0)
        {
            return null;
        }

        var skipped = new HashSet<string>(StringComparer.Ordinal);
        for (var added = 0; added < MaxImports; added++)
        {
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (model is null || pasted.End > text.Length)
            {
                break;
            }

            var unbound = model.GetDiagnostics(pasted, cancellationToken)
                .Where(d => s_ids.Contains(d.Id) && d.Location.IsInSource && !skipped.Contains(text.ToString(d.Location.SourceSpan)))
                .ToList();
            Document? changed = null;
            foreach (var diagnostic in unbound)
            {
                var name = text.ToString(diagnostic.Location.SourceSpan);
                var imports = new List<(string Namespace, CodeAction Action)>();
                foreach (var provider in providers.Where(p => p.FixableDiagnosticIds.Contains(diagnostic.Id)))
                {
                    try
                    {
                        await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) =>
                        {
                            foreach (var leaf in Leaves(action))
                            {
                                if (Namespace(leaf.Title) is { } ns)
                                {
                                    lock (imports)
                                    {
                                        imports.Add((ns, leaf));
                                    }
                                }
                            }
                        }, cancellationToken)).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log($"Paste imports: {provider.GetType().Name}: {ex.Message}");
                    }
                }

                var candidates = imports.DistinctBy(i => i.Namespace).ToList();
                var chosen = candidates.Count == 1 ? candidates[0] : candidates.Where(c => preferred.Contains(c.Namespace)).ToList() is [var only] ? only : default;
                if (chosen.Action is null)
                {
                    skipped.Add(name); // ambiguous, or nothing to import: the lightbulb has it
                    continue;
                }

                var operations = await chosen.Action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
                if (operations.OfType<ApplyChangesOperation>().LastOrDefault()?.ChangedSolution.GetDocument(document.Id) is { } next)
                {
                    changed = next;
                    break;
                }

                skipped.Add(name);
            }

            if (changed is null)
            {
                break;
            }

            // The pasted code moves by what was inserted before it (the using's line).
            var after = await changed.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var shift = after.GetTextChanges(text).Where(c => c.Span.End <= pasted.Start).Sum(c => (c.NewText?.Length ?? 0) - c.Span.Length);
            pasted = new TextSpan(pasted.Start + shift, pasted.Length);
            document = changed;
        }

        return ReferenceEquals(document, original) ? null : await WorkspaceEdits.ComputeAsync(original.Project.Solution, document.Project.Solution, versionOf, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The namespace a fix imports, from its title: <c>using System.Text;</c> or <c>Imports System.Text</c>.</summary>
    internal static string? Namespace(string title)
    {
        var trimmed = title.Trim();
        foreach (var prefix in new[] { "using ", "Imports " })
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal) && !trimmed.StartsWith(prefix + "static ", StringComparison.Ordinal))
            {
                var ns = trimmed[prefix.Length..].TrimEnd(';').Trim();
                return ns.Length > 0 && ns.All(c => char.IsLetterOrDigit(c) || c is '.' or '_') ? ns : null;
            }
        }

        return null;
    }

    private static bool IsAddImport(CodeFixProvider provider) =>
        provider.GetType().Name.Contains("AddImport", StringComparison.Ordinal);

    private static IEnumerable<CodeAction> Leaves(CodeAction action) =>
        action.NestedActions.IsDefaultOrEmpty ? [action] : action.NestedActions.SelectMany(Leaves);
}
