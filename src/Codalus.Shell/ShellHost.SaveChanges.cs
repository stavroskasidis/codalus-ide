using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Dock.Model.Core;

namespace Codalus.Shell;

/// <summary>
/// Asking to save before closing, as the classic IDE does: one modal dialog, "Save changes to the following items?", for
/// closing a tab, Close All (and All But This, All But Pinned, All to the Right), closing the solution and exiting. It lists
/// the unsaved files under their solution, solution folders and project (<see cref="SaveChangesTree"/>), each checked when
/// there are several: Save saves the checked ones and discards the changes to the others, Don't Save discards them all,
/// and Cancel (or Escape) closes nothing. A save that fails closes nothing either.
/// </summary>
public sealed partial class ShellHost
{
    private const string SaveButton = "Save";
    internal const string DontSaveButton = "Don't Save";
    private const string CancelButton = "Cancel";

    /// <summary>The save-changes dialog's title.</summary>
    internal static string SaveChangesTitle => Localizer.Get("msg.saveChanges.title", "Save changes to the following items?");

    /// <summary>The documents a save-changes dialog is open for: a second click on a tab's close asks once.</summary>
    private readonly HashSet<Docking.ShellDocument> _askingToSave = [];

    /// <summary>A file (or document) with unsaved changes: its name, its file, the documents that show it, and what saves it.</summary>
    private sealed record UnsavedItem(string Name, string? Path, IReadOnlyList<Docking.ShellDocument> Documents, Func<Task<bool>> SaveAsync);

    /// <summary>Whether a file or document has unsaved changes (exiting asks).</summary>
    public bool HasUnsavedDocuments =>
        _models.Values.Any(m => m.IsModified) || (Layout is { } layout && layout.Documents.Any(d => d is not CodeEditorDocument && d.AsksToSave));

    /// <summary>
    /// The unsaved files among <paramref name="closing"/>: a code file whose every view closes (a file still shown in
    /// another view keeps its changes there), and each other document that asks to save. With <paramref name="everyModel"/>
    /// (exiting), every unsaved code file, shown or not.
    /// </summary>
    private List<UnsavedItem> UnsavedAmong(IReadOnlyCollection<Docking.ShellDocument> closing, bool everyModel = false)
    {
        var items = new List<UnsavedItem>();
        foreach (var model in _models.Values.Where(m => m.IsModified).ToList())
        {
            var views = Editors.Where(e => e.Model == model).ToList<Docking.ShellDocument>();
            if (everyModel || (views.Count > 0 && views.All(closing.Contains)))
            {
                items.Add(new UnsavedItem(model.Name, model.IsReadOnly ? null : model.Path, views, () => SaveAsync(model)));
            }
        }

        foreach (var document in closing.Where(d => d is not CodeEditorDocument && d.AsksToSave))
        {
            items.Add(new UnsavedItem(document.Title, document.FilePath, [document],
                () => document is Views.ISavableDocument savable ? SaveDocumentAsync(document, savable) : Task.FromResult(false)));
        }

        return items;
    }

    /// <summary>
    /// Asks about <paramref name="items"/> with the save-changes dialog and saves what the answer says. True when the
    /// close may go on: Don't Save, or Save with every checked file saved; false for Cancel, or a save that failed.
    /// </summary>
    private async Task<bool> AskToSaveAsync(IReadOnlyList<UnsavedItem> items)
    {
        if (items.Count == 0)
        {
            return true;
        }

        var several = items.Count > 1;
        var answer = await Dialogs.ShowAsync(SaveChangesRequest(SaveChangesTree.Rows(Solutions.Solution, [.. Solutions.Projects.Values], [.. items.Select(i => (i.Name, i.Path))])))
            .ConfigureAwait(true);
        switch (answer.Button)
        {
            case DontSaveButton:
                return true;
            case SaveButton:
                var saved = true;
                for (var i = 0; i < items.Count; i++)
                {
                    if (!several || answer.Checked.Contains(SaveChangesTree.FileId(i)))
                    {
                        saved &= await items[i].SaveAsync().ConfigureAwait(true);
                    }
                }

                return saved;
            default:
                return false;
        }
    }

    /// <summary>The save-changes dialog for <paramref name="rows"/> (<see cref="SaveChangesTree.Rows"/>); Save is the default.</summary>
    internal static DialogRequest SaveChangesRequest(IReadOnlyList<DialogChoice> rows) => new(
        SaveChangesTitle,
        rows.Any(r => r.Checkable) ? Localizer.Get("msg.saveChanges.checkedOnesAreSaved", "Save saves the checked files and discards the changes to the others.") : string.Empty,
        [SaveButton, DontSaveButton, CancelButton])
    {
        Choices = rows,
        Icon = Controls.CommandIcons.Save,
    };

    /// <summary>
    /// Asks about the unsaved files among <paramref name="closing"/>, once, and lets their documents close without asking
    /// again; false (nothing may close) for Cancel or a save that failed.
    /// </summary>
    private async Task<bool> AskBeforeClosingAsync(IReadOnlyList<Docking.ShellDocument> closing)
    {
        var items = UnsavedAmong(closing);
        if (!await AskToSaveAsync(items).ConfigureAwait(true))
        {
            return false;
        }

        foreach (var document in items.SelectMany(i => i.Documents))
        {
            document.SkipSavePrompt();
        }

        return true;
    }

    /// <summary>A tab with unsaved changes was closed: the save-changes dialog for its file, then it closes unless cancelled.</summary>
    private async Task ConfirmCloseAsync(Docking.ShellDocument document)
    {
        if (!_askingToSave.Add(document))
        {
            return;
        }

        try
        {
            if (document.AsksToSave && await AskToSaveAsync(UnsavedAmong([document])).ConfigureAwait(true))
            {
                document.CloseConfirmed();
            }
        }
        finally
        {
            _askingToSave.Remove(document);
        }
    }

    /// <summary>Closes <paramref name="documents"/> together (M56), asking once about the unsaved ones among them; Cancel closes none.</summary>
    private async Task CloseDocumentsAsync(IReadOnlyList<Docking.ShellDocument> documents)
    {
        if (documents.Count > 0 && await AskBeforeClosingAsync(documents).ConfigureAwait(true))
        {
            Layout.CloseDocuments(documents.Where(d => d.Owner is not null));
        }
    }

    /// <summary>Close All (or Close All But This): together, so no tab becomes active on the way (M56).</summary>
    public Task CloseAllDocumentsAsync(bool exceptActive)
    {
        var active = Layout.ActiveDocument;
        return CloseDocumentsAsync([.. Layout.Documents.Where(d => !exceptActive || d != active)]);
    }

    /// <summary>Closes every document that isn't pinned (M34d), together.</summary>
    public Task CloseUnpinnedDocumentsAsync() => CloseDocumentsAsync([.. Layout.Documents.Where(d => !d.IsPinned)]);

    /// <summary>Window > Close All to the Right: the tabs after the active one in its group.</summary>
    public Task CloseDocumentsToTheRightAsync()
    {
        if (Layout.ActiveDocument is not { Owner: IDock { VisibleDockables: { } tabs } } active)
        {
            return Task.CompletedTask;
        }

        return CloseDocumentsAsync([.. tabs.Skip(tabs.IndexOf(active) + 1).OfType<Docking.ShellDocument>()]);
    }

    /// <summary>Asks before quitting with unsaved changes; true when the window may close.</summary>
    public async Task<bool> ConfirmQuitAsync()
    {
        if (!await AskToSaveAsync(UnsavedAmong([.. Layout.Documents], everyModel: true)).ConfigureAwait(true))
        {
            return false;
        }

        DeleteAllBackups();
        return true;
    }
}
