namespace DaedalusStudio.Plugins.Welcome;

/// <summary>
/// The built-in Start page. It uses only the public plugin API (ADR 0003): a tree tool window, commands, a
/// setting and a status bar item. Shell features are reached through built-in commands, as any plugin would.
/// </summary>
public sealed class WelcomePlugin : IDaedalusPlugin
{
    internal const string ViewId = "welcome.view";
    internal const string ShowOnStartupSetting = "welcome.showOnStartup";

    private StartTree _tree = new(null);
    private IPluginContext? _context;

    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _tree = new StartTree(context.Commands);
        _tree.ShowOnStartup = context.Settings.GetValue(ShowOnStartupSetting, true);
        context.Subscriptions.Add(context.Window.RegisterTreeDataProvider(ViewId, _tree));
        context.Subscriptions.Add(context.Commands.Register("welcome.show", async (_, ct) =>
        {
            await ShowAsync(preserveFocus: false, ct).ConfigureAwait(false);
            return null;
        }));
        context.Subscriptions.Add(context.Commands.Register("welcome.refresh", _tree.Refresh));
        context.Subscriptions.Add(context.Commands.Register("welcome.toggleShowOnStartup", async (_, ct) =>
        {
            await context.Commands.ExecuteAsync("workbench.setSetting", ct, ShowOnStartupSetting, !_tree.ShowOnStartup).ConfigureAwait(false);
            return null;
        }));

        var status = context.Window.CreateStatusBarItem("start", StatusBarAlignment.Left, priority: -100);
        status.Text = "$(home) Start";
        status.Tooltip = "Show the Start page";
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

/// <summary>The Start tree: groups of actions, and the recent solutions (from the shell's <c>workbench.getRecentSolutions</c>).</summary>
internal sealed class StartTree(ICommands? commands) : ITreeDataProvider<StartNode>
{
    private const string Repository = "https://github.com/stavroskasidis/daedalus-studio";

    public bool ShowOnStartup { get; set; } = true;

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

    private IReadOnlyList<StartNode> BuildRoots(IReadOnlyList<StartNode> recent) =>
    [
        Group("Start",
            Action("Open Solution or Project…", "folder", "workbench.openSolution"),
            Action("Show All Commands", "search", "workbench.showCommandPalette"),
            Action("Open Settings", "gear", "workbench.openSettings"),
            Action("Keyboard Shortcuts", "keyboard", "workbench.openKeyboardShortcuts"),
            Action("Select Color Theme", "color-mode", "workbench.selectTheme")),

        Group("Recent", recent.Count > 0
            ? [.. recent]
            : [new StartNode("No recent solutions yet") { Icon = "history", Tooltip = "Solutions you open will be listed here." }]),

        Group("Plugins",
            Action("Installed Plugins", "extensions", "workbench.view.plugins"),
            Action("Open User Plugins Folder", "folder", "workbench.openPluginsFolder")),

        Group("Learn",
            Action("Documentation", "book", "workbench.openExternal", Repository + "#readme"),
            Action("Writing Plugins", "book", "workbench.openExternal", Repository + "/blob/master/docs/plugins.md")),

        new StartNode("Show Start Page on Startup")
        {
            Icon = ShowOnStartup ? "check" : "circle",
            Description = ShowOnStartup ? "On" : "Off",
            Tooltip = "Toggle whether this page opens when Daedalus Studio starts.",
            Command = new TreeItemCommand("welcome.toggleShowOnStartup"),
        },
    ];

    private static StartNode Group(string label, params StartNode[] children) => new(label) { Children = children };

    private static StartNode Action(string label, string icon, string command, params object?[] args) =>
        new(label) { Icon = icon, Command = new TreeItemCommand(command, args) };
}
