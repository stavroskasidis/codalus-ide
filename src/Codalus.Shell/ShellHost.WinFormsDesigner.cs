using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Output;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell;

/// <summary>
/// The Windows Forms designer (M41a, ADR 0073): a form's or user control's code file opens in the designer on Windows
/// (View Code with F7, View Designer with Shift+F7, as Visual Studio); the Toolbox; the Properties window over the
/// designer's selection. Off Windows the designer is Windows only (D31): the form opens as code, and View Designer says so.
/// </summary>
public sealed partial class ShellHost
{
    public const string OpenFormsInDesignerSetting = "windowsForms.openInDesigner";

    private readonly Dictionary<CodeDocument, int> _heldModels = [];
    private IDesignerPropertySource? _propertySource;
    private bool _toldWindowsOnly;
    private string? _winFormsCSharpState;

    /// <summary>The designer's service (tests replace it to run a fake designer host on any OS).</summary>
    public WinFormsDesignerService WinFormsDesigner { get; set; } = null!;

    public ToolboxViewModel Toolbox { get; } = new();

    public OutputChannel WinFormsDesignerLog => Workbench.Output.GetOrCreate("codalus.winformsDesigner", "Windows Forms Designer");

    /// <summary>A designer whose selection the Properties window shows instead of the Solution Explorer's (null: the Solution Explorer's).</summary>
    public IDesignerPropertySource? PropertySource
    {
        get => _propertySource;
        set
        {
            if (!ReferenceEquals(_propertySource, value))
            {
                _propertySource = value;
                PropertySourceChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (value is not null)
            {
                PropertySourceChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? PropertySourceChanged;

    private void InitializeWinFormsDesigner()
    {
        WinFormsDesigner = new WinFormsDesignerService(Solutions, path => Trust.IsTrusted(path), BuildForPreviewAsync, PreviewPropertiesAsync)
        {
            StartupProject = () => Runs.StartupProject,
        };
    }

    /// <summary>The designer's hooks into the layout (once it exists).</summary>
    private void WireWinFormsDesigner()
    {
        Layout.ActiveDocumentChanged += (_, document) =>
        {
            Toolbox.IsAvailable = document is WinFormsDesignerDocument;
            if (document is WinFormsDesignerDocument designer)
            {
                PropertySource = designer;
                _ = designer.LoadToolboxAsync(); // the Toolbox has the active designer's project's controls
            }

            Workbench.Context.Set("editorHasWinFormsDesigner", ActiveEditor?.Model.Path is { } path && WinFormsDesignerService.CodeFileOf(path) is { } code && WinFormsDesigner.HasDesigner(code));
        };
        Layout.DocumentClosed += (_, document) =>
        {
            if (document is WinFormsDesignerDocument designer)
            {
                _ = designer.CloseAsync();
            }
        };
        Languages.StatusChanged += (_, e) => Ui.Post(() => OnWinFormsLanguageStatus(e.LanguageId, e.Status));
        Explorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SolutionExplorerViewModel.Selected))
            {
                Workbench.Context.Set("explorerItemHasWinFormsDesigner", Explorer.Selected?.Node is { Kind: Codalus.Workbench.Projects.ExplorerNodeKind.File, Path: { } file }
                    && WinFormsDesignerService.CodeFileOf(file) is { } code && WinFormsDesigner.HasDesigner(code));
            }
        };
    }

    /// <summary>
    /// The C# service has loaded the projects again (loading, then ready): the active designer asks again for the
    /// project's own controls, which a compilation from before the restore lacked (off Windows, Windows Forms' reference
    /// pack comes with the restore, and the C# service is ready before it). Only on that change: the service is also
    /// ready after each pass over the diagnostics (each edit, each change in the designer), which changes no project.
    /// </summary>
    internal void OnWinFormsLanguageStatus(string languageId, WorkspaceStatusParams status)
    {
        if (languageId != CSharpLanguageId)
        {
            return;
        }

        var was = _winFormsCSharpState;
        _winFormsCSharpState = status.State;
        if (status.State == WorkspaceStates.Ready && was != WorkspaceStates.Ready && Layout.ActiveDocument is WinFormsDesignerDocument designer)
        {
            _ = designer.LoadToolboxAsync();
        }
    }

    /// <summary>Whether opening <paramref name="path"/> shows the designer: a form's code file, on Windows, unless the setting is off.</summary>
    public bool OpensInWinFormsDesigner(string path) =>
        Workbench.Settings.GetBoolean(OpenFormsInDesignerSetting, true) && WinFormsDesigner.IsWindows() && Remote is null && WinFormsDesigner.HasDesigner(path);

    /// <summary>Off Windows, the first form opened says once why it opens as code.</summary>
    private void TellWinFormsWindowsOnly(string path)
    {
        if (!_toldWindowsOnly && !WinFormsDesigner.IsWindows() && WinFormsDesigner.HasDesigner(path))
        {
            _toldWindowsOnly = true;
            StatusBar.ShowMessage(Localizer.Get("msg.winFormsDesigner.windowsOnly", "The Windows Forms designer runs on Windows only: Windows Forms draws only there. On this OS the form opens as code, which edits, completes and builds."), TimeSpan.FromSeconds(8));
        }
    }

    /// <summary>
    /// View Designer: opens a form's code file (or its designer file's form) in the designer. Off Windows it says the
    /// designer is Windows only and opens the code instead.
    /// </summary>
    public async Task OpenWinFormsDesignerAsync(string path, bool preview = false)
    {
        var code = WinFormsDesignerService.CodeFileOf(Path.GetFullPath(path)) ?? Path.GetFullPath(path);
        if (!WinFormsDesigner.IsWindows() || Remote is not null)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.winFormsDesigner.windowsOnly", "The Windows Forms designer runs on Windows only: Windows Forms draws only there. On this OS the form opens as code, which edits, completes and builds."));
            await OpenFileAsync(code, preview, at: null, initialContent: null, textEditor: true).ConfigureAwait(true);
            return;
        }

        if (Layout.FindDocument<WinFormsDesignerDocument>(WinFormsDesignerDocument.IdFor(code)) is { } open)
        {
            Layout.OpenDocument(open, preview, focus: !preview);
            return;
        }

        var document = new WinFormsDesignerDocument(this, code);
        document.CloseRequested += (_, _) => _ = ConfirmCloseAsync(document);
        Layout.OpenDocument(document, preview, focus: !preview);
        await document.LoadAsync().ConfigureAwait(true);
    }

    /// <summary>View Code (F7): the designer's code file.</summary>
    public async Task ViewWinFormsCodeAsync(WinFormsDesignerDocument? document = null)
    {
        if ((document ?? Layout.ActiveDocument as WinFormsDesignerDocument) is { } designer)
        {
            await OpenFileAsync(designer.CodePath, preview: false, at: null, initialContent: null, textEditor: true).ConfigureAwait(true);
        }
    }

    /// <summary>View Designer (Shift+F7): the active editor's form, or the form selected in the Solution Explorer.</summary>
    public async Task ViewWinFormsDesignerAsync()
    {
        var selected = Explorer.Selected?.Node is { Kind: Codalus.Workbench.Projects.ExplorerNodeKind.File, Path: { } file } ? file : null;
        var path = Workbench.Context.GetValue("focusedView") is "solutionExplorer" ? selected ?? ActiveEditor?.Model.Path : ActiveEditor?.Model.Path ?? selected;
        if (path is not null && WinFormsDesignerService.CodeFileOf(path) is { } code && WinFormsDesigner.HasDesigner(code))
        {
            await OpenWinFormsDesignerAsync(code).ConfigureAwait(true);
        }
        else
        {
            StatusBar.ShowMessage(Localizer.Get("msg.winFormsDesigner.notAForm", "This file has no designer: View Designer opens a form's or user control's code file."), TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>Code the designer can't read: the designer closes and the code opens (at the statement, when there is one), with the reason, as Visual Studio does.</summary>
    internal async Task OpenWinFormsAsCodeAsync(WinFormsDesignerDocument document, string problem, (string Path, int Line)? at)
    {
        Layout.CloseDocument(document);
        Workbench.Notifications.Warning(problem);
        WinFormsDesignerLog.AppendLine(problem);
        if (at is { } where)
        {
            await OpenFileAsync(where.Path, preview: false, at: new Codalus.LanguageServices.Protocol.Position(where.Line, 0), initialContent: null, textEditor: true).ConfigureAwait(true);
        }
        else
        {
            await OpenFileAsync(document.CodePath, preview: false, at: null, initialContent: null, textEditor: true).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ text models a designer holds

    /// <summary>
    /// The text model of <paramref name="path"/> (the editor's when it's open), kept while a designer holds it, so the
    /// designer's edits go through the same undo stack, language service and save as typing in the code view.
    /// </summary>
    internal async Task<CodeDocument> AcquireModelAsync(string path)
    {
        path = Path.GetFullPath(path);
        BeginModelRequest(path);
        try
        {
            var model = await ModelAsync(path).ConfigureAwait(true);
            _heldModels[model] = _heldModels.GetValueOrDefault(model) + 1;
            return model;
        }
        finally
        {
            EndModelRequest(path);
        }
    }

    /// <summary>A designer lets go of a text model: forgotten when no editor shows it and no one else is about to take it.</summary>
    internal void ReleaseModel(CodeDocument model)
    {
        if (_heldModels.TryGetValue(model, out var count) && count > 1)
        {
            _heldModels[model] = count - 1;
            return;
        }

        _heldModels.Remove(model);
        if (Editors.Any(e => e.Model == model) || IsHeld(model))
        {
            return;
        }

        _models.Remove(model.Path);
        lock (_snapshots)
        {
            _snapshots.Remove(model);
        }

        if (model.LanguageId is not null)
        {
            _ = Languages.CloseAsync(model.Path);
        }

        model.Dispose();
    }

    /// <summary>Whether a designer (or the Code Search preview) holds the model, or a caller waits for its file's model to take it.</summary>
    internal bool IsHeld(CodeDocument model) => _heldModels.ContainsKey(model) || _modelRequests.ContainsKey(model.Path);

    /// <summary>Whether a code editor shows <paramref name="path"/>.</summary>
    internal bool HasEditorFor(string path) => Editors.Any(e => SolutionPaths.Comparer.Equals(e.Model.Path, path) && e.Owner is not null);

    internal Task<bool> SaveModelAsync(CodeDocument model) => SaveAsync(model);
}
