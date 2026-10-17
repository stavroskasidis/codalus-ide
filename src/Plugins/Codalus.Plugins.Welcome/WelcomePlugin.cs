namespace Codalus.Plugins.Welcome;

/// <summary>
/// The built-in Start page. It uses only the public plugin API (ADR 0003): a tree tool window, commands, a
/// setting and a status bar item. Shell features are reached through built-in commands, as any plugin would.
/// Onboarding (M23a): until the user is done with it, a Get Started group offers the theme, the keyboard scheme and
/// the importers; a tip of the day runs what it describes.
/// </summary>
public sealed class WelcomePlugin : ICodalusPlugin
{
    internal const string ViewId = "welcome.view";
    internal const string ShowOnStartupSetting = "welcome.showOnStartup";
    internal const string OnboardedSetting = "welcome.onboarded";
    internal const string ThemeSetting = "workbench.colorTheme";
    internal const string KeyboardSchemeSetting = "keyboard.scheme";

    /// <summary>The keyboard schemes the shell offers (its <c>keyboard.scheme</c> setting).</summary>
    internal static readonly IReadOnlyList<(string Id, string Title, string Description)> KeyboardSchemes =
    [
        ("default", "Default", "The classic IDE's keys: F5 debugs, F6 builds, Ctrl+Q searches"),
        ("vscode", "VS Code", "F1 is the command palette, Ctrl+, opens Settings, Ctrl+W closes"),
        ("resharper", "ReSharper", "The default with ReSharper's: Ctrl+Shift+R refactors, Ctrl+W expands the selection"),
        ("emacs", "Emacs", "The default with Emacs's editing keys and Ctrl+X chords"),
    ];

    private StartTree _tree = new(null);
    private IPluginContext? _context;

    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _tree = new StartTree(context.Commands, context.Settings, context.Localize);
        _tree.ShowOnStartup = context.Settings.GetValue(ShowOnStartupSetting, true);
        context.Subscriptions.Add(context.Window.RegisterTreeDataProvider(ViewId, _tree));
        context.Subscriptions.Add(context.Commands.Register("welcome.show", async (_, ct) =>
        {
            await ShowAsync(preserveFocus: false, ct).ConfigureAwait(false);
            return null;
        }));
        context.Subscriptions.Add(context.Commands.Register("welcome.refresh", _tree.Refresh));
        context.Subscriptions.Add(context.Commands.Register("welcome.chooseKeyboardScheme", async (_, ct) =>
        {
            var current = context.Settings.GetValue(KeyboardSchemeSetting, "default");
            var items = KeyboardSchemes.Select(s => new QuickPickItem(context.Localize($"scheme.{s.Id}", s.Title)) { Description = s.Id == current ? context.Localize("start.current", "current") : null, Detail = context.Localize($"scheme.{s.Id}.description", s.Description), Tag = s.Id }).ToList();
            if (await context.Window.ShowQuickPickAsync(items, context.Localize("start.chooseAKeyboardScheme2", "Choose a keyboard scheme"), ct).ConfigureAwait(false) is { Tag: string id })
            {
                await context.Commands.ExecuteAsync("workbench.setSetting", ct, KeyboardSchemeSetting, id).ConfigureAwait(false);
            }

            return null;
        }));
        context.Subscriptions.Add(context.Commands.Register("welcome.finishOnboarding", async (_, ct) =>
        {
            await context.Commands.ExecuteAsync("workbench.setSetting", ct, OnboardedSetting, true).ConfigureAwait(false);
            return null;
        }));
        context.Subscriptions.Add(context.Commands.Register("welcome.showOnboarding", async (_, ct) =>
        {
            await context.Commands.ExecuteAsync("workbench.setSetting", ct, OnboardedSetting, false).ConfigureAwait(false);
            await ShowAsync(preserveFocus: false, ct).ConfigureAwait(false);
            return null;
        }));
        context.Subscriptions.Add(context.Commands.Register("welcome.nextTip", () =>
        {
            _tree.TipOffset++;
            _tree.Refresh();
        }));
        context.Subscriptions.Add(context.Commands.Register("welcome.toggleShowOnStartup", async (_, ct) =>
        {
            await context.Commands.ExecuteAsync("workbench.setSetting", ct, ShowOnStartupSetting, !_tree.ShowOnStartup).ConfigureAwait(false);
            return null;
        }));

        var status = context.Window.CreateStatusBarItem("start", StatusBarAlignment.Left, priority: -100);
        status.Text = "$(home) " + context.Localize("start.start", "Start");
        status.Tooltip = context.Localize("start.showTheStartPage", "Show the Start page");
        status.Command = "welcome.show";
        status.Show();
        context.Subscriptions.Add(status);

        context.Settings.Changed += OnSettingsChanged;
        context.Subscriptions.Add(new Unsubscribe(() => context.Settings.Changed -= OnSettingsChanged));

        if (_tree.ShowOnStartup)
        {
            // Not awaited: the shell opens the view, which waits for this activation to finish.
            _ = ShowAsync(preserveFocus: true, CancellationToken.None);
        }

        context.Log.Info("Start page ready.");
        return Task.CompletedTask;
    }

    private async Task ShowAsync(bool preserveFocus, CancellationToken cancellationToken)
    {
        try
        {
            await _context!.Commands.ExecuteAsync("workbench.view." + ViewId, cancellationToken, new ShowViewOptions(preserveFocus)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context!.Log.Warning($"Could not show the Start page: {ex.Message}");
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Affects(ShowOnStartupSetting))
        {
            _tree.ShowOnStartup = _context!.Settings.GetValue(ShowOnStartupSetting, true);
            _tree.Refresh();
        }
        else if (e.Affects(OnboardedSetting) || e.Affects(ThemeSetting) || e.Affects(KeyboardSchemeSetting))
        {
            _tree.Refresh();
        }
    }

    /// <summary>Arguments of the shell's <c>workbench.view.&lt;id&gt;</c> commands.</summary>
    public sealed record ShowViewOptions(bool PreserveFocus);

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

internal sealed record StartNode(string Label)
{
    public string? Icon { get; init; }

    public string? Description { get; init; }

    public string? Tooltip { get; init; }

    public TreeItemCommand? Command { get; init; }

    public IReadOnlyList<StartNode> Children { get; init; } = [];
}

/// <summary>
/// The Start tree: Get Started (until done), groups of actions, the recent solutions (from the shell's
/// <c>workbench.getRecentSolutions</c>) and a tip.
/// </summary>
internal sealed class StartTree(ICommands? commands, ISettings? settings = null, Func<string, string, string>? localize = null) : ITreeDataProvider<StartNode>
{
    private string T(string key, string english) => localize?.Invoke(key, english) ?? english;

    /// <summary>
    /// <c>workbench.colorTheme</c> as a line (M45): "Harbor, Follow System", "Ember, Dark"; the values from before the theme
    /// families read as Classic. A plugin's theme shows its id.
    /// </summary>
    internal string ThemeLabel(string theme)
    {
        var (family, variant) = theme switch
        {
            "followSystem" => ("classic", (string?)null),
            "blue" or "light" or "dark" or "highContrast" => ("classic", theme),
            _ when theme.Split('.') is [var f, var v] => (f, v),
            _ => (theme, null),
        };
        var familyName = family switch
        {
            "harbor" => "Harbor",
            "graphite" => "Graphite",
            "daylight" => "Daylight",
            "ember" => "Ember",
            "classic" => "Classic",
            _ => null,
        };
        if (familyName is null)
        {
            return theme;
        }

        var mode = variant switch
        {
            null => T("start.followSystem", "Follow System"),
            "light" => T("start.themeLight", "Light"),
            "dark" => T("start.themeDark", "Dark"),
            "highContrast" => T("start.themeHighContrast", "High Contrast"),
            "blue" => T("start.themeBlue", "Blue"),
            _ => variant,
        };
        return $"{familyName}, {mode}";
    }

    private const string Repository = "https://github.com/stavroskasidis/codalus-ide";

    /// <summary>Tips: each runs what it describes.</summary>
    internal static readonly IReadOnlyList<(string Text, string Command)> Tips =
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

    public bool ShowOnStartup { get; set; } = true;

    /// <summary>Tips moved past with Next Tip, on top of the day's.</summary>
    public int TipOffset { get; set; }

    public event EventHandler<TreeDataChangedEventArgs<StartNode>>? Changed;

    public void Refresh() => Changed?.Invoke(this, new TreeDataChangedEventArgs<StartNode>(null));

    public async Task<IReadOnlyList<StartNode>> GetChildrenAsync(StartNode? element, CancellationToken cancellationToken) =>
        element is null ? BuildRoots(await RecentAsync(cancellationToken).ConfigureAwait(false)) : element.Children;

    private async Task<IReadOnlyList<StartNode>> RecentAsync(CancellationToken cancellationToken)
    {
        if (commands is null)
        {
            return [];
        }

        var result = await commands.ExecuteAsync("workbench.getRecentSolutions", cancellationToken).ConfigureAwait(false);
        if (result is not { ValueKind: System.Text.Json.JsonValueKind.Array } list)
        {
            return [];
        }

        return [.. list.EnumerateArray().Take(8).Select(entry => new StartNode(entry.GetProperty("name").GetString() ?? "?")
        {
            Icon = "solution",
            Description = Path.GetDirectoryName(entry.GetProperty("path").GetString()),
            Tooltip = entry.GetProperty("path").GetString(),
            Command = new TreeItemCommand("workbench.openSolution", entry.GetProperty("path").GetString()),
        })];
    }

    public TreeItem GetTreeItem(StartNode element) => new(element.Label)
    {
        Icon = element.Icon,
        Description = element.Description,
        Tooltip = element.Tooltip,
        Command = element.Command,
        Collapsible = element.Children.Count > 0 ? TreeItemCollapsibleState.Expanded : TreeItemCollapsibleState.None,
    };

    private List<StartNode> BuildRoots(IReadOnlyList<StartNode> recent)
    {
        var roots = new List<StartNode>();
        if (settings?.GetValue(WelcomePlugin.OnboardedSetting, false) != true)
        {
            roots.Add(GetStarted());
        }

        roots.AddRange(Common(recent));
        return roots;
    }

    /// <summary>The first-run steps (M23a): the look, the keys, and what to bring from elsewhere.</summary>
    private StartNode GetStarted()
    {
        var theme = settings?.GetValue(WelcomePlugin.ThemeSetting, "harbor") ?? "harbor";
        var scheme = settings?.GetValue(WelcomePlugin.KeyboardSchemeSetting, "default") ?? "default";
        return Group(T("start.getStarted", "Get Started"),
            Action(T("start.chooseAColorTheme", "Choose a Color Theme"), "color-mode", "workbench.selectTheme") with { Description = ThemeLabel(theme) },
            Action(T("start.chooseAKeyboardScheme", "Choose a Keyboard Scheme"), "keyboard", "welcome.chooseKeyboardScheme") with
            {
                Description = WelcomePlugin.KeyboardSchemes.FirstOrDefault(s => s.Id == scheme).Title ?? scheme,
                Tooltip = T("start.defaultVsCodeResharperOr", "Default, VS Code, ReSharper or Emacs keys"),
            },
            Action(T("start.importFromVsCode", "Import from VS Code…"), "download", "workbench.importVsCodeSettings") with { Tooltip = T("start.settingsAndKeyBindingsFrom", "Settings and key bindings from VS Code's user folder") },
            Action(T("start.importSettingsFromAFile", "Import Settings from a File…"), "download", "workbench.importSettings") with { Tooltip = T("start.aFileExportedWithTools", "A file exported with Tools → Export Settings") },
            Action(T("start.fontsAndColors", "Fonts and Colors"), "color", "workbench.openFontsAndColors"),
            Action(T("start.done", "Done"), "check-circle", "welcome.finishOnboarding") with { Tooltip = T("start.hideGetStartedHelpGet", "Hide Get Started (Help → Get Started brings it back)") });
    }

    private StartNode Tip()
    {
        var index = (DateTime.Today.DayOfYear + TipOffset) % Tips.Count;
        var tip = Tips[index];
        return Group(T("start.tip", "Tip"),
            new StartNode(T($"tip.{index}", tip.Text)) { Icon = "lightbulb", Tooltip = T($"tip.{index}", tip.Text), Command = new TreeItemCommand(tip.Command) },
            Action(T("start.nextTip", "Next Tip"), "arrow-right", "welcome.nextTip"));
    }

    private IEnumerable<StartNode> Common(IReadOnlyList<StartNode> recent) =>
    [
        Group(T("start.start", "Start"),
            Action(T("start.openSolutionOrProject", "Open Solution or Project…"), "folder", "workbench.openSolution"),
            Action(T("start.showAllCommands", "Show All Commands"), "search", "workbench.showCommandPalette"),
            Action(T("start.openSettings", "Open Settings"), "gear", "workbench.openSettings"),
            Action(T("start.keyboardShortcuts", "Keyboard Shortcuts"), "keyboard", "workbench.openKeyboardShortcuts"),
            Action(T("start.selectColorTheme", "Select Color Theme"), "color-mode", "workbench.selectTheme")),

        Group(T("start.recent", "Recent"), recent.Count > 0
            ? [.. recent]
            : [new StartNode(T("start.noRecentSolutionsYet", "No recent solutions yet")) { Icon = "history", Tooltip = T("start.solutionsYouOpenWillBe", "Solutions you open will be listed here.") }]),

        Tip(),

        Group(T("start.plugins", "Plugins"),
            Action(T("start.installedPlugins", "Installed Plugins"), "extensions", "workbench.view.plugins"),
            Action(T("start.openUserPluginsFolder", "Open User Plugins Folder"), "folder", "workbench.openPluginsFolder")),

        Group(T("start.learn", "Learn"),
            Action(T("start.documentation", "Documentation"), "book", "workbench.openExternal", Repository + "#readme"),
            Action(T("start.writingPlugins", "Writing Plugins"), "book", "workbench.openExternal", Repository + "/blob/master/docs/plugins.md")),

        new StartNode(T("start.showStartPageOnStartup", "Show Start Page on Startup"))
        {
            Icon = ShowOnStartup ? "check" : "circle",
            Description = ShowOnStartup ? T("start.on", "On") : T("start.off", "Off"),
            Tooltip = T("start.toggleWhetherThisPageOpens", "Toggle whether this page opens when Codalus IDE starts."),
            Command = new TreeItemCommand("welcome.toggleShowOnStartup"),
        },
    ];

    private static StartNode Group(string label, params StartNode[] children) => new(label) { Children = children };

    private static StartNode Action(string label, string icon, string command, params object?[] args) =>
        new(label) { Icon = icon, Command = new TreeItemCommand(command, args) };
}
