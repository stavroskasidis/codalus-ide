using System.Text.Json;
using Codalus.Plugins.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// The Start page (M66): a tree tool window of actions, the recent solutions and a tip, with the Start status item and
/// its commands. It was the built-in Welcome plugin, the only one that activated at startup, so the plugin host
/// started at every launch for it alone; now it is the shell's own, with the same ids, commands, settings and items,
/// shown through the same tree view as a plugin's. Get Started is the first-run setup (M46).
/// </summary>
public sealed partial class ShellHost
{
    internal const string StartPageViewId = "welcome.view";
    internal const string StartPageShowOnStartupSetting = "welcome.showOnStartup";
    internal const string StartPageOnboardedSetting = "welcome.onboarded";
    private const string StartPageRepository = "https://github.com/stavroskasidis/codalus-ide";

    /// <summary>The keyboard schemes the shell offers (its <c>keyboard.scheme</c> setting).</summary>
    private static readonly (string Id, string Title, string Description)[] s_keyboardSchemes =
    [
        ("default", "Default", "The classic IDE's keys: F5 debugs, F6 builds, Ctrl+Q searches"),
        ("vscode", "VS Code", "F1 is the command palette, Ctrl+, opens Settings, Ctrl+W closes"),
        ("resharper", "ReSharper", "The default with ReSharper's: Ctrl+Shift+R refactors, Ctrl+W expands the selection"),
        ("emacs", "Emacs", "The default with Emacs's editing keys and Ctrl+X chords"),
    ];

    /// <summary>Tips: each runs what it describes.</summary>
    internal static readonly IReadOnlyList<(string Text, string Command)> StartPageTips =
    [
        ("Search commands, settings, files and symbols from the box in the title bar", "workbench.action.featureSearch"),
        ("Go To All finds any file, type or member by name", "workbench.action.gotoAll"),
        ("Every shortcut can be changed, and four keyboard schemes are built in", "workbench.openKeyboardShortcuts"),
        ("Fonts and Colors overrides any syntax, editor or window color over the theme", "workbench.openFontsAndColors"),
        ("Save window layouts and switch between them from the Window menu", "workbench.saveWindowLayout"),
        ("Distraction-free mode hides everything but your documents", "workbench.toggleDistractionFree"),
        ("Bring your VS Code settings and shortcuts over in one step", "workbench.importVsCodeSettings"),
        ("Export your settings, shortcuts and layouts to one file for another machine", "workbench.exportSettings"),
        ("The integrated terminal runs your own shell next to your code", "workbench.view.terminal"),
        ("C# Interactive evaluates C# as you type it", "workbench.view.interactive"),
    ];

    /// <summary>Tips moved past with Next Tip, on top of the day's.</summary>
    private int _startPageTipOffset;

    internal static ToolWindowDescriptor StartPageToolWindow() =>
        new(StartPageViewId, "Start", ToolWindowLocation.Left, ToolWindowKind.Tree, ContributionSource.Shell) { Icon = "home" };

    internal static IReadOnlyList<SettingDescriptor> StartPageSettings() =>
    [
        SettingDescriptor.Create(StartPageShowOnStartupSetting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Show on Startup",
            Category = "Welcome",
            Description = "Show the Start page when Codalus IDE starts.",
            Scope = SettingScope.User,
        },
        SettingDescriptor.Create(StartPageOnboardedSetting, "false", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Onboarded",
            Category = "Welcome",
            Description = "Whether Get Started has been completed or skipped.",
            Scope = SettingScope.User,
        },
    ];

    /// <summary>The Start page's commands, menus, status item and tree.</summary>
    private void InitializeStartPage()
    {
        var commands = Workbench.Commands;
        void Add(string id, string title, string category, Func<Task> run, bool palette = true) =>
            commands.Register(new CommandDescriptor(id, title, ContributionSource.Shell) { Category = category, ShowInPalette = palette }, async (_, _) =>
            {
                await Infrastructure.Ui.RunAsync(run).ConfigureAwait(false);
                return null;
            });

        Add("welcome.show", "Start Page", "Welcome", () =>
        {
            Layout.ShowToolWindow(StartPageViewId);
            return Task.CompletedTask;
        });
        Add("welcome.showOnboarding", "Get Started", "Welcome", () => RunAsync("help.setUp", []));
        Add("welcome.chooseKeyboardScheme", "Choose Keyboard Scheme", "Preferences", ChooseKeyboardSchemeAsync);
        Add("welcome.toggleShowOnStartup", "Toggle Show Start Page on Startup", "Welcome", () =>
            Workbench.Settings.SetAsync(StartPageShowOnStartupSetting, !Workbench.Settings.GetBoolean(StartPageShowOnStartupSetting, true), SettingScope.User, CancellationToken.None));
        Add("welcome.refresh", "Refresh", "Welcome", () =>
        {
            RefreshStartPage();
            return Task.CompletedTask;
        });
        Add("welcome.nextTip", "Next Tip", "Welcome", () =>
        {
            _startPageTipOffset++;
            RefreshStartPage();
            return Task.CompletedTask;
        }, palette: false);

        var shell = ContributionSource.Shell;
        Workbench.Menus.Add(
        [
            new MenuItemRule(MenuLocations.MenuBar("help"), "welcome.show", shell) { Group = "navigation", Order = 1 },
            new MenuItemRule(MenuLocations.ToolWindowTitle, "welcome.refresh", shell) { When = WhenClause.Parse("view == " + StartPageViewId) },
            new MenuItemRule(MenuLocations.ToolWindowTitle, "welcome.toggleShowOnStartup", shell) { When = WhenClause.Parse("view == " + StartPageViewId) },
        ]);

        Workbench.StatusBar.Set(new StatusBarItem("start", "$(home) " + Localizer.Get("startPage.start", "Start"), StatusBarAlignment.Left, -100, shell.Id)
        {
            Tooltip = Localizer.Get("startPage.showTheStartPage", "Show the Start page"),
            Command = "welcome.show",
        });

        Plugins.RegisterBuiltInTree(StartPageViewId, StartPageChildrenAsync);
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(StartPageShowOnStartupSetting))
            {
                RefreshStartPage();
            }
        };
        Recent.Changed += (_, _) => RefreshStartPage();
    }

    private void RefreshStartPage() => Plugins.RefreshBuiltInTree(StartPageViewId);

    /// <summary>At startup, unless turned off: shown without the focus, before anything else takes the left column.</summary>
    private void ShowStartPageOnStartup()
    {
        if (Workbench.Settings.GetBoolean(StartPageShowOnStartupSetting, true))
        {
            Layout.ShowToolWindow(StartPageViewId, focus: false);
        }
    }

    private async Task ChooseKeyboardSchemeAsync()
    {
        var current = Workbench.Settings.GetString(BuiltIns.KeyboardSchemeSetting, "default");
        var items = s_keyboardSchemes.Select(s => new QuickPickItem(Localizer.Get($"startPage.scheme.{s.Id}", s.Title))
        {
            Detail = (s.Id == current ? Localizer.Get("startPage.current", "current") + " · " : string.Empty) + Localizer.Get($"startPage.scheme.{s.Id}.description", s.Description),
            Tag = s.Id,
        }).ToList();
        if (await Palette.ShowAsync(items, Localizer.Get("startPage.chooseAKeyboardScheme", "Choose a keyboard scheme"), Math.Max(0, Array.FindIndex(s_keyboardSchemes, s => s.Id == current))).ConfigureAwait(true) is { Tag: string id })
        {
            await Workbench.Settings.SetAsync(BuiltIns.KeyboardSchemeSetting, id, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ the tree

    private sealed record StartNode(string Handle, string Label, string? Icon = null, string? Description = null, string? Tooltip = null, TreeItemCommandDto? Command = null)
    {
        public List<StartNode> Children { get; init; } = [];
    }

    private Task<IReadOnlyList<TreeItemDto>> StartPageChildrenAsync(string? handle, CancellationToken cancellationToken)
    {
        var roots = StartPageRoots();
        var level = handle is null ? roots : Find(roots, handle)?.Children ?? [];
        return Task.FromResult<IReadOnlyList<TreeItemDto>>(
            [.. level.Select(n => new TreeItemDto(n.Handle, n.Label, n.Description, n.Tooltip, n.Icon, n.Children.Count > 0 ? "expanded" : "none", n.Command))]);

        static StartNode? Find(List<StartNode> nodes, string handle)
        {
            foreach (var node in nodes)
            {
                if (node.Handle == handle)
                {
                    return node;
                }

                if (Find(node.Children, handle) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    private List<StartNode> StartPageRoots()
    {
        string T(string key, string english) => Localizer.Get("startPage." + key, english);
        static TreeItemCommandDto Command(string id, params string[] args) => new(id, [.. args.Select(a => JsonSerializer.SerializeToElement(a, Docking.ShellJsonContext.Default.String))]);
        StartNode Action(string group, string label, string icon, string command, params string[] args) =>
            new($"{group}/{command}/{string.Join('|', args)}", label, icon, Command: Command(command, args));

        var recent = Recent.Items.Take(8).Select(path => new StartNode("recent/" + path, Path.GetFileName(path), "solution", Path.GetDirectoryName(path), path, Command("workbench.openSolution", path))).ToList();
        var tipIndex = (DateTime.Today.DayOfYear + _startPageTipOffset) % StartPageTips.Count;
        var tip = StartPageTips[tipIndex];
        var showOnStartup = Workbench.Settings.GetBoolean(StartPageShowOnStartupSetting, true);
        return
        [
            new("start", T("start", "Start"))
            {
                Children =
                [
                    Action("start", T("openSolutionOrProject", "Open Solution or Project…"), "folder", "workbench.openSolution"),
                    Action("start", T("showAllCommands", "Show All Commands"), "search", "workbench.showCommandPalette"),
                    Action("start", T("openSettings", "Open Settings"), "gear", "workbench.openSettings"),
                    Action("start", T("keyboardShortcuts", "Keyboard Shortcuts"), "keyboard", "workbench.openKeyboardShortcuts"),
                    Action("start", T("selectColorTheme", "Select Color Theme"), "color-mode", "workbench.selectTheme"),
                ],
            },
            new("recent", T("recent", "Recent"))
            {
                Children = recent.Count > 0
                    ? recent
                    : [new StartNode("recent/none", T("noRecentSolutionsYet", "No recent solutions yet"), "history", Tooltip: T("solutionsYouOpenWillBe", "Solutions you open will be listed here."))],
            },
            new("tip", T("tip", "Tip"))
            {
                Children =
                [
                    new StartNode($"tip/{tipIndex}", Localizer.Get($"startPage.tip.{tipIndex}", tip.Text), "lightbulb", Tooltip: Localizer.Get($"startPage.tip.{tipIndex}", tip.Text), Command: Command(tip.Command)),
                    Action("tip", T("nextTip", "Next Tip"), "arrow-right", "welcome.nextTip"),
                ],
            },
            new("plugins", T("plugins", "Plugins"))
            {
                Children =
                [
                    Action("plugins", T("installedPlugins", "Installed Plugins"), "extensions", "workbench.view.plugins"),
                    Action("plugins", T("openUserPluginsFolder", "Open User Plugins Folder"), "folder", "workbench.openPluginsFolder"),
                ],
            },
            new("learn", T("learn", "Learn"))
            {
                Children =
                [
                    Action("learn", T("documentation", "Documentation"), "book", "workbench.openExternal", StartPageRepository + "#readme"),
                    Action("learn", T("writingPlugins", "Writing Plugins"), "book", "workbench.openExternal", StartPageRepository + "/blob/master/docs/plugins.md"),
                ],
            },
            new("showOnStartup", T("showStartPageOnStartup", "Show Start Page on Startup"), showOnStartup ? "check" : "circle", showOnStartup ? T("on", "On") : T("off", "Off"),
                T("toggleWhetherThisPageOpens", "Toggle whether this page opens when Codalus IDE starts."), Command("welcome.toggleShowOnStartup")),
        ];
    }
}
