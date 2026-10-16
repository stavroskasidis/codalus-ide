using System.Text.RegularExpressions;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;

namespace Codalus.Shell;

/// <summary>
/// Find in Files and Replace in Files: the Find and Replace window (<see cref="FindReplaceViewModel"/>) over the
/// documents and its results in Find Results. Replace All goes through <see cref="ApplyWorkspaceEditAsync"/>, so open
/// documents change in the editor (undoable, unsaved) and the others on disk.
/// </summary>
public sealed partial class ShellHost
{
    public FindReplaceViewModel FindReplace { get; private set; } = null!;

    private void InitializeFindInFiles()
    {
        FindResults = new FindResultsViewModel(FilesToSearchAsync, OpenTextOf, OpenFindResultAsync);
        FindResults.ShowRequested += (_, _) => Layout.ShowToolWindow(FindResultsViewModel.ToolWindowId, focus: false);
        FindReplace = new FindReplaceViewModel(
            FindResults,
            () => Window is { } window ? window.PickFolderAsync(Localizer.Get("msg.findInFiles.lookInFolder", "Look in Folder")) : Task.FromResult<string?>(null),
            ReplaceAllInFilesAsync,
            ReplaceInFileAsync);
    }

    /// <summary>Ctrl+Shift+F: Find and Replace on Find in Files, with the editor's selection to find.</summary>
    public void FindInFiles() => FindReplace.Open(SelectedTextToFind(), replace: false);

    /// <summary>Ctrl+Shift+H: Find and Replace on Replace in Files.</summary>
    public void ReplaceInFiles() => FindReplace.Open(SelectedTextToFind(), replace: true);

    private string? SelectedTextToFind()
    {
        var selection = ActiveEditor?.Controller?.Editor.TextArea.Selection;
        return selection is { IsEmpty: false, IsMultiline: false } ? selection.GetText() : null;
    }

    /// <summary>The files a search looks in: its scope's, with its file types applied (except to the current document).</summary>
    private async Task<IReadOnlyList<string>> FilesToSearchAsync(FindInFilesOptions options)
    {
        var filter = new FileTypeFilter(options.FileTypes);
        switch (options.LookIn)
        {
            case FindScopes.EntireSolution:
                return Filter(SearchableFiles(), Solutions.Solution?.Directory);

            case FindScopes.CurrentProject:
                return Filter(CurrentProjectFiles(), Solutions.Solution?.Directory);

            case FindScopes.CurrentDocument:
                return ActiveEditor is { } editor && !editor.Model.IsReadOnly ? [editor.Model.Path] : [];

            case FindScopes.OpenDocuments:
                return Filter([.. _models.Values.Where(m => !m.IsReadOnly).Select(m => m.Path).Order(StringComparer.OrdinalIgnoreCase)], Solutions.Solution?.Directory);

            default:
                var folder = options.LookIn;
                if (!Directory.Exists(folder))
                {
                    throw new DirectoryNotFoundException($"The folder '{folder}' doesn't exist.");
                }

                return await Task.Run(() => Filter(
                    [.. Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                        .Order(StringComparer.OrdinalIgnoreCase)],
                    folder)).ConfigureAwait(true);
        }

        IReadOnlyList<string> Filter(IReadOnlyList<string> files, string? root) => [.. files.Where(f => filter.Matches(f, root))];
    }

    /// <summary>The active document's project's files (project items that exist and the project file), or none.</summary>
    private IReadOnlyList<string> CurrentProjectFiles()
    {
        var path = ActiveEditor?.Model.Path;
        var project = path is null ? null : Solutions.Projects.Values.FirstOrDefault(p =>
            SolutionService.PathComparer.Equals(p.Path, path) || p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path)));
        if (project is null)
        {
            return [];
        }

        var files = new HashSet<string>(SolutionService.PathComparer) { project.Path };
        files.UnionWith(project.Items.Where(i => i.Kind != "Folder").Select(i => i.Path));
        return [.. files.Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Opens a result in the text editor with the match selected.</summary>
    private async Task OpenFindResultAsync(string path, Position position, int length)
    {
        if (await OpenFileAsync(path, preview: false, textEditor: true).ConfigureAwait(true) is { } view)
        {
            view.Navigate(position, length);
        }
    }

    /// <summary>
    /// Replace: replaces <paramref name="row"/>'s match in its document (opened, undoable) and returns the row's line
    /// with its next match, or marked replaced when it has none; null when the text there no longer matches.
    /// </summary>
    private async Task<FindResultRow?> ReplaceInFileAsync(FindInFilesOptions options, FindResultRow row, string replaceWith)
    {
        var regex = options.CreateRegex();
        if (await OpenFileAsync(row.Path, preview: false, textEditor: true).ConfigureAwait(true) is not { Model: { IsReadOnly: false } model } view
            || row.Line > model.Text.LineCount)
        {
            return null;
        }

        var line = model.Text.GetLineByNumber(row.Line);
        var text = model.Text.GetText(line.Offset, line.Length);
        var match = row.Column <= text.Length ? regex.Match(text, row.Column) : System.Text.RegularExpressions.Match.Empty;
        if (!match.Success || match.Index != row.Column || match.Length == 0)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.findInFiles.theTextAtThisResultChanged", "The text at this result changed; it was not replaced."), TimeSpan.FromSeconds(4));
            return null;
        }

        var replacement = options.Replacement(match, replaceWith);
        model.Text.Replace(line.Offset + match.Index, match.Length, replacement);
        var after = model.Text.GetText(line.Offset, line.Length);
        var resume = match.Index + replacement.Length;
        var next = regex.Match(after, Math.Min(resume, after.Length));
        while (next.Success && next.Length == 0)
        {
            next = next.NextMatch();
        }

        if (next.Success && FindResultsPage.MatchLine(row.Path, row.Line, after, regex) is { } rest)
        {
            return rest with { Column = next.Index, Length = next.Length };
        }

        view.Navigate(new Position(row.Line - 1, resume));
        return new FindResultRow(row.Path, row.Line, match.Index, replacement.Length, after.Trim(), false) { IsReplaced = true };
    }

    /// <summary>Replace All: every match in the scope's files, after a confirmation; the changed lines go to Find Results.</summary>
    private async Task ReplaceAllInFilesAsync(FindInFilesOptions options, string replaceWith)
    {
        var regex = options.CreateRegex();
        IReadOnlyList<string> files;
        try
        {
            files = await FilesToSearchAsync(options).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(ex.Message);
            return;
        }

        var openTexts = files.Select(f => (f, OpenTextOf(f))).Where(x => x.Item2 is not null).ToDictionary(x => x.f, x => x.Item2!, SolutionService.PathComparer);
        StatusBar.ShowMessage(Localizer.Format("msg.findInFiles.searchingFiles", "Searching {0} files…", files.Count));
        var (changes, rows, occurrences) = await Task.Run(async () =>
        {
            var changes = new List<TextDocumentEdit>();
            var rows = new List<FindResultRow>();
            var occurrences = 0;
            foreach (var file in files)
            {
                string text;
                try
                {
                    text = openTexts.TryGetValue(file, out var open) ? open : await File.ReadAllTextAsync(file).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (text.Contains('\0', StringComparison.Ordinal))
                {
                    continue; // binary
                }

                var edits = new List<TextEdit>();
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    var count = 0;
                    var replaced = regex.Replace(line, m =>
                    {
                        if (m.Length == 0)
                        {
                            return m.Value;
                        }

                        count++;
                        return options.Replacement(m, replaceWith);
                    });
                    if (count == 0 || replaced == line)
                    {
                        continue;
                    }

                    occurrences += count;
                    edits.Add(new TextEdit(new LanguageServices.Protocol.Range(new Position(i, 0), new Position(i, line.Length)), replaced));
                    if (FindResultsPage.MatchLine(file, i + 1, line, regex) is { } before)
                    {
                        rows.Add(new FindResultRow(file, i + 1, before.Column, before.Length, replaced.Trim(), false));
                    }
                }

                if (edits.Count > 0)
                {
                    changes.Add(new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(file), null), [.. edits]));
                }
            }

            return (changes, rows, occurrences);
        }).ConfigureAwait(true);
        StatusBar.ClearMessage();

        if (changes.Count > 0)
        {
            var notOpen = changes.Count(c => DocumentUris.ToPath(c.TextDocument.Uri) is { } path && !_models.ContainsKey(path));
            var message = Localizer.Format("msg.findInFiles.replaceOccurrencesOfWithIn", "Replace {0} occurrence(s) of \"{1}\" with \"{2}\" in {3} file(s)?", occurrences, options.Query, replaceWith, changes.Count);
            if (notOpen > 0)
            {
                message += " " + Localizer.Format("msg.findInFiles.filesThatArentOpenWillBe", "{0} file(s) that aren't open will be changed on disk, which can't be undone.", notOpen);
            }

            if (!await Dialogs.ConfirmAsync(Localizer.Get("msg.findInFiles.replaceAll", "Replace All"), message, Localizer.Get("msg.findInFiles.replaceAll", "Replace All")).ConfigureAwait(true))
            {
                return;
            }

            await ApplyWorkspaceEditAsync(new WorkspaceEdit([.. changes])).ConfigureAwait(true);
        }

        FindResults.ShowReplaced(options, replaceWith, rows, occurrences, changes.Count);
    }
}
