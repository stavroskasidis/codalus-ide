using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Windows.Input;
using Avalonia;
using Codalus.Platform;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Workbench;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Settings;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.ToolWindows;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

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

    /// <summary>Shows and activates the window (another process asked this one to open something, or the debugger stopped).</summary>
    void BringToFront();

    /// <summary>Full screen (M23a): the window covers the screen, without its frame; false returns to how it was.</summary>
    bool IsFullScreen { get; set; }

    /// <summary>Asks for an existing file (null: cancelled).</summary>
    Task<string?> PickFileAsync(string title, string filterName, string[] patterns);

    /// <summary>Asks where to save a file (null: cancelled).</summary>
    Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string[] patterns);

    /// <summary>Asks for one or more existing files of any type (empty: cancelled).</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title);

    /// <summary>Asks for a folder (null: cancelled).</summary>
    Task<string?> PickFolderAsync(string title);
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

    /// <summary>The shell is being disposed: its timers stay stopped.</summary>
    private bool _disposing;

    /// <summary><see cref="OnInteractive"/> has run.</summary>
    private bool _interactive;

    /// <summary>Cancelled when the shell is disposed: work it started for itself (the environment check) stops with it.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    public ShellHost(Application app, ShellOptions options)
    {
        Options = options;
        Paths = AppPaths.Resolve(PlatformInfo.Current, Environment.GetEnvironmentVariable, options.UserDataDir);
        Paths.EnsureCreated();
        _usedBefore = WasUsed(Paths); // before anything writes to the folder (M46)
        Workbench = new WorkbenchServices(Paths, PlatformInfo.Current, workspaceSettingsFile: null, options.WatchFiles);
        Workbench.Context.Set("isDevelopment", options.Development || IsDebugBuild);
        LoadLocalization();
        UseUserDotnet();
        Workbench.Settings.Register([LocaleDescriptor(), .. BuiltIns.Settings(), BuiltIns.RestoreOnOpenSetting(), .. BuiltIns.EditorSettings(), .. BuiltIns.CodeSearchSettings(), .. BuiltIns.BuildSettings(), .. BuiltIns.DebugSettings(), .. BuiltIns.TestingSettings(), .. BuiltIns.PackageSettings(), .. BuiltIns.WebSettings(), .. BuiltIns.UpdateSettings(), .. BuiltIns.DataSettings(), .. BuiltIns.GitSettings(), .. BuiltIns.ContainerSettings(), .. BuiltIns.ProfilingSettings(), .. BuiltIns.RemoteSettings(), .. BuiltIns.FSharpSettings(), .. BuiltIns.WinFormsSettings(), .. BuiltIns.TemplatesSettings()]);
        InitializeTerminalProfiles();
        Theme = new ThemeService(app, Workbench, () => Plugins?.Themes ?? []);
        Theme.Changed += (_, _) => UseFamilyDefaults();
        UseFamilyDefaults();
        ListenForOpenedFiles(app);
        CrashReporter.Install(Paths.Logs, () => Workbench.Settings.GetBoolean(CrashReporter.SettingKey, false), () =>
        {
            var log = Workbench.Log.GetText();
            return log.Length > 8000 ? log[^8000..] : log;
        });

        foreach (var window in BuiltIns.ToolWindows().Append(BuiltIns.CodeSearchToolWindow()).Concat(BuiltIns.DebugToolWindows()).Append(BuiltIns.GitToolWindow()).Append(BuiltIns.TestToolWindow()).Append(BuiltIns.CoverageToolWindow()).Append(BuiltIns.TerminalToolWindow()).Append(BuiltIns.InteractiveToolWindow()).Concat(BuiltIns.WebToolWindows()).Concat(BuiltIns.DataToolWindows()).Concat(BuiltIns.ContainerToolWindows()).Concat(BuiltIns.ProfilingToolWindows()).Concat(BuiltIns.XamlToolWindows()).Concat(BuiltIns.WinFormsToolWindows()).Concat(BuiltIns.RemoteToolWindows()).Concat(BuiltIns.AiToolWindows()).Concat(BuiltIns.MobileToolWindows()))
        {
            Workbench.ToolWindows.Register(window);
        }

        Plugins = new PluginManager(Workbench, _ => PluginHostLocator.Resolve(), ShellInfo.Version)
        {
            DebugHostFile = Options.DebugPluginHost,
            DevelopmentDirs = Options.PluginDirs,
        };
        Plugins.Prompts = new PluginPrompts(this);
        Plugins.HostRuntimeMissing = ReportHostRuntimeMissing; // one notification, not one per plugin (M49); any thread
        EditorFont = new EditorFont(Workbench.Settings);
        Output = new OutputViewModel(Workbench.Output, EditorFont) { WordWrap = Workbench.Settings.GetBoolean("output.wordWrap") };
        Notifications = new NotificationsViewModel(Workbench.Notifications, () => Workbench.Settings.GetInt32("workbench.notifications.autoHideSeconds", 8));
        PluginList = new PluginsViewModel(Plugins, (id, off) => _ = SetPluginTurnedOffAsync(id, off));
        Palette = new QuickPickViewModel();
        Dialogs = new DialogViewModel();
        StatusBar = new StatusBarViewModel(Workbench.StatusBar, id => CommandFor(id)) { IsVisible = Workbench.Settings.GetBoolean("workbench.statusBar.visible", true) };
        InitializeToolbar();
        InitializeCustomizations();
        InitializeSolutions();
        InitializeBuild();
        InitializePackages();
        InitializeProjectManagement();
        InitializeProjectSystem();
        InitializeTerminal();
        InitializeInteractive();
        InitializeDebugging();
        InitializeGit();
        InitializeHosting();
        InitializeEditor();
        InitializeRefactoring();
        InitializeCodeSearch();
        InitializeNavigation();
        LoadSnippets();
        InitializeEditingSafety();
        InitializeBookmarks();
        InitializeTesting();
        InitializeWeb();
        InitializeWebLanguages();
        InitializeFSharp();
        InitializeNpm();
        InitializeHotReload();
        InitializeEntityFramework();
        InitializeWebTools();
        InitializeData();
        InitializeContainers();
        InitializeAspire();
        InitializeProfiling();
        InitializeXaml();
        InitializeWinFormsDesigner();
        InitializeUpdates();
        InitializeRemote();
        InitializeMobile();
        InitializeProgress();
        InitializePluginFiles();
        HookAuthentication();
        InitializeSourceControlProviders();
        InitializePluginDevelopment();
        InitializePluginGallery();
        InitializePluginBisect();
        InitializePluginPerformance();
        InitializeComponentsPage();
        InitializeAi();
        InitializeAgent();

        // A file given on the command line: start its language service now, alongside the window.
        if (options.OpenFile is { } file && Languages.LanguageIdOf(file) is { } languageId)
        {
            _ = Languages.ClientAsync(languageId);
        }

        Layout = new LayoutManager(Workbench.ToolWindows, CreateToolContent);
        InitializeReferences();
        InitializeCustomEditors();
        WireEditorToLayout();
        WireWinFormsDesigner();

        BuiltIns.RegisterCommands(this);
        BuiltIns.RegisterSolutionCommands(this);
        BuiltIns.RegisterEditorCommands(this);
        BuiltIns.RegisterCodeSearchCommands(this);
        BuiltIns.RegisterBuildCommands(this);
        BuiltIns.RegisterDebugCommands(this);
        BuiltIns.RegisterWebCommands(this);
        BuiltIns.RegisterDataCommands(this);
        BuiltIns.RegisterContainerCommands(this);
        BuiltIns.RegisterProfilingCommands(this);
        BuiltIns.RegisterXamlCommands(this);
        BuiltIns.RegisterWinFormsCommands(this);
        BuiltIns.RegisterRemoteCommands(this);
        BuiltIns.RegisterAiCommands(this);
        BuiltIns.RegisterMobileCommands(this);
        BuiltIns.RegisterFSharpCommands(this);
        BuiltIns.RegisterGitCommands(this);
        BuiltIns.RegisterTestingCommands(this);
        BuiltIns.RegisterPackageCommands(this);
        BuiltIns.RegisterProjectManagementCommands(this);
        BuiltIns.RegisterProjectSystemCommands(this);
        BuiltIns.RegisterTemplatesCommands(this);
        BuiltIns.RegisterEditCommands(this);
        BuiltIns.RegisterMenuBar(Workbench.Menus, Workbench.Platform);
        BuiltIns.RegisterEditorContextMenu(Workbench.Menus, Workbench.Platform);
        BuiltIns.RegisterDocumentTabContextMenu(Workbench.Menus);
        BuiltIns.RegisterSolutionMenus(Workbench.Menus);
        BuiltIns.RegisterGitChangesMenus(Workbench.Menus, Workbench.Platform);
        BuiltIns.RegisterBuildMenus(Workbench.Menus);
        BuiltIns.RegisterWebMenus(Workbench.Menus);
        BuiltIns.RegisterDataMenus(Workbench.Menus);
        BuiltIns.RegisterContainerMenus(Workbench.Menus);
        BuiltIns.RegisterProfilingMenus(Workbench.Menus);
        BuiltIns.RegisterXamlMenus(Workbench.Menus);
        BuiltIns.RegisterWinFormsMenus(Workbench.Menus);
        BuiltIns.RegisterRemoteMenus(Workbench.Menus);
        BuiltIns.RegisterAiMenus(Workbench.Menus);
        BuiltIns.RegisterMobileMenus(Workbench.Menus);
        BuiltIns.RegisterFSharpMenus(Workbench.Menus);
        BuiltIns.RegisterPackageMenus(Workbench.Menus);
        BuiltIns.RegisterProjectManagementMenus(Workbench.Menus);
        BuiltIns.RegisterProjectSystemMenus(Workbench.Menus);
        BuiltIns.RegisterTemplatesMenus(Workbench.Menus);
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
        Layout.ActiveDocumentChanged += (_, _) => UpdateDiffContext();
        Layout.ActiveDocumentChanged += (_, _) => OnSqlEditorActivated();
        Notifications.Changed += (_, _) => UpdateStatusItems();
        Theme.Changed += (_, _) => UpdateStatusItems();
        // Any quick pick (the palette is the shared one): Escape closes it before it hides the toasts.
        Palette.Opened += (_, _) => Workbench.Context.Set("inCommandPalette", true);
        Infrastructure.SafeClipboard.Busy += OnClipboardBusy;

        // Activation event onSolutionOpened (API 1.0, docs/plugins.md): plugins that start with a solution.
        Solutions.SolutionChanged += (_, _) =>
        {
            if (Solutions.Solution is not null)
            {
                _ = Plugins.ActivateByEventAsync("onSolutionOpened", CancellationToken.None);
            }
        };
        Palette.Closed += (_, _) => Workbench.Context.Set("inCommandPalette", false);

        DiscoverPlugins();
        RestoreLayout();
        InitializeTabs();
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
            Localizer.Get("msg.shellHost.codalusStudioCrashedLastTimeA", "Codalus IDE crashed last time. A crash report was saved on this computer; attach it to an issue if you report the problem."),
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
        OpenExternalUrl(new Uri(folder + Path.DirectorySeparatorChar));
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

    /// <summary>The Ctrl+Tab switcher of open documents.</summary>
    public DocumentSwitcherViewModel Switcher { get; } = new();

    public DialogViewModel Dialogs { get; }

    public StatusBarViewModel StatusBar { get; }

    public IShellWindow? Window { get; set; }

    /// <summary>The element that last had the keyboard focus outside the menus (Edit → Cut, Copy, Paste act on it).</summary>
    public Avalonia.Visual? LastFocus { get; set; }

    /// <summary>The window size and position saved with the layout, if any.</summary>
    public WindowPlacement? InitialPlacement { get; private set; }

    private string StateFile => Path.Combine(Paths.State, "state.json");

    // ------------------------------------------------------------------ startup

    /// <summary>
    /// Called once the window is interactive: deferred work that must not delay the first frame. Nothing once the shell is
    /// being disposed (its window closed before its first frames), or the second time: what it starts, such as the
    /// listener for other <c>codalus</c> processes, would outlive the shell (a second listener replaced the first, which
    /// was never disposed).
    /// </summary>
    public void OnInteractive()
    {
        if (_disposing || _interactive)
        {
            return;
        }

        _interactive = true;

        _ = OfferRecoveryAsync();
        StartUpdates();

        // The first-run setup (M46) shows before the start window; its Tools step offers what M37 offers here.
        var setup = FirstRunDecision;
        Workbench.Log.AppendLine($"[setup] First-run setup: {setup}.");
        if (Options.SmokeTest is null && Options.Bench is null)
        {
            _ = CheckEnvironmentAsync(offer: setup != SetupDecision.Show);
        }
        _ = SyncSettingsAsync(quiet: true);
        foreach (var error in Options.Errors)
        {
            Workbench.Notifications.Warning(error);
        }

        if (Options.Remote is { } remoteTarget)
        {
            // A remote window (M21): the path is the remote machine's.
            if (Codalus.Workbench.Remote.RemoteTargetSpec.Parse(remoteTarget, containerTool: ContainerEngine.Tool) is { } spec)
            {
                _ = ConnectRemoteAsync(spec, Options.Path);
            }
            else
            {
                Workbench.Notifications.Error($"'{remoteTarget}' isn't a remote target: use ssh:user@host[:port]{(OperatingSystem.IsWindows() ? " or wsl:<distribution>" : string.Empty)}.");
            }
        }
        else if (Options.Path is { } path)
        {
            // A folder opens its solution, or itself in folder mode (ADR 0033).
            _ = Directory.Exists(path) ? OpenFolderAsync(path) : OpenSolutionAsync(path);
        }

        if (Options.Diff is { } diff)
        {
            _ = OpenFileDiffAsync(diff.Left, diff.Right);
        }

        // Later 'codalus diff' invocations open here (in the local window: a remote one leaves them to it).
        if (Options.Remote is null)
        {
            _instance = Infrastructure.SingleInstance.Listen(Infrastructure.SingleInstance.PipeName(Paths.State), OnInstanceRequestAsync);
        }

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
        _ = Plugins.ActivateDebuggedPluginsAsync(CancellationToken.None);
        _ = CheckPluginUpdatesOnStartupAsync();
        _ = ResumePluginBisectAsync();
        if (setup == SetupDecision.Show)
        {
            Ui.Post(() => ShowSetup(firstStart: true));
        }
        else
        {
            OnStartupWithoutSolution();
        }
    }

    private Infrastructure.SingleInstance? _instance;

    /// <summary>A request from another <c>codalus</c> process (on a background thread).</summary>
    private Task<bool> OnInstanceRequestAsync(Infrastructure.InstanceRequest request)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Post(async () =>
        {
            try
            {
                if (request.Command != "openPath")
                {
                    Window?.BringToFront();
                }

                switch (request)
                {
                    case { Command: "openPath", Arguments: [var openPath] }:
                        // A folder or solution from the OS or a terminal: here when nothing is open (it comes to the front then).
                        done.TrySetResult(TakeOpenPath(openPath));
                        break;
                    case { Command: "diff", Arguments: [var left, var right] }:
                        await OpenFileDiffAsync(left, right).ConfigureAwait(true);
                        done.TrySetResult(true);
                        break;
                    case { Command: "merge", Arguments: [var local, var remote, var @base, var merged] }:
                        done.TrySetResult(await MergeFilesAsync(local, remote, @base, merged).ConfigureAwait(true));
                        break;
                    case { Command: "open", Arguments: [var openFile, ..] }:
                        var at = request.Arguments is [_, var line, var column]
                            && int.TryParse(line, System.Globalization.CultureInfo.InvariantCulture, out var l) && int.TryParse(column, System.Globalization.CultureInfo.InvariantCulture, out var c)
                            ? new LanguageServices.Protocol.Position(l, c)
                            : null;
                        done.TrySetResult(await OpenFileAsync(openFile, at: at).ConfigureAwait(true) is not null);
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
        // Updates and uninstalls from the Plugin Gallery wait for this start (M22c), before anything loads.
        ApplyPendingPluginChanges();
        var builtIn = Options.BuiltInPluginsDir ?? Path.Combine(AppContext.BaseDirectory, "plugins");
        var roots = Options.PluginDirs.Select(d => (d, false))
            .Append((Paths.PluginsDirectory, false))
            .Append((builtIn, true));
        var retired = new List<(string Id, string Directory)>();
        Plugins.AddPlugins(PluginDiscovery.Scan(roots, Workbench.Platform, Options.EnableProposedApi, retired));
        Plugins.ReportRetired(retired);

        // Plugins' file icon themes (API 1.0): the setting, applied.
        RegisterIconThemes();

        // Plugins' color themes (API 1.0): offered by the setting, and applied when it names one.
        if (Plugins.Themes.Count > 0)
        {
            Workbench.Settings.Register([BuiltIns.ColorThemeSetting(Theme.AllChoices)]);
            Theme.Apply();
        }
    }

    private object? CreateToolContent(ToolWindowDescriptor window) => window.Kind switch
    {
        ToolWindowKind.Tree => new TreeToolViewModel(window.Id, Plugins, Run, () => TitleActions(window.Id)),
        ToolWindowKind.Markdown => new MarkdownToolViewModel(window.Id, Plugins, Run),
        ToolWindowKind.View => new Views.DeclarativeToolViewModel(window.Id, Plugins),
        _ => window.Id switch
        {
            BuiltIns.OutputWindow => Output,
            BuiltIns.NotificationsWindow => Notifications,
            BuiltIns.PluginsWindow => PluginList,
            SolutionExplorerViewModel.ToolWindowId => Explorer,
            PropertiesWindowViewModel.ToolWindowId => PropertiesWindow,
            TestExplorerViewModel.ToolWindowId => OpenTestExplorer(),
            CoverageResultsViewModel.ToolWindowId => CoverageResults,
            ErrorListViewModel.ToolWindowId => ErrorList,
            FindResultsViewModel.ToolWindowId => FindResults,
            ReferencesViewModel.ToolWindowId => References,
            DocumentOutlineViewModel.ToolWindowId => DocumentOutline,
            CodeSearchToolWindowId => CodeSearch,
            HierarchyViewModel.ToolWindowId => Hierarchy,
            SymbolBrowserViewModel.ClassViewId => ShowClassView(),
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
            TaskRunnerViewModel.ToolWindowId => TaskRunner,
            HttpResponseViewModel.ToolWindowId => HttpResponse,
            ThreadsViewModel.ToolWindowId => ThreadList,
            ModulesViewModel.ToolWindowId => ModuleList,
            LiveTreeViewModel.ToolWindowId => LiveTree,
            LivePropertiesViewModel.ToolWindowId => LiveProperties,
            ParallelStacksViewModel.ToolWindowId => ParallelStacksWindow,
            ParallelWatchViewModel.ToolWindowId => ParallelWatchWindow,
            ProcessesViewModel.ToolWindowId => ProcessesWindow,
            ExceptionSettingsViewModel.ToolWindowId => ExceptionSettingsWindow,
            GitChangesViewModel.ToolWindowId => GitChanges,
            PullRequestsViewModel.ToolWindowId => OpenPullRequests(),
            WorkItemsViewModel.ToolWindowId => OpenWorkItems(),
            WorktreesViewModel.ToolWindowId => OpenWorktrees(),
            TerminalViewModel.ToolWindowId => TerminalWindow,
            InteractiveViewModel.ToolWindowId => InteractiveWindow,
            EntityFrameworkViewModel.ToolWindowId => OpenEntityFramework(),
            DatabaseExplorerViewModel.ToolWindowId => OpenDatabaseExplorer(),
            QueryResultsViewModel.ToolWindowId => QueryResults,
            ContainersViewModel.ToolWindowId => ContainersWindow,
            AiChatViewModel.ToolWindowId => AiChat,
            AspireViewModel.ToolWindowId => AspireWindow,
            DiagnosticToolsViewModel.ToolWindowId => DiagnosticTools,
            CountersViewModel.ToolWindowId => CountersWindow,
            BenchmarksViewModel.ToolWindowId => BenchmarksWindow,
            XamlPreviewViewModel.ToolWindowId => XamlPreview,
            ToolboxViewModel.ToolWindowId => Toolbox,
            RemotePortsViewModel.ToolWindowId => PortsWindow,
            DevicesViewModel.ToolWindowId => DevicesWindow,
            SourceControlProvidersWindow => SourceControlProviders(),
            PluginGalleryWindow => PluginGallery(),
            ComponentsWindow => ComponentsPage(),
            _ => null,
        },
    };

    /// <summary>Commands contributed to a tool window's title bar (<c>toolWindow/title</c>).</summary>
    private IReadOnlyList<ToolbarActionViewModel> TitleActions(string viewId)
    {
        var scope = Workbench.Context.With(new Dictionary<string, object?>(StringComparer.Ordinal) { ["view"] = viewId, ["focusedView"] = viewId });
        return [.. Workbench.Menus.GetItems(MenuLocations.ToolWindowTitle, scope).Where(i => i.Submenu is null).Select(i => new ToolbarActionViewModel(i.Title, i.KeyLabel, CommandFor(i.Command), Controls.CommandIcons.For(i.Command)?.Kind))];
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
            if (BuiltIns.ViewMenuEntry(window.Id, window.Title, id, ContributionSource.Shell) is { } entry)
            {
                added.Add(entry);
            }
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

    public Task RunAsync(string commandId, IReadOnlyList<JsonElement> args) => RunAsync(commandId, args, Workbench.Context);

    /// <summary>Runs a command whose enablement is read in <paramref name="scope"/> (a menu with keys of its own, D38).</summary>
    public async Task RunAsync(string commandId, IReadOnlyList<JsonElement> args, Workbench.Context.IContextKeyReader scope)
    {
        try
        {
            await Workbench.Commands.ExecuteAsync(commandId, args, scope, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (CommandNotEnabledException)
        {
            Ui.Post(() => StatusBar.ShowMessage(Localizer.Format("msg.shellHost.isNotAvailableRightNow", "“{0}” is not available right now.", Title(commandId)), TimeSpan.FromSeconds(4)));
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

    /// <summary>
    /// Color Theme (the quick pick): every family following the system and in each variant, Classic's four, then the
    /// plugins' themes. Up and Down preview a theme; Enter saves it, Escape goes back (M45).
    /// </summary>
    public async Task SelectThemeAsync()
    {
        var current = ThemeService.Normalize(Theme.Setting);
        var choices = Theme.AllChoices;
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

    /// <summary>Settings, searched for <paramref name="search"/> (Fonts and Colors' link to the font settings).</summary>
    public void OpenSettings(string search)
    {
        OpenSettings();
        if (Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is { } settings)
        {
            settings.ViewModel.Search = search;
        }
    }

    /// <summary>Tools → Fonts and Colors (M23a).</summary>
    public void OpenFontsAndColors() =>
        Layout.OpenDocument(Layout.FindDocument<FontsAndColorsDocument>(FontsAndColorsDocument.DocumentId) ?? new FontsAndColorsDocument(new FontsAndColorsViewModel(this)));

    public void OpenSettings() =>
        Layout.OpenDocument(Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId)
            ?? new SettingsDocument(new SettingsViewModel(Workbench.Settings, () => Run("workbench.openSettingsJson"), this)));

    public void OpenKeyboardShortcuts()
    {
        if (Layout.FindDocument<KeyboardShortcutsDocument>(KeyboardShortcutsDocument.DocumentId) is { } open)
        {
            Layout.OpenDocument(open);
            return;
        }

        Layout.OpenDocument(new KeyboardShortcutsDocument(new KeyboardShortcutsViewModel(Workbench, () => Run("workbench.openKeybindingsJson"))));
    }

    public Task OpenSettingsJsonAsync() => OpenTextFileAsync(Paths.SettingsFile, "// Codalus IDE user settings. See File > Settings for every option.\n{\n}\n");

    /// <summary>
    /// Preferences → Configure Language-Specific Settings: picks a language (the active document's first), adds its
    /// <c>"[languageId]": { }</c> block to the user settings when there is none, and opens the file there. Settings in
    /// the block win over the general ones for that language's documents.
    /// </summary>
    public async Task ConfigureLanguageSettingsAsync(string? languageId = null)
    {
        if (languageId is null)
        {
            var active = ActiveEditor?.Model.LanguageId;
            var languages = Workbench.LanguageDefinitions.Languages.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var items = languages
                .Select(l => new QuickPickItem(l.Name) { Tag = l.Id, Detail = Workbench.Settings.OverriddenLanguages.Contains(l.Id) ? $"{l.Id} · configured" : l.Id })
                .ToList();
            var index = Math.Max(0, languages.FindIndex(l => l.Id == active));
            if (await Palette.ShowAsync(items, Localizer.Get("msg.settings.whichLanguage", "Configure the settings of which language?"), index).ConfigureAwait(true) is not { Tag: string picked })
            {
                return;
            }

            languageId = picked;
        }

        var key = SettingsService.LanguageKey(languageId);
        await Workbench.Settings.AddLanguageBlockAsync(languageId, SettingScope.User, CancellationToken.None).ConfigureAwait(true);

        // Inside the block, ready to type a setting.
        var text = File.Exists(Paths.SettingsFile) ? await File.ReadAllTextAsync(Paths.SettingsFile).ConfigureAwait(true) : string.Empty;
        LanguageServices.Protocol.Position? at = null;
        if (text.IndexOf($"\"{key}\"", StringComparison.Ordinal) is var found and >= 0)
        {
            var offset = text.IndexOf('{', found) is var brace and >= 0 ? brace + 1 : found;
            var line = text.AsSpan(0, offset).Count('\n');
            at = new LanguageServices.Protocol.Position(line, offset - (text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1));
        }

        await OpenFileAsync(Paths.SettingsFile, preview: false, at, "// Codalus IDE user settings. See File > Settings for every option.\n{\n}\n").ConfigureAwait(true);
    }

    public Task OpenKeybindingsJsonAsync() => OpenTextFileAsync(Paths.KeybindingsFile, KeybindingsTemplate);

    private const string KeybindingsTemplate = "// Codalus IDE key bindings. Entries override the defaults; \"-command\" removes a default binding.\n[\n]\n";

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
            RevealPath(Paths.PluginsDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.shellHost.couldNotOpen", "Could not open {0}: {1}", Paths.PluginsDirectory, ex.Message));
        }
    }

    public void OpenExternal(IReadOnlyList<JsonElement> args)
    {
        if (args.Count == 0 || args[0].ValueKind != JsonValueKind.String
            || !Uri.TryCreate(args[0].GetString(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("workbench.openExternal expects an http(s) URL.");
        }

        OpenExternalUrl(url);
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
        Workbench.StatusBar.Set(new StatusBarItem("codalus.notifications", count > 0 ? $"$(bell-dot) {count}" : "$(bell)", StatusBarAlignment.Right, int.MaxValue, ContributionSource.Shell.Id)
        {
            Tooltip = count == 0 ? "No notifications" : count == 1 ? "1 notification" : $"{count} notifications",
            AccessibleName = count == 0 ? "No notifications" : $"Notifications: {count}",
            Command = BuiltIns.ViewCommandPrefix + BuiltIns.NotificationsWindow,
        });

        Workbench.Context.Set("toastsVisible", Notifications.HasToasts);
    }

    /// <summary>The <c>keyboardScheme</c> context key, which the default key bindings depend on (M29).</summary>
    private string _keyboardScheme = "default";

    private void ApplyKeyboardScheme()
    {
        var scheme = Workbench.Settings.GetString(BuiltIns.KeyboardSchemeSetting, "default");
        scheme = scheme is "vscode" or "resharper" or "emacs" ? scheme : "default";
        if (scheme != _keyboardScheme)
        {
            // Only the active scheme's own bindings are among the defaults: menus and the shortcuts editor show its keys.
            _keyboardScheme = scheme;
            Workbench.Keybindings.SetDefaults(BuiltIns.ParseDefaultKeybindings(Workbench.Platform, scheme));
        }

        Workbench.Context.Set("keyboardScheme", scheme);
    }

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

        if (e.Affects(HotReloadSetting))
        {
            UpdateToolbarHotReload();
        }

        if (e.Affects("languageServices.analysisScope") || e.Affects("navigation.sourceLink") || e.Affects(XmlSchemasSetting))
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
        var state = ReadJson(StateFile, ShellJsonContext.Default.ShellState);
        if (state?.RecentCommands is { } recent)
        {
            Workbench.Commands.SetRecent(recent);
        }

        CodeSearch.RestoreState(state?.CodeSearch);
    }

    /// <summary>Saves the layout (with the window placement) and the small state file. Called when the window closes.</summary>
    public void SaveState(WindowPlacement? placement)
    {
        WriteJson(Paths.LayoutFile, Layout.Capture(placement), ShellJsonContext.Default.LayoutSnapshot);
        WriteJson(StateFile, new ShellState { RecentCommands = Workbench.Commands.RecentCommandIds, CodeSearch = CodeSearch.CaptureState() }, ShellJsonContext.Default.ShellState);
        _ = SaveCodeSearchIndex(onlyIfChanged: true); // flushed in DisposeAsync
        SaveEditorSession(); // next to the solution, written in the background (flushed in DisposeAsync)
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

    /// <summary>Another program held the clipboard through a copy or a paste's retries.</summary>
    private void OnClipboardBusy() => Ui.Post(() => StatusBar.ShowMessage(
        Localizer.Get("msg.clipboard.busy", "The clipboard is in use by another program. Try again in a moment."), TimeSpan.FromSeconds(6)));

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        StopTimers();
        CloseEditors();
        SyntaxVisualizer.Dispose();
        CodeMetrics.Dispose();
        StatusBar.Dispose();
        Notifications.Dispose();
        Theme.Dispose();
        Editor.CodeColors.Release(Languages.LanguageIdOf);
        ReleaseViewCallbacks();
        CrashReporter.Release(this);
        Infrastructure.SafeClipboard.Busy -= OnClipboardBusy;
        var designers = StopWinFormsDesignersAsync();
        var openSolution = Solutions.Solution?.Path;
        ApplyUpdateOnExit();
        _lifetime.Cancel(); // not awaited: the dispose stays on the UI thread until its first await below
        await FlushSolutionStateAsync().ConfigureAwait(false);
        if (SelfUpdate is not null)
        {
            await SelfUpdate.DisposeAsync().ConfigureAwait(false);
        }

        _updateHttp.Dispose();
        await DisposeCompanionAsync().ConfigureAwait(false);
        _instance?.Dispose();
        _mcpBridge?.Dispose();
        await McpServers.DisposeAsync().ConfigureAwait(false);
        await ExternalAgents.DisposeAsync().ConfigureAwait(false);
        Askpass?.Dispose();
        Terminals?.Dispose();
        if (Interactive is not null)
        {
            _ = Interactive.DisposeAsync().AsTask();
        }
        await Debugger.DisposeAsync().ConfigureAwait(false);
        Git.Dispose();
        EntityFramework.Dispose();
        await Data.DisposeAsync().ConfigureAwait(false);
        await DisposeProfilingAsync().ConfigureAwait(false);
        await DisposeXamlAsync().ConfigureAwait(false);
        await Languages.DisposeAsync().ConfigureAwait(false);
        await Solutions.DisposeAsync().ConfigureAwait(false);
        if (Remote is { } remote)
        {
            Remote = null;
            await remote.DisposeAsync().ConfigureAwait(false);
        }

        await Plugins.DisposeAsync().ConfigureAwait(false);
        Tests.Dispose(); // after the test plugin: its last results can't schedule a notification any more
        await designers.ConfigureAwait(false);
        Workbench.Dispose();
        RestartIfRequested(openSolution);
    }

    /// <summary>
    /// Tears the open editors and their models down as closing their documents does: each editor's timers (inlay hints, CodeLens, symbols)
    /// keep running until then, and hold the editor and this shell.
    /// </summary>
    private void CloseEditors()
    {
        foreach (var editor in Editors)
        {
            editor.View?.Close();
        }

        _previewView?.Close();

        // The documents' models too, as closing the last editor of each does: their file watchers keep them, and they
        // hold this shell's handlers.
        foreach (var model in _models.Values)
        {
            model.Dispose();
        }

        _models.Clear();
    }

    /// <summary>
    /// Takes back the views' static callbacks that call this shell, unless another shell set them since: a static would
    /// hold this shell, with all it reaches, once it's disposed.
    /// </summary>
    private void ReleaseViewCallbacks()
    {
        Views.GitChangesView.ReportError = Unless(Views.GitChangesView.ReportError);
        Views.DiffView.ReportError = Unless(Views.DiffView.ReportError);
        Views.MergeEditorView.ReportError = Unless(Views.MergeEditorView.ReportError);
        Views.BreakpointsView.RunCommand = Unless(Views.BreakpointsView.RunCommand);
        Views.BreakpointsView.AskLabels = Unless(Views.BreakpointsView.AskLabels);
        Views.VariableActions.AddWatch = Unless(Views.VariableActions.AddWatch);
        Views.VariableActions.QuickWatch = Unless(Views.VariableActions.QuickWatch);
        Views.VariableActions.Visualize = Unless(Views.VariableActions.Visualize);
        Views.VariableActions.PluginVisualizers = Unless(Views.VariableActions.PluginVisualizers);
        Views.VariableActions.VisualizeWithPlugin = Unless(Views.VariableActions.VisualizeWithPlugin);
        Views.VariableActions.CopyText = Unless(Views.VariableActions.CopyText);
        Views.VariableActions.AskAi = Unless(Views.VariableActions.AskAi);
        Views.VariableActions.CanAskAi = Unless(Views.VariableActions.CanAskAi);
    }

    /// <summary>The callback, or null when it calls this shell.</summary>
    private T? Unless<T>(T? callback)
        where T : Delegate => callback?.Target == this ? null : callback;

    /// <summary>
    /// (Re)starts one of the shell's timers, unless the shell is being disposed: its dispose raises events (the solution
    /// closing) whose handlers, posted to run later, would start them again after <see cref="StopTimers"/>.
    /// </summary>
    private void StartTimer(Avalonia.Threading.DispatcherTimer timer)
    {
        timer.Stop();
        if (!_disposing)
        {
            timer.Start();
        }
    }

    /// <summary>
    /// Stops the shell's timers. The dispatcher holds every enabled timer and the shell its tick calls, and a pending
    /// one-shot timer of a dispatcher that no longer runs never ticks: the hosting status timer (every two minutes) kept
    /// each disposed shell of the shell tests reachable, with all it reached, until a full run ran out of memory.
    /// </summary>
    private void StopTimers()
    {
        foreach (var timer in new[] { _statusTimer, _repositoryTimer, _autoRecoverTimer, _indexTimer, _codeSearchRefresh, _gitRepositoryTimer, _endpointsRefresh })
        {
            timer?.Stop();
        }
    }
}
