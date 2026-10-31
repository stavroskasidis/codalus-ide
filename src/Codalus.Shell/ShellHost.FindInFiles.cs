using System.Text.RegularExpressions;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
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
        FindResults = new FindResultsViewModel(FilesToSearchAsync, OpenFindResultAsync);
        FindResults.ShowRequested += (_, _) => Layout.ShowToolWindow(FindResultsViewModel.ToolWindowId, focus: false);
        FindResults.RootFolder = () => Solutions.Solution?.Directory;
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

    /// <summary>
    /// The files a search looks in: its scope's, with its file types applied (except to the current document), and their
    /// open texts, gathered off the UI thread (<see cref="GatherAsync"/>).
    /// </summary>
    private Task<FilesToSearch> FilesToSearchAsync(FindInFilesOptions options, CancellationToken cancellationToken)
    {
        var filter = new FileTypeFilter(options.FileTypes);
        var root = Solutions.Solution?.Directory;
        switch (options.LookIn)
        {
            case FindScopes.EntireSolution:
                return GatherAsync(SearchableFiles(), path => filter.Matches(path, root), mustExist: true, cancellationToken);

            case FindScopes.CurrentProject:
                return GatherAsync(CurrentProjectFiles(), path => filter.Matches(path, root), mustExist: true, cancellationToken);

            case FindScopes.CurrentDocument:
                var document = ActiveEditor is { } editor && !editor.Model.IsReadOnly ? editor.Model.Path : null;
                return GatherAsync(() => document is null ? [] : [document], null, mustExist: false, cancellationToken);

            case FindScopes.OpenDocuments:
                List<string> open = [.. _models.Values.Where(m => !m.IsReadOnly).Select(m => m.Path)];
                return GatherAsync(() => open, path => filter.Matches(path, root), mustExist: false, cancellationToken);

            default:
                var folder = options.LookIn;
                if (!Directory.Exists(folder))
                {
                    throw new DirectoryNotFoundException($"The folder '{folder}' doesn't exist.");
                }

                return GatherAsync(
                    () => Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }),
                    path => filter.Matches(path, folder),
                    mustExist: false,
                    cancellationToken);
        }
    }

    /// <summary>The active document's project's files (its items and the project file), listed when called (off the UI thread), or none.</summary>
    private Func<IEnumerable<string>> CurrentProjectFiles()
    {
        var path = ActiveEditor?.Model.Path;
        var projects = Solutions.Projects.Values;
        return () => ProjectListing(projects, path) is { } project
            ? project.Items.Where(i => i.Kind != "Folder").Select(i => i.Path).Prepend(project.Path)
            : [];
    }

    /// <summary>The first of <paramref name="projects"/> that is <paramref name="path"/> or lists it.</summary>
    private static ProjectInfo? ProjectListing(IEnumerable<ProjectInfo> projects, string? path) =>
        path is null ? null : projects.FirstOrDefault(p =>
            SolutionService.PathComparer.Equals(p.Path, path) || p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path)));

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
        IReadOnlyDictionary<string, string> openTexts;
        try
        {
            (files, openTexts) = await FilesToSearchAsync(options, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(ex.Message);
            return;
        }

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
