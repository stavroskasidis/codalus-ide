using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using LspCodeAction = DaedalusStudio.LanguageServices.Protocol.CodeAction;
using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>
/// The lightbulb: code fixes for the diagnostics in a range and refactorings available there. Actions are listed by
/// title (nested actions flattened as "Parent: Child"); <c>codeAction/resolve</c> runs the chosen one and returns its
/// edits, which the client previews before applying. The last list is kept for resolving, like completion.
/// </summary>
internal sealed class CodeActionsFeature(DiagnosticsService diagnostics, Action<string> log)
{
    private readonly Lock _gate = new();
    private Cached? _last;
    private long _nextResultId;

    private sealed record Cached(long ResultId, Document Document, IReadOnlyList<RoslynCodeAction> Actions);

    internal sealed record ActionData(long ResultId, int Index);

    public async Task<LspCodeAction[]> ActionsAsync(Document document, TextSpan span, CancellationToken cancellationToken)
    {
        var fixes = new List<(RoslynCodeAction Action, bool Preferred)>();
        var refactorings = new List<RoslynCodeAction>();
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

        // Code fixes for the diagnostics touching the span (a caret: its line).
        var line = text.Lines.GetLineFromPosition(span.Start);
        var lineSpan = span.Length == 0 ? TextSpan.FromBounds(line.Start, line.End) : span;
        var found = await diagnostics.DiagnosticsInSpanAsync(document, lineSpan, cancellationToken).ConfigureAwait(false);
        var providers = ProviderCatalog.CodeFixes(document.Project).ToList();
        foreach (var diagnostic in found.Where(d => !d.IsSuppressed).OrderBy(d => d.Severity == DiagnosticSeverity.Error ? 0 : 1))
        {
            foreach (var provider in providers.Where(p => p.FixableDiagnosticIds.Contains(diagnostic.Id)))
            {
                var context = new CodeFixContext(document, diagnostic, (action, _) =>
                {
                    lock (fixes)
                    {
                        fixes.Add((action, diagnostic.Severity == DiagnosticSeverity.Error));
                    }
                }, cancellationToken);
                await Guard(provider.GetType().Name, () => provider.RegisterCodeFixesAsync(context)).ConfigureAwait(false);
            }
        }

        // Refactorings at the caret or selection.
        foreach (var provider in ProviderCatalog.Refactorings)
        {
            var context = new CodeRefactoringContext(document, span, action =>
            {
                lock (refactorings)
                {
                    refactorings.Add(action);
                }
            }, cancellationToken);
            await Guard(provider.GetType().Name, () => provider.ComputeRefactoringsAsync(context)).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var flat = new List<(RoslynCodeAction Action, string Title, string Kind, bool Preferred)>();
        foreach (var (action, preferred) in fixes)
        {
            Flatten(action, null, CodeActionKind.QuickFix, preferred, flat);
        }

        foreach (var action in refactorings)
        {
            Flatten(action, null, CodeActionKind.Refactor, false, flat);
        }

        var unique = flat.DistinctBy(a => a.Title).ToList();
        long resultId;
        lock (_gate)
        {
            resultId = ++_nextResultId;
            _last = new Cached(resultId, document, [.. unique.Select(a => a.Action)]);
        }

        return [.. unique.Select((a, i) => new LspCodeAction(a.Title)
        {
            Kind = a.Kind,
            IsPreferred = a.Preferred ? true : null,
            Data = JsonSerializer.SerializeToElement(new ActionData(resultId, i), CodeActionJsonContext.Default.ActionData),
        })];
    }

    public async Task<LspCodeAction> ResolveAsync(LspCodeAction action, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        if (action.Data is not { } data || data.Deserialize(CodeActionJsonContext.Default.ActionData) is not { } key)
        {
            return action;
        }

        Cached? cached;
        lock (_gate)
        {
            cached = _last;
        }

        if (cached is null || cached.ResultId != key.ResultId || key.Index >= cached.Actions.Count)
        {
            return action;
        }

        var solution = cached.Document.Project.Solution;
        var operations = await cached.Actions[key.Index].GetOperationsAsync(cancellationToken).ConfigureAwait(false);
        var changed = operations.OfType<ApplyChangesOperation>().LastOrDefault()?.ChangedSolution;
        if (changed is null)
        {
            return action with { Edit = new WorkspaceEdit([]) };
        }

        return action with { Edit = await WorkspaceEdits.ComputeAsync(solution, changed, versionOf, cancellationToken).ConfigureAwait(false) };
    }

    private static void Flatten(RoslynCodeAction action, string? parent, string kind, bool preferred, List<(RoslynCodeAction, string, string, bool)> into)
    {
        var title = parent is null ? action.Title : parent + ": " + action.Title;
        if (action.NestedActions.IsDefaultOrEmpty)
        {
            into.Add((action, title, kind, preferred));
            return;
        }

        foreach (var nested in action.NestedActions)
        {
            Flatten(nested, title, kind, preferred, into);
        }
    }

    private async Task Guard(string provider, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"{provider} failed: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodeActionsFeature.ActionData))]
internal sealed partial class CodeActionJsonContext : JsonSerializerContext;
