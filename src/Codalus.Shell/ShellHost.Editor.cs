using System.Text.Json;
using AvaloniaEdit.Document;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Platform;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Debugging;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Build;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Languages;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// The editor (ADR 0005): one <see cref="CodeDocument"/> per open file, shown in one or more tabs; the language
/// service for the files' languages; saving, reloading, navigation and the editor's status bar items.
/// </summary>
public sealed partial class ShellHost : IEditorServices
{
    public const string CSharpLanguageId = "csharp";

    public const string RazorLanguageId = "razor";

    /// <summary>Avalonia XAML (M19a, ADR 0047), served by the C# host.</summary>
    public const string AxamlLanguageId = "axaml";

    /// <summary><c>.xaml</c> files (.NET MAUI's, M20a): the C# host serves them with MAUI's dialect when their project references MAUI.</summary>
    public const string XamlLanguageId = "xaml";

    /// <summary>Visual Basic (M35): served by the C# host, on Roslyn's VB services.</summary>
    public const string VisualBasicLanguageId = "vb";
    private const string StatusSource = "codalus.editor";

    private readonly Dictionary<string, CodeDocument> _models = new(SolutionPaths.Comparer);
    private readonly Dictionary<string, Task<CodeDocument>> _modelReads = new(SolutionPaths.Comparer);
    private readonly Dictionary<string, int> _modelRequests = new(SolutionPaths.Comparer);
    private readonly Dictionary<CodeDocument, ITextSource> _snapshots = [];
    private readonly List<(string Path, Position Position)> _back = [];
    private readonly List<(string Path, Position Position)> _forward = [];
    private int _nextViewId = 1;

    public LanguageService Languages { get; private set; } = null!;

    public ErrorListViewModel ErrorList { get; private set; } = null!;

    public EditorOptions EditorOptions { get; private set; } = null!;

    /// <summary>The active editor tab, if the active document is one.</summary>
    public CodeEditorDocument? ActiveEditor => Layout.ActiveDocument as CodeEditorDocument;

    public IEnumerable<CodeEditorDocument> Editors => Layout.Documents.OfType<CodeEditorDocument>();

    /// <summary>
    /// The editor tabs and Code Search's preview, which isn't a tab but asks the language service for the same things
    /// (semantic colors, the navigation bar's symbols, sticky scroll): what the language service's start, restart and
    /// readiness refresh.
    /// </summary>
    private IEnumerable<CodeEditorDocument> LanguageViews => _previewDocument is { } preview ? Editors.Append(preview) : Editors;

    public event EventHandler? ThemeChanged;

    public event EventHandler? ProjectContextsChanged;

    EditorOptions IEditorServices.Options => EditorOptions;

    EditorTheme IEditorServices.Theme => EditorTheme;

    /// <summary>TextMate grammars and language configurations (ADR 0016).</summary>
    public TextMateSupport TextMate { get; private set; } = null!;

    TextMateSupport IEditorServices.TextMate => TextMate;

    DiagnosticsStore IEditorServices.Diagnostics => Languages.Diagnostics;

    /// <summary>The number of projects from which the C# host runs with the server garbage collector (<see cref="WithServerGc"/>).</summary>
    internal const int ServerGcProjects = 20;

    /// <summary>
    /// The C# host's launch for a solution of <paramref name="projects"/> projects (0.9). A large solution's host runs with
    /// the server garbage collector: its background pass compiles and binds on many threads at once, which waited on the
    /// workstation collector's single-threaded collections (measured locally: a 94-project solution's first pass took
    /// 92 s, 51 s with the server collector, at the same peak of about 6 GB). A small solution keeps the workstation
    /// collector, which keeps less memory when idle (250 MB against 395 for 50 small projects). The host keeps the
    /// collector it started with: a file opened before the solution starts it on the workstation one.
    /// </summary>
    internal static Rpc.ProcessLaunchInfo? WithServerGc(Rpc.ProcessLaunchInfo? launch, int projects) =>
        launch is null || projects < ServerGcProjects
            ? launch
            : launch with
            {
                Environment = new Dictionary<string, string>(launch.Environment ?? new Dictionary<string, string>(), StringComparer.Ordinal)
                {
                    ["DOTNET_gcServer"] = "1",
                },
            };

    private void InitializeEditor()
    {
        EditorOptions = new EditorOptions(Workbench.Settings);
        var registry = Workbench.LanguageServers;
        registry.Register(new LanguageServiceDescriptor(CSharpLanguageId, "C#", [".cs", ".csx"], "C#", () => WithServerGc(HostLaunchInfo.Resolve(), Solutions.Solution?.Projects.Length ?? 0))
        {
            // Razor files go to the C# host, which serves them through the Razor source generator (ADR 0023), and so
            // does Avalonia XAML, on the same projects' type information (ADR 0047).
            Languages = [CSharpLanguageId, RazorLanguageId, AxamlLanguageId, XamlLanguageId, VisualBasicLanguageId],

            // Visual Basic's projects too (M35): one workspace, so C# and VB projects reference each other.
            MoreProjectLanguages = ["VB"],
        });
        registry.Register(new LanguageServiceDescriptor("markup", "JSON, XML and YAML", [], null, MarkupHostLaunch.Resolve)
        {
            Languages = MarkupHostLaunch.Languages,
            InitializationOptions = MarkupOptions(),
            Settings = MarkupOptions,
        });
        BuiltInLanguages.Register(Workbench.LanguageDefinitions);
        TextMate = new TextMateSupport(Workbench.LanguageDefinitions) { Log = line => LanguageLog.AppendLine(line) };
        Languages = new LanguageService(registry, Solutions, languages: Workbench.LanguageDefinitions)
        {
            SettingsProvider = section => section is { Length: > 0 } ? Workbench.Settings.GetSection(section) : null,
            PropertyEvaluator = EvaluatePropertyAsync,
            ApplyEditHandler = edit => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyWorkspaceEditAsync(edit)),
        };
        Languages.DocumentChanged += (_, path) => Ui.Post(() => Explorer.OnFileChanged(path)); // members under files
        ErrorList = new ErrorListViewModel(Languages.Diagnostics, Builds, Solutions, () => Layout is null ? null : ActiveEditor?.Model.Path, (path, position) => OpenFileAsync(path, preview: false, position), Workbench.Settings)
        {
            FixWithAi = FixErrorWithAiAsync,
            ExplainWithAi = row => AskAiAboutDiagnosticAsync(row.Path, row.Position.Line + 1, row.Code, row.Description),
            CanUseAi = () => Ai.IsEnabled,
        };
    }

    /// <summary>
    /// The markup host's options: schema downloads (setting <c>json.schemaDownload</c>), their cache folder, and the XML
    /// schema associations (setting <c>xml.schemas</c>: a glob over file paths → an XSD, relative to the solution's folder).
    /// </summary>
    private System.Text.Json.JsonElement MarkupOptions()
    {
        var associations = new System.Text.Json.Nodes.JsonArray();
        var root = Solutions?.Solution?.Path is { } solution ? Path.GetDirectoryName(solution) : null;
        if (Workbench.Settings.GetValue(XmlSchemasSetting) is { ValueKind: System.Text.Json.JsonValueKind.Object } schemas)
        {
            foreach (var association in schemas.EnumerateObject().Where(a => a.Value.ValueKind == System.Text.Json.JsonValueKind.String))
            {
                var schema = association.Value.GetString()!;
                associations.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["pattern"] = association.Name,
                    ["schema"] = Path.IsPathRooted(schema) || root is null ? schema : Path.GetFullPath(Path.Combine(root, schema)),
                });
            }
        }

        var options = new System.Text.Json.Nodes.JsonObject
        {
            ["schemaDownload"] = Workbench.Settings.GetBoolean(SchemaDownloadSetting, true),
            ["schemaCache"] = Path.Combine(Paths.Cache, "schemas"),
            ["xmlSchemas"] = associations,
        };
        using var document = System.Text.Json.JsonDocument.Parse(options.ToJsonString());
        return document.RootElement.Clone();
    }

    public const string SchemaDownloadSetting = "json.schemaDownload";

    public const string XmlSchemasSetting = "xml.schemas";

    /// <summary>A project's evaluated property and where it is set, for MSBuild hover (from the build host, M9b).</summary>
    private async Task<(string? Value, string? Origin)?> EvaluatePropertyAsync(string project, string name, CancellationToken cancellationToken)
    {
        if (!Solutions.Projects.ContainsKey(project))
        {
            return null;
        }

        var result = await ProjectManagement.GetPropertiesAsync(project, [name], null, cancellationToken).ConfigureAwait(false);
        if (result.Properties.FirstOrDefault() is not { } property)
        {
            return null;
        }

        var where = property.DefinedIn is { } file ? $"{Path.GetFileName(file)}:{property.Line}" : null;
        var origin = property.Origin switch
        {
            Projects.Protocol.PropertyOrigins.Project => $"set in {where}",
            Projects.Protocol.PropertyOrigins.Import => $"set in {where}",
            Projects.Protocol.PropertyOrigins.Sdk => "a default of the SDK" + (where is null ? string.Empty : $" ({where})"),
            Projects.Protocol.PropertyOrigins.Global => "a global property",
            _ => "not set",
        };
        return (property.Value, origin + $" ({result.Configuration}|{result.Platform})");
    }

    /// <summary>The editor's reactions to the layout (after <see cref="InitializeEditor"/> and the layout exist).</summary>
    private void WireEditorToLayout()
    {
        Languages.Log += (_, line) => LanguageLog.AppendLine(line);
        Languages.HostCrashed += (_, e) => Ui.Post(() =>
        {
            var name = Workbench.LanguageServers.Get(e.LanguageId)?.DisplayName ?? e.LanguageId;
            if (e.WillRestart)
            {
                Workbench.Notifications.Warning(Localizer.Format("msg.editor.theLanguageServiceStoppedUnexpectedlyAnd", "The {0} language service stopped unexpectedly and is restarting.", name));
            }
            else
            {
                Workbench.Notifications.Error(Localizer.Format("msg.editor.theLanguageServiceKeepsCrashingAnd", "The {0} language service keeps crashing and was stopped. Reopen the solution to try again.", name));
            }
        });
        Languages.StartFailed += (_, e) => Ui.Post(() =>
        {
            // M49: a host that couldn't start says why once; the IDE's missing runtime is the one runtime notification.
            if (e.Error is Codalus.Rpc.HostRuntimeMissingException missing && ReportHostRuntimeMissing(missing))
            {
                return;
            }

            var name = Workbench.LanguageServers.Get(e.LanguageId)?.DisplayName ?? e.LanguageId;
            Workbench.Notifications.Error(Localizer.Format("msg.editor.languageServiceCouldntStart", "The {0} language service couldn't start: {1}", name, e.Error.Message));
        });
        Languages.HostRestarted += (_, _) => Ui.Post(RefreshEditors);
        Languages.StatusChanged += (_, e) => Ui.Post(() => OnLanguageStatus(e.LanguageId, e.Status));
        Solutions.DesignTimeProgressChanged += (_, _) => Ui.Post(UpdateLanguageStatus);
        Languages.Diagnostics.Changed += (_, _) => ScheduleDiagnosticsStatus();
        // Once for a burst: each editor finds its project context again, in an index built for it.
        var projectContexts = new UiDebouncer(() =>
        {
            _contextIndex = null;
            ProjectContextsChanged?.Invoke(this, EventArgs.Empty);
        }, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1));
        Solutions.ProjectChanged += (_, _) => projectContexts.Request();
        Solutions.CompilerInputsChanged += (_, _) => projectContexts.Request();
        Solutions.SolutionChanged += (_, _) => projectContexts.Request();
        InitializeProfiles();
        InitializeSync();
        InitializeColors(); // before ThemeChanged's subscribers: they read the customized theme
        Theme.Changed += (_, _) => ThemeChanged?.Invoke(this, EventArgs.Empty);

        // Lines of code outside the editor (Find Results, the WCF Test Client's response) take the editor's colors (M34h).
        Editor.CodeColors.Configure(Languages.LanguageIdOf, TextMate.GrammarFor, id => id is null || id == Theme.ActiveTheme ? EditorTheme : Editor.EditorTheme.For(id));
        ThemeChanged += (_, _) => Editor.CodeColors.NotifyChanged();
        Layout.DocumentClosed += (_, document) => OnDocumentClosed(document);
        Layout.ActiveDocumentChanged += (_, _) => OnActiveEditorChanged();
        Layout.ActiveDocumentChanged += (_, _) => RememberActiveDocument();
        Switcher.Chosen += (_, document) => Layout.OpenDocument(document, focus: true);
        _ = ConfigureLanguagesAsync();
    }

    public Workbench.Output.OutputChannel LanguageLog => Workbench.Output.GetOrCreate("codalus.languages", "Language Services");

    private string? _analysisProject;

    private Task ConfigureLanguagesAsync()
    {
        var scope = Workbench.Settings.GetString("languageServices.analysisScope", "openDocuments");
        _analysisProject = scope == "currentProject" ? CurrentAnalysisProject() : null;
        return Languages.ConfigureAsync(JsonSerializer.SerializeToElement(
            new Dictionary<string, string>
            {
                ["analysisScope"] = scope,
                ["analysisProject"] = _analysisProject ?? string.Empty,
                ["sourceLink"] = Workbench.Settings.GetBoolean("navigation.sourceLink") ? "true" : "false",
            },
            Docking.ShellJsonContext.Default.DictionaryStringString));
    }

    /// <summary>The current project of the <c>currentProject</c> analysis scope: the active document's, else the startup project.</summary>
    public string? CurrentAnalysisProject() =>
        (Layout is null ? null : ActiveEditor?.Model.Path) is { } path && ErrorListViewModel.ProjectOf(Solutions.Projects.Values, path) is { } project
            ? project.Path
            : Runs.StartupProject;

    /// <summary>With the <c>currentProject</c> scope, a document of another project (or a new startup project) moves the analysis there.</summary>
    private void FollowAnalysisProject()
    {
        if (Workbench.Settings.GetString("languageServices.analysisScope", "openDocuments") == "currentProject"
            && !string.Equals(CurrentAnalysisProject(), _analysisProject, StringComparison.Ordinal))
        {
            _ = ConfigureLanguagesAsync();
        }
    }

    // ------------------------------------------------------------------ opening

    /// <summary>
    /// Opens a file in the editor (or shows its open tab). <paramref name="preview"/> opens it in the group's preview
    /// tab; <paramref name="at"/> moves the caret there.
    /// </summary>
    /// <param name="activate">False to add the tab behind the active one, its view made when it's first shown (a session restored).</param>
    public async Task<CodeEditorDocument?> OpenFileAsync(string path, bool preview = false, Position? at = null, string? initialContent = null, bool textEditor = false, bool activate = true)
    {
        path = Path.GetFullPath(path);
        if (initialContent is null && Views.ImageDocument.IsImage(path) && File.Exists(path))
        {
            OpenImage(path, preview);
            return null;
        }

        // A binary file opens in the hex viewer, unless the text editor was asked for.
        if (initialContent is null && !textEditor && !_models.ContainsKey(path) && File.Exists(path) && Views.HexDocument.LooksBinary(path))
        {
            OpenHex(path, preview);
            return null;
        }

        // A .resx file opens in the resource editor, unless the text editor was asked for or has it open.
        if (initialContent is null && !textEditor && at is null && Views.ResxEditorDocument.IsResx(path) && !_models.ContainsKey(path) && File.Exists(path))
        {
            OpenResx(path, preview);
            return null;
        }

        // A form's code file opens in the Windows Forms designer on Windows (M41a), unless the text editor was asked for.
        if (initialContent is null && !textEditor && at is null && File.Exists(path) && OpensInWinFormsDesigner(path))
        {
            await OpenWinFormsDesignerAsync(path, preview).ConfigureAwait(true);
            return null;
        }

        if (initialContent is null && !textEditor)
        {
            TellWinFormsWindowsOnly(path);
        }

        // A plugin's custom editor claims the file (API 1.0), unless the text editor was asked for.
        if (initialContent is null && !textEditor && at is null && Plugins.CustomEditorFor(path) is { } custom && File.Exists(path)
            && await OpenInCustomEditorAsync(path, custom, preview).ConfigureAwait(true))
        {
            return null;
        }

        if (Editors.FirstOrDefault(e => SolutionPaths.Comparer.Equals(e.Model.Path, path) && e.Owner is not null) is { } open)
        {
            Layout.OpenDocument(open, preview, focus: !preview, activate);
            if (at is { } position)
            {
                open.Navigate(position);
            }

            return open;
        }

        CodeEditorDocument view;
        if (!_models.ContainsKey(path) && !File.Exists(path) && initialContent is not null)
        {
            var created = CodeDocument.Create(path, initialContent);
            Track(created);
            view = CreateView(created, primary: true);
            Layout.OpenDocument(view, preview, focus: !preview, activate);
        }
        else
        {
            BeginModelRequest(path);
            try
            {
                CodeDocument model;
                try
                {
                    model = await ModelAsync(path).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Workbench.Notifications.Error(Localizer.Format("msg.editor.couldNotOpen", "Could not open {0}: {1}", path, ex.Message));
                    return null;
                }

                view = CreateView(model, primary: true);
                Layout.OpenDocument(view, preview, focus: !preview, activate);
            }
            finally
            {
                EndModelRequest(path);
            }
        }

        if (at is { } caret)
        {
            view.Navigate(caret);
        }

        return view;
    }

    /// <summary>
    /// Opens several files at once (the OS's multi-open, a drop, several <c>codalus file</c> requests together): each as a
    /// tab behind the active one, its view made when it's first shown, and the last one active (M56). Opening each as the
    /// active tab built every one's editor in turn on the UI thread, with its language requests, for tabs not looked at.
    /// </summary>
    /// <returns>For each file, whether it opened in a text editor.</returns>
    public async Task<IReadOnlyList<bool>> OpenFilesAsync(IReadOnlyList<(string Path, Position? At)> files)
    {
        var opened = new bool[files.Count];
        // The text editors' files are read first, together, so their tabs then open in one go: a layout pass between two of
        // them made the first tab of an empty group active, and built its editor, before the last one was.
        var reads = files.Select(f => Path.GetFullPath(f.Path)).Distinct(SolutionPaths.Comparer).Where(OpensAsText).ToList();
        foreach (var path in reads)
        {
            BeginModelRequest(path);
        }

        try
        {
            foreach (var read in reads.Select(ModelAsync).ToList())
            {
                await ((Task)read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext); // a failed read: the open says why
            }

            // Adding a tab to an empty group made it active (its tab strip selects it), before the last one was.
            using (Layout.DeferActiveDocument())
            {
                for (var i = 0; i < files.Count; i++)
                {
                    opened[i] = await OpenFileAsync(files[i].Path, at: files[i].At, activate: i == files.Count - 1).ConfigureAwait(true) is not null;
                }
            }
        }
        finally
        {
            foreach (var path in reads)
            {
                EndModelRequest(path);
            }
        }

        return opened;
    }

    /// <summary>Whether <see cref="OpenFileAsync"/> opens the file in a text editor: not an image, a binary, resources, a form or a plugin's editor.</summary>
    private bool OpensAsText(string path) =>
        _models.ContainsKey(path)
        || (File.Exists(path) && !Views.ImageDocument.IsImage(path) && !Views.ResxEditorDocument.IsResx(path) && !OpensInWinFormsDesigner(path)
            && Plugins.CustomEditorFor(path) is null && !Views.HexDocument.LooksBinary(path));

    /// <summary>
    /// The text model of a file that editors, designers and Code Search's preview share: the tracked one, or the one
    /// being read for another caller, or a new one. Two models of one file would be two documents for the language
    /// service, and letting go of either would close the file there or send it the text of a model already gone (M47:
    /// Code Search's preview, asked for the same result by two batches while the C# host started, left the host with
    /// an empty Shapes.cs). A caller takes the model (an editor's view or a hold) between
    /// <see cref="BeginModelRequest"/> and <see cref="EndModelRequest"/>, so another caller's release meanwhile keeps it.
    /// </summary>
    private Task<CodeDocument> ModelAsync(string path)
    {
        if (_models.TryGetValue(path, out var model))
        {
            return Task.FromResult(model);
        }

        if (_modelReads.TryGetValue(path, out var reading))
        {
            return reading;
        }

        reading = ReadModelAsync(path);
        if (!reading.IsCompleted)
        {
            _modelReads[path] = reading;
        }

        return reading;
    }

    private async Task<CodeDocument> ReadModelAsync(string path)
    {
        try
        {
            var model = await CodeDocument.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            Track(model);
            return model;
        }
        finally
        {
            _modelReads.Remove(path);
        }
    }

    private void BeginModelRequest(string path) => _modelRequests[path] = _modelRequests.GetValueOrDefault(path) + 1;

    private void EndModelRequest(string path)
    {
        if (_modelRequests.TryGetValue(path, out var count) && count > 1)
        {
            _modelRequests[path] = count - 1;
        }
        else
        {
            _modelRequests.Remove(path);
        }
    }

    /// <summary>Kept for callers that open settings files: a file that may not exist yet, never as a preview.</summary>
    public async Task OpenTextFileAsync(string path, string initialContent) => await OpenFileAsync(path, preview: false, at: null, initialContent).ConfigureAwait(true);

    /// <summary>
    /// Opens a read-only document whose text comes from the language service: a source-generated file, or a type
    /// decompiled from a referenced assembly.
    /// </summary>
    public async Task<CodeEditorDocument?> OpenVirtualAsync(string uri, Position? at)
    {
        if (Editors.FirstOrDefault(e => e.Model.Uri == uri) is { } open)
        {
            Layout.OpenDocument(open);
            if (at is { } position)
            {
                open.Navigate(position);
            }

            return open;
        }

        if (Languages.RunningClient(CSharpLanguageId) is not { } client)
        {
            return null;
        }

        CodeDocument model;
        if (DocumentUris.IsMetadata(uri))
        {
            if (await client.MetadataTextAsync(uri, CancellationToken.None).ConfigureAwait(true) is not { } metadata)
            {
                ShowMessage("The type could not be decompiled.");
                return null;
            }

            var name = metadata.TypeName[(metadata.TypeName.LastIndexOf('.') + 1)..].Split('`')[0];
            model = CodeDocument.ReadOnly(Path.Combine(metadata.AssemblyName, name + ".cs"), uri, metadata.Text, MetadataOriginTitle(metadata.Origin));
        }
        else if (await client.SourceGeneratedTextAsync(uri, CancellationToken.None).ConfigureAwait(true) is { } generated)
        {
            model = CodeDocument.ReadOnly(DocumentUris.GeneratedPath(uri)!, uri, generated.Text, "generated");
        }
        else
        {
            ShowMessage("The generated file is no longer available.");
            return null;
        }

        model.LanguageId = CSharpLanguageId;
        _models[uri] = model;
        var view = CreateView(model, primary: true);
        Layout.OpenDocument(view, focus: true);
        if (at is { } caret)
        {
            view.Navigate(caret);
        }

        return view;
    }

    private CodeEditorDocument CreateView(CodeDocument model, bool primary)
    {
        var id = primary && !Layout.Documents.Any(d => d.Id == "file:" + model.Uri) ? "file:" + model.Uri : $"file:{model.Uri}#{_nextViewId++}";
        var view = new CodeEditorDocument(model, id, m => Editors.Count(e => e.Model == m), this, EditorFont);
        view.CloseRequested += (_, _) => _ = ConfirmCloseAsync(view);
        view.ControllerAttached += (_, _) =>
        {
            if (Layout.ActiveDocument == view)
            {
                OnActiveEditorChanged();
            }
        };
        return view;
    }

    private void Track(CodeDocument model)
    {
        _models[model.Path] = model;
        model.LanguageId = Languages.LanguageIdOf(model.Path);
        _snapshots[model] = model.Text.CreateSnapshot();
        model.ContentChanged += (_, e) =>
        {
            _editedSinceReady.Add(model.Path);
            _snapshots[model] = model.Text.CreateSnapshot();

            // TODO(issue): didChange is JSON-encoded on the UI thread before its first await: the whole text at each
            // keystroke for a server that syncs whole documents, every change of an update of thousands (Format
            // Document, Replace All). An ordered writer in JsonRpcConnection, carrying every outgoing message and fed
            // the snapshot rather than the text, would move it off without reordering.
            _ = Languages.ChangeAsync(model.Path, e.Version, e.Changes);
            ClearXamlBuildDiagnostics(model.Path);
        };
        model.Saved += (_, _) => _ = Languages.SavedAsync(model.Path);
        model.Saved += (_, _) => Ui.Post(() => OnWebDocumentSaved(model.Path));
        model.Saved += (_, _) => Ui.Post(() => RunCustomToolOnSave(model.Path));
        model.Saved += (_, _) => Ui.Post(() => OnSavedForPreview(model.Path));
        model.Saved += (_, _) => Ui.Post(() => OnSavedForDebugEdits(model.Path));
        model.ConflictDetected += (_, _) => _ = ResolveConflictAsync(model);
        TrackForDiffs(model);
        if (model.LanguageId is { } languageId && !model.IsLarge)
        {
            // A large file stays out of the language service (M56, as in VS Code): its didOpen alone, the whole text in
            // JSON, kept the window busy, and the service would analyze it at every edit.
            _ = OpenInLanguageServiceAsync(model, languageId);
        }
    }

    private async Task OpenInLanguageServiceAsync(CodeDocument model, string languageId)
    {
        (string, int) Text()
        {
            // Called from the language service's threads (replay after a restart): read the immutable snapshot.
            lock (_snapshots)
            {
                return (_snapshots.TryGetValue(model, out var snapshot) ? snapshot.Text : string.Empty, model.Version);
            }
        }

        // TODO(issue): didOpen makes the whole text a string and JSON-encodes it on the UI thread (0.1-0.3 s for a file
        // just under the large-file limits, more for XML with the default escaping); the ordered writer of didChange's
        // TODO, with relaxed escaping, would move it off too.
        if (await Languages.OpenAsync(new LanguageDocument(model.Path, languageId, Text)).ConfigureAwait(true))
        {
            foreach (var editor in LanguageViews.Where(e => e.Model == model))
            {
                editor.Controller?.RefreshAll();
            }

            if (languageId == RazorLanguageId)
            {
                // HTML and CSS in Razor files come from the HTML server (ADR 0027).
                await OfferWebLanguagesAsync("html").ConfigureAwait(true);
            }
        }
        else
        {
            // No service: the CodeLens rows kept for it go.
            foreach (var editor in LanguageViews.Where(e => e.Model == model))
            {
                editor.Controller?.Insights.Refresh();
            }

            await OfferWebLanguagesAsync(languageId).ConfigureAwait(true);
            await OfferFSharpAsync(languageId).ConfigureAwait(true);
        }
    }

    private void OnDocumentClosed(Docking.ShellDocument document)
    {
        if (document is not CodeEditorDocument closed || Editors.Any(e => e.Model == closed.Model) || IsHeld(closed.Model))
        {
            return;
        }

        // The last view of the file: forget the model.
        var model = closed.Model;
        _models.Remove(model.IsReadOnly ? model.Uri : model.Path);
        lock (_snapshots)
        {
            _snapshots.Remove(model);
        }

        if (!model.IsReadOnly && model.LanguageId is not null)
        {
            _ = Languages.CloseAsync(model.Path);
            CloseRazorHtml(model.Path);
        }

        model.Dispose();
    }

    /// <summary>Asks the language service for everything again (after it restarted or finished loading projects).</summary>
    private void RefreshEditors()
    {
        foreach (var editor in LanguageViews)
        {
            editor.Controller?.RefreshAll();
        }
    }

    private WorkspaceStatusParams? _languageStatus;

    /// <summary>The languages whose service reported loading (projects changed, a restart) since it last reported ready.</summary>
    private readonly HashSet<string> _languagesLoading = new(StringComparer.Ordinal);

    /// <summary>The languages whose service has reported ready at least once.</summary>
    private readonly HashSet<string> _languagesReady = new(StringComparer.Ordinal);

    /// <summary>The documents edited since a language service last reported ready (their editors asked again on their own).</summary>
    private readonly HashSet<string> _editedSinceReady = new(SolutionPaths.Comparer);

    private void OnLanguageStatus(string languageId, WorkspaceStatusParams status)
    {
        _languageStatus = status;
        UpdateLanguageStatus();
        if (status.State != WorkspaceStates.Ready)
        {
            _languagesLoading.Add(languageId);
            return;
        }

        Diagnostics.BenchEvents.BenchEvents.Emit(Diagnostics.BenchEvents.BenchEventNames.LanguageReady);
        if (_languagesLoading.Remove(languageId) | _languagesReady.Add(languageId))
        {
            _editedSinceReady.Clear();
            RefreshEditors(); // the projects loaded: everything may know more now
        }
        else
        {
            RefreshEditorsAfterEdits();
        }
    }

    /// <summary>
    /// The language service reported ready again after the background pass that edits start (at every pause in typing):
    /// the other editors ask again for what those edits may have changed in them (colors, hints, CodeLens counts). An
    /// edited document's editors have asked already, and a hidden editor asks when it shows again: asking for every
    /// open editor at each pause kept a large solution's language service busy with documents nobody looked at.
    /// </summary>
    private void RefreshEditorsAfterEdits()
    {
        foreach (var editor in LanguageViews.Where(e => !_editedSinceReady.Contains(e.Model.Path)))
        {
            editor.Controller?.RefreshWhenShown();
        }

        _editedSinceReady.Clear();
    }

    /// <summary>
    /// The C# status with real counts (M34b): design-time builds while they run, then the projects the language service
    /// is compiling. The editor works meanwhile; only squiggles that depend on unloaded projects wait.
    /// </summary>
    private void UpdateLanguageStatus()
    {
        if (_languageStatus is not { } status)
        {
            return;
        }

        var text = Solutions.DesignTimeProgress is { Completed: false, Total: > 0 } building
            ? $"$(refresh) Loading C# projects {building.Built}/{building.Total}…"
            : "$(refresh) Analyzing C# projects…";
        Workbench.StatusBar.Set(new StatusBarItem("codalus.languageStatus", status.State == WorkspaceStates.Ready ? string.Empty : text, StatusBarAlignment.Left, 50, StatusSource)
        {
            Visible = status.State != WorkspaceStates.Ready,
            Tooltip = $"{status.Projects} project(s) loaded in the C# language service",
        });
    }

    // ------------------------------------------------------------------ IEditorServices

    LanguageHostClient? IEditorServices.Client(CodeDocument document) =>
        document is { LanguageId: { } languageId, IsLarge: false } ? Languages.RunningClient(languageId) : null;

    bool IEditorServices.ExpectsCodeLens(CodeDocument document) =>
        document is { IsReadOnly: false, IsLarge: false, LanguageId: CSharpLanguageId } && document.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        && !Languages.HasFailed(CSharpLanguageId) && Languages.RunningClient(CSharpLanguageId) is not { Capabilities.CodeLensProvider: null };

    public void OpenLocations(IReadOnlyList<Location> locations, string title) => _ = OpenLocationsAsync(locations, title);

    private async Task OpenLocationsAsync(IReadOnlyList<Location> locations, string title)
    {
        var target = locations[0];
        if (locations.Count > 1)
        {
            var items = locations.Select(l => new QuickPickItem($"{Path.GetFileName(DocumentUris.ToPath(l.Uri) ?? DocumentUris.GeneratedPath(l.Uri))}({l.Range.Start.Line + 1})")
            {
                Detail = DocumentUris.ToPath(l.Uri) ?? "generated",
                Tag = l,
            }).ToList();
            if (await Palette.ShowAsync(items, title).ConfigureAwait(true) is not { Tag: Location picked })
            {
                return;
            }

            target = picked;
        }

        RememberLocation();
        await OpenLocationAsync(target).ConfigureAwait(true);
    }

    public async Task OpenLocationAsync(Location location)
    {
        if (DocumentUris.IsGenerated(location.Uri) || DocumentUris.IsMetadata(location.Uri))
        {
            await OpenVirtualAsync(location.Uri, location.Range.Start).ConfigureAwait(true);
        }
        else if (DocumentUris.ToPath(location.Uri) is { } path)
        {
            await OpenFileAsync(path, preview: false, location.Range.Start).ConfigureAwait(true);
        }
    }

    public ProjectContext? ProjectContextOf(CodeDocument document) =>
        (_contextIndex ??= ContextIndex()).TryGetValue(document.Path, out var project)
            ? new ProjectContext(project.Path, project.Name, project.TargetFrameworks, Solutions.ActiveFramework(project.Path) ?? project.TargetFrameworks[0])
            : null;

    /// <summary>
    /// The multi-targeted projects' files by path, the first project that lists a file winning, built at the first lookup
    /// after their projects changed: each editor looked through every item of every such project after each burst, 50-150
    /// ms with 100 editors and 300 of them (M56).
    /// </summary>
    private Dictionary<string, Codalus.Projects.Protocol.ProjectInfo>? _contextIndex;

    private Dictionary<string, Codalus.Projects.Protocol.ProjectInfo> ContextIndex()
    {
        var index = new Dictionary<string, Codalus.Projects.Protocol.ProjectInfo>(SolutionPaths.Comparer);
        foreach (var project in Solutions.Projects.Values.Where(p => p.TargetFrameworks.Length > 1))
        {
            foreach (var item in project.Items)
            {
                index.TryAdd(item.Path, project);
            }
        }

        return index;
    }

    public async Task SetActiveFrameworkAsync(string projectPath, string targetFramework)
    {
        try
        {
            await Solutions.SetActiveFrameworkAsync(projectPath, targetFramework, CancellationToken.None).ConfigureAwait(true);
            ProjectContextsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or InvalidOperationException)
        {
            Workbench.Notifications.Error(ex.Message);
        }
    }

    public void ShowMessage(string message) => StatusBar.ShowMessage(message, TimeSpan.FromSeconds(5));

    void IEditorServices.Log(string message) => LanguageLog.AppendLine("[editor] " + message);

    public void OpenLink(string target)
    {
        if (target.StartsWith("command:", StringComparison.Ordinal))
        {
            Run(target["command:".Length..]);
        }
        else if (Uri.TryCreate(target, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            OpenExternalUrl(url);
        }
    }

    // ------------------------------------------------------------------ navigation history

    /// <summary>A caret move of more lines than this within a document is a step of the history, as in the classic IDE.</summary>
    internal const int HistoryLineJump = 10;

    /// <summary>Where the caret of the active editor is, for the history when it leaves (M50).</summary>
    private (string Path, Position Position)? _historyCaret;
    private bool _movingInHistory;

    /// <summary>A document just became active: its caret's first moves (opening at a definition) aren't steps.</summary>
    private bool _historySettling;

    private void RememberLocation()
    {
        if (ActiveEditor is { Controller: { } controller } editor && !editor.Model.IsReadOnly)
        {
            Remember((editor.Model.Path, editor.Model.PositionAt(controller.Editor.CaretOffset)));
        }
    }

    private void Remember((string Path, Position Position) location)
    {
        if (_movingInHistory)
        {
            return;
        }

        // Go To Definition remembers where it started, and then the editor's change remembers it again: once is enough.
        if (_back is [.., var last] && SolutionPaths.Comparer.Equals(last.Path, location.Path) && last.Position.Line == location.Position.Line)
        {
            _forward.Clear();
            return;
        }

        _back.Add(location);
        if (_back.Count > 50)
        {
            _back.RemoveAt(0);
        }

        _forward.Clear();
    }

    /// <summary>
    /// The history's steps besides the jumps (M50, Back and Forward did nothing after ordinary navigation): leaving a
    /// document for another, and a caret move of more than <see cref="HistoryLineJump"/> lines within one.
    /// </summary>
    private void TrackHistory()
    {
        var current = HistoryCaret();
        if (_historyCaret is { } previous && current is { } now
            && (!SolutionPaths.Comparer.Equals(previous.Path, now.Path) || Math.Abs(previous.Position.Line - now.Position.Line) > HistoryLineJump))
        {
            Remember(previous);
        }

        if (current is not null)
        {
            _historyCaret = current;
        }
    }

    private (string Path, Position Position)? HistoryCaret() => ActiveEditor is { Controller: { } controller } editor && !editor.Model.IsReadOnly
        ? (editor.Model.Path, editor.Model.PositionAt(controller.Editor.CaretOffset))
        : null;

    /// <summary>
    /// The active document changed: the step from the one before is taken now, and the new one's caret is read once the
    /// opening has placed it (a definition's line is where it opened, not a jump within it).
    /// </summary>
    private void OnHistoryDocumentChanged()
    {
        if (_historySettling)
        {
            TrackDocumentStep();
            return;
        }

        TrackDocumentStep();
        _historySettling = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _historySettling = false;
            if (HistoryCaret() is { } settled)
            {
                _historyCaret = settled;
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void TrackDocumentStep()
    {
        if (_historyCaret is { } previous && HistoryCaret() is { } now && !SolutionPaths.Comparer.Equals(previous.Path, now.Path))
        {
            Remember(previous);
            _historyCaret = now;
        }
    }

    private void OnHistoryCaretChanged(object? sender, EventArgs e)
    {
        if (ActiveEditor?.Controller == sender && !_historySettling)
        {
            TrackHistory();
        }
    }

    public async Task NavigateBackAsync() => await MoveInHistoryAsync(_back, _forward).ConfigureAwait(true);

    public async Task NavigateForwardAsync() => await MoveInHistoryAsync(_forward, _back).ConfigureAwait(true);

    private async Task MoveInHistoryAsync(List<(string Path, Position Position)> from, List<(string Path, Position Position)> to)
    {
        if (from.Count == 0)
        {
            return;
        }

        if (ActiveEditor is { Controller: { } controller } editor)
        {
            to.Add((editor.Model.Path, editor.Model.PositionAt(controller.Editor.CaretOffset)));
        }

        var (path, position) = from[^1];
        from.RemoveAt(from.Count - 1);
        _movingInHistory = true;
        try
        {
            await OpenFileAsync(path, preview: false, position).ConfigureAwait(true);
        }
        finally
        {
            _movingInHistory = false;
            _historyCaret = ActiveEditor is { Controller: { } arrived } at && !at.Model.IsReadOnly ? (at.Model.Path, at.Model.PositionAt(arrived.Editor.CaretOffset)) : null;
        }
    }

    // ------------------------------------------------------------------ saving and closing

    private async Task ConfirmCloseAsync(Docking.ShellDocument document)
    {
        var name = document is CodeEditorDocument editor ? editor.Model.Name : document.Title;
        var choice = await Workbench.Notifications.Warning(Localizer.Format("msg.editor.saveChangesToBeforeClosing", "Save changes to {0} before closing?", name), "Save", "Don't Save").Result.ConfigureAwait(false);
        await Ui.RunAsync(async () =>
        {
            if (choice == "Save")
            {
                if ((document is CodeEditorDocument code && await SaveAsync(code.Model).ConfigureAwait(true))
                    || (document is Views.ISavableDocument savable && await SaveDocumentAsync(document, savable).ConfigureAwait(true)))
                {
                    document.CloseConfirmed();
                }
            }
            else if (choice == "Don't Save")
            {
                document.CloseConfirmed();
            }
        }).ConfigureAwait(false);
    }

    public async Task SaveActiveDocumentAsync()
    {
        if (ActiveEditor is { } editor)
        {
            await SaveAsync(editor.Model).ConfigureAwait(true);
        }
        else if (Layout.ActiveDocument is Views.ISavableDocument savable)
        {
            await SaveDocumentAsync(Layout.ActiveDocument, savable).ConfigureAwait(true);
        }
    }

    public async Task SaveAllAsync()
    {
        foreach (var model in _models.Values.Where(m => m.IsModified).ToList())
        {
            await SaveAsync(model).ConfigureAwait(true);
        }

        foreach (var document in Layout.Documents.Where(d => d.IsModified && d is Views.ISavableDocument).ToList())
        {
            await SaveDocumentAsync(document, (Views.ISavableDocument)document).ConfigureAwait(true);
        }
    }

    private async Task<bool> SaveAsync(CodeDocument model)
    {
        try
        {
            await BeforeSaveAsync(model).ConfigureAwait(true);
            await model.SaveAsync(CancellationToken.None).ConfigureAwait(true);
            AfterSave(model);
            StatusBar.ShowMessage(Localizer.Format("msg.editor.saved", "Saved {0}", model.Name), TimeSpan.FromSeconds(3));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.editor.couldNotSave", "Could not save {0}: {1}", model.Path, ex.Message));
            return false;
        }
    }

    private async Task ResolveConflictAsync(CodeDocument model)
    {
        var choice = await Workbench.Notifications.Warning(Localizer.Format("msg.editor.changedOnDiskAndYouHave", "{0} changed on disk, and you have unsaved changes.", model.Name), "Reload", "Keep Mine").Result.ConfigureAwait(false);
        if (choice == "Reload")
        {
            await Ui.RunAsync(async () =>
            {
                try
                {
                    await model.ReloadAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Workbench.Notifications.Error(Localizer.Format("msg.editor.couldNotReload", "Could not reload {0}: {1}", model.Path, ex.Message));
                }
            }).ConfigureAwait(false);
        }
    }

    public void CloseActiveDocument()
    {
        if (Layout.ActiveDocument is { } document)
        {
            Layout.CloseDocument(document);
        }
    }

    /// <summary>Close All (or Close All But This): together, so no tab becomes active on the way (M56).</summary>
    public void CloseAllDocuments(bool exceptActive)
    {
        var active = Layout.ActiveDocument;
        Layout.CloseDocuments([.. Layout.Documents.Where(d => !exceptActive || d != active)]);
    }

    /// <summary>Closes every document that isn't pinned (M34d), together.</summary>
    public void CloseUnpinnedDocuments() => Layout.CloseDocuments([.. Layout.Documents.Where(d => !d.IsPinned)]);

    public bool HasUnsavedDocuments => _models.Values.Any(m => m.IsModified);

    /// <summary>Asks before quitting with unsaved changes; true when the window may close.</summary>
    public async Task<bool> ConfirmQuitAsync()
    {
        var unsaved = _models.Values.Where(m => m.IsModified).ToList();
        if (unsaved.Count == 0)
        {
            DeleteAllBackups();
            return true;
        }

        var names = string.Join(", ", unsaved.Select(m => m.Name));
        var choice = await Workbench.Notifications.Warning(Localizer.Format("msg.editor.youHaveUnsavedChangesIn", "You have unsaved changes in {0}.", names), "Save All and Exit", "Exit Without Saving").Result.ConfigureAwait(true);
        if (choice == "Save All and Exit")
        {
            foreach (var model in unsaved)
            {
                await SaveAsync(model).ConfigureAwait(true);
            }

            if (unsaved.All(m => !m.IsModified))
            {
                DeleteAllBackups();
                return true;
            }

            return false;
        }

        if (choice == "Exit Without Saving")
        {
            DeleteAllBackups();
            return true;
        }

        return false;
    }

    /// <summary>Closing a solution closes its documents together (unsaved ones stay open, as the user has to decide).</summary>
    private void CloseSolutionDocuments() => Layout.CloseDocuments([.. Editors.Where(e => !e.Model.IsModified)]);

    // ------------------------------------------------------------------ split views

    /// <summary>Opens the active document in a new group to the right (or below).</summary>
    public void SplitActiveEditor(bool below)
    {
        if (ActiveEditor is not { } editor)
        {
            return;
        }

        var view = CreateView(editor.Model, primary: false);
        Layout.OpenInNewGroup(view, below);
        if (editor.Controller is { } controller)
        {
            view.Navigate(editor.Model.PositionAt(controller.Editor.CaretOffset));
        }
    }

    /// <summary>Opens a live preview of the active Markdown document beside it (Ctrl+K V).</summary>
    public MarkdownPreviewDocument? ShowMarkdownPreview()
    {
        if (ActiveEditor is not { Model: { LanguageId: "markdown" } model })
        {
            StatusBar.ShowMessage(Localizer.Get("msg.editor.openAMarkdownFileToPreview", "Open a Markdown file to preview it."), TimeSpan.FromSeconds(4));
            return null;
        }

        if (Layout.FindDocument<MarkdownPreviewDocument>(MarkdownPreviewDocument.DocumentIdFor(model.Path)) is { } open)
        {
            Layout.OpenDocument(open, focus: false);
            return open;
        }

        var preview = new MarkdownPreviewDocument(model, link =>
        {
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                OpenExternalUrl(uri);
            }
            else if (link.StartsWith("command:", StringComparison.Ordinal))
            {
                Run(link["command:".Length..]);
            }
        });
        Layout.OpenInNewGroup(preview, below: false);
        return preview;
    }

    /// <summary>Ctrl+Tab: activates the next (or previous) document tab, wrapping around.</summary>
    /// <summary>Open documents, the most recently active first (the Ctrl+Tab switcher's order).</summary>
    private readonly List<Docking.ShellDocument> _recentDocuments = [];

    private void RememberActiveDocument()
    {
        if (Layout.ActiveDocument is { } active)
        {
            _recentDocuments.Remove(active);
            _recentDocuments.Insert(0, active);
        }
    }

    /// <summary>The open documents, the most recently active first; documents never active follow in tab order.</summary>
    public IReadOnlyList<Docking.ShellDocument> RecentDocuments
    {
        get
        {
            var open = Layout.Documents.ToList();
            _recentDocuments.RemoveAll(d => !open.Contains(d));
            return [.. _recentDocuments, .. open.Where(d => !_recentDocuments.Contains(d))];
        }
    }

    /// <summary>
    /// Ctrl+Tab (Ctrl+Shift+Tab back): opens the switcher on the document used before the active one, or moves its
    /// selection while it's open. Releasing Ctrl switches (<see cref="Views.MainWindow"/>).
    /// </summary>
    public void SwitchDocument(int direction)
    {
        if (Switcher.IsOpen)
        {
            Switcher.Move(direction);
            return;
        }

        var documents = RecentDocuments;
        if (documents.Count < 2)
        {
            return;
        }

        Switcher.Open([.. documents.Select(d => new DocumentSwitcherItem(d, d.Title, d.FilePath is { } path ? Path.GetDirectoryName(path) : null, d.HasTabIcon ? d.TabIcon : null))], direction);
    }

    public void CycleDocument(int direction)
    {
        var documents = Layout.Documents.ToList();
        if (documents.Count < 2 || Layout.ActiveDocument is not { } active)
        {
            return;
        }

        var index = (documents.IndexOf(active) + direction + documents.Count) % documents.Count;
        Layout.OpenDocument(documents[index], focus: true);
    }

    /// <summary>
    /// Window → Open Documents… (a classic IDE's list of active files, M34e): every open document,
    /// pinned ones marked, including those whose tabs don't fit their group; the one picked becomes active.
    /// </summary>
    public async Task ShowOpenDocumentsAsync()
    {
        var items = Layout.Documents
            .Select(d => new QuickPickItem(d.Title)
            {
                Detail = (d.IsPinned ? "pinned · " : d.IsPreview ? "preview · " : string.Empty) + d.FilePath,
                Icon = d.HasTabIcon ? d.TabIcon : null,
                Tag = d,
            })
            .ToList();
        if (items.Count > 0 && await Palette.ShowAsync(items, "Open documents").ConfigureAwait(true) is { Tag: Docking.ShellDocument document })
        {
            Layout.OpenDocument(document, focus: true);
        }
    }

    /// <summary>A document tab's Copy Full Path (M34e).</summary>
    public async Task CopyActiveDocumentPathAsync()
    {
        if (Layout.ActiveDocument?.FilePath is { Length: > 0 } path && Window?.Clipboard is { } clipboard)
        {
            await Codalus.Shell.Infrastructure.SafeClipboard.TrySetTextAsync(clipboard, path).ConfigureAwait(true);
        }
    }

    /// <summary>A document tab's Open Containing Folder (Reveal in Finder on macOS, M34e).</summary>
    public void RevealActiveDocument()
    {
        if (Layout.ActiveDocument?.FilePath is not { Length: > 0 } path || !File.Exists(path))
        {
            return; // an unsaved document, or a remote window's (not on this machine)
        }

        try
        {
            RevealPath(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.editor.couldNotOpenTheFileManager", "Could not open the file manager: {0}", ex.Message));
        }
    }

    /// <summary>Window → Split: a second editor of the same document inside the tab.</summary>
    public void ToggleEditorSplit() => ActiveEditor?.View?.ToggleSplit();

    // ------------------------------------------------------------------ status bar

    private void OnActiveEditorChanged()
    {
        ErrorList.OnActiveDocumentChanged();
        Explorer.OnActiveDocumentChanged();
        FollowAnalysisProject();
        OnNavigationEditorChanged();
        TrackCopies(ActiveEditor?.Controller);
        Workbench.Context.Set("editorLanguageId", ActiveEditor?.Model.LanguageId);
        Workbench.Context.Set("editorExtension", ActiveEditor?.Model.Path is { } activePath ? Path.GetExtension(activePath).ToLowerInvariant() : null);
        FollowXamlEditor();
        OnHistoryDocumentChanged();
        if (ActiveEditor is { Controller: { } controller } editor)
        {
            controller.CaretChanged -= OnCaretChanged;
            controller.CaretChanged += OnCaretChanged;
            controller.CaretChanged -= OnHistoryCaretChanged;
            controller.CaretChanged += OnHistoryCaretChanged;
            controller.CaretChanged -= OnPreviewCaretChanged;
            controller.CaretChanged += OnPreviewCaretChanged;
            UpdateEditorStatus(editor);
        }
        else
        {
            Workbench.StatusBar.RemoveFrom(StatusSource + ".document");
        }
    }

    private bool _caretStatusScheduled;

    /// <summary>
    /// The caret moves on every keystroke: the status bar follows at most every 100 ms, after the frame showing the
    /// keystroke has rendered (typing latency budget, ADR 0002).
    /// </summary>
    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (_caretStatusScheduled || ActiveEditor?.Controller != sender)
        {
            return;
        }

        _caretStatusScheduled = true;
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            _caretStatusScheduled = false;
            if (ActiveEditor is { } editor)
            {
                UpdateEditorStatus(editor);
            }
        }, TimeSpan.FromMilliseconds(100), Avalonia.Threading.DispatcherPriority.Background);
    }

    private void UpdateEditorStatus(CodeEditorDocument editor)
    {
        if (editor.Controller is not { } controller)
        {
            return;
        }

        const string source = StatusSource + ".document";
        var caret = controller.Caret;
        var model = editor.Model;
        var position = $"Ln {caret.Line}, Col {caret.Column}" + (caret.SelectionLength > 0 ? $" ({caret.SelectionLength} selected)" : string.Empty) + (caret.Carets > 1 ? $" · {caret.Carets} carets" : string.Empty);
        Workbench.StatusBar.Set(new StatusBarItem("codalus.editor.position", position, StatusBarAlignment.Right, 900, source) { AccessibleName = position, Command = "editor.action.gotoLine" });
        var indentation = controller.Editor.Options.ConvertTabsToSpaces ? $"Spaces: {controller.Editor.Options.IndentationSize}" : $"Tab Size: {controller.Editor.Options.IndentationSize}";
        Workbench.StatusBar.Set(new StatusBarItem("codalus.editor.indentation", indentation, StatusBarAlignment.Right, 890, source));
        Workbench.StatusBar.Set(new StatusBarItem("codalus.editor.encoding", model.Format.EncodingName, StatusBarAlignment.Right, 880, source) { Tooltip = "Encoding" });
        Workbench.StatusBar.Set(new StatusBarItem("codalus.editor.eol", model.Format.LineEndingName, StatusBarAlignment.Right, 870, source) { Tooltip = "Line endings (click to change)", Command = "editor.action.changeEndOfLine" });
        var language = model.LanguageId is { } id ? Workbench.LanguageDefinitions.Get(id)?.Name ?? id : "Plain Text";
        Workbench.StatusBar.Set(new StatusBarItem("codalus.editor.language", language, StatusBarAlignment.Right, 860, source) { Tooltip = "Language" });
        Workbench.Context.Set("editorHasSelection", caret.SelectionLength > 0);
        Workbench.Context.Set("editorHasMultipleSelections", caret.Carets > 1);
    }

    private int _diagnosticsStatusScheduled;

    /// <summary>The error and warning counts, once for a burst of changes (raised on any thread, a file at a time).</summary>
    private void ScheduleDiagnosticsStatus()
    {
        if (Interlocked.Exchange(ref _diagnosticsStatusScheduled, 1) == 1)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _diagnosticsStatusScheduled, 0);
            var (errors, warnings) = (0, 0);
            foreach (var diagnostic in Languages.Diagnostics.All.SelectMany(f => f.Items))
            {
                errors += diagnostic.Severity == DiagnosticSeverity.Error ? 1 : 0;
                warnings += diagnostic.Severity == DiagnosticSeverity.Warning ? 1 : 0;
            }

            var command = BuiltIns.ViewCommandPrefix + ErrorListViewModel.ToolWindowId;
            Workbench.StatusBar.Set(new StatusBarItem("codalus.problems.errors", $"$(error) {errors}", StatusBarAlignment.Left, 100, StatusSource)
            {
                Tooltip = $"{errors} error(s): open the Error List",
                AccessibleName = $"{errors} errors",
                Command = command,
            });
            Workbench.StatusBar.Set(new StatusBarItem("codalus.problems.warnings", $"$(warning) {warnings}", StatusBarAlignment.Left, 99, StatusSource)
            {
                Tooltip = $"{warnings} warning(s): open the Error List",
                AccessibleName = $"{warnings} warnings",
                Command = command,
            });
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary><c>editor.action.changeEndOfLine</c>: converts every line ending of the active document.</summary>
    public async Task ChangeEndOfLineAsync()
    {
        if (ActiveEditor is not { Model.IsReadOnly: false } editor)
        {
            return;
        }

        var items = new[] { new QuickPickItem("LF") { Tag = "\n" }, new QuickPickItem("CRLF") { Tag = "\r\n" } };
        if (await Palette.ShowAsync(items, "Select the line ending").ConfigureAwait(true) is not { Tag: string lineEnding })
        {
            return;
        }

        var model = editor.Model;
        var document = model.Text;
        using (document.RunUpdate())
        {
            for (var line = document.LineCount; line >= 1; line--)
            {
                var documentLine = document.GetLineByNumber(line);
                if (documentLine.DelimiterLength > 0 && document.GetText(documentLine.EndOffset, documentLine.DelimiterLength) != lineEnding)
                {
                    document.Replace(documentLine.EndOffset, documentLine.DelimiterLength, lineEnding);
                }
            }
        }

        model.SetLineEnding(lineEnding);
        UpdateEditorStatus(editor);
    }
}
