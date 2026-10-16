using System.Text.Json;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.History;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Editing safety and convenience (M12b): format on save, the local history of saved versions, AutoRecover of unsaved
/// changes, the clipboard ring and Paste JSON as Classes.
/// </summary>
public sealed partial class ShellHost
{
    public const string FormatOnSaveSetting = "editor.formatOnSave";
    public const string AutoRecoverSetting = "files.autoRecover";
    public const string CodeCleanupFixesSetting = "codeCleanup.fixes";
    public const string CodeCleanupOnSaveSetting = "codeCleanup.onSave";
    private const int ClipboardRingSize = 15;
    private readonly List<string> _clipboardRing = [];
    private CodeEditorController? _copyTracked;
    private Avalonia.Threading.DispatcherTimer? _autoRecoverTimer;

    public LocalHistory History { get; private set; } = null!;

    /// <summary>Copies and cuts in the editors, newest first (tests).</summary>
    internal IReadOnlyList<string> ClipboardRing => _clipboardRing;

    private string BackupsDirectory => Path.Combine(Workbench.Paths.State, "backups");

    private void InitializeEditingSafety()
    {
        History = new LocalHistory(Path.Combine(Workbench.Paths.State, "history"));
        _autoRecoverTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoRecoverTimer.Tick += (_, _) => BackUpModifiedDocuments();
        _autoRecoverTimer.Start();
    }

    private void TrackCopies(CodeEditorController? controller)
    {
        if (_copyTracked == controller)
        {
            return;
        }

        if (_copyTracked is not null)
        {
            _copyTracked.Copied -= OnCopied;
            _copyTracked.Pasted -= OnPasted;
        }

        _copyTracked = controller;
        if (controller is not null)
        {
            controller.Copied += OnCopied;
            controller.Pasted += OnPasted;
        }
    }

    public const string PasteAddsImportsSetting = "editor.pasteAddsImports";

    /// <summary>The last copy in an editor and the namespaces in scope where it was copied (paste with automatic usings).</summary>
    private (string Text, string[] Namespaces)? _copySource;

    private void OnCopied(object? sender, string text)
    {
        _copySource = sender is CodeEditorController source && source.Model.LanguageId is CSharpLanguageId or VisualBasicLanguageId
            ? (text, NamespacesInScope(source.Model.Text.Text))
            : null;
        _clipboardRing.Remove(text);
        _clipboardRing.Insert(0, text);
        if (_clipboardRing.Count > ClipboardRingSize)
        {
            _clipboardRing.RemoveAt(_clipboardRing.Count - 1);
        }
    }

    /// <summary>
    /// Paste with automatic usings (deferred from M12b): C# and VB code pasted into an editor gets the usings (Imports)
    /// it needs from the language service, preferring the namespaces of the file it was copied from. Undo removes them.
    /// </summary>
    private async void OnPasted(object? sender, PastedText pasted)
    {
        if (sender is not CodeEditorController { Model: { IsReadOnly: false } model } controller || model.LanguageId is not (CSharpLanguageId or VisualBasicLanguageId)
            || !Workbench.Settings.ForLanguage(model.LanguageId).GetBoolean(PasteAddsImportsSetting, true)
            || ((IEditorServices)this).Client(model) is not { } client)
        {
            return;
        }

        try
        {
            var document = model.Text;
            var range = new LanguageServices.Protocol.Range(PositionAt(document, pasted.Offset), PositionAt(document, pasted.Offset + pasted.Length));
            var preferred = _copySource is { } source && source.Text.ReplaceLineEndings() == pasted.Text.ReplaceLineEndings() ? source.Namespaces : null;
            var version = model.Version;
            var edit = await client.PasteImportsAsync(model.Uri, range, preferred, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            if (edit is not { DocumentChanges.Length: > 0 } || model.Version != version || ActiveEditor?.Controller != controller)
            {
                return; // nothing to add, or the user typed meanwhile
            }

            var added = edit.DocumentChanges.SelectMany(c => c.Edits).Select(e => e.NewText.Trim()).Where(t => t.Length > 0).ToList();
            if (await ApplyWorkspaceEditAsync(edit).ConfigureAwait(true))
            {
                StatusBar.ShowMessage(string.Join(" ", added).ReplaceLineEndings(" "), TimeSpan.FromSeconds(4));
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException)
        {
            LanguageLog.AppendLine($"[paste imports] {model.Name}: {ex.Message}");
        }
    }

    private static LanguageServices.Protocol.Position PositionAt(AvaloniaEdit.Document.TextDocument document, int offset)
    {
        var location = document.GetLocation(Math.Clamp(offset, 0, document.TextLength));
        return new LanguageServices.Protocol.Position(location.Line - 1, location.Column - 1);
    }

    /// <summary>The namespaces in scope in a C# or VB file: its usings (Imports) and the namespaces it declares.</summary>
    internal static string[] NamespacesInScope(string text) =>
    [
        .. NamespaceLines().Matches(text).Select(m => m.Groups["ns"].Value).Distinct(StringComparer.Ordinal),
    ];

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(?:global\s+)?(?:using|Imports|namespace|Namespace)\s+(?<ns>[A-Za-z_][\w.]*)\s*(?:;|\{|$)", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex NamespaceLines();

    /// <summary>Ctrl+Shift+V: pastes one of the last copies or cuts.</summary>
    public async Task PasteFromClipboardRingAsync()
    {
        if (ActiveEditor is not { Controller: { } controller } editor || editor.Model.IsReadOnly)
        {
            return;
        }

        if (_clipboardRing.Count == 0)
        {
            ShowMessage("The clipboard ring is empty: copy or cut something in the editor first.");
            return;
        }

        var items = _clipboardRing.Select((text, i) => new QuickPickItem(OneLine(text)) { Detail = $"{text.Length} characters", Tag = i }).ToList();
        if (await Palette.ShowAsync(items, "Paste from Clipboard Ring").ConfigureAwait(true) is not { Tag: int index })
        {
            return;
        }

        controller.Editor.TextArea.Selection.ReplaceSelectionWithText(_clipboardRing[index]);
    }

    private static string OneLine(string text)
    {
        var line = text.ReplaceLineEndings(" ⏎ ").Trim();
        return line.Length > 100 ? line[..100] + "…" : line;
    }

    /// <summary>Edit → Paste JSON as Classes: C# classes for the JSON on the clipboard, at the caret.</summary>
    public async Task PasteJsonAsClassesAsync()
    {
        if (ActiveEditor is not { Controller: { } controller } editor || editor.Model.IsReadOnly || Window?.Clipboard is not { } clipboard)
        {
            return;
        }

        var json = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(json))
        {
            ShowMessage("The clipboard has no text.");
            return;
        }

        string code;
        try
        {
            code = JsonClassGenerator.Generate(json, "Root");
        }
        catch (JsonException ex)
        {
            ShowMessage("The clipboard text is not valid JSON: " + ex.Message);
            return;
        }

        controller.Editor.TextArea.Selection.ReplaceSelectionWithText(code);
    }

    /// <summary>Edit → Paste XML as Classes: C# classes for the XML on the clipboard, for <c>XmlSerializer</c>, at the caret.</summary>
    public async Task PasteXmlAsClassesAsync()
    {
        if (ActiveEditor is not { Controller: { } controller } editor || editor.Model.IsReadOnly || Window?.Clipboard is not { } clipboard)
        {
            return;
        }

        var xml = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(xml))
        {
            ShowMessage("The clipboard has no text.");
            return;
        }

        string code;
        try
        {
            code = XmlClassGenerator.Generate(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            ShowMessage("The clipboard text is not valid XML: " + ex.Message);
            return;
        }

        controller.Editor.TextArea.Selection.ReplaceSelectionWithText(code);
    }

    // ------------------------------------------------------------------ saving: format on save, local history

    /// <summary>Ctrl+K Ctrl+E: Code Cleanup of the active C# document with the profile in <c>codeCleanup.fixes</c>.</summary>
    public async Task CodeCleanupAsync()
    {
        if (ActiveLanguageEditor() is not var (editor, _, client) || editor.Model.IsReadOnly)
        {
            return;
        }

        if (editor.Model.LanguageId != CSharpLanguageId)
        {
            ShowMessage("Code Cleanup is available for C#.");
            return;
        }

        if (await CleanUpAsync(client, editor.Model).ConfigureAwait(true))
        {
            StatusBar.ShowMessage(Localizer.Format("msg.editingSafety.codeCleanupFinishedOn", "Code Cleanup finished on {0}.", editor.Model.Name), TimeSpan.FromSeconds(4));
        }
        else
        {
            StatusBar.ShowMessage(Localizer.Get("msg.editingSafety.codeCleanupFoundNothingToChange", "Code Cleanup found nothing to change."), TimeSpan.FromSeconds(4));
        }
    }

    private async Task<bool> CleanUpAsync(LanguageServices.Client.LanguageHostClient client, CodeDocument model)
    {
        var fixes = Workbench.Settings.GetStrings(CodeCleanupFixesSetting);
        var edit = await client.CodeCleanupAsync(model.Uri, fixes.Count > 0 ? [.. fixes] : null, CancellationToken.None).ConfigureAwait(true);
        if (edit is not { DocumentChanges.Length: > 0 })
        {
            return false;
        }

        return await ApplyWorkspaceEditAsync(edit).ConfigureAwait(true);
    }

    /// <summary>Before a document saves: Code Cleanup (<c>codeCleanup.onSave</c>, C#) and Format Document (<c>editor.formatOnSave</c>).</summary>
    private async Task BeforeSaveAsync(CodeDocument model)
    {
        if (Workbench.Settings.ForLanguage(model.LanguageId).GetBoolean(CodeCleanupOnSaveSetting) && !model.IsReadOnly && model.LanguageId == CSharpLanguageId
            && ((IEditorServices)this).Client(model) is { } cleanupClient)
        {
            try
            {
                await CleanUpAsync(cleanupClient, model).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException)
            {
                LanguageLog.AppendLine($"[code cleanup on save] {model.Name}: {ex.Message}");
            }
        }

        if (!Workbench.Settings.ForLanguage(model.LanguageId).GetBoolean(FormatOnSaveSetting) || model.IsReadOnly)
        {
            return;
        }

        if (await FormatWithPrettierAsync(model).ConfigureAwait(true))
        {
            return;
        }

        if (((IEditorServices)this).Client(model) is not { Capabilities.DocumentFormattingProvider: true } client)
        {
            return;
        }

        try
        {
            var controller = Editors.FirstOrDefault(e => e.Model == model)?.Controller;
            var options = new LanguageServices.Protocol.FormattingOptions(controller?.Editor.Options.IndentationSize ?? 4, controller?.Editor.Options.ConvertTabsToSpaces ?? true);
            var edits = await client.FormatAsync(model.Uri, null, options, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            if (edits.Length > 0)
            {
                model.ApplyEdits(edits);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException)
        {
            LanguageLog.AppendLine($"[format on save] {model.Name}: {ex.Message}");
        }
    }

    /// <summary>After a document saved: its local history, its backup, and the user's snippets when they changed.</summary>
    private void AfterSave(CodeDocument model)
    {
        var text = model.Text.Text;
        _ = Task.Run(() =>
        {
            try
            {
                History.Record(model.Path, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Workbench.Log.AppendLine($"[history] {model.Path}: {ex.Message}");
            }
        });
        DeleteBackup(model.Path);
        if (Path.GetDirectoryName(model.Path) is { } folder && Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(folder, UserSnippetsDirectory))
        {
            LoadUserSnippets();
        }
    }

    /// <summary>Shows the active file's saved versions; the chosen one opens in a diff against the current text, with a restore choice.</summary>
    public async Task ShowLocalHistoryAsync()
    {
        if (ActiveEditor is not { } editor || editor.Model.IsReadOnly)
        {
            return;
        }

        var entries = History.Entries(editor.Model.Path);
        if (entries.Count == 0)
        {
            ShowMessage("No local history for this file yet: versions are kept each time it is saved.");
            return;
        }

        var items = entries.Select(e => new QuickPickItem(e.Time.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture))
        {
            Detail = Humanize(DateTimeOffset.UtcNow - e.Time),
            Tag = e,
        }).ToList();
        if (await Palette.ShowAsync(items, $"Local History: {editor.Model.Name}").ConfigureAwait(true) is not { Tag: HistoryEntry entry })
        {
            return;
        }

        var old = await File.ReadAllTextAsync(entry.File).ConfigureAwait(true);
        await OpenFileDiffAsync(entry.File, editor.Model.Path).ConfigureAwait(true); // the saved version against the file on disk
        var choice = await Workbench.Notifications.Info(Localizer.Format("msg.editingSafety.restoreToTheVersionOf", "Restore {0} to the version of {1:g}?", editor.Model.Name, entry.Time.ToLocalTime()), "Restore").Result.ConfigureAwait(true);
        if (choice == "Restore")
        {
            RestoreFromHistory(editor.Model, old);
        }
    }

    /// <summary>Replaces a document's text with a saved version (an undoable edit; the document stays unsaved).</summary>
    internal static void RestoreFromHistory(CodeDocument model, string text) => model.Text.Replace(0, model.Text.TextLength, text);

    private static string Humanize(TimeSpan age) => age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
        : $"{(int)age.TotalDays} days ago";

    // ------------------------------------------------------------------ AutoRecover

    private sealed record Backup(string Path, string Text);

    private string BackupFile(string path) =>
        Path.Combine(BackupsDirectory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..24] + ".json");

    /// <summary>Every 30 s: writes the text of each modified document to the backups folder (setting <c>files.autoRecover</c>).</summary>
    internal void BackUpModifiedDocuments()
    {
        if (!Workbench.Settings.GetBoolean(AutoRecoverSetting, true))
        {
            return;
        }

        var modified = _models.Values.Where(m => m.IsModified && !m.IsReadOnly).Select(m => new Backup(m.Path, m.Text.Text)).ToList();
        if (modified.Count == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(BackupsDirectory);
                foreach (var backup in modified)
                {
                    File.WriteAllText(BackupFile(backup.Path), JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = backup.Path, ["text"] = backup.Text }, Docking.ShellJsonContext.Default.DictionaryStringString));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Workbench.Log.AppendLine($"[autorecover] {ex.Message}");
            }
        });
    }

    private void DeleteBackup(string path)
    {
        try
        {
            File.Delete(BackupFile(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>On a clean exit (the user saved or discarded every change): no backups are left.</summary>
    internal void DeleteAllBackups()
    {
        try
        {
            if (Directory.Exists(BackupsDirectory))
            {
                Directory.Delete(BackupsDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>At startup: offers to restore the unsaved changes the last session left behind (it crashed or was killed).</summary>
    internal async Task OfferRecoveryAsync()
    {
        if (!Directory.Exists(BackupsDirectory))
        {
            return;
        }

        var backups = new List<Backup>();
        foreach (var file in Directory.EnumerateFiles(BackupsDirectory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize(await File.ReadAllTextAsync(file).ConfigureAwait(true), Docking.ShellJsonContext.Default.DictionaryStringString) is { } data
                    && data.TryGetValue("path", out var path) && data.TryGetValue("text", out var text))
                {
                    backups.Add(new Backup(path, text));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
            }
        }

        if (backups.Count == 0)
        {
            return;
        }

        var choice = await Workbench.Notifications.Warning(
            backups.Count == 1 ? $"Codalus IDE recovered unsaved changes to {Path.GetFileName(backups[0].Path)}." : $"Codalus IDE recovered unsaved changes to {backups.Count} files.",
            "Restore", "Discard").Result.ConfigureAwait(true);
        if (choice == "Restore")
        {
            foreach (var backup in backups)
            {
                if (await OpenFileAsync(backup.Path).ConfigureAwait(true) is { } document)
                {
                    RestoreFromHistory(document.Model, backup.Text);
                }
            }
        }
        else if (choice == "Discard")
        {
            DeleteAllBackups();
        }
    }
}
