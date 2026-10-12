using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using DaedalusStudio.Platform;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.Input;
using DaedalusStudio.Workbench.Menus;
using MenuItem = Avalonia.Controls.MenuItem;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// Renders the workbench menu model: an in-window menu bar on Windows and Linux, the global menu bar
/// (<see cref="NativeMenu"/>) on macOS, and the context menus. Items are rebuilt each time a menu opens, so
/// enablement and when-clauses reflect the current context. Submenus nest (M34a), entries show their command's
/// icon (colored by family), shortcut and full name as a tooltip.
/// </summary>
internal static class MenuBuilder
{
    public static void Attach(Window window, Menu menuBar, ShellHost shell)
    {
        var menus = shell.Workbench.Menus;
        if (shell.Workbench.Platform == PlatformKind.MacOS)
        {
            menuBar.IsVisible = false;
            var native = new NativeMenu();
            NativeMenu.SetMenu(window, native);
            void RebuildNative() => BuildNative(native, shell);
            RebuildNative();
            menus.Changed += (_, _) => Ui.Post(RebuildNative);
            return;
        }

        void Rebuild() => Build(menuBar, shell);
        Rebuild();
        menus.Changed += (_, _) => Ui.Post(Rebuild);
    }

    /// <summary>
    /// A context menu of <paramref name="location"/>'s items, or null when none is visible. <paramref name="commandFor"/>
    /// makes each entry's command (the editor passes the document and caret).
    /// </summary>
    public static ContextMenu? Context(ShellHost shell, string location, Func<string, ICommand>? commandFor = null)
    {
        if (shell.Workbench.Menus.GetItems(location).Count == 0)
        {
            return null;
        }

        var menu = new ContextMenu();
        Fill(menu.Items, location, shell, commandFor ?? (c => shell.CommandFor(c)));
        return menu;
    }

    private static void Build(Menu menuBar, ShellHost shell)
    {
        menuBar.Items.Clear();
        foreach (var menu in shell.Workbench.Menus.MenuBar)
        {
            var location = MenuLocations.MenuBar(menu.Id);
            if (shell.Workbench.Menus.GetItems(location).Count == 0)
            {
                continue;
            }

            var top = new MenuItem { Header = menu.Title };
            AutomationProperties.SetName(top, menu.Title.Replace("_", string.Empty, StringComparison.Ordinal));
            Fill(top.Items, location, shell, c => shell.CommandFor(c));
            top.SubmenuOpened += (_, e) =>
            {
                if (e.Source == top)
                {
                    Fill(top.Items, location, shell, c => shell.CommandFor(c));
                }
            };
            menuBar.Items.Add(top);
        }
    }

    private static void Fill(ItemCollection items, string location, ShellHost shell, Func<string, ICommand> commandFor)
    {
        items.Clear();
        string? group = null;
        foreach (var item in shell.Workbench.Menus.GetItems(location))
        {
            if (group is not null && item.Group != group)
            {
                items.Add(new Separator());
            }

            group = item.Group;
            if (item.Submenu is { } submenu)
            {
                var child = new MenuItem { Header = item.Title };
                AutomationProperties.SetName(child, item.Title);
                Fill(child.Items, submenu, shell, commandFor);
                items.Add(child);
                continue;
            }

            items.Add(Entry(item, shell, commandFor(item.Command)));
        }
    }

    private static MenuItem Entry(Workbench.Menus.MenuItem item, ShellHost shell, ICommand command)
    {
        var header = new DockPanel { LastChildFill = true };
        if (item.KeyLabel is { } keys)
        {
            var label = new TextBlock { Text = keys, Margin = new Avalonia.Thickness(32, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            label.Classes.Add("muted");
            DockPanel.SetDock(label, Avalonia.Controls.Dock.Right);
            header.Children.Add(label);
        }

        header.Children.Add(new TextBlock { Text = item.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        var entry = new MenuItem
        {
            Header = header,
            IsEnabled = item.Enabled,
            Command = command,
            Icon = CommandIcons.Create(item.Command, id => shell.Workbench.ToolWindows.Find(id)?.Icon),
        };
        AutomationProperties.SetName(entry, item.Title);
        if (item.KeyLabel is { } accelerator)
        {
            AutomationProperties.SetAcceleratorKey(entry, accelerator);
        }

        // D25: the command's full name and its shortcut, after a pause, so it doesn't get in the way.
        ToolTip.SetTip(entry, item.KeyLabel is { } shortcut ? $"{item.Description ?? item.Title} ({shortcut})" : item.Description ?? item.Title);
        ToolTip.SetShowDelay(entry, 1200);
        ToolTip.SetPlacement(entry, PlacementMode.RightEdgeAlignedTop);
        return entry;
    }

    private static void BuildNative(NativeMenu native, ShellHost shell)
    {
        native.Items.Clear();
        foreach (var menu in shell.Workbench.Menus.MenuBar)
        {
            var location = MenuLocations.MenuBar(menu.Id);
            var top = new NativeMenuItem(menu.Title.Replace("_", string.Empty, StringComparison.Ordinal)) { Menu = new NativeMenu() };
            FillNative(top.Menu, location, shell);
            top.Menu.Opening += (_, _) => FillNative(top.Menu, location, shell);
            native.Items.Add(top);
        }
    }

    /// <summary>
    /// The macOS menu bar: the same tree as native submenus. Apple's menus carry no command icons (the Human Interface
    /// Guidelines keep them for a few system items), so the native menu shows titles and shortcuts only.
    /// </summary>
    private static void FillNative(NativeMenu menu, string location, ShellHost shell)
    {
        menu.Items.Clear();
        string? group = null;
        foreach (var item in shell.Workbench.Menus.GetItems(location))
        {
            if (group is not null && item.Group != group)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
            }

            group = item.Group;
            if (item.Submenu is { } submenu)
            {
                var child = new NativeMenuItem(item.Title) { Menu = new NativeMenu() };
                FillNative(child.Menu, submenu, shell);
                child.Menu.Opening += (_, _) => FillNative(child.Menu, submenu, shell);
                menu.Items.Add(child);
                continue;
            }

            var binding = shell.Workbench.Keybindings.GetBinding(item.Command);
            menu.Items.Add(new NativeMenuItem(item.Title)
            {
                IsEnabled = item.Enabled,
                Command = shell.CommandFor(item.Command),

                // The native menu can only show single-chord gestures.
                Gesture = binding is { Chords.Count: 1 } ? KeyMapper.ToGesture(binding.Chords[0]) : null,
            });
        }
    }
}
