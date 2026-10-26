using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Find All References, Rename with preview, the lightbulb (code fixes and refactorings with a preview), formatting,
/// Find in Files (ADR 0005, M3b), and applying the language service's workspace edits. Go To All is Code Search (M47).
/// </summary>
public sealed partial class ShellHost
{
    private const int MaxPreviewLines = 3000;

    public FindResultsViewModel FindResults { get; private set; } = null!;

    private void InitializeRefactoring() => InitializeFindInFiles();

    /// <summary>The active editor, its controller and its language service, or nulls with a message.</summary>
    private (CodeEditorDocument Editor, CodeEditorController Controller, LanguageHostClient Client)? ActiveLanguageEditor()
    {
        if (ActiveEditor is { Controller: { } controller } editor && ((IEditorServices)this).Client(editor.Model) is { } client)
        {
            return (editor, controller, client);
        }

        ShowMessage(ActiveEditor is null ? "No editor is active." : "This file has no language service.");
        return null;
    }

    // ------------------------------------------------------------------ Find All References

    public async Task FindReferencesAsync()
    {
        if (ActiveLanguageEditor() is not var (editor, controller, client))
        {
            return;
        }

        var offset = controller.Editor.CaretOffset;
        var name = WordAt(editor.Model.Text.Text, offset);
        if (editor.Model.LanguageId == CSharpLanguageId)
        {
            // Our host classifies references (read, write, declaration) for grouping and filtering (M12a).
            if (await client.ReferenceItemsAsync(editor.Model.Uri, editor.Model.PositionAt(offset), CancellationToken.None).ConfigureAwait(true) is not { Items.Length: > 0 } classified)
            {
                ShowMessage("No references found.");
                return;
            }

            Layout.ShowToolWindow(FindResultsViewModel.ToolWindowId, focus: false);
            await FindResults.ShowReferencesAsync($"References to '{classified.SymbolName}'", classified.Items).ConfigureAwait(true);
            return;
        }

        var locations = await client.ReferencesAsync(editor.Model.Uri, editor.Model.PositionAt(offset), includeDeclaration: true, CancellationToken.None).ConfigureAwait(true);
        if (locations.Length == 0)
        {
            ShowMessage("No references found.");
            return;
        }

        Layout.ShowToolWindow(FindResultsViewModel.ToolWindowId, focus: false);
        await FindResults.ShowLocationsAsync($"References to '{name}'", locations).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ Rename

    public async Task RenameSymbolAsync()
    {
        if (ActiveLanguageEditor() is not var (editor, controller, client) || editor.Model.IsReadOnly)
        {
            return;
        }

        var position = editor.Model.PositionAt(controller.Editor.CaretOffset);
        var word = WordAt(controller.Editor.Document, controller.Editor.CaretOffset);
        string placeholder;
        if (client.Capabilities?.PrepareRenameProvider == false)
        {
            // Servers without prepareRename (ADR 0016): rename the word at the caret.
            if (word.Length == 0)
            {
                ShowMessage("You cannot rename this element.");
                return;
            }

            placeholder = word;
        }
        else if (await client.PrepareRenameAsync(editor.Model.Uri, position, CancellationToken.None).ConfigureAwait(true) is { } prepared)
        {
            placeholder = prepared.Placeholder is { Length: > 0 } text ? text : word;
        }
        else
        {
            ShowMessage("You cannot rename this element.");
            return;
        }

        var csharp = editor.Model.LanguageId == CSharpLanguageId;
        var choice = await Dialogs.ShowAsync(new DialogRequest("Rename", $"New name for '{placeholder}':", ["Preview", "Cancel"])
        {
            Input = placeholder,
            Validate = name => name == placeholder ? "Enter a different name."
                : string.IsNullOrWhiteSpace(name) ? "Enter a name."
                : !csharp || IsIdentifier(name) ? null
                : "Not a valid C# identifier.",
        }).ConfigureAwait(true);
        if (choice.Button != "Preview" || choice.Input is not { } newName)
        {
            return;
        }

        var edit = await client.RenameAsync(editor.Model.Uri, position, newName, CancellationToken.None).ConfigureAwait(true);
        if (edit is null || edit.DocumentChanges.Length == 0 && edit.ResourceOperations.Length == 0)
        {
            ShowMessage("Nothing to rename.");
            return;
        }

        await PreviewAndApplyAsync($"Rename '{placeholder}' to '{newName}'", edit).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ the lightbulb

    /// <summary>Ctrl+.: the code fixes and refactorings at the caret (or selection), previewed before applying.</summary>
    /// <summary>
    /// The AI items after the language's code actions (M51), while AI assistance is on: Fix with AI and Explain with AI for
    /// the first diagnostic on the line, and Generate Tests with AI. Each runs through AiService when chosen.
    /// </summary>
    internal List<QuickPickItem> AiCodeActions(string path, IReadOnlyList<Diagnostic> diagnostics)
    {
        var items = new List<QuickPickItem>();
        if (!Ai.IsEnabled)
        {
            return items;
        }

        var detail = Localizer.Get("msg.ai.codeActionDetail", "AI");
        if (diagnostics.OrderBy(d => d.Severity).FirstOrDefault() is { } first)
        {
            items.Add(new QuickPickItem(Localizer.Get("msg.ai.fixWithAi", "Fix with AI")) { Detail = detail, Tag = (Func<Task>)FixDiagnosticWithAiAsync });
            items.Add(new QuickPickItem(Localizer.Get("msg.ai.explainWithAi", "Explain with AI")) { Detail = detail, Tag = (Func<Task>)(() => AskAiAboutDiagnosticAsync(path, first.Range.Start.Line + 1, first.Code, first.Message)) });
        }

        items.Add(new QuickPickItem(Localizer.Get("msg.ai.generateTestsWithAi", "Generate Tests with AI")) { Detail = detail, Tag = (Func<Task>)GenerateTestsWithAiAsync });
        return items;
    }

    public async Task ShowCodeActionsAsync()
    {
        if (ActiveLanguageEditor() is not var (editor, controller, client) || editor.Model.IsReadOnly)
        {
            return;
        }

        var model = editor.Model;
        var selection = controller.Editor.TextArea.Selection;
        var start = selection.IsEmpty ? controller.Editor.CaretOffset : selection.SurroundingSegment.Offset;
        var end = selection.IsEmpty ? start : selection.SurroundingSegment.EndOffset;
        var range = new LanguageServices.Protocol.Range(model.PositionAt(start), model.PositionAt(end));
        var line = range.Start.Line;
        var diagnostics = Languages.Diagnostics.For(model.Path).Where(d => d.Range.Start.Line <= line && d.Range.End.Line >= line).ToArray();
        StatusBar.ShowMessage(Localizer.Get("msg.refactoring.lookingForCodeActions", "Looking for code actions…"));
        var actions = (await client.CodeActionsAsync(model.Uri, range, diagnostics, CancellationToken.None).ConfigureAwait(true)).Select(a => (Action: a, Client: client)).ToList();

        // A linter's quick fixes (ESLint, ADR 0027): its edits come with the action.
        foreach (var linter in Languages.RunningLinters(model.Path))
        {
            try
            {
                actions.AddRange((await linter.CodeActionsAsync(model.Uri, range, diagnostics, CancellationToken.None).ConfigureAwait(true)).Where(a => a.Edit is not null || a.Command is not null).Select(a => (a, linter)));
            }
            catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or OperationCanceledException)
            {
                LanguageLog.AppendLine($"[code actions] a linter failed: {ex.Message}");
            }
        }

        StatusBar.ClearMessage();
        var ai = AiCodeActions(model.Path, diagnostics);
        if (actions.Count == 0 && ai.Count == 0)
        {
            ShowMessage("No code actions here.");
            return;
        }

        var items = actions
            .OrderBy(a => a.Action.Kind == CodeActionKind.QuickFix ? 0 : 1)
            .ThenBy(a => a.Action.IsPreferred == true ? 0 : 1)
            .Select(a => new QuickPickItem(a.Action.Title) { Detail = a.Action.Kind == CodeActionKind.QuickFix ? "Fix" : "Refactoring", Tag = a })
            .Concat(ai)
            .ToList();
        var chosen = await Palette.ShowAsync(items, "Quick actions and refactorings").ConfigureAwait(true);
        if (chosen?.Tag is Func<Task> aiAction)
        {
            await aiAction().ConfigureAwait(true);
            return;
        }

        if (chosen is not { Tag: (CodeAction picked, LanguageServices.Client.LanguageHostClient owner) })
        {
            return;
        }

        if (owner != client)
        {
            // A linter's fix: its edit, then its command (whose edit the server sends back, applied as it comes).
            if (picked.Edit is { } linterEdit && (linterEdit.DocumentChanges.Length > 0 || linterEdit.ResourceOperations.Length > 0))
            {
                await ApplyWorkspaceEditAsync(linterEdit).ConfigureAwait(true);
            }

            if (picked.Command is { } command)
            {
                await owner.ExecuteCommandAsync(command, CancellationToken.None).ConfigureAwait(true);
            }

            return;
        }

        // Refactorings that ask first (Generate constructor…, Extract interface…, Change signature…; ADR 0018).
        if (await client.CodeActionOptionsAsync(picked, CancellationToken.None).ConfigureAwait(true) is { } options)
        {
            var answer = await Dialogs.ShowAsync(new DialogRequest(options.Title, options.Message ?? (options.NameLabel ?? string.Empty), ["OK", "Cancel"])
            {
                Input = options.NameLabel is null ? null : options.Name ?? string.Empty,
                Validate = options.NameLabel is null ? null : name => IsIdentifier(name) ? null : "Not a valid C# identifier.",
                Choices = [.. options.Items.Select(i => new DialogChoice(i.Id, i.Label, i.Checked, i.Detail) { Icon = MemberIcon(i.Detail) })],
                Reorderable = options.Reorderable,
                Icon = "tools",
                Context = ActiveEditor?.Model is { } active ? System.IO.Path.GetFileName(active.Path) : null,
            }).ConfigureAwait(true);
            if (answer.Button != "OK")
            {
                return;
            }

            picked = new CodeActionChoices(answer.Input, [.. answer.Checked]).AttachTo(picked);
        }

        var resolved = await client.ResolveCodeActionAsync(picked, CancellationToken.None).ConfigureAwait(true);
        if (resolved.Edit is not { } edit || edit.DocumentChanges.Length == 0 && edit.ResourceOperations.Length == 0)
        {
            ShowMessage("The action made no changes (it may need an editor feature Codalus doesn't have yet).");
            return;
        }

        await PreviewAndApplyAsync(picked.Title, edit).ConfigureAwait(true);
    }

    /// <summary>A checklist row's icon from the member kind the language server names (M34h's checklist dialog).</summary>
    private static string? MemberIcon(string? detail) => detail?.ToLowerInvariant() switch
    {
        { } d when d.Contains("property", StringComparison.Ordinal) => "tools",
        { } d when d.Contains("method", StringComparison.Ordinal) => "method",
        { } d when d.Contains("field", StringComparison.Ordinal) => "column",
        { } d when d.Contains("parameter", StringComparison.Ordinal) => "braces",
        _ => null,
    };

    // ------------------------------------------------------------------ formatting

    public async Task FormatAsync(bool selectionOnly)
    {
        if (!selectionOnly && ActiveEditor is { Model: { IsReadOnly: false } document } && await FormatWithPrettierAsync(document).ConfigureAwait(true))
        {
            return;
        }

        if (ActiveLanguageEditor() is not var (editor, controller, client) || editor.Model.IsReadOnly)
        {
            return;
        }

        var model = editor.Model;
        LanguageServices.Protocol.Range? range = null;
        if (selectionOnly)
        {
            var selection = controller.Editor.TextArea.Selection;
            if (selection.IsEmpty)
            {
                var line = model.Text.GetLineByOffset(controller.Editor.CaretOffset);
                range = new LanguageServices.Protocol.Range(model.PositionAt(line.Offset), model.PositionAt(line.EndOffset));
            }
            else
            {
                range = new LanguageServices.Protocol.Range(model.PositionAt(selection.SurroundingSegment.Offset), model.PositionAt(selection.SurroundingSegment.EndOffset));
            }
        }

        var version = model.Version;
        var options = new FormattingOptions(controller.Editor.Options.IndentationSize, controller.Editor.Options.ConvertTabsToSpaces);
        var edits = await client.FormatAsync(model.Uri, range, options, CancellationToken.None).ConfigureAwait(true);
        if (version == model.Version && edits.Length > 0)
        {
            model.ApplyEdits(edits);
        }
    }

    // ------------------------------------------------------------------ Find in Files (ShellHost.FindInFiles.cs)

    /// <summary>The solution's files (project items that exist, project and solution files), or the open files.</summary>
    private IReadOnlyList<string> SearchableFiles()
    {
        if (Solutions.Solution is not { } solution)
        {
            return [.. _models.Values.Where(m => !m.IsReadOnly).Select(m => m.Path)];
        }

        var files = new HashSet<string>(Codalus.Workbench.Projects.SolutionService.PathComparer);
        foreach (var project in Solutions.Projects.Values)
        {
            files.Add(project.Path);
            foreach (var item in project.Items.Where(i => i.Kind != "Folder"))
            {
                files.Add(item.Path);
            }
        }

        foreach (var folder in solution.Folders)
        {
            files.UnionWith(folder.Files);
        }

        return [.. files.Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The editor's text of an open file, or null.</summary>
    private string? OpenTextOf(string path) => _models.TryGetValue(path, out var model) ? model.Text.Text : null;

    // ------------------------------------------------------------------ workspace edits

    /// <summary>Shows the edit in the preview and applies it on Apply; true when it was applied.</summary>
    private async Task<bool> PreviewAndApplyAsync(string title, WorkspaceEdit edit)
    {
        var details = await PreviewAsync(edit).ConfigureAwait(true);
        var files = edit.DocumentChanges.Select(c => c.TextDocument.Uri).Concat(edit.ResourceOperations.Select(o => o.Uri)).Distinct(StringComparer.Ordinal).Count();
        var choice = await Dialogs.ShowAsync(new DialogRequest(title, files == 1 ? "Changes in 1 file:" : $"Changes in {files} files:", ["Apply", "Cancel"])
        {
            Details = details,
        }).ConfigureAwait(true);
        return choice.Button == "Apply" && await ApplyWorkspaceEditAsync(edit).ConfigureAwait(true);
    }

    /// <summary>
    /// Applies a workspace edit: open documents change in the editor (undoable, left unsaved, like the classic IDE);
    /// other files are changed on disk with their encoding kept; files that don't exist are created.
    /// </summary>
    public async Task<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, bool quiet = false)
    {
        var failed = new List<string>();
        foreach (var change in edit.DocumentChanges)
        {
            if (DocumentUris.ToPath(change.TextDocument.Uri) is not { } path)
            {
                continue;
            }

            if (_models.TryGetValue(path, out var model))
            {
                if (change.TextDocument.Version is { } version && version != model.Version || !model.ApplyEdits(change.Edits))
                {
                    failed.Add(model.Name);
                }

                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    var (text, format) = await TextFiles.ReadAsync(path, CancellationToken.None).ConfigureAwait(true);
                    await TextFiles.WriteAsync(path, ApplyToText(text, change.Edits), format, CancellationToken.None).ConfigureAwait(true);
                }
                else
                {
                    await TextFiles.WriteAsync(path, ApplyToText(string.Empty, change.Edits), TextFileFormat.Default, CancellationToken.None).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        if (failed.Count > 0)
        {
            Workbench.Notifications.Warning("Some changes could not be applied (the files changed meanwhile): " + string.Join(", ", failed));
            return false;
        }

        // Renamed and deleted files, after the text edits (which are in their old positions).
        foreach (var operation in edit.ResourceOperations)
        {
            if (DocumentUris.ToPath(operation.Uri) is not { } path)
            {
                continue;
            }

            try
            {
                await ApplyResourceOperationAsync(operation, path).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException)
            {
                failed.Add(Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        if (failed.Count > 0)
        {
            Workbench.Notifications.Warning("Some files could not be renamed or deleted: " + string.Join(", ", failed));
            return false;
        }

        var count = edit.DocumentChanges.Length + edit.ResourceOperations.Count(o => !edit.DocumentChanges.Any(c => c.TextDocument.Uri == o.Uri));
        if (!quiet)
        {
            ShowMessage(count == 1 ? "Changed 1 file." : $"Changed {count} files.");
        }
        return true;
    }

    /// <summary>
    /// Renames or deletes a file of a workspace edit through the build host, so project items, <c>DependentUpon</c> and
    /// solution entries follow (without a solution, on disk). An open file is saved first (it holds the edit's text) and
    /// reopened under its new name; a deleted one is closed.
    /// </summary>
    private async Task ApplyResourceOperationAsync(ResourceOperation operation, string path)
    {
        var open = Editors.FirstOrDefault(e => string.Equals(e.Model.Path, path, StringComparison.Ordinal));
        var caret = open?.Controller?.Editor.CaretOffset;
        if (operation.Kind == ResourceOperationKinds.Rename && operation.NewUri is { } newUri && DocumentUris.ToPath(newUri) is { } newPath)
        {
            if (open is not null && open.Model.IsModified)
            {
                await open.Model.SaveAsync(CancellationToken.None).ConfigureAwait(true);
            }

            if (open is not null)
            {
                Layout.CloseDocument(open);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            if (Solutions.IsOpen)
            {
                await Solutions.RenameAsync(path, newPath, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                File.Move(path, newPath);
            }

            if (open is not null && await OpenFileAsync(newPath).ConfigureAwait(true) is { Controller: { } controller } && caret is { } offset)
            {
                controller.Editor.CaretOffset = Math.Min(offset, controller.Editor.Document.TextLength);
            }
        }
        else if (operation.Kind == ResourceOperationKinds.Delete)
        {
            if (open is not null)
            {
                open.Model.Text.UndoStack.MarkAsOriginalFile(); // nothing to save: the file goes
                Layout.CloseDocument(open);
            }

            if (Solutions.IsOpen)
            {
                await Solutions.DeleteAsync([path], CancellationToken.None).ConfigureAwait(true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Lines of a preview: per file a header, then for each changed region the old lines and the new lines.</summary>
    private async Task<IReadOnlyList<DetailLine>> PreviewAsync(WorkspaceEdit edit)
    {
        var lines = new List<DetailLine>();
        foreach (var change in edit.DocumentChanges)
        {
            if (DocumentUris.ToPath(change.TextDocument.Uri) is not { } path)
            {
                continue;
            }

            var exists = _models.ContainsKey(path) || File.Exists(path);
            var text = OpenTextOf(path) ?? (File.Exists(path) ? (await TextFiles.ReadAsync(path, CancellationToken.None).ConfigureAwait(true)).Text : string.Empty);
            lines.Add(new DetailLine(exists ? path : path + " (new file)", "header"));
            lines.AddRange(DiffLines(text, change.Edits));
            if (lines.Count > MaxPreviewLines)
            {
                lines.Add(new DetailLine("…"));
                break;
            }
        }

        foreach (var operation in edit.ResourceOperations)
        {
            if (DocumentUris.ToPath(operation.Uri) is { } path)
            {
                lines.Add(operation.Kind == ResourceOperationKinds.Rename && operation.NewUri is { } to && DocumentUris.ToPath(to) is { } target
                    ? new DetailLine($"{path} → renamed to {Path.GetFileName(target)}", "header")
                    : new DetailLine($"{path} → deleted", "removed"));
            }
        }

        return lines;
    }

    /// <summary>The edited regions of <paramref name="text"/>: old lines (removed) then new lines (added).</summary>
    internal static IEnumerable<DetailLine> DiffLines(string text, IReadOnlyList<LanguageServices.Protocol.TextEdit> edits)
    {
        var oldLines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var ordered = edits.OrderBy(e => e.Range.Start.Line).ThenBy(e => e.Range.Start.Character).ToList();
        var index = 0;
        while (index < ordered.Count)
        {
            // Edits touching the same lines form one region.
            var first = ordered[index].Range.Start.Line;
            var last = ordered[index].Range.End.Line;
            var group = new List<LanguageServices.Protocol.TextEdit> { ordered[index++] };
            while (index < ordered.Count && ordered[index].Range.Start.Line <= last)
            {
                last = Math.Max(last, ordered[index].Range.End.Line);
                group.Add(ordered[index++]);
            }

            first = Math.Min(first, oldLines.Length - 1);
            last = Math.Min(last, oldLines.Length - 1);
            var region = string.Join("\n", oldLines[first..(last + 1)]);
            var shifted = group.Select(e => e with
            {
                Range = new LanguageServices.Protocol.Range(e.Range.Start with { Line = e.Range.Start.Line - first }, e.Range.End with { Line = e.Range.End.Line - first }),
            });
            var replaced = ApplyToText(region, shifted);
            yield return new DetailLine($"@@ line {first + 1}");
            foreach (var line in oldLines[first..(last + 1)])
            {
                yield return new DetailLine("- " + line, "removed");
            }

            foreach (var line in replaced.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                yield return new DetailLine("+ " + line, "added");
            }
        }
    }

    /// <summary>Applies LSP edits to a string (line/character positions).</summary>
    internal static string ApplyToText(string text, IEnumerable<LanguageServices.Protocol.TextEdit> edits)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        int Offset(Position position)
        {
            if (position.Line >= lineStarts.Count)
            {
                return text.Length;
            }

            var start = lineStarts[position.Line];
            var end = position.Line + 1 < lineStarts.Count ? lineStarts[position.Line + 1] - 1 : text.Length;
            return Math.Min(start + position.Character, end);
        }

        var spans = edits.Select(e => (Start: Offset(e.Range.Start), End: Offset(e.Range.End), e.NewText)).OrderByDescending(s => s.Start).ThenByDescending(s => s.End);
        var builder = new System.Text.StringBuilder(text);
        foreach (var (start, end, newText) in spans)
        {
            builder.Remove(start, end - start).Insert(start, newText);
        }

        return builder.ToString();
    }

    /// <summary>A C# identifier, roughly (the language service checks it exactly).</summary>
    private static bool IsIdentifier(string name)
    {
        var text = name.StartsWith('@') ? name[1..] : name;
        return text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static string WordAt(string text, int offset)
    {
        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
        {
            start--;
        }

        var end = offset;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        return text[start..end];
    }

    /// <summary>The identifier-like word around an offset (letters, digits, '_' and '-').</summary>
    private static string WordAt(AvaloniaEdit.Document.TextDocument document, int offset)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';
        var start = offset;
        var end = offset;
        while (start > 0 && IsWord(document.GetCharAt(start - 1)))
        {
            start--;
        }

        while (end < document.TextLength && IsWord(document.GetCharAt(end)))
        {
            end++;
        }

        return document.GetText(start, end - start);
    }
}
