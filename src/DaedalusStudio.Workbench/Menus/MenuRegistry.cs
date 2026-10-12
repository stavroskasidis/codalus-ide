using DaedalusStudio.Platform;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Keybindings;

namespace DaedalusStudio.Workbench.Menus;

/// <summary>Well-known menu locations (ADR 0003).</summary>
public static class MenuLocations
{
    public const string CommandPalette = "commandPalette";
    public const string ToolWindowTitle = "toolWindow/title";
    public const string MenuBarPrefix = "menuBar/";

    /// <summary>The code editor's context menu. Commands get the document URI and the zero-based caret line and column.</summary>
    public const string EditorContext = "editor/context";

    public const string SolutionExplorerContext = "solutionExplorer/context";

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
}

public sealed class MenuRegistry(CommandRegistry commands, KeybindingService keybindings, IContextKeyReader context)
{
    private static readonly string[] s_groupOrder = ["navigation", "file", "edit", "view", "default", "plugins", "help", "exit"];

    private readonly Lock _gate = new();
    private readonly List<MenuItemRule> _items = [];
    private readonly List<MenuBarMenu> _menuBar = [];

    public event EventHandler? Changed;

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
            _menuBar.Add(menu);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(IEnumerable<MenuItemRule> items)
    {
        lock (_gate)
        {
            _items.AddRange(items);
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

    public IReadOnlyList<MenuItem> GetItems(string location, IContextKeyReader scope)
    {
        List<MenuItemRule> rules;
        lock (_gate)
        {
            rules = _items.Where(i => i.Location == location).ToList();
        }

        var result = new List<MenuItem>();
        foreach (var rule in rules
                     .OrderBy(r => GroupRank(r.Group))
                     .ThenBy(r => r.Group, StringComparer.Ordinal)
                     .ThenBy(r => r.Order))
        {
            if (!rule.When.Evaluate(scope))
            {
                continue;
            }

            if (rule.Submenu is { } submenu)
            {
                if (submenu != location && GetItems(submenu, scope).Count > 0)
                {
                    result.Add(new MenuItem(string.Empty, rule.Title ?? submenu, null, true, rule.Group) { Submenu = submenu });
                }

                continue;
            }

            if (commands.Find(rule.Command) is not { } descriptor)
            {
                continue;
            }

            result.Add(new MenuItem(
                rule.Command,
                rule.Title ?? (location == MenuLocations.CommandPalette ? descriptor.DisplayTitle : descriptor.Title),
                keybindings.GetBinding(rule.Command)?.ToLabel(keybindings.Platform)
                    ?? (rule.Keys is { } keys ? KeySequence.Parse(keys, keybindings.Platform).ToLabel(keybindings.Platform) : null),
                descriptor.Enablement.Evaluate(scope),
                rule.Group)
            {
                Description = descriptor.DisplayTitle,
            });
        }

        return result;
    }

    private static int GroupRank(string group)
    {
        var index = Array.IndexOf(s_groupOrder, group);
        return index >= 0 ? index : s_groupOrder.Length - 2;
    }
}
