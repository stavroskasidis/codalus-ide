using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
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
    };

    /// <summary>A diff document for a view model: the shell's services, the layout from the settings, shortcuts and opening files.</summary>
    private DiffDocument NewDiffDocument(DiffViewModel viewModel)
    {
        viewModel.LoadLayout(Workbench.Settings.GetString(BuiltIns.DiffLayoutSetting, DiffViewModel.LayoutSideBySide));
        viewModel.SaveLayout = vm => _ = SaveDiffLayoutAsync(vm);
        viewModel.ShortcutFor = command => Workbench.Keybindings.GetBinding(command)?.ToLabel(Workbench.Platform);
        viewModel.OpenFile = (path, line) => OpenFileAsync(path, at: new Position(line - 1, 0));
        return new DiffDocument(viewModel, EditorFont, DiffServices);
    }

    private async Task SaveDiffLayoutAsync(DiffViewModel viewModel)
    {
        try
        {
            await Workbench.Settings.SetAsync(BuiltIns.DiffLayoutSetting, viewModel.Layout, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Workbench.Log.AppendLine($"[diff] The diff's layout wasn't saved: {ex.Message}");
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
