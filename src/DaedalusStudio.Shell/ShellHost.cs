using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Windows.Input;
using Avalonia;
using DaedalusStudio.Platform;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.Theming;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Plugins;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.StatusBar;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>What the shell needs from its main window.</summary>
public interface IShellWindow
{
    void FocusPane(bool backwards);

    void RequestClose();

    Avalonia.Input.Platform.IClipboard? Clipboard { get; }

    /// <summary>Asks for a solution or project file to open (null: cancelled).</summary>
    Task<string?> PickSolutionAsync();

    /// <summary>Asks for a project file to add to the solution (null: cancelled).</summary>
    Task<string?> PickProjectAsync();

    /// <summary>Shows and activates the window (another process asked this one to open something).</summary>
    void BringToFront();

    /// <summary>Asks for an existing file (null: cancelled).</summary>
    Task<string?> PickFileAsync(string title, string filterName, string[] patterns);

    /// <summary>Asks where to save a file (null: cancelled).</summary>
    Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string[] patterns);
}

/// <summary>
/// The running shell: the workbench services, the plugin manager, the layout and the view models of the
/// window chrome. Everything that is not a view lives here, so headless tests can drive it directly.
/// </summary>
public sealed partial class ShellHost : IAsyncDisposable
{
#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    private readonly HashSet<string> _viewCommands = new(StringComparer.Ordinal);

    public ShellHost(Application app, ShellOptions options)
    {
        Options = options;
        Paths = AppPaths.Resolve(PlatformInfo.Current, Environment.GetEnvironmentVariable, options.UserDataDir);
        Paths.EnsureCreated();
        Workbench = new WorkbenchServices(Paths, PlatformInfo.Current, workspaceSettingsFile: null, options.WatchFiles);
        Workbench.Context.Set("isDevelopment", options.Development || IsDebugBuild);
        Workbench.Settings.Register([.. BuiltIns.Settings(), BuiltIns.RestoreOnOpenSetting(), .. BuiltIns.EditorSettings(), .. BuiltIns.BuildSettings(), .. BuiltIns.DebugSettings(), .. BuiltIns.TestingSettings(), .. BuiltIns.PackageSettings()]);
        InitializeTerminalProfiles();
        Theme = new ThemeService(app, Workbench);
        CrashReporter.Install(Paths.Logs, () => Workbench.Settings.GetBoolean(CrashReporter.SettingKey, false), () =>
        {
            var log = Workbench.Log.GetText();
            return log.Length > 8000 ? log[^8000..] : log;
        });

        foreach (var window in BuiltIns.ToolWindows().Concat(BuiltIns.DebugToolWindows()).Append(BuiltIns.GitToolWindow()).Append(BuiltIns.TestToolWindow()).Append(BuiltIns.CoverageToolWindow()).Append(BuiltIns.TerminalToolWindow()).Append(BuiltIns.InteractiveToolWindow()).Concat(BuiltIns.WebToolWindows()))
        {
            Workbench.ToolWindows.Register(window);
        }

        Plugins = new PluginManager(Workbench, _ => PluginHostLocator.Resolve(), ShellInfo.Version);
        Plugins.Prompts = new PluginPrompts(this);
        EditorFont = new EditorFont(Workbench.Settings);
        Output = new OutputViewModel(Workbench.Output, EditorFont) { WordWrap = Workbench.Settings.GetBoolean("output.wordWrap") };
        Notifications = new NotificationsViewModel(Workbench.Notifications, () => Workbench.Settings.GetInt32("workbench.notifications.autoHideSeconds", 8));
        PluginList = new PluginsViewModel(Plugins);
        Palette = new QuickPickViewModel();
        Dialogs = new DialogViewModel();
        StatusBar = new StatusBarViewModel(Workbench.StatusBar, id => CommandFor(id)) { IsVisible = Workbench.Settings.GetBoolean("workbench.statusBar.visible", true) };
        InitializeToolbar();
        InitializeSolutions();
        InitializeBuild();
        InitializePackages();
        InitializeProjectManagement();
        InitializeTerminal();
        InitializeInteractive();
        InitializeDebugging();
        InitializeGit();
        InitializeEditor();
        InitializeRefactoring();
        InitializeNavigation();
        LoadSnippets();
        InitializeEditingSafety();
        InitializeBookmarks();
        InitializeTesting();
        InitializeWeb();

        // A file given on the command line: start its language service now, alongside the window.
        if (options.OpenFile is { } file && Languages.LanguageIdOf(file) is { } languageId)
        {
            _ = Languages.ClientAsync(languageId);
        }

        Layout = new LayoutManager(Workbench.ToolWindows, CreateToolContent);
        WireEditorToLayout();

        BuiltIns.RegisterCommands(this);
        BuiltIns.RegisterSolutionCommands(this);
        BuiltIns.RegisterEditorCommands(this);
        BuiltIns.RegisterBuildCommands(this);
        BuiltIns.RegisterDebugCommands(this);
        BuiltIns.RegisterWebCommands(this);
        BuiltIns.RegisterGitCommands(this);
        BuiltIns.RegisterTestingCommands(this);
        BuiltIns.RegisterPackageCommands(this);
        BuiltIns.RegisterProjectManagementCommands(this);
        BuiltIns.RegisterMenus(Workbench.Menus);
        BuiltIns.RegisterSolutionMenus(Workbench.Menus);
        BuiltIns.RegisterEditorMenus(Workbench.Menus);
        BuiltIns.RegisterBuildMenus(Workbench.Menus);
        BuiltIns.RegisterDebugMenus(Workbench.Menus);
        BuiltIns.RegisterWebMenus(Workbench.Menus);
        BuiltIns.RegisterGitMenus(Workbench.Menus);
        BuiltIns.RegisterTestingMenus(Workbench.Menus);
        BuiltIns.RegisterPackageMenus(Workbench.Menus);
        BuiltIns.RegisterProjectManagementMenus(Workbench.Menus);
        BuiltIns.RegisterTerminalMenus(Workbench.Menus);
        BuiltIns.RegisterInteractiveMenus(Workbench.Menus);
        Workbench.Keybindings.SetDefaults(BuiltIns.ParseDefaultKeybindings(Workbench.Platform));
        Workbench.LoadUserKeybindings();
        SyncViewCommands();
        BuiltIns.RegisterTerminalCommands(this);
        BuiltIns.RegisterInteractiveCommands(this);
        LoadState();

        Workbench.ToolWindows.Changed += (_, _) => Ui.Post(SyncViewCommands);
        Workbench.ToolWindows.ShowRequested += (_, window) => Ui.Post(() => Layout.ShowToolWindow(window.Id));
        Workbench.Output.ShowRequested += (_, channel) => Ui.Post(() =>
        {
            Output.Selected = channel;
            Layout.ShowToolWindow(BuiltIns.OutputWindow, focus: false);
        });
        ApplyKeyboardScheme();
        Workbench.Settings.Changed += (_, e) => Ui.Post(() => OnSettingsChanged(e));
        Layout.ActiveDocumentChanged += (_, document) => Workbench.Context.Set("activeDocumentKind", document?.Kind);
        Notifications.Changed += (_, _) => UpdateStatusItems();
        Theme.Changed += (_, _) => UpdateStatusItems();
        Palette.Closed += (_, _) => Workbench.Context.Set("inCommandPalette", false);

        DiscoverPlugins();
        RestoreLayout();
        UpdateStatusItems();
        ReportPreviousCrashes();
    }

    /// <summary>Crash reports written since the last start: say so once, with a way to open them.</summary>
    private void ReportPreviousCrashes()
    {
        if (CrashReporter.Folder is not { } folder)
        {
            return;
        }

        var marker = Path.Combine(folder, ".seen");
        var since = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : DateTime.MinValue;
        var reports = CrashReporter.ReportsSince(since);
        if (reports.Count == 0)
        {
            return;
        }

        try
        {
            File.WriteAllText(marker, string.Empty);
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }

        var notification = Workbench.Notifications.Warning(
            "Daedalus Studio crashed last time. A crash report was saved on this computer; attach it to an issue if you report the problem.",
            "Open Folder");
        _ = notification.Result.ContinueWith(
            t =>
            {
                if (t.Result == "Open Folder")
                {
                    OpenCrashReports();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    public void OpenCrashReports()
    {
        if (CrashReporter.Folder is not { } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        ShellIntegration.OpenUrl(new Uri(folder + Path.DirectorySeparatorChar));
    }

    public ShellOptions Options { get; }

    public AppPaths Paths { get; }

    public WorkbenchServices Workbench { get; }

    public PluginManager Plugins { get; }

    public ThemeService Theme { get; }

    public LayoutManager Layout { get; }

    public EditorFont EditorFont { get; }

    public OutputViewModel Output { get; }

    public NotificationsViewModel Notifications { get; }

    public PluginsViewModel PluginList { get; }

    public QuickPickViewModel Palette { get; }

    public DialogViewModel Dialogs { get; }

    public StatusBarViewModel StatusBar { get; }

    public IShellWindow? Window { get; set; }

    /// <summary>The window size and position saved with the layout, if any.</summary>
    public WindowPlacement? InitialPlacement { get; private set; }

    private string StateFile => Path.Combine(Paths.State, "state.json");

    // ------------------------------------------------------------------ startup

    /// <summary>Called once the window is interactive: deferred work that must not delay the first frame.</summary>
    public void OnInteractive()
    {
        _ = OfferRecoveryAsync();
        foreach (var error in Options.Errors)
        {
            Workbench.Notifications.Warning(error);
        }

        if (Options.Path is { } path)
        {
            // TODO(issue): open a folder without a solution or project file (post-MVP).
            _ = Directory.Exists(path) ? OpenFolderAsync(path) : OpenSolutionAsync(path);
        }

        if (Options.Diff is { } diff)
        {
            _ = OpenFileDiffAsync(diff.Left, diff.Right);
        }

        // Later 'daedalus diff' invocations open here.
        _instance = Infrastructure.SingleInstance.Listen(Infrastructure.SingleInstance.PipeName(Paths.State), OnInstanceRequestAsync);

        // Started as git's merge tool or editor: do that, then exit with its outcome.
        if (Options.Merge is { } merge)
        {
            _ = ExitWhenDoneAsync(MergeFilesAsync(merge.Local, merge.Remote, merge.Base, merge.Merged));
        }
        else if (Options.WaitFile is { } waitFile)
        {
            _ = ExitWhenDoneAsync(EditAndWaitAsync(waitFile));
        }

        _ = Plugins.ActivateStartupPluginsAsync(CancellationToken.None);
    }

    private Infrastructure.SingleInstance? _instance;

    /// <summary>A request from another <c>daedalus</c> process (on a background thread).</summary>
    private Task<bool> OnInstanceRequestAsync(Infrastructure.InstanceRequest request)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Post(async () =>
        {
            try
            {
                Window?.BringToFront();
                switch (request)
                {
                    case { Command: "diff", Arguments: [var left, var right] }:
                        await OpenFileDiffAsync(left, right).ConfigureAwait(true);
                        done.TrySetResult(true);
                        break;
                    case { Command: "merge", Arguments: [var local, var remote, var @base, var merged] }:
                        done.TrySetResult(await MergeFilesAsync(local, remote, @base, merged).ConfigureAwait(true));
                        break;
                    case { Command: "wait", Arguments: [var file] }:
                        done.TrySetResult(await EditAndWaitAsync(file).ConfigureAwait(true));
                        break;
                    default:
                        done.TrySetResult(false);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                Workbench.Notifications.Error(ex.Message);
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    /// <summary>The process's exit code when it was started for a merge or an edit (1: not resolved).</summary>
    public static int ExitCode { get; private set; }

    private async Task ExitWhenDoneAsync(Task<bool> work)
    {
        var succeeded = await work.ConfigureAwait(true);
        ExitCode = succeeded ? 0 : 1;
        Window?.RequestClose();
    }

    private void DiscoverPlugins()
    {
        var builtIn = Options.BuiltInPluginsDir ?? Path.Combine(AppContext.BaseDirectory, "plugins");
        var roots = Options.PluginDirs.Select(d => (d, false))
            .Append((Paths.PluginsDirectory, false))
            .Append((builtIn, true));
        Plugins.AddPlugins(PluginDiscovery.Scan(roots, Workbench.Platform, Options.EnableProposedApi));
    }

    private object? CreateToolContent(ToolWindowDescriptor window) => window.Kind switch
    {
        ToolWindowKind.Tree => new TreeToolViewModel(window.Id, Plugins, Run, () => TitleActions(window.Id)),
        ToolWindowKind.Markdown => new MarkdownToolViewModel(window.Id, Plugins, Run),
        _ => window.Id switch
        {
            BuiltIns.OutputWindow => Output,
            BuiltIns.NotificationsWindow => Notifications,
            BuiltIns.PluginsWindow => PluginList,
            SolutionExplorerViewModel.ToolWindowId => Explorer,
            TestExplorerViewModel.ToolWindowId => OpenTestExplorer(),
            CoverageResultsViewModel.ToolWindowId => CoverageResults,
            ErrorListViewModel.ToolWindowId => ErrorList,
            FindResultsViewModel.ToolWindowId => FindResults,
            DocumentOutlineViewModel.ToolWindowId => DocumentOutline,
            HierarchyViewModel.ToolWindowId => Hierarchy,
            CodeDefinitionViewModel.ToolWindowId => CodeDefinition,
            BookmarksViewModel.ToolWindowId => BookmarkList,
            TaskListViewModel.ToolWindowId => TaskList,
            SyntaxVisualizerViewModel.ToolWindowId => SyntaxVisualizer,
            CodeMetricsViewModel.ToolWindowId => CodeMetrics,
            VariablesViewModel.LocalsWindowId => Locals,
            VariablesViewModel.AutosWindowId => Autos,
            WatchViewModel.ToolWindowId => Watch,
            CallStackViewModel.ToolWindowId => CallStack,
            BreakpointsViewModel.ToolWindowId => BreakpointList,
            DebugConsoleViewModel.ToolWindowId => DebugConsole,
            QuickWatchViewModel.ToolWindowId => QuickWatch,
            EndpointsExplorerViewModel.ToolWindowId => Endpoints,
            HttpResponseViewModel.ToolWindowId => HttpResponse,
            ThreadsViewModel.ToolWindowId => ThreadList,
            ModulesViewModel.ToolWindowId => ModuleList,
            ExceptionSettingsViewModel.ToolWindowId => ExceptionSettingsWindow,
            GitChangesViewModel.ToolWindowId => GitChanges,
            TerminalViewModel.ToolWindowId => TerminalWindow,
            InteractiveViewModel.ToolWindowId => InteractiveWindow,
            _ => null,
        },
    };

    /// <summary>Commands contributed to a tool window's title bar (<c>toolWindow/title</c>).</summary>
    private IReadOnlyList<ToolbarActionViewModel> TitleActions(string viewId)
    {
        var scope = Workbench.Context.With(new Dictionary<string, object?>(StringComparer.Ordinal) { ["view"] = viewId, ["focusedView"] = viewId });
        return [.. Workbench.Menus.GetItems(MenuLocations.ToolWindowTitle, scope).Select(i => new ToolbarActionViewModel(i.Title, i.KeyLabel, CommandFor(i.Command)))];
    }

    /// <summary>One <c>workbench.view.&lt;id&gt;</c> command (and View menu entry) per tool window.</summary>
    private void SyncViewCommands()
    {
        var added = new List<MenuItemRule>();
        foreach (var window in Workbench.ToolWindows.All)
        {
            var id = BuiltIns.ViewCommandPrefix + window.Id;
            if (!_viewCommands.Add(id))
            {
                continue;
            }

            var windowId = window.Id;
            Workbench.Commands.Register(new CommandDescriptor(id, window.Title, ContributionSource.Shell) { Category = "View" }, async (args, _) =>
            {
                var preserveFocus = args.Count > 0 && args[0].ValueKind == JsonValueKind.Object
                    && args[0].TryGetProperty("preserveFocus", out var p) && p.ValueKind == JsonValueKind.True;
                await Ui.RunAsync(() => Layout.ShowToolWindow(windowId, focus: !preserveFocus)).ConfigureAwait(false);
                return null;
            });
            added.Add(new MenuItemRule(MenuLocations.MenuBar("view"), id, ContributionSource.Shell) { Group = "view" });
        }

        if (added.Count > 0)
        {
            Workbench.Menus.Add(added);
        }
    }

    // ------------------------------------------------------------------ running commands

    /// <summary>Runs a command from the UI; failures become notifications, never exceptions.</summary>
    public void Run(string commandId, IReadOnlyList<JsonElement> args) => _ = RunAsync(commandId, args);

    public void Run(string commandId) => _ = RunAsync(commandId, []);

    public async Task RunAsync(string commandId, IReadOnlyList<JsonElement> args)
    {
        try
        {
            await Workbench.Commands.ExecuteAsync(commandId, args, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (CommandNotEnabledException)
        {
            Ui.Post(() => StatusBar.ShowMessage($"“{Title(commandId)}” is not available right now.", TimeSpan.FromSeconds(4)));
        }
        catch (Exception ex)
        {
            Workbench.Log.AppendLine($"[command] {commandId} failed: {ex}");
            ReportError($"“{Title(commandId)}” failed: {ex.Message}");
        }
    }

    /// <summary>An error notification whose "Show Logs" action opens the shell log.</summary>
    public void ReportError(string message)
    {
        var notification = Workbench.Notifications.Error(message, "Show Logs");
        _ = notification.Result.ContinueWith(
            t =>
            {
                if (t.Result == "Show Logs")
                {
                    Ui.Post(ShowLogs);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    public ICommand CommandFor(string commandId, params JsonElement[] args) => new RelayCommand(() => Run(commandId, args));

    private string Title(string commandId) => Workbench.Commands.Find(commandId)?.DisplayTitle ?? commandId;

    // ------------------------------------------------------------------ built-in command implementations

    public void ShowCommandPalette() => _ = ShowCommandPaletteAsync();

    private async Task ShowCommandPaletteAsync()
    {
        var context = Workbench.Context;
        var recent = Workbench.Commands.RecentCommandIds.Select((id, rank) => (id, rank)).ToDictionary(x => x.id, x => x.rank, StringComparer.Ordinal);
        var items = Workbench.Commands.Descriptors
            .Where(d => d.ShowInPalette && d.Enablement.Evaluate(context) && Workbench.Menus.IsVisibleInPalette(d.Id, context))
            .Select(d => (Descriptor: d, Recent: recent.GetValueOrDefault(d.Id, -1)))
            .OrderBy(x => x.Recent < 0 ? int.MaxValue : x.Recent)
            .ThenBy(x => x.Descriptor.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new QuickPickItem(x.Descriptor.DisplayTitle)
            {
                KeyLabel = Workbench.Keybindings.GetBinding(x.Descriptor.Id)?.ToLabel(Workbench.Platform),
                Detail = x.Recent >= 0 ? "recently used" : null,
                Tag = x.Descriptor.Id,
            })
            .ToList();

        context.Set("inCommandPalette", true);
        var picked = await Palette.ShowAsync(items, "Type the name of a command to run").ConfigureAwait(true);
        if (picked?.Tag is string id)
        {
            Workbench.Commands.MarkRecent(id);
            Run(id);
        }
    }

    public async Task SelectThemeAsync()
    {
        var current = Theme.Setting;
        var choices = ThemeService.Choices;
        var items = choices.Select(c => new QuickPickItem(c.Title) { Tag = c.Id, Detail = c.Id == current ? "current" : null }).ToList();
        var active = Math.Max(0, choices.ToList().FindIndex(c => c.Id == current));
        Workbench.Context.Set("inCommandPalette", true);
        var picked = await Palette.ShowAsync(items, "Select a color theme (Up/Down to preview)", active, item => Theme.Preview(item?.Tag as string)).ConfigureAwait(true);
        if (picked?.Tag is string id && id != current)
        {
            await Workbench.Settings.SetAsync(ThemeService.SettingKey, id, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }

        Theme.Preview(null);
    }

    public void OpenSettings() =>
        Layout.OpenDocument(Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId)
            ?? new SettingsDocument(new SettingsViewModel(Workbench.Settings, () => Run("workbench.openSettingsJson"))));

    public void OpenKeyboardShortcuts()
    {
        if (Layout.FindDocument<KeyboardShortcutsDocument>(KeyboardShortcutsDocument.DocumentId) is { } open)
        {
            Layout.OpenDocument(open);
            return;
        }

        Layout.OpenDocument(new KeyboardShortcutsDocument(new KeyboardShortcutsViewModel(Workbench, () => Run("workbench.openKeybindingsJson"))));
    }

    public Task OpenSettingsJsonAsync() => OpenTextFileAsync(Paths.SettingsFile, "// Daedalus Studio user settings. See File > Settings for every option.\n{\n}\n");

    public Task OpenKeybindingsJsonAsync() => OpenTextFileAsync(Paths.KeybindingsFile, KeybindingsTemplate);

    private const string KeybindingsTemplate = "// Daedalus Studio key bindings. Entries override the defaults; \"-command\" removes a default binding.\n[\n]\n";

    public void ShowLogs()
    {
        Output.Selected = Workbench.Log;
        Layout.ShowToolWindow(BuiltIns.OutputWindow);
    }

    public void OpenPluginsFolder()
    {
        Directory.CreateDirectory(Paths.PluginsDirectory);
        try
        {
            ShellIntegration.RevealInFileManager(Paths.PluginsDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Workbench.Notifications.Error($"Could not open {Paths.PluginsDirectory}: {ex.Message}");
        }
    }

    public void OpenExternal(IReadOnlyList<JsonElement> args)
    {
        if (args.Count == 0 || args[0].ValueKind != JsonValueKind.String
            || !Uri.TryCreate(args[0].GetString(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("workbench.openExternal expects an http(s) URL.");
        }

        ShellIntegration.OpenUrl(url);
    }

    /// <summary>
    /// <c>editor.activePosition</c>: <c>[documentUri, line, character]</c> (zero-based) of the active editor's caret, or
    /// null. Plugins use it for commands run from the palette or a menu (the editor context menu passes the same values).
    /// </summary>
    public JsonElement? ActiveEditorPosition()
    {
        if (ActiveEditor is not { Controller: { } controller } editor)
        {
            return null;
        }

        var caret = controller.Editor.TextArea.Caret;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(LanguageServices.Protocol.DocumentUris.FromPath(editor.Model.Path));
            writer.WriteNumberValue(caret.Line - 1);
            writer.WriteNumberValue(caret.Column - 1);
            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary><c>workbench.openFile [pathOrUri, line?]</c>: opens a file in an editor, optionally at a zero-based line.</summary>
    public async Task OpenFileCommandAsync(IReadOnlyList<JsonElement> args)
    {
        var target = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
        var path = target is null ? null : LanguageServices.Protocol.DocumentUris.ToPath(target) ?? (Path.IsPathRooted(target) ? target : null);
        if (path is null)
        {
            throw new ArgumentException("workbench.openFile expects a file path or file: URI.");
        }

        var line = args.Count > 1 && args[1].ValueKind == JsonValueKind.Number ? args[1].GetInt32() : (int?)null;
        await OpenFileAsync(path, preview: false, line is { } l ? new LanguageServices.Protocol.Position(l, 0) : null).ConfigureAwait(true);
    }

    /// <summary><c>project.addReferenceTo [projectPath, referencedProjectPath]</c>: adds a project reference.</summary>
    public Task AddProjectReferenceCommandAsync(IReadOnlyList<JsonElement> args, CancellationToken cancellationToken)
    {
        if (args.Count < 2 || args[0].GetString() is not { } project || args[1].GetString() is not { } reference)
        {
            throw new ArgumentException("project.addReferenceTo expects a project path and the referenced project's path.");
        }

        return Solutions.AddProjectReferenceAsync(project, reference, cancellationToken);
    }

    /// <summary><c>workbench.setSetting [key, value]</c>: writes a user setting (null resets it).</summary>
    public Task SetSettingAsync(IReadOnlyList<JsonElement> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args[0].ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("workbench.setSetting expects [key, value].");
        }

        JsonElement? value = args.Count > 1 && args[1].ValueKind != JsonValueKind.Null ? args[1] : null;
        return Workbench.Settings.SetAsync(args[0].GetString()!, value, SettingScope.User, cancellationToken);
    }

    public void ShowAbout() => Workbench.Notifications.Info(ShellInfo.VersionText.Replace("\n", " · ", StringComparison.Ordinal));

    // ------------------------------------------------------------------ status bar and settings

    private void UpdateStatusItems()
    {
        var count = Notifications.Count;
        Workbench.StatusBar.Set(new StatusBarItem("daedalus.notifications", count > 0 ? $"$(bell-dot) {count}" : "$(bell)", StatusBarAlignment.Right, int.MaxValue, ContributionSource.Shell.Id)
        {
            Tooltip = count == 0 ? "No notifications" : count == 1 ? "1 notification" : $"{count} notifications",
            AccessibleName = count == 0 ? "No notifications" : $"Notifications: {count}",
            Command = BuiltIns.ViewCommandPrefix + BuiltIns.NotificationsWindow,
        });

        Workbench.Context.Set("toastsVisible", Notifications.HasToasts);
    }

    /// <summary>The <c>keyboardScheme</c> context key, which the default key bindings depend on (M29).</summary>
    private void ApplyKeyboardScheme() =>
        Workbench.Context.Set("keyboardScheme", Workbench.Settings.GetString(BuiltIns.KeyboardSchemeSetting, "default") == "vscode" ? "vscode" : "default");

    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        if (e.Affects("workbench.statusBar.visible"))
        {
            StatusBar.IsVisible = Workbench.Settings.GetBoolean("workbench.statusBar.visible", true);
        }

        if (e.Affects(BuiltIns.KeyboardSchemeSetting))
        {
            ApplyKeyboardScheme();
        }

        if (e.Affects("languageServices.analysisScope") || e.Affects("navigation.sourceLink"))
        {
            _ = ConfigureLanguagesAsync();
        }

        if (e.Affects("output.wordWrap"))
        {
            Output.WordWrap = Workbench.Settings.GetBoolean("output.wordWrap");
        }
    }

    // ------------------------------------------------------------------ persistence

    private void RestoreLayout()
    {
        if (ReadJson(Paths.LayoutFile, ShellJsonContext.Default.LayoutSnapshot) is { } snapshot && Layout.Restore(snapshot))
        {
            InitialPlacement = snapshot.Window;
        }
    }

    private void LoadState()
    {
        if (ReadJson(StateFile, ShellJsonContext.Default.ShellState)?.RecentCommands is { } recent)
        {
            Workbench.Commands.SetRecent(recent);
        }
    }

    /// <summary>Saves the layout (with the window placement) and the small state file. Called when the window closes.</summary>
    public void SaveState(WindowPlacement? placement)
    {
        WriteJson(Paths.LayoutFile, Layout.Capture(placement), ShellJsonContext.Default.LayoutSnapshot);
        WriteJson(StateFile, new ShellState { RecentCommands = Workbench.Commands.RecentCommandIds }, ShellJsonContext.Default.ShellState);
        SaveEditorSession();
    }

    private T? ReadJson<T>(string path, JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), info) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Workbench.Log.AppendLine($"[state] Ignoring {path}: {ex.Message}");
            return null;
        }
    }

    private void WriteJson<T>(string path, T value, JsonTypeInfo<T> info)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, info));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Log.AppendLine($"[state] Could not write {path}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _instance?.Dispose();
        Terminals?.Dispose();
        if (Interactive is not null)
        {
            _ = Interactive.DisposeAsync().AsTask();
        }
        await Debugger.DisposeAsync().ConfigureAwait(false);
        Git.Dispose();
        await Languages.DisposeAsync().ConfigureAwait(false);
        await Solutions.DisposeAsync().ConfigureAwait(false);
        await Plugins.DisposeAsync().ConfigureAwait(false);
        Workbench.Dispose();
    }
}
