using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>Turns the difference between two solutions (rename, code fix) into an LSP <see cref="WorkspaceEdit"/>.</summary>
internal static class WorkspaceEdits
{
    /// <summary>
    /// Text edits of every changed document, in positions of the old text. A document the change adds becomes an
    /// edit inserting its whole text into a file that doesn't exist yet (the client creates it). A document renamed (a
    /// new name or folders: Rename File to Match Type) or removed becomes a resource operation, applied after the edits.
    /// </summary>
    public static async Task<WorkspaceEdit> ComputeAsync(Solution oldSolution, Solution newSolution, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        var edits = new List<TextDocumentEdit>();
        var operations = new List<ResourceOperation>();
        foreach (var project in newSolution.GetChanges(oldSolution).GetProjectChanges())
        {
            // Renamed documents: the same document with another name or folders.
            foreach (var id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: false))
            {
                var oldDocument = oldSolution.GetDocument(id)!;
                var newDocument = newSolution.GetDocument(id)!;
                if (oldDocument.FilePath is { } oldPath && NewPath(oldDocument, newDocument) is { } newPath
                    && !string.Equals(oldPath, newPath, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                {
                    operations.Add(new ResourceOperation(ResourceOperationKinds.Rename, DocumentUris.FromPath(oldPath), DocumentUris.FromPath(newPath)));
                }
            }

            foreach (var id in project.GetRemovedDocuments())
            {
                if (oldSolution.GetDocument(id)?.FilePath is { } removed)
                {
                    operations.Add(new ResourceOperation(ResourceOperationKinds.Delete, DocumentUris.FromPath(removed)));
                }
            }

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

            // .editorconfig files (Configure severity, M12b).
            foreach (var id in project.GetChangedAnalyzerConfigDocuments())
            {
                var oldDocument = oldSolution.GetAnalyzerConfigDocument(id)!;
                var newDocument = newSolution.GetAnalyzerConfigDocument(id)!;
                if (oldDocument.FilePath is not { } path)
                {
                    continue;
                }

                var oldText = await oldDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var newText = await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var textEdits = newText.GetTextChanges(oldText).Select(c => new Protocol.TextEdit(Positions.ToRange(oldText, c.Span), c.NewText ?? string.Empty)).ToArray();
                if (textEdits.Length > 0)
                {
                    edits.Add(new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), versionOf(path)), textEdits));
                }
            }

            foreach (var id in project.GetAddedAnalyzerConfigDocuments())
            {
                var added = newSolution.GetAnalyzerConfigDocument(id)!;
                if (added.FilePath is not { } path)
                {
                    continue;
                }

                var text = await added.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var start = new Position(0, 0);
                edits.Add(new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), null), [new Protocol.TextEdit(new Protocol.Range(start, start), text.ToString())]));
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

        return new WorkspaceEdit([.. edits.DistinctBy(e => e.TextDocument.Uri)]) { ResourceOperations = [.. operations] };
    }

    /// <summary>
    /// Where a changed document now lives: its new file path, or its old folder with the new name and folders (Roslyn
    /// changes a document's name and folders, not always its path); null when neither changed.
    /// </summary>
    private static string? NewPath(Document oldDocument, Document newDocument)
    {
        if (newDocument.FilePath is { } path && !string.Equals(path, oldDocument.FilePath, StringComparison.Ordinal))
        {
            return path;
        }

        if (newDocument.Name == oldDocument.Name && newDocument.Folders.SequenceEqual(oldDocument.Folders) || oldDocument.FilePath is null)
        {
            return null;
        }

        // The project's folder: the old path without its folders and name.
        var directory = Path.GetDirectoryName(oldDocument.FilePath)!;
        for (var i = 0; i < oldDocument.Folders.Count; i++)
        {
            directory = Path.GetDirectoryName(directory)!;
        }

        return Path.Combine([directory, .. newDocument.Folders, newDocument.Name]);
    }
}
