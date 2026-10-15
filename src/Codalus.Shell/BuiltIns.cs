using System.Text.Json;
using Codalus.Platform;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Keybindings;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>The shell's own contributions, registered through the same registries plugins use.</summary>
internal static partial class BuiltIns
{
    public const string OutputWindow = "output";
    public const string NotificationsWindow = "notifications";
    public const string PluginsWindow = "plugins";
    public const string ViewCommandPrefix = "workbench.view.";

    /// <summary>
    /// Default key bindings. <c>primary</c> is ⌘ on macOS and Ctrl elsewhere; <c>win</c>/<c>mac</c>/<c>linux</c>
    /// override <c>key</c> per platform (an entry with no key for the current platform is skipped).
    /// </summary>
    public const string DefaultKeybindings = """
        [
          { "key": "primary+shift+p", "command": "workbench.showCommandPalette" },
          { "key": "ctrl+q", "command": "workbench.action.featureSearch", "when": "keyboardScheme != vscode" },
          { "key": "f1", "command": "workbench.showCommandPalette", "when": "keyboardScheme == vscode" },
          { "key": "shift+alt+enter", "mac": "ctrl+cmd+f", "command": "workbench.toggleFullScreen" },
          { "key": "f1", "command": "editor.action.showHelp", "when": "keyboardScheme != vscode && editorTextFocus" },
          { "key": "primary+,", "command": "workbench.openSettings", "when": "keyboardScheme == vscode" },
          { "key": "primary+,", "command": "workbench.action.gotoAll", "when": "keyboardScheme != vscode" },
          { "key": "primary+k primary+s", "command": "workbench.openKeyboardShortcuts", "when": "keyboardScheme == vscode" },
          { "key": "primary+k primary+s", "command": "editor.action.surroundWithSnippet", "when": "keyboardScheme != vscode && editorTextFocus" },
          { "key": "primary+k primary+t", "command": "workbench.selectTheme" },
          { "key": "primary+shift+u", "command": "workbench.view.output" },
          { "key": "ctrl+alt+o", "command": "workbench.view.output", "when": "keyboardScheme != vscode" },
          { "key": "ctrl+alt+l", "command": "workbench.view.solutionExplorer", "when": "keyboardScheme != vscode" },
          { "key": "f6", "command": "workbench.focusNextPane", "when": "keyboardScheme == vscode" },
          { "key": "shift+f6", "command": "workbench.focusPreviousPane", "when": "keyboardScheme == vscode" },
          { "key": "f6", "command": "build.buildSolution", "when": "keyboardScheme != vscode" },
          { "key": "alt+f6", "command": "workbench.focusNextPane", "when": "keyboardScheme != vscode" },
          { "key": "alt+shift+f6", "command": "workbench.focusPreviousPane", "when": "keyboardScheme != vscode" },
          { "key": "primary+s", "command": "file.save", "when": "activeDocumentKind == textFile" },
          { "key": "primary+s", "command": "file.save", "when": "activeDocumentKind == customEditor" },
          { "key": "primary+w", "command": "workbench.closeActiveDocument", "when": "activeDocumentKind && keyboardScheme == vscode" },
          { "win": "ctrl+f4", "linux": "ctrl+f4", "mac": "cmd+f4", "command": "workbench.closeActiveDocument", "when": "activeDocumentKind" },
          { "mac": "cmd+q", "command": "workbench.quit" },
          { "linux": "ctrl+q", "command": "workbench.quit", "when": "keyboardScheme == vscode" },
          { "key": "escape", "command": "workbench.hideToasts", "when": "toastsVisible && !inCommandPalette" }
        ]
        """;

    /// <summary>The keyboard scheme (M29): the <c>keyboardScheme</c> context key the default bindings depend on.</summary>
    public const string KeyboardSchemeSetting = "keyboard.scheme";

    public static IReadOnlyList<SettingDescriptor> Settings()
    {
        var shell = ContributionSource.Shell;
        return
        [
            SettingDescriptor.Create(CrashReporter.SettingKey, "false", SettingType.Boolean, shell) with
            {
                Title = "Save Crash Reports",
                Category = "Privacy",
                Description = "When Codalus IDE crashes, save a report (version, OS, the error and recent log lines) in the logs folder on this computer. Nothing is sent anywhere.",
            },
            SettingDescriptor.Create(Workbench.Plugins.PluginManager.DisabledSetting, "[]", SettingType.StringArray, shell) with
            {
                Title = "Turned-Off Plugins",
                Category = "Plugins",
                Description = "The ids of plugins that don't load (the Plugins window turns them off and on). A workspace can turn more off for itself; the lists add up. Changes apply when Codalus IDE starts again.",
                Scope = SettingScope.Workspace,
            },
            SettingDescriptor.Create(Workbench.Menus.MenuCustomizations.SettingKey, "{}", SettingType.Object, shell) with
            {
                Title = "Menu and Toolbar Customizations",
                Category = "Appearance",
                Description = "Commands added to and removed from menus, context menus and the standard toolbar, their order, and your own toolbars: { \"locations\": { \"menuBar/tools\": { \"add\": [], \"remove\": [], \"order\": [] } }, \"toolbars\": { \"Name\": [] } }. Tools → Customize edits it.",
                Scope = SettingScope.User,
                Order = 6,
            },
            SettingDescriptor.Create(ShellHost.SyncTargetSetting, "\"\"", SettingType.String, shell) with
            {
                Title = "Sync Target",
                Category = "Settings Sync",
                Description = "Where your settings, keyboard shortcuts, snippets and saved layouts sync: a folder (one a cloud drive syncs) or a Git repository's URL (a gist's clone URL works). Empty: sync is off. Each profile has its own.",
                Scope = SettingScope.User,
            },
            SettingDescriptor.Create(ShellHost.SyncIgnoredSetting, "[]", SettingType.StringArray, shell) with
            {
                Title = "Settings Not Synced",
                Category = "Settings Sync",
                Description = "Settings that stay on this machine (the sync settings always do), such as paths that differ between machines.",
                Scope = SettingScope.User,
            },
            SettingDescriptor.Create(ShellHost.StartupSetting, "\"startWindow\"", SettingType.String, shell) with
            {
                Title = "On Startup",
                Category = "Environment",
                Description = "What Codalus IDE shows when it starts without a solution: the start window (recent solutions, create, open, clone), an empty window, or the last solution.",
                Enum = ["startWindow", "empty", "lastSolution"],
                EnumDescriptions = ["The start window", "An empty window", "The last solution"],
                Scope = SettingScope.User,
            },
            SettingDescriptor.Create(KeyboardSchemeSetting, "\"default\"", SettingType.String, shell) with
            {
                Title = "Keyboard Scheme",
                Category = "Keyboard",
                Description = "Default: the classic IDE shortcuts (F6 builds, Ctrl+Q searches, Ctrl+, goes to anything, F1 is help). VS Code: VS Code's (F1 is the command palette, Ctrl+, opens Settings, Ctrl+W closes). ReSharper: the default with ReSharper's (Ctrl+Shift+R refactors, Ctrl+W expands the selection, Alt+F7 finds usages). Emacs: the default with Emacs's editing keys (Ctrl+F/B/N/P move, Ctrl+K kills to the line's end, Ctrl+Space sets the mark, Ctrl+X Ctrl+S saves, Alt+X runs a command). Your own key bindings apply on top of any of them.",
                Enum = ["default", "vscode", "resharper", "emacs"],
                EnumDescriptions = ["Default", "VS Code", "ReSharper", "Emacs"],
                Scope = SettingScope.User,
                Order = 2,
            },
            ColorThemeSetting(ThemeService.Choices),
            SettingDescriptor.Create("workbench.statusBar.visible", "true", SettingType.Boolean, shell) with
            {
                Title = "Status Bar Visible",
                Category = "Appearance",
                Description = "Show the status bar at the bottom of the window.",
                Scope = SettingScope.User,
                Order = 2,
            },
            SettingDescriptor.Create(ShellHost.ColorTabsByProjectSetting, "false", SettingType.Boolean, shell) with
            {
                Title = "Color Document Tabs by Project",
                Category = "Appearance",
                Description = "Mark each document tab with its project's color, a stripe along the tab's bottom edge.",
                Scope = SettingScope.User,
                Order = 3,
            },
            SettingDescriptor.Create(Theming.ColorCustomizations.TokensKey, "{}", SettingType.Object, shell) with
            {
                Title = "Syntax Colors",
                Category = "Appearance",
                Description = "Colors over the theme's for kinds of code, as VS Code writes them: \"comments\", \"strings\", \"keywords\", \"numbers\", \"types\", \"functions\", \"variables\", or \"semanticTokenColors\" by token type; under \"[Dark]\" (a theme's name) for one theme only. Tools → Fonts and Colors edits them.",
                Scope = SettingScope.User,
                Order = 4,
            },
            SettingDescriptor.Create(Theming.ColorCustomizations.WorkbenchKey, "{}", SettingType.Object, shell) with
            {
                Title = "Editor and Window Colors",
                Category = "Appearance",
                Description = "Colors over the theme's for the editor (VS Code's ids, such as \"editor.selectionBackground\") and the window (resource keys such as \"Shell.Background\"); under \"[Dark]\" for one theme only. Tools → Fonts and Colors edits them.",
                Scope = SettingScope.User,
                Order = 5,
            },
            SettingDescriptor.Create("workbench.notifications.autoHideSeconds", "8", SettingType.Integer, shell) with
            {
                Title = "Auto-Hide Informational Toasts",
                Category = "Notifications",
                Description = "Seconds after which informational toasts hide (they stay in the Notifications window). 0 keeps them until dismissed.",
                Minimum = 0,
                Maximum = 120,
                Scope = SettingScope.User,
            },
            SettingDescriptor.Create("editor.fontFamily", "\"Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace\"", SettingType.String, shell) with
            {
                Title = "Font Family",
                Category = "Text Editor",
                Description = "Comma-separated fonts for text views such as Output; the first installed one is used.",
                Order = 1,
            },
            SettingDescriptor.Create("editor.fontSize", "13", SettingType.Number, shell) with
            {
                Title = "Font Size",
                Category = "Text Editor",
                Description = "Font size in points for text views.",
                Minimum = 6,
                Maximum = 72,
                Order = 2,
            },
            SettingDescriptor.Create("output.wordWrap", "false", SettingType.Boolean, shell) with
            {
                Title = "Word Wrap",
                Category = "Output",
                Description = "Wrap long lines in the Output window.",
            },
        ];
    }

    public static IEnumerable<ToolWindowDescriptor> ToolWindows()
    {
        var shell = ContributionSource.Shell;
        yield return new ToolWindowDescriptor(OutputWindow, "Output", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "output", VisibleByDefault = true };
        yield return new ToolWindowDescriptor(NotificationsWindow, "Notifications", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "bell" };
        yield return new ToolWindowDescriptor(PluginsWindow, "Plugins", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "extensions" };
        yield return new ToolWindowDescriptor(ViewModels.SolutionExplorerViewModel.ToolWindowId, "Solution Explorer", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "solution", VisibleByDefault = true };
        yield return new ToolWindowDescriptor(ViewModels.PropertiesWindowViewModel.ToolWindowId, "Properties", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "properties" };
        yield return new ToolWindowDescriptor(ViewModels.ErrorListViewModel.ToolWindowId, "Error List", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "error", VisibleByDefault = true };
        yield return new ToolWindowDescriptor(ViewModels.FindResultsViewModel.ToolWindowId, "Find Results", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "search" };
        yield return new ToolWindowDescriptor(ViewModels.DocumentOutlineViewModel.ToolWindowId, "Document Outline", ToolWindowLocation.Left, ToolWindowKind.BuiltIn, shell) { Icon = "document-text" };
        yield return new ToolWindowDescriptor(ViewModels.HierarchyViewModel.ToolWindowId, "Hierarchy", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "method" };
        yield return new ToolWindowDescriptor(ViewModels.CodeDefinitionViewModel.ToolWindowId, "Code Definition", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "file-code" };
        yield return new ToolWindowDescriptor(ViewModels.BookmarksViewModel.ToolWindowId, "Bookmarks", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "star" };
        yield return new ToolWindowDescriptor(ViewModels.TaskListViewModel.ToolWindowId, "Task List", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "check" };
        yield return new ToolWindowDescriptor(ViewModels.SyntaxVisualizerViewModel.ToolWindowId, "Syntax Visualizer", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "namespace" };
        yield return new ToolWindowDescriptor(ViewModels.CodeMetricsViewModel.ToolWindowId, "Code Metrics Results", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "beaker" };
    }

    public static void RegisterCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, string category, CommandHandler handler, string? enablement = null, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = category,
                    Enablement = WhenClause.Parse(enablement),
                    ShowInPalette = palette,
                },
                handler);

        Add("workbench.showCommandPalette", "Show All Commands", "View", Ui(shell.ShowCommandPalette));
        Add("workbench.action.featureSearch", "Search", "View", Ui(shell.FeatureSearchAsync));
        Add("workbench.openSettings", "Open Settings", "Preferences", Ui(shell.OpenSettings));
        Add("workbench.openOptions", "Options…", string.Empty, Ui(shell.OpenSettings), palette: false);
        Add("workbench.openSettingsJson", "Open User Settings (JSON)", "Preferences", Ui(shell.OpenSettingsJsonAsync));
        Add("workbench.action.configureLanguageBasedSettings", "Configure Language-Specific Settings…", "Preferences", Ui(() => shell.ConfigureLanguageSettingsAsync()));
        Add("workbench.openKeyboardShortcuts", "Open Keyboard Shortcuts", "Preferences", Ui(shell.OpenKeyboardShortcuts));
        Add("workbench.openFontsAndColors", "Fonts and Colors", "Preferences", Ui(shell.OpenFontsAndColors));
        Add("workbench.customize", "Customize…", "Preferences", Ui(shell.OpenCustomize));
        Add("workbench.exportSettings", "Export Settings…", "Preferences", Ui(() => shell.ExportSettingsAsync()));
        Add("workbench.importSettings", "Import Settings…", "Preferences", Ui(() => shell.ImportSettingsAsync()));
        Add("workbench.importVsCodeSettings", "Import Settings from VS Code…", "Preferences", Ui(() => shell.ImportVsCodeSettingsAsync()));
        Add("workbench.importVsSettings", "Import a .vssettings File…", "Preferences", Ui(() => shell.ImportVsSettingsAsync()));
        Add("workbench.openKeybindingsJson", "Open Keyboard Shortcuts (JSON)", "Preferences", Ui(shell.OpenKeybindingsJsonAsync));
        Add("workbench.selectTheme", "Color Theme", "Preferences", Ui(shell.SelectThemeAsync));
        Add("workbench.resetLayout", "Reset Layout", "View", Ui(shell.Layout.ResetLayout));
        Add("workbench.saveWindowLayout", "Save Window Layout…", "View", Ui(() => shell.SaveWindowLayoutAsync()));
        Add("workbench.applyWindowLayout", "Apply Window Layout…", "View", Ui(() => shell.ApplyWindowLayoutAsync()));
        Add("workbench.deleteWindowLayout", "Delete Window Layout…", "View", Ui(() => shell.DeleteWindowLayoutAsync()));
        Add("workbench.toggleFullScreen", "Full Screen", "View", Ui(shell.ToggleFullScreen));
        Add("workbench.toggleDistractionFree", "Distraction-Free Mode", "View", Ui(shell.ToggleDistractionFree));
        Add("workbench.focusNextPane", "Focus Next Pane", "View", Ui(() => shell.Window?.FocusPane(backwards: false)));
        Add("workbench.focusPreviousPane", "Focus Previous Pane", "View", Ui(() => shell.Window?.FocusPane(backwards: true)));
        Add("workbench.clearNotifications", "Clear All Notifications", "Notifications", Ui(shell.Workbench.Notifications.DismissAll));
        Add("workbench.hideToasts", "Hide Notification Toasts", "Notifications", Ui(shell.Notifications.HideToasts));
        Add("workbench.showLogs", "Show Logs", "Help", Ui(shell.ShowLogs));
        Add("workbench.openPluginsFolder", "Open User Plugins Folder", "Plugins", Ui(shell.OpenPluginsFolder));
        Add("file.save", "Save", "File", Ui(shell.SaveActiveDocumentAsync), enablement: "activeDocumentKind == textFile");
        Add("workbench.closeActiveDocument", "Close Document", "File", Ui(shell.CloseActiveDocument), enablement: "activeDocumentKind");
        Add("workbench.quit", PlatformInfo.Current == PlatformKind.Windows ? Localizer.Get("command.workbench.quit.windows", "Exit") : Localizer.Get("command.workbench.quit.other", "Quit"), "File", Ui(() => shell.Window?.RequestClose()));
        Add("help.about", "About Codalus IDE", "Help", Ui(shell.ShowAbout));
        Add("help.openCrashReports", "Open Crash Reports Folder", "Help", Ui(shell.OpenCrashReports));
        Add("update.check", "Check for Updates", "Help", Ui(() => _ = shell.CheckForUpdatesAsync()));
        Add("update.whatsNew", "What's New", "Help", Ui(() => shell.ShowWhatsNew()));
        Add("update.restart", "Restart to Update", "Help", Ui(shell.RestartToUpdate));
        Add("workbench.restart", "Restart", "File", Ui(shell.Restart));
        Add("remote.reopenInContainer", "Reopen in Container", "Dev Containers", Ui(() => shell.ReopenInContainerAsync()));
        Add("help.checkEnvironment", "Check Environment", "Help", Ui(shell.OpenCheckEnvironment));
        Add("workbench.showStartWindow", "Start Window", "File", Ui(shell.ShowStartWindow));
        Add("settingsSync.turnOn", "Settings Sync: Turn On…", "Preferences", Ui(() => shell.TurnOnSettingsSyncAsync()));
        Add("settingsSync.syncNow", "Settings Sync: Sync Now", "Preferences", Ui(() => shell.SyncSettingsAsync()));
        Add("settingsSync.turnOff", "Settings Sync: Turn Off", "Preferences", Ui(shell.TurnOffSettingsSyncAsync));
        Add("workbench.profiles.switch", "Switch Profile…", "Profiles", Ui(() => shell.SwitchProfileAsync()));
        Add("workbench.profiles.new", "New Profile…", "Profiles", Ui(() => shell.NewProfileAsync()));
        Add("workbench.profiles.delete", "Delete Profile…", "Profiles", Ui(() => shell.DeleteProfileAsync()));
        Add("workbench.profiles.export", "Export Profile…", "Profiles", Ui(() => shell.ExportSettingsAsync()));
        Add("workbench.profiles.import", "Import Profile…", "Profiles", Ui(() => shell.ImportProfileAsync()));

        // Helpers for plugins (not in the palette): plugins reach shell features through commands.
        Add("workbench.openExternal", "Open URL", "Developer", async (args, _) =>
        {
            await Infrastructure.Ui.RunAsync(() => shell.OpenExternal(args)).ConfigureAwait(false);
            return null;
        }, palette: false);
        Add("workbench.setSetting", "Change a Setting", "Developer", async (args, ct) =>
        {
            await shell.SetSettingAsync(args, ct).ConfigureAwait(false);
            return null;
        }, palette: false);
        Add("editor.activePosition", "Active Editor Position", "Developer", (_, _) => Infrastructure.Ui.RunAsync(shell.ActiveEditorPosition), palette: false);
        Add("workbench.openFile", "Open File", "Developer", async (args, _) =>
        {
            await Infrastructure.Ui.RunAsync(() => shell.OpenFileCommandAsync(args)).ConfigureAwait(false);
            return null;
        }, palette: false);
        Add("project.addReferenceTo", "Add a Project Reference", "Developer", async (args, ct) =>
        {
            await shell.AddProjectReferenceCommandAsync(args, ct).ConfigureAwait(false);
            return null;
        }, palette: false);
    }

    /// <summary>View → Other Windows: the tool windows that aren't on the View menu itself.</summary>
    public const string OtherWindowsMenu = "menuBar/view/otherWindows";

    /// <summary>Debug → Windows: the debugger's tool windows.</summary>
    public const string DebugWindowsMenu = "menuBar/debug/windows";

    /// <summary>The tool windows on the View menu itself, in this order (as a classic IDE's View menu); the rest go to submenus.</summary>
    private static readonly string[] s_mainViewWindows =
        ["solutionExplorer", "git.changes", "testExplorer", "bookmarks", "codeDefinition", "errorList", "output", "taskList", "terminal", "notifications", "properties"];

    /// <summary>Tool windows whose menu entry is contributed explicitly (with a command that does more than show them).</summary>
    private static readonly HashSet<string> s_explicitViewEntries = new(StringComparer.Ordinal) { "interactive", "web.endpoints", "web.taskRunner", "containers", "aspire", AiChatViewModel.ToolWindowId, DiagnosticToolsViewModel.ToolWindowId, CountersViewModel.ToolWindowId, XamlPreviewViewModel.ToolWindowId };

    /// <summary>Where a tool window's <c>workbench.view.*</c> entry goes, or null when it has an explicit one.</summary>
    public static MenuItemRule? ViewMenuEntry(string windowId, string title, string command, ContributionSource source)
    {
        if (s_explicitViewEntries.Contains(windowId))
        {
            return null;
        }

        var main = Array.IndexOf(s_mainViewWindows, windowId);
        if (main >= 0)
        {
            return new MenuItemRule(MenuLocations.MenuBar("view"), command, source) { Group = "view", Order = main * 10 };
        }

        var location = windowId.StartsWith("debug.", StringComparison.Ordinal) ? DebugWindowsMenu : OtherWindowsMenu;
        return new MenuItemRule(location, command, source) { Group = "windows", Order = AlphabeticalOrder(title) };
    }

    /// <summary>An order that sorts titles alphabetically (by their first four letters).</summary>
    internal static int AlphabeticalOrder(string title)
    {
        var order = 0;
        for (var i = 0; i < 4; i++)
        {
            order = (order * 64) + (i < title.Length ? Math.Clamp(char.ToUpperInvariant(title[i]) - ' ', 0, 63) : 0);
        }

        return order;
    }

    /// <summary>Self-update (M32, ADR 0029): Options → Environment → Updates.</summary>
    public static IReadOnlyList<SettingDescriptor> UpdateSettings() =>
    [
        SettingDescriptor.Create(Workbench.Updates.UpdateService.ModeSetting, "\"auto\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Updates",
            Category = "Updates",
            Description = "Installed builds keep themselves up to date: download new releases in the background and apply them when the IDE restarts, only say that one is available, or don't check at all (no update traffic; Help → Check for Updates still works). Development builds never check.",
            Enum = ["auto", "notify", "off"],
            EnumDescriptions = ["Download in the background and update at the next restart", "Say that a release is available", "Don't check"],
        },
        SettingDescriptor.Create(Workbench.Updates.UpdateService.ChannelSetting, "\"\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Update Channel",
            Category = "Updates",
            Description = "Stable releases, or nightly builds of the next version. Empty: the channel the installed build came from. Moving from nightly builds to stable releases installs the newest stable release, even when it is older, after a confirmation.",
            Enum = ["", "stable", "nightly"],
            EnumDescriptions = ["The installed build's channel", "Stable releases", "Nightly builds"],
        },
    ];

    /// <summary>The default key bindings, with the keyboard scheme's own (ReSharper, Emacs) last, so they win.</summary>
    public static IReadOnlyList<KeybindingRule> ParseDefaultKeybindings(PlatformKind platform, string scheme = "default") =>
        [
            .. KeybindingService.ParseRules(DefaultKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(SolutionKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(EditorKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(BuildKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(DebugKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(GitKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(TestingKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(ProjectManagementKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(ProjectSystemKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(TerminalKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(InteractiveKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(WebKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
        .. KeybindingService.ParseRules(DataKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
        .. KeybindingService.ParseRules(ProfilingKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
        .. KeybindingService.ParseRules(XamlKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
        .. KeybindingService.ParseRules(AiKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
        .. KeybindingService.ParseRules(FSharpKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
        .. KeybindingService.ParseRules(SchemeKeybindings, KeybindingSource.Default, platform, null, diagnostics: null)
            .Where(r => r.When.Text.Contains($"keyboardScheme == {scheme}", StringComparison.Ordinal)),
        ];

    private static CommandHandler Ui(Action action) => async (_, _) =>
    {
        await Infrastructure.Ui.RunAsync(action).ConfigureAwait(false);
        return null;
    };

    private static CommandHandler Ui(Func<Task> action) => async (_, _) =>
    {
        await Infrastructure.Ui.RunAsync(action).ConfigureAwait(false);
        return null;
    };

    /// <summary><c>workbench.colorTheme</c>, offering <paramref name="choices"/> (the built-in themes, and plugins' once they're known).</summary>
    public static SettingDescriptor ColorThemeSetting(IReadOnlyList<ThemeChoice> choices) =>
        SettingDescriptor.Create(ThemeService.SettingKey, "\"followSystem\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Color Theme",
            Category = "Appearance",
            Description = "The color theme. Follow System uses the operating system's light, dark or high-contrast setting. Plugins can add themes.",
            Enum = [.. choices.Select(c => c.Id)],
            EnumDescriptions = [.. choices.Select(c => c.Title)],
            Scope = SettingScope.User,
            Order = 1,
        };
}
