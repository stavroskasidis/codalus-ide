using Daedalus.Workbench.Commands;
using Daedalus.Workbench.Context;
using Daedalus.Workbench.Keybindings;

namespace Daedalus.Workbench.Menus;

/// <summary>Well-known menu locations (ADR 0003).</summary>
public static class MenuLocations
{
    public const string CommandPalette = "commandPalette";
    public const string ToolWindowTitle = "toolWindow/title";
    public const string MenuBarPrefix = "menuBar/";

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
}

/// <summary>A resolved, visible menu entry.</summary>
public sealed record MenuItem(string Command, string Title, string? KeyLabel, bool Enabled, string Group);

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
            if (!rule.When.Evaluate(scope) || commands.Find(rule.Command) is not { } descriptor)
            {
                continue;
            }

            result.Add(new MenuItem(
                rule.Command,
                rule.Title ?? (location == MenuLocations.CommandPalette ? descriptor.DisplayTitle : descriptor.Title),
                keybindings.GetBinding(rule.Command)?.ToLabel(keybindings.Platform),
                descriptor.Enablement.Evaluate(scope),
                rule.Group));
        }

        return result;
    }

    private static int GroupRank(string group)
    {
        var index = Array.IndexOf(s_groupOrder, group);
        return index >= 0 ? index : s_groupOrder.Length - 2;
    }
}
