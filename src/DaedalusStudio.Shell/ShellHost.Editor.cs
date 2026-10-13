using System.Text.Json;
using AvaloniaEdit.Document;
using DaedalusStudio.LanguageServices.Client;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Platform;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Shell.Theming;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.StatusBar;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell;

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
    private const string StatusSource = "daedalus.editor";

    private readonly Dictionary<string, CodeDocument> _models = new(SolutionPaths.Comparer);
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

    public event EventHandler? ThemeChanged;

    public event EventHandler? ProjectContextsChanged;

    EditorOptions IEditorServices.Options => EditorOptions;

    EditorTheme IEditorServices.Theme => EditorTheme;

    /// <summary>TextMate grammars and language configurations (ADR 0016).</summary>
    public TextMateSupport TextMate { get; private set; } = null!;

    TextMateSupport IEditorServices.TextMate => TextMate;

    DiagnosticsStore IEditorServices.Diagnostics => Languages.Diagnostics;

    private void InitializeEditor()
    {
        EditorOptions = new EditorOptions(Workbench.Settings);
        var registry = Workbench.LanguageServers;
        registry.Register(new LanguageServiceDescriptor(CSharpLanguageId, "C#", [".cs", ".csx"], "C#", HostLaunchInfo.Resolve)
        {
            // Razor files go to the C# host, which serves them through the Razor source generator (ADR 0023), and so
            // does Avalonia XAML, on the same projects' type information (ADR 0047).
            Languages = [CSharpLanguageId, RazorLanguageId, AxamlLanguageId],
        });
        registry.Register(new LanguageServiceDescriptor("markup", "JSON, XML and YAML", [], null, MarkupHostLaunch.Resolve)
        {
            Languages = MarkupHostLaunch.Languages,
            InitializationOptions = MarkupOptions(),
        });
        BuiltInLanguages.Register(Workbench.LanguageDefinitions);
        TextMate = new TextMateSupport(Workbench.LanguageDefinitions) { Log = line => LanguageLog.AppendLine(line) };
        Languages = new LanguageService(registry, Solutions, languages: Workbench.LanguageDefinitions)
        {
            SettingsProvider = section => section is { Length: > 0 } ? Workbench.Settings.GetSection(section) : null,
            PropertyEvaluator = EvaluatePropertyAsync,
            ApplyEditHandler = edit => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyWorkspaceEditAsync(edit)),
        };
        ErrorList = new ErrorListViewModel(Languages.Diagnostics, Builds, Solutions, () => Layout is null ? null : ActiveEditor?.Model.Path, (path, position) => OpenFileAsync(path, preview: false, position));
    }

    /// <summary>The markup host's options: schema downloads (setting <c>json.schemaDownload</c>) and their cache folder.</summary>
    private System.Text.Json.JsonElement MarkupOptions()
    {
        var options = new System.Text.Json.Nodes.JsonObject
        {
            ["schemaDownload"] = Workbench.Settings.GetBoolean(SchemaDownloadSetting, true),
            ["schemaCache"] = Path.Combine(Paths.Cache, "schemas"),
        };
        using var document = System.Text.Json.JsonDocument.Parse(options.ToJsonString());
        return document.RootElement.Clone();
    }

    public const string SchemaDownloadSetting = "json.schemaDownload";

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
        Languages.HostRestarted += (_, _) => Ui.Post(RefreshEditors);
        Languages.StatusChanged += (_, e) => Ui.Post(() => OnLanguageStatus(e.LanguageId, e.Status));
        Solutions.DesignTimeProgressChanged += (_, _) => Ui.Post(UpdateLanguageStatus);
        Languages.Diagnostics.Changed += (_, _) => Ui.Post(ScheduleDiagnosticsStatus);
        Solutions.ProjectChanged += (_, _) => ProjectContextsChanged?.Invoke(this, EventArgs.Empty);
        Solutions.CompilerInputsChanged += (_, _) => ProjectContextsChanged?.Invoke(this, EventArgs.Empty);
        InitializeProfiles();
        InitializeSync();
        InitializeColors(); // before ThemeChanged's subscribers: they read the customized theme
        Theme.Changed += (_, _) => ThemeChanged?.Invoke(this, EventArgs.Empty);
        Layout.DocumentClosed += (_, document) => OnDocumentClosed(document);
        Layout.ActiveDocumentChanged += (_, _) => OnActiveEditorChanged();
        _ = ConfigureLanguagesAsync();
    }

    public Workbench.Output.OutputChannel LanguageLog => Workbench.Output.GetOrCreate("daedalus.languages", "Language Services");

    private Task ConfigureLanguagesAsync() =>
        Languages.ConfigureAsync(JsonSerializer.SerializeToElement(
            new Dictionary<string, string>
            {
                ["analysisScope"] = Workbench.Settings.GetString("languageServices.analysisScope", "openDocuments"),
                ["sourceLink"] = Workbench.Settings.GetBoolean("navigation.sourceLink") ? "true" : "false",
            },
            Docking.ShellJsonContext.Default.DictionaryStringString));

    // ------------------------------------------------------------------ opening

    /// <summary>
    /// Opens a file in the editor (or shows its open tab). <paramref name="preview"/> opens it in the group's preview
    /// tab; <paramref name="at"/> moves the caret there.
    /// </summary>
    public async Task<CodeEditorDocument?> OpenFileAsync(string path, bool preview = false, Position? at = null, string? initialContent = null)
    {
        path = Path.GetFullPath(path);
        if (initialContent is null && Views.ImageDocument.IsImage(path) && File.Exists(path))
        {
            OpenImage(path, preview);
            return null;
        }

        if (Editors.FirstOrDefault(e => SolutionPaths.Comparer.Equals(e.Model.Path, path) && e.Owner is not null) is { } open)
        {
            Layout.OpenDocument(open, preview, focus: !preview);
            if (at is { } position)
            {
                open.Navigate(position);
            }

            return open;
        }

        CodeDocument model;
        if (_models.TryGetValue(path, out var existing))
        {
            model = existing;
        }
        else
        {
            try
            {
                model = File.Exists(path) || initialContent is null
                    ? await CodeDocument.OpenAsync(path, CancellationToken.None).ConfigureAwait(true)
                    : CodeDocument.Create(path, initialContent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Workbench.Notifications.Error(Localizer.Format("msg.editor.couldNotOpen", "Could not open {0}: {1}", path, ex.Message));
                return null;
            }

            Track(model);
        }

        var view = CreateView(model, primary: true);
        Layout.OpenDocument(view, preview, focus: !preview);
        if (at is { } caret)
        {
            view.Navigate(caret);
        }

        return view;
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
            _snapshots[model] = model.Text.CreateSnapshot();
            _ = Languages.ChangeAsync(model.Path, e.Version, e.Changes);
            ClearXamlBuildDiagnostics(model.Path);
        };
        model.Saved += (_, _) => _ = Languages.SavedAsync(model.Path);
        model.Saved += (_, _) => Ui.Post(() => OnWebDocumentSaved(model.Path));
        model.Saved += (_, _) => Ui.Post(() => RunCustomToolOnSave(model.Path));
        model.Saved += (_, _) => Ui.Post(() => OnSavedForPreview(model.Path));
        model.ConflictDetected += (_, _) => _ = ResolveConflictAsync(model);
        if (model.LanguageId is { } languageId)
        {
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

        if (await Languages.OpenAsync(new LanguageDocument(model.Path, languageId, Text)).ConfigureAwait(true))
        {
            foreach (var editor in Editors.Where(e => e.Model == model))
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
            await OfferWebLanguagesAsync(languageId).ConfigureAwait(true);
        }
    }

    private void OnDocumentClosed(Docking.ShellDocument document)
    {
        if (document is not CodeEditorDocument closed || Editors.Any(e => e.Model == closed.Model))
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
        foreach (var editor in Editors)
        {
            editor.Controller?.RefreshAll();
        }
    }

    private WorkspaceStatusParams? _languageStatus;

    private void OnLanguageStatus(string languageId, WorkspaceStatusParams status)
    {
        _languageStatus = status;
        UpdateLanguageStatus();
        if (status.State == WorkspaceStates.Ready)
        {
            Diagnostics.BenchEvents.BenchEvents.Emit(Diagnostics.BenchEvents.BenchEventNames.LanguageReady);
            RefreshEditors();
        }
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
        Workbench.StatusBar.Set(new StatusBarItem("daedalus.languageStatus", status.State == WorkspaceStates.Ready ? string.Empty : text, StatusBarAlignment.Left, 50, StatusSource)
        {
            Visible = status.State != WorkspaceStates.Ready,
            Tooltip = $"{status.Projects} project(s) loaded in the C# language service",
        });
    }

    // ------------------------------------------------------------------ IEditorServices

    LanguageHostClient? IEditorServices.Client(CodeDocument document) =>
        document.LanguageId is { } languageId ? Languages.RunningClient(languageId) : null;

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

    public ProjectContext? ProjectContextOf(CodeDocument document)
    {
        foreach (var project in Solutions.Projects.Values.Where(p => p.TargetFrameworks.Length > 1))
        {
            if (project.Items.Any(i => SolutionPaths.Comparer.Equals(i.Path, document.Path)))
            {
                return new ProjectContext(project.Path, project.Name, project.TargetFrameworks, Solutions.ActiveFramework(project.Path) ?? project.TargetFrameworks[0]);
            }
        }

        return null;
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
            ShellIntegration.OpenUrl(url);
        }
    }

    // ------------------------------------------------------------------ navigation history

    private void RememberLocation()
    {
        if (ActiveEditor is { Controller: { } controller } editor && !editor.Model.IsReadOnly)
        {
            _back.Add((editor.Model.Path, editor.Model.PositionAt(controller.Editor.CaretOffset)));
            if (_back.Count > 50)
            {
                _back.RemoveAt(0);
            }

            _forward.Clear();
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
        await OpenFileAsync(path, preview: false, position).ConfigureAwait(true);
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
                if (document is CodeEditorDocument code && await SaveAsync(code.Model).ConfigureAwait(true))
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
    }

    public async Task SaveAllAsync()
    {
        foreach (var model in _models.Values.Where(m => m.IsModified).ToList())
        {
            await SaveAsync(model).ConfigureAwait(true);
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

    public void CloseAllDocuments(bool exceptActive)
    {
        var active = Layout.ActiveDocument;
        foreach (var document in Layout.Documents.Where(d => !exceptActive || d != active).ToList())
        {
            Layout.CloseDocument(document);
        }
    }

    /// <summary>Closes every document that isn't pinned (M34d).</summary>
    public void CloseUnpinnedDocuments()
    {
        foreach (var document in Layout.Documents.Where(d => !d.IsPinned).ToList())
        {
            Layout.CloseDocument(document);
        }
    }

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

    // ------------------------------------------------------------------ session (open documents per solution)

    private string? SessionFile(string? solutionPath)
    {
        if (solutionPath is null)
        {
            return null;
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(solutionPath.ToUpperInvariant())))[..16];
        return Path.Combine(Paths.State, "sessions", hash + ".json");
    }

    /// <summary>Remembers the open solution's documents and carets (files on disk only).</summary>
    public void SaveEditorSession()
    {
        if (SessionFile(Solutions.Solution?.Path) is not { } file)
        {
            return;
        }

        var documents = Editors
            .Where(e => !e.Model.IsReadOnly && File.Exists(e.Model.Path))
            .DistinctBy(e => e.Model)
            .Select(e =>
            {
                var caret = e.Controller is { } controller ? e.Model.PositionAt(controller.Editor.CaretOffset) : new Position(0, 0);
                return new Docking.SessionDocument(e.Model.Path, caret.Line, caret.Character) { Pinned = e.IsPinned ? true : null };
            })
            .ToList();
        WriteJson(file, new Docking.EditorSession(documents, ActiveEditor?.Model.Path)
        {
            StartupProject = Runs.ChosenStartupProject,
            Breakpoints = Debugger.Breakpoints.All.Count == 0 ? null : [.. Debugger.Breakpoints.All.Select(ToSession)],
            FunctionBreakpoints = Debugger.Breakpoints.Functions.Count == 0 ? null : [.. Debugger.Breakpoints.Functions.Select(ToSession)],
            BreakOnOtherExceptions = Debugger.ExceptionSettings.BreakOnOthers ? true : null,
            AspNetEnvironment = Runs.EnvironmentOverride,
            ExceptionTypes = Debugger.ExceptionSettings.Changes() is { Count: > 0 } exceptionTypes ? exceptionTypes : null,
            ExceptionConditions = Debugger.ExceptionSettings.Conditions() is { Count: > 0 } conditions ? conditions : null,
            Watches = Watch.Expressions.Count == 0 ? null : Watch.Expressions,
            DataTips = Debugger.PinnedTips.All is { Count: > 0 } tips ? [.. tips.Select(t => new Docking.SessionDataTip(t.Path, t.Line, t.Expression, t.Comment))] : null,
            LaunchProfiles = Runs.ChosenProfiles.Count == 0 ? null : new Dictionary<string, string>(Runs.ChosenProfiles),
            MultipleStartupProjects = Runs.MultipleStartup ? true : null,
            StartupProjects = Runs.StartupEntries is { Count: > 0 } entries ? [.. entries.Select(e => new Docking.SessionStartupProject(e.Project, e.Action.ToString()))] : null,
        }, Docking.ShellJsonContext.Default.EditorSession);
    }

    /// <summary>Reopens the documents that were open the last time this solution was closed.</summary>
    private async Task RestoreEditorSessionAsync(string solutionPath)
    {
        if (SessionFile(solutionPath) is not { } file || ReadJson(file, Docking.ShellJsonContext.Default.EditorSession) is not { } session)
        {
            Runs.Load(null, null);
            Debugger.Breakpoints.Clear();
            Debugger.ExceptionSettings.Reset();
            Runs.EnvironmentOverride = null;
            Watch.Load(null);
            Debugger.PinnedTips.Load([]);
            return;
        }

        Runs.Load(
            session.StartupProject,
            session.LaunchProfiles,
            session.MultipleStartupProjects == true,
            [.. (session.StartupProjects ?? []).Select(p => new StartupEntry(p.Path, Enum.TryParse<StartupAction>(p.Action, out var action) ? action : StartupAction.None))]);
        Debugger.Breakpoints.Load((session.Breakpoints ?? []).Select(FromSession), (session.FunctionBreakpoints ?? []).Select(FromSession));
        Debugger.ExceptionSettings.Load(session.BreakOnOtherExceptions ?? false, session.ExceptionTypes, session.ExceptionConditions);
        Runs.EnvironmentOverride = session.AspNetEnvironment;
        Watch.Load(session.Watches);
        Debugger.PinnedTips.Load((session.DataTips ?? []).Select(t => (t.Path, t.Line, t.Expression, t.Comment)));

        foreach (var document in session.Documents.Where(d => File.Exists(d.Path)))
        {
            if (await OpenFileAsync(document.Path, preview: false, new Position(document.Line, document.Character)).ConfigureAwait(true) is { } opened && document.Pinned == true)
            {
                Layout.SetPinned(opened, true);
            }
        }

        if (session.Active is { } active && Editors.FirstOrDefault(e => SolutionPaths.Comparer.Equals(e.Model.Path, active)) is { } editor)
        {
            Layout.OpenDocument(editor);
        }
    }

    /// <summary>Closing a solution closes its documents (unsaved ones stay open, as the user has to decide).</summary>
    private void CloseSolutionDocuments()
    {
        foreach (var editor in Editors.Where(e => !e.Model.IsModified).ToList())
        {
            Layout.CloseDocument(editor);
        }
    }

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
                ShellIntegration.OpenUrl(uri);
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
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, path).ConfigureAwait(true);
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
            DaedalusStudio.Platform.ShellIntegration.RevealInFileManager(path);
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
        OnNavigationEditorChanged();
        TrackCopies(ActiveEditor?.Controller);
        Workbench.Context.Set("editorLanguageId", ActiveEditor?.Model.LanguageId);
        FollowXamlEditor();
        if (ActiveEditor is { Controller: { } controller } editor)
        {
            controller.CaretChanged -= OnCaretChanged;
            controller.CaretChanged += OnCaretChanged;
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
        Workbench.StatusBar.Set(new StatusBarItem("daedalus.editor.position", position, StatusBarAlignment.Right, 900, source) { AccessibleName = position, Command = "editor.action.gotoLine" });
        var indentation = controller.Editor.Options.ConvertTabsToSpaces ? $"Spaces: {controller.Editor.Options.IndentationSize}" : $"Tab Size: {controller.Editor.Options.IndentationSize}";
        Workbench.StatusBar.Set(new StatusBarItem("daedalus.editor.indentation", indentation, StatusBarAlignment.Right, 890, source));
        Workbench.StatusBar.Set(new StatusBarItem("daedalus.editor.encoding", model.Format.EncodingName, StatusBarAlignment.Right, 880, source) { Tooltip = "Encoding" });
        Workbench.StatusBar.Set(new StatusBarItem("daedalus.editor.eol", model.Format.LineEndingName, StatusBarAlignment.Right, 870, source) { Tooltip = "Line endings (click to change)", Command = "editor.action.changeEndOfLine" });
        var language = model.LanguageId is { } id ? Workbench.LanguageDefinitions.Get(id)?.Name ?? id : "Plain Text";
        Workbench.StatusBar.Set(new StatusBarItem("daedalus.editor.language", language, StatusBarAlignment.Right, 860, source) { Tooltip = "Language" });
        Workbench.Context.Set("editorHasSelection", caret.SelectionLength > 0);
        Workbench.Context.Set("editorHasMultipleSelections", caret.Carets > 1);
    }

    private bool _diagnosticsStatusScheduled;

    private void ScheduleDiagnosticsStatus()
    {
        if (_diagnosticsStatusScheduled)
        {
            return;
        }

        _diagnosticsStatusScheduled = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _diagnosticsStatusScheduled = false;
            var all = Languages.Diagnostics.All.SelectMany(f => f.Items).ToList();
            var errors = all.Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnings = all.Count(d => d.Severity == DiagnosticSeverity.Warning);
            var command = BuiltIns.ViewCommandPrefix + ErrorListViewModel.ToolWindowId;
            Workbench.StatusBar.Set(new StatusBarItem("daedalus.problems.errors", $"$(error) {errors}", StatusBarAlignment.Left, 100, StatusSource)
            {
                Tooltip = $"{errors} error(s): open the Error List",
                AccessibleName = $"{errors} errors",
                Command = command,
            });
            Workbench.StatusBar.Set(new StatusBarItem("daedalus.problems.warnings", $"$(warning) {warnings}", StatusBarAlignment.Left, 99, StatusSource)
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
