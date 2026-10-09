using System.Text.Json;
using Daedalus.Platform;
using Daedalus.Shell.Theming;
using Daedalus.Workbench.Commands;
using Daedalus.Workbench.Context;
using Daedalus.Workbench.Keybindings;
using Daedalus.Workbench.Menus;
using Daedalus.Workbench.Settings;
using Daedalus.Workbench.ToolWindows;

namespace Daedalus.Shell;

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
          { "key": "f1", "command": "workbench.showCommandPalette" },
          { "key": "primary+,", "command": "workbench.openSettings" },
          { "key": "primary+k primary+s", "command": "workbench.openKeyboardShortcuts" },
          { "key": "primary+k primary+t", "command": "workbench.selectTheme" },
          { "key": "primary+shift+u", "command": "workbench.view.output" },
          { "key": "f6", "command": "workbench.focusNextPane" },
          { "key": "shift+f6", "command": "workbench.focusPreviousPane" },
          { "key": "primary+s", "command": "file.save", "when": "activeDocumentKind == textFile" },
          { "key": "primary+w", "command": "workbench.closeActiveDocument", "when": "activeDocumentKind" },
          { "win": "ctrl+f4", "linux": "ctrl+f4", "command": "workbench.closeActiveDocument", "when": "activeDocumentKind" },
          { "mac": "cmd+q", "linux": "ctrl+q", "command": "workbench.quit" },
          { "key": "escape", "command": "workbench.hideToasts", "when": "toastsVisible && !inCommandPalette" }
        ]
        """;

    public static IReadOnlyList<SettingDescriptor> Settings()
    {
        var shell = ContributionSource.Shell;
        return
        [
            SettingDescriptor.Create(ThemeService.SettingKey, "\"followSystem\"", SettingType.String, shell) with
            {
                Title = "Color Theme",
                Category = "Appearance",
                Description = "The color theme. Follow System uses the operating system's light, dark or high-contrast setting.",
                Enum = [.. ThemeService.Choices.Select(c => c.Id)],
                EnumDescriptions = [.. ThemeService.Choices.Select(c => c.Title)],
                Scope = SettingScope.User,
                Order = 1,
            },
            SettingDescriptor.Create("workbench.statusBar.visible", "true", SettingType.Boolean, shell) with
            {
                Title = "Status Bar Visible",
                Category = "Appearance",
                Description = "Show the status bar at the bottom of the window.",
                Scope = SettingScope.User,
                Order = 2,
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
        yield return new ToolWindowDescriptor(ViewModels.SolutionExplorerViewModel.ToolWindowId, "Solution Explorer", ToolWindowLocation.Left, ToolWindowKind.BuiltIn, shell) { Icon = "solution", VisibleByDefault = true };
        yield return new ToolWindowDescriptor(ViewModels.ErrorListViewModel.ToolWindowId, "Error List", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "error", VisibleByDefault = true };
        yield return new ToolWindowDescriptor(ViewModels.FindResultsViewModel.ToolWindowId, "Find Results", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "search" };
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
        Add("workbench.openSettings", "Open Settings", "Preferences", Ui(shell.OpenSettings));
        Add("workbench.openSettingsJson", "Open User Settings (JSON)", "Preferences", Ui(shell.OpenSettingsJsonAsync));
        Add("workbench.openKeyboardShortcuts", "Open Keyboard Shortcuts", "Preferences", Ui(shell.OpenKeyboardShortcuts));
        Add("workbench.openKeybindingsJson", "Open Keyboard Shortcuts (JSON)", "Preferences", Ui(shell.OpenKeybindingsJsonAsync));
        Add("workbench.selectTheme", "Color Theme", "Preferences", Ui(shell.SelectThemeAsync));
        Add("workbench.resetLayout", "Reset Layout", "View", Ui(shell.Layout.ResetLayout));
        Add("workbench.focusNextPane", "Focus Next Pane", "View", Ui(() => shell.Window?.FocusPane(backwards: false)));
        Add("workbench.focusPreviousPane", "Focus Previous Pane", "View", Ui(() => shell.Window?.FocusPane(backwards: true)));
        Add("workbench.clearNotifications", "Clear All Notifications", "Notifications", Ui(shell.Workbench.Notifications.DismissAll));
        Add("workbench.hideToasts", "Hide Notification Toasts", "Notifications", Ui(shell.Notifications.HideToasts));
        Add("workbench.showLogs", "Show Logs", "Help", Ui(shell.ShowLogs));
        Add("workbench.openPluginsFolder", "Open User Plugins Folder", "Plugins", Ui(shell.OpenPluginsFolder));
        Add("file.save", "Save", "File", Ui(shell.SaveActiveDocumentAsync), enablement: "activeDocumentKind == textFile");
        Add("workbench.closeActiveDocument", "Close Document", "File", Ui(shell.CloseActiveDocument), enablement: "activeDocumentKind");
        Add("workbench.quit", PlatformInfo.Current == PlatformKind.Windows ? "Exit" : "Quit", "File", Ui(() => shell.Window?.RequestClose()));
        Add("help.about", "About Daedalus Studio", "Help", Ui(shell.ShowAbout));

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
    }

    public static void RegisterMenus(MenuRegistry menus)
    {
        menus.AddMenuBarMenu(new MenuBarMenu("file", "_File", 10));
        menus.AddMenuBarMenu(new MenuBarMenu("view", "_View", 30));
        menus.AddMenuBarMenu(new MenuBarMenu("tools", "_Tools", 70));
        menus.AddMenuBarMenu(new MenuBarMenu("help", "_Help", 90));

        var shell = ContributionSource.Shell;
        MenuItemRule Item(string menu, string command, string group, int order = 0) =>
            new(MenuLocations.MenuBar(menu), command, shell) { Group = group, Order = order };

        menus.Add(
        [
            Item("file", "file.save", "file", 1),
            Item("file", "workbench.closeActiveDocument", "file", 2),
            Item("file", "workbench.openSettings", "preferences", 1),
            Item("file", "workbench.openKeyboardShortcuts", "preferences", 2),
            Item("file", "workbench.selectTheme", "preferences", 3),
            Item("file", "workbench.quit", "exit"),
            Item("view", "workbench.showCommandPalette", "navigation"),
            Item("view", "workbench.resetLayout", "layout", 1),
            Item("view", "workbench.focusNextPane", "layout", 2),
            Item("view", "workbench.focusPreviousPane", "layout", 3),
            Item("tools", "workbench.openPluginsFolder", "plugins", 100),
            Item("help", "workbench.showLogs", "help", 1),
            Item("help", "help.about", "help", 2),
        ]);
    }

    public static IReadOnlyList<KeybindingRule> ParseDefaultKeybindings(PlatformKind platform) =>
        [
            .. KeybindingService.ParseRules(DefaultKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(SolutionKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(EditorKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(BuildKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(DebugKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
            .. KeybindingService.ParseRules(GitKeybindings, KeybindingSource.Default, platform, null, diagnostics: null),
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
}
