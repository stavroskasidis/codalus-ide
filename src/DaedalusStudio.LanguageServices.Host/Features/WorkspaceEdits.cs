using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;

namespace DaedalusStudio.LanguageServices.Host.Features;

/// <summary>Turns the difference between two solutions (rename, code fix) into an LSP <see cref="WorkspaceEdit"/>.</summary>
internal static class WorkspaceEdits
{
    /// <summary>
    /// Text edits of every changed document, in positions of the old text. A document the change adds becomes an
    /// edit inserting its whole text into a file that doesn't exist yet (the client creates it).
    /// TODO(issue): removed and renamed documents (e.g. "Move type to file" renaming the current file).
    /// </summary>
    public static async Task<WorkspaceEdit> ComputeAsync(Solution oldSolution, Solution newSolution, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var edits = new List<TextDocumentEdit>();
        foreach (var project in newSolution.GetChanges(oldSolution).GetProjectChanges())
        {
            foreach (var id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var oldDocument = oldSolution.GetDocument(id)!;
                var newDocument = newSolution.GetDocument(id)!;
                if (oldDocument.FilePath is not { } path)
                {
                    continue;
                }

                var oldText = await oldDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var changes = await newDocument.GetTextChangesAsync(oldDocument, cancellationToken).ConfigureAwait(false);
                var textEdits = changes.Select(c => new Protocol.TextEdit(Positions.ToRange(oldText, c.Span), c.NewText ?? string.Empty)).ToArray();
                if (textEdits.Length > 0)
                {
                    edits.Add(new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), versionOf(path)), textEdits));
                }
            }

            foreach (var id in project.GetAddedDocuments())
            {
                var added = newSolution.GetDocument(id)!;
                var path = added.FilePath ?? Path.Combine(Path.GetDirectoryName(project.NewProject.FilePath) ?? string.Empty, added.Name);
                var text = await added.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var start = new Position(0, 0);
                edits.Add(new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), null), [new Protocol.TextEdit(new Protocol.Range(start, start), text.ToString())]));
            }
        }

        return new WorkspaceEdit([.. edits.DistinctBy(e => e.TextDocument.Uri)]);
    }
}
