using Codalus.LanguageServices.Host.Features;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Razor;

/// <summary>
/// Rename of C# in Razor files (#391). Roslyn's <see cref="Microsoft.CodeAnalysis.Rename.Renamer"/> edits only the
/// project's own documents, never the ones the Razor generator wrote: a symbol's uses there (an <c>@code</c> block's
/// field, <c>@currentCount</c> in markup) are edited in the Razor files the generator copied them from, through its
/// <c>#line</c> mappings, and the generator writes the renamed C# again from them.
/// </summary>
internal static class RazorRename
{
    /// <summary>The C# identifier to rename at a Razor offset, with its range in the Razor file; null in markup.</summary>
    public static async Task<PrepareRenameResult?> PrepareAsync(RazorMapping mapping, int offset, CancellationToken cancellationToken)
    {
        if (mapping.ToGenerated(offset) is not { } generated
            || await RenameFeature.PrepareAsync(generated.Document, generated.Offset, cancellationToken).ConfigureAwait(false) is not { } prepared
            || generated.Part.ToRazor(prepared.Range) is not { } range)
        {
            return null;
        }

        return prepared with { Range = range };
    }

    /// <summary>Renames the C# symbol at a Razor offset: its uses in C# files and in every Razor file.</summary>
    public static Task<WorkspaceEdit?> RenameAsync(RazorMapping mapping, int offset, string newName, Func<string, int?> versionOf, CancellationToken cancellationToken) =>
        mapping.ToGenerated(offset) is { } generated
            ? RenameFeature.RenameAsync(generated.Document, generated.Offset, newName, versionOf, cancellationToken)
            : Task.FromResult<WorkspaceEdit?>(null);

    /// <summary>
    /// <paramref name="edit"/> with the symbol's declarations and uses in the C# the Razor generator wrote, moved to
    /// the Razor files they came from (code the generator wrote itself has no mapping and stays as it is).
    /// </summary>
    public static async Task<WorkspaceEdit> WithRazorAsync(WorkspaceEdit edit, Solution solution, ISymbol symbol, string newName, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var spans = new Dictionary<string, HashSet<LinePositionSpan>>(Paths.Comparer);
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false))
        {
            foreach (var location in referenced.Definition.Locations.Concat(referenced.Locations.Where(l => !l.IsImplicit).Select(l => l.Location)))
            {
                if (location.IsInSource && RazorLocations.IsRazorGenerated(location.SourceTree?.FilePath)
                    && location.GetMappedLineSpan() is { HasMappedPath: true } mapped && RazorFiles.IsRazor(mapped.Path))
                {
                    var path = Path.GetFullPath(mapped.Path);
                    (spans.TryGetValue(path, out var set) ? set : spans[path] = []).Add(mapped.Span);
                }
            }
        }

        if (spans.Count == 0)
        {
            return edit;
        }

        var changes = edit.DocumentChanges.ToList();
        foreach (var (path, lineSpans) in spans)
        {
            if (RazorFiles.Find(solution, path) is not { } razor)
            {
                continue;
            }

            // Only the symbol's name: a mapping that covers more (an expression) or other text is not a use to rename.
            var text = await razor.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var edits = lineSpans
                .Where(s => s.End.Line < text.Lines.Count && text.Lines.GetTextSpan(s) is var span && span.End <= text.Length && text.ToString(span) == symbol.Name)
                .OrderBy(s => s.Start)
                .Select(s => new Protocol.TextEdit(Positions.ToRange(s), newName))
                .ToArray();
            if (edits.Length == 0)
            {
                continue;
            }

            var uri = DocumentUris.FromPath(path);
            changes.RemoveAll(c => string.Equals(c.TextDocument.Uri, uri, StringComparison.OrdinalIgnoreCase));
            changes.Add(new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(uri, versionOf(path)), edits));
        }

        return edit with { DocumentChanges = [.. changes] };
    }
}
