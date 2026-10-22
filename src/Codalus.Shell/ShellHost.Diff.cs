using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// The diff document (#86, D45): what it needs from the shell to color its sides like the editor (the grammars, the
/// languages of files, the open documents and their language services).
/// </summary>
public sealed partial class ShellHost
{
    private DiffServices? _diffServices;

    /// <summary>The services of diff documents and merge editors.</summary>
    public DiffServices DiffServices => _diffServices ??= new DiffServices(this)
    {
        LanguageOf = path => CodeColors.LanguageOf(path),
        OpenDocument = OpenDocumentOf,
        OpenEditor = path => OpenDocumentOf(path) is { } model ? Layout.Documents.OfType<CodeEditorDocument>().FirstOrDefault(d => d.Model == model)?.Controller : null,
        SemanticTokensAsync = SemanticTokensForAsync,
        SaveAsync = SaveAsync,
        ShowDialogAsync = request => Dialogs.ShowAsync(request),
        ShowMessage = message => Workbench.Notifications.Warning(message),
        DiscardAllAsync = (path, unsavedEditsLost) => DiscardAsync(path, unsavedEditsLost),
    };

    /// <summary>The diff documents of a file's working tree (they show its open buffer).</summary>
    private IEnumerable<DiffDocument> WorkingTreeDiffsOf(string path) =>
        Layout.Documents.OfType<DiffDocument>().Where(d => d.ViewModel is { IsGitDiff: true, Staged: false } vm && Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(vm.Path, path));

    /// <summary>An open document's edits and saves refresh its working-tree diffs (D45 question 11), after a pause.</summary>
    private void TrackForDiffs(CodeDocument model)
    {
        model.ContentChanged += (_, _) => Ui.Post(() => ReloadDiffs(WorkingTreeDiffsOf(model.Path), TimeSpan.FromMilliseconds(500)));
        model.Saved += (_, _) => Ui.Post(() => ReloadDiffs(WorkingTreeDiffsOf(model.Path), TimeSpan.FromMilliseconds(100)));
    }

    /// <summary>The index or the status changed: every Git diff reads its texts again, after a pause.</summary>
    private void ReloadGitDiffs() => ReloadDiffs(Layout.Documents.OfType<DiffDocument>().Where(d => d.ViewModel.IsGitDiff), TimeSpan.FromMilliseconds(300));

    private static void ReloadDiffs(IEnumerable<DiffDocument> documents, TimeSpan delay)
    {
        foreach (var document in documents.ToList())
        {
            document.ViewModel.RequestReload(delay);
        }
    }

    /// <summary>The context keys of the staging commands: what the active diff allows.</summary>
    private void UpdateDiffContext()
    {
        var vm = (Layout.ActiveDocument as DiffDocument)?.ViewModel;
        Workbench.Context.Set("diffCanStage", vm?.CanStage == true);
        Workbench.Context.Set("diffCanUnstage", vm?.CanUnstage == true);
    }

    /// <summary>Runs a diff command that takes time on the active diff document's view.</summary>
    public Task OnDiffAsync(Func<DiffView, Task> action) =>
        Layout.ActiveDocument is DiffDocument { View: { } view } ? action(view) : Task.CompletedTask;

    /// <summary>A diff document for a view model: the shell's services, the layout from the settings, shortcuts and opening files.</summary>
    private DiffDocument NewDiffDocument(DiffViewModel viewModel)
    {
        viewModel.LoadLayout(Workbench.Settings.GetString(BuiltIns.DiffLayoutSetting, DiffViewModel.LayoutSideBySide));
        viewModel.SaveLayout = vm => _ = SaveDiffLayoutAsync(vm);
        viewModel.ShortcutFor = command => Workbench.Keybindings.GetBinding(command)?.ToLabel(Workbench.Platform);
        viewModel.OpenFile = (path, line) => OpenFileAsync(path, at: new Position(line - 1, 0));
        viewModel.OpenStaged = path => OpenDiffAsync(path, staged: true);
        viewModel.LoadStagingControls(Workbench.Settings.GetBoolean(BuiltIns.DiffStagingControlsSetting, false));
        viewModel.SaveStagingControls = vm => _ = SaveDiffSettingAsync(BuiltIns.DiffStagingControlsSetting, vm.ShowStagingControls);
        return new DiffDocument(viewModel, EditorFont, DiffServices);
    }

    private Task SaveDiffLayoutAsync(DiffViewModel viewModel) =>
        SaveDiffSettingAsync(BuiltIns.DiffLayoutSetting, () => Workbench.Settings.SetAsync(BuiltIns.DiffLayoutSetting, viewModel.Layout, SettingScope.User, CancellationToken.None));

    private Task SaveDiffSettingAsync(string key, bool value) =>
        SaveDiffSettingAsync(key, () => Workbench.Settings.SetAsync(key, value, SettingScope.User, CancellationToken.None));

    private async Task SaveDiffSettingAsync(string key, Func<Task> save)
    {
        try
        {
            await save().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Workbench.Log.AppendLine($"[diff] {key} wasn't saved: {ex.Message}");
        }
    }

    /// <summary>Runs a diff command on the active diff document's view.</summary>
    public Task OnDiff(Action<DiffView> action)
    {
        if (Layout.ActiveDocument is DiffDocument { View: { } view })
        {
            action(view);
        }

        return Task.CompletedTask;
    }

    /// <summary>Runs a diff command on the active diff document's view model (it needs no view).</summary>
    public Task OnDiffDocument(Action<DiffViewModel> action)
    {
        if (Layout.ActiveDocument is DiffDocument document)
        {
            action(document.ViewModel);
        }

        return Task.CompletedTask;
    }

    private CodeDocument? OpenDocumentOf(string path) =>
        _models.TryGetValue(Path.GetFullPath(path), out var model) && !model.IsReadOnly ? model : null;

    /// <summary>
    /// The language service's semantic tokens for the whole open document of <paramref name="path"/> while its text is
    /// <paramref name="text"/> (the side of a diff that is the open document), in the shell's legend; null otherwise.
    /// </summary>
    private async Task<int[]?> SemanticTokensForAsync(string path, string text, CancellationToken cancellationToken)
    {
        if (OpenDocumentOf(path) is not { } model || model.Text.Text != text
            || ((IEditorServices)this).Client(model) is not { Capabilities.SemanticTokensProvider: { } provider } client)
        {
            return null;
        }

        var version = model.Version;
        var range = provider.Range ? new LanguageServices.Protocol.Range(new Position(0, 0), new Position(model.Text.LineCount, 0)) : null;
        var tokens = await client.SemanticTokensAsync(model.Uri, range, cancellationToken).ConfigureAwait(true);
        return version == model.Version ? SemanticColorizer.Remap(tokens.Data, provider.Legend.TokenTypes) : null;
    }
}
