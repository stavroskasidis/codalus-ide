using Codalus.Platform;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Keybindings;

namespace Codalus.Workbench.Menus;

/// <summary>Well-known menu locations (ADR 0003).</summary>
public static class MenuLocations
{
    public const string CommandPalette = "commandPalette";
    public const string ToolWindowTitle = "toolWindow/title";
    public const string MenuBarPrefix = "menuBar/";

    /// <summary>The code editor's context menu. Commands get the document URI and the zero-based caret line and column.</summary>
    public const string EditorContext = "editor/context";

    public const string SolutionExplorerContext = "solutionExplorer/context";

    /// <summary>A document tab's context menu (M34e). Its commands act on the active document, which the tab becomes.</summary>
    public const string DocumentTabContext = "documentTab/context";

    /// <summary>
    /// The Git Changes window's menu for a file, a folder or the repository node (#23, D38). Commands get the node's path,
    /// its section (<c>changes</c>, <c>staged</c>, <c>conflicts</c>) and the paths of the files the window shows under it (a
    /// filter can hide some; with no third argument a command acts on the whole folder). Its when-clauses read
    /// <c>gitChangeKind</c> (<c>file</c>, <c>folder</c>, <c>repository</c>), <c>gitChangeSection</c>,
    /// <c>gitChangeAllUntracked</c> and <c>gitChangeExists</c>, set for that menu only.
    /// </summary>
    public const string GitChangesContext = "gitChanges/context";

    /// <summary>
    /// A branch's menu in the Git Changes window's branch drop-down (#23, D38). Commands get the branch's full ref
    /// (<c>refs/heads/main</c>, <c>refs/remotes/origin/main</c>). Its when-clauses read <c>gitBranchKind</c> (<c>local</c>,
    /// <c>remote</c>), <c>gitBranchIsCurrent</c>, <c>gitBranchHasUpstream</c>, <c>gitBranchInOtherWorktree</c>,
    /// <c>gitBranchHasLocal</c>, <c>gitHasRemote</c>, <c>gitHeadDetached</c> and <c>gitHeadUnborn</c>, set for that menu only.
    /// </summary>
    public const string GitChangesBranch = "gitChanges/branch";

    public static string MenuBar(string menu) => MenuBarPrefix + menu;
}

/// <summary>A top-level menu-bar menu such as File or View.</summary>
public sealed record MenuBarMenu(string Id, string Title, int Order);

/// <summary>A contributed menu entry. Items are grouped by <see cref="Group"/>; groups are separated visually.</summary>
public sealed record MenuItemRule(string Location, string Command, ContributionSource Source)
{
    public string Group { get; init; } = "default";

    public int Order { get; init; }

    public WhenClause When { get; init; } = WhenClause.Always;

    /// <summary>Overrides the command title in this menu.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Makes the entry a submenu titled <see cref="Title"/> whose items are those of this location (its
    /// <see cref="Command"/> is ignored). It is shown while it has visible items.
    /// </summary>
    public string? Submenu { get; init; }

    /// <summary>
    /// Keys the focused control handles itself (Undo's <c>primary+z</c>), shown in the menu when the command has no
    /// key binding of its own. Keybinding syntax; never dispatched.
    /// </summary>
    public string? Keys { get; init; }

    /// <summary>A submenu entry: <paramref name="submenu"/> is the location of its items.</summary>
    public static MenuItemRule ForSubmenu(string location, string submenu, string title, ContributionSource source) =>
        new(location, string.Empty, source) { Submenu = submenu, Title = title };
}

/// <summary>A resolved, visible menu entry. A <see cref="Submenu"/> entry has no command: its items are that location's.</summary>
public sealed record MenuItem(string Command, string Title, string? KeyLabel, bool Enabled, string Group)
{
    public string? Submenu { get; init; }

    /// <summary>The command's full name (<c>Category: Title</c>) for tooltips.</summary>
    public string? Description { get; init; }

    /// <summary>The keys shown (the command's binding, or the rule's <see cref="MenuItemRule.Keys"/>), for native menus.</summary>
    public KeySequence? Keys { get; init; }

    /// <summary>
    /// The rule's own <see cref="MenuItemRule.Keys"/> as a label, whatever the command's binding: what a menu whose entries
    /// pass arguments shows (D38, question 13), since the command's global binding runs it without them.
    /// </summary>
    public string? RuleKeyLabel { get; init; }
}

public sealed class MenuRegistry(CommandRegistry commands, KeybindingService keybindings, IContextKeyReader context)
{
    private static readonly string[] s_groupOrder = ["navigation", "file", "edit", "view", "default", "plugins", "help", "exit"];

    private readonly Lock _gate = new();
    private readonly List<MenuItemRule> _items = [];
    private readonly List<MenuBarMenu> _menuBar = [];
    private MenuCustomizations _customizations = MenuCustomizations.None;

    public event EventHandler? Changed;

    /// <summary>The user's additions, removals and order (M23b), applied by <see cref="GetItems(string, IContextKeyReader)"/>.</summary>
    public MenuCustomizations Customizations
    {
        get
        {
            lock (_gate)
            {
                return _customizations;
            }
        }

        set
        {
            lock (_gate)
            {
                _customizations = value;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<MenuBarMenu> MenuBar
    {
        get
        {
            lock (_gate)
            {
                return [.. _menuBar.OrderBy(m => m.Order)];
            }
        }
    }

    /// <summary>Every contributed rule, whatever its when-clause (tests and the menu layout checks).</summary>
    public IReadOnlyList<MenuItemRule> Rules
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public void AddMenuBarMenu(MenuBarMenu menu)
    {
        lock (_gate)
        {
            _menuBar.RemoveAll(m => m.Id == menu.Id);
            _menuBar.Add(menu with { Title = Localization.Localizer.Get($"menu.{menu.Id}", menu.Title) });
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(IEnumerable<MenuItemRule> items)
    {
        lock (_gate)
        {
            // The shell's submenu titles in the display language (M23b).
            _items.AddRange(items.Select(i => i is { Submenu: { } submenu, Title: { } title } && i.Source == ContributionSource.Shell
                ? i with { Title = Localization.Localizer.Get($"menu.{submenu}", title) }
                : i));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveFrom(ContributionSource source)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Source == source);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// True if <paramref name="command"/> may be listed in the command palette: it has no <c>commandPalette</c>
    /// rule (every command is listed by default), or one whose when-clause holds.
    /// </summary>
    public bool IsVisibleInPalette(string command, IContextKeyReader scope)
    {
        lock (_gate)
        {
            var rules = _items.Where(i => i.Location == MenuLocations.CommandPalette && i.Command == command).ToList();
            return rules.Count == 0 || rules.Any(r => r.When.Evaluate(scope));
        }
    }

    /// <summary>The visible items of a location, ordered by group then order; <see cref="MenuItem.Group"/> marks separators.</summary>
    public IReadOnlyList<MenuItem> GetItems(string location) => GetItems(location, context);

    public IReadOnlyList<MenuItem> GetItems(string location, IContextKeyReader scope) => GetItems(location, scope, everything: false);

    /// <summary>
    /// Every item of a location whatever the when-clauses, empty submenus too, with the user's customizations: what
    /// Tools → Customize lists.
    /// </summary>
    public IReadOnlyList<MenuItem> GetAllItems(string location) => GetItems(location, context, everything: true);

    /// <summary>The location's items as contributed, without the user's customizations (Customize's Reset compares with them).</summary>
    public IReadOnlyList<string> GetContributedKeys(string location)
    {
        lock (_gate)
        {
            return [.. _items.Where(i => i.Location == location)
                .OrderBy(r => GroupRank(r.Group)).ThenBy(r => r.Group, StringComparer.Ordinal).ThenBy(r => r.Order)
                .Select(r => r.Submenu ?? r.Command)];
        }
    }

    private List<MenuItem> GetItems(string location, IContextKeyReader scope, bool everything)
    {
        List<MenuItemRule> rules;
        MenuCustomizations customizations;
        lock (_gate)
        {
            rules = _items.Where(i => i.Location == location).ToList();
            customizations = _customizations;
        }

        var customization = customizations.For(location);
        var result = new List<MenuItem>();
        foreach (var rule in rules
                     .OrderBy(r => GroupRank(r.Group))
                     .ThenBy(r => r.Group, StringComparer.Ordinal)
                     .ThenBy(r => r.Order))
        {
            if ((!everything && !rule.When.Evaluate(scope)) || customization.Remove.Contains(rule.Submenu ?? rule.Command))
            {
                continue;
            }

            if (rule.Submenu is { } submenu)
            {
                if (submenu != location && (everything || GetItems(submenu, scope, everything: false).Count > 0))
                {
                    result.Add(new MenuItem(string.Empty, rule.Title ?? submenu, null, true, rule.Group) { Submenu = submenu });
                }

                continue;
            }

            if (commands.Find(rule.Command) is not { } descriptor)
            {
                continue;
            }

            var ruleKeys = rule.Keys is { } hint ? KeySequence.Parse(hint, keybindings.Platform) : null;
            var keys = keybindings.GetBinding(rule.Command) ?? ruleKeys;
            result.Add(new MenuItem(
                rule.Command,
                rule.Title ?? (location == MenuLocations.CommandPalette ? descriptor.DisplayTitle : descriptor.Title),
                keys?.ToLabel(keybindings.Platform),
                descriptor.Enablement.Evaluate(scope),
                rule.Group)
            {
                Description = descriptor.DisplayTitle,
                Keys = keys,
                RuleKeyLabel = ruleKeys?.ToLabel(keybindings.Platform),
            });
        }

        // The user's own commands: added to this location, or a toolbar of theirs.
        var added = customization.Add.AsEnumerable();
        if (location.StartsWith(MenuCustomizations.ToolbarPrefix, StringComparison.Ordinal))
        {
            var name = location[MenuCustomizations.ToolbarPrefix.Length..];
            added = customizations.Toolbars.Where(t => t.Name == name).SelectMany(t => t.Commands).Concat(added);
        }

        foreach (var command in added)
        {
            if (customization.Remove.Contains(command) || result.Any(i => i.Command == command) || commands.Find(command) is not { } descriptor)
            {
                continue;
            }

            var keys = keybindings.GetBinding(command);
            result.Add(new MenuItem(command, descriptor.Title, keys?.ToLabel(keybindings.Platform), descriptor.Enablement.Evaluate(scope), "~custom")
            {
                Description = descriptor.DisplayTitle,
                Keys = keys,
            });
        }

        if (customization.Order is { } order)
        {
            // In the user's order, as one group: their order crosses the contributed groups' separators.
            var ranks = order.Select((key, i) => (key, i)).DistinctBy(p => p.key).ToDictionary(p => p.key, p => p.i, StringComparer.Ordinal);
            int Rank(MenuItem item) => ranks.TryGetValue(item.Submenu ?? item.Command, out var i) ? i : int.MaxValue;
            result = [.. result.Select((item, index) => (item, index)).OrderBy(p => Rank(p.item)).ThenBy(p => p.index).Select(p => p.item with { Group = "custom" })];
        }

        return result;
    }

    private static int GroupRank(string group)
    {
        var index = Array.IndexOf(s_groupOrder, group);
        return index >= 0 ? index : s_groupOrder.Length - 2;
    }
}
