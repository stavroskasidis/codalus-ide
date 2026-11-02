using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;

namespace Codalus.Shell;

/// <summary>
/// Document tab options (the gear beside a group's tabs, as the classic IDE's): where the tabs go, how they're colored,
/// multiple rows. Each entry is a command that sets a user setting, so the palette has them too.
/// </summary>
internal static partial class BuiltIns
{
    /// <summary>The menu of a document group's gear (<see cref="Controls.TabStripButtons"/>).</summary>
    public const string TabOptionsMenu = "documentTabStrip/options";

    /// <summary>Its Color Document Tabs by submenu.</summary>
    public const string TabColorByMenu = "documentTabStrip/options/colorBy";

    public static void RegisterTabCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<Task> handler) =>
            commands.Register(new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Window" }, Ui(handler));

        Add("workbench.tabs.placeLeft", "Place Tabs on the Left", () => shell.SetTabOptionAsync(ShellHost.TabPlacementSetting, "left"));
        Add("workbench.tabs.placeTop", "Place Tabs on the Top", () => shell.SetTabOptionAsync(ShellHost.TabPlacementSetting, "top"));
        Add("workbench.tabs.placeRight", "Place Tabs on the Right", () => shell.SetTabOptionAsync(ShellHost.TabPlacementSetting, "right"));
        Add("workbench.tabs.colorByNone", "Don't Color Document Tabs", () => shell.SetTabOptionAsync(ShellHost.ColorTabsBySetting, "none"));
        Add("workbench.tabs.colorByProject", "Color Document Tabs by Project", () => shell.SetTabOptionAsync(ShellHost.ColorTabsBySetting, "project"));
        Add("workbench.tabs.colorByFileExtension", "Color Document Tabs by File Extension", () => shell.SetTabOptionAsync(ShellHost.ColorTabsBySetting, "fileExtension"));
        Add("workbench.tabs.toggleMultipleRows", "Show Tabs in Multiple Rows", () =>
            shell.SetTabOptionAsync(ShellHost.TabsInMultipleRowsSetting, !shell.Workbench.Settings.GetBoolean(ShellHost.TabsInMultipleRowsSetting, false)));
        Add("workbench.tabs.toggleMultipleRowsWithMouseWheel", "Toggle Multiple Rows with Mouse Wheel", () =>
            shell.SetTabOptionAsync(ShellHost.MultipleRowsWithMouseWheelSetting, !shell.Workbench.Settings.GetBoolean(ShellHost.MultipleRowsWithMouseWheelSetting, true)));
        Add("workbench.tabs.options", "Tab Options…", () =>
        {
            shell.OpenSettings("workbench.editor.");
            return Task.CompletedTask;
        });
    }

    public static void RegisterTabMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        var order = 0;
        MenuItemRule Item(string location, string command, string group, string? title = null, string? isChecked = null) =>
            new(location, command, shell) { Group = group, Order = ++order, Title = title, Checked = isChecked is null ? null : WhenClause.Parse(isChecked) };

        menus.Add(
        [
            Item(TabOptionsMenu, "workbench.tabs.placeLeft", "1-placement", isChecked: "tabPlacement == left"),
            Item(TabOptionsMenu, "workbench.tabs.placeTop", "1-placement", isChecked: "tabPlacement == top"),
            Item(TabOptionsMenu, "workbench.tabs.placeRight", "1-placement", isChecked: "tabPlacement == right"),
            MenuItemRule.ForSubmenu(TabOptionsMenu, TabColorByMenu, "Color Document Tabs by", shell) with { Group = "2-color", Order = ++order },
            Item(TabColorByMenu, "workbench.tabs.colorByNone", "1-color", "None", "tabColorBy == none"),
            Item(TabColorByMenu, "workbench.tabs.colorByProject", "1-color", "Project", "tabColorBy == project"),
            Item(TabColorByMenu, "workbench.tabs.colorByFileExtension", "1-color", "File Extension", "tabColorBy == fileExtension"),
            Item(TabOptionsMenu, "workbench.tabs.toggleMultipleRows", "3-rows", isChecked: "tabsInMultipleRows"),
            Item(TabOptionsMenu, "workbench.tabs.toggleMultipleRowsWithMouseWheel", "3-rows", isChecked: "tabsMultipleRowsWithMouseWheel"),
            Item(TabOptionsMenu, "workbench.tabs.options", "4-options", "Options…"),
        ]);
    }
}
