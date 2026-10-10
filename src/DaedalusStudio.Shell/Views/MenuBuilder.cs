using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using DaedalusStudio.Platform;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.Input;
using DaedalusStudio.Workbench.Menus;
using MenuItem = Avalonia.Controls.MenuItem;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// Renders the workbench menu model: an in-window menu bar on Windows and Linux, the global menu bar
/// (<see cref="NativeMenu"/>) on macOS. Items are rebuilt each time a menu opens, so enablement and
/// when-clauses reflect the current context.
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
            Fill(top, location, shell);
            top.SubmenuOpened += (_, e) =>
            {
                if (e.Source == top)
                {
                    Fill(top, location, shell);
                }
            };
            menuBar.Items.Add(top);
        }
    }

    private static void Fill(MenuItem top, string location, ShellHost shell)
    {
        top.Items.Clear();
        string? group = null;
        foreach (var item in shell.Workbench.Menus.GetItems(location))
        {
            if (group is not null && item.Group != group)
            {
                top.Items.Add(new Separator());
            }

            group = item.Group;
            if (item.Submenu is { } submenu)
            {
                var child = new MenuItem { Header = item.Title };
                AutomationProperties.SetName(child, item.Title);
                Fill(child, submenu, shell);
                top.Items.Add(child);
                continue;
            }

            var header = new DockPanel { LastChildFill = true };
            if (item.KeyLabel is { } keys)
            {
                var label = new TextBlock { Text = keys, Margin = new Avalonia.Thickness(32, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                label.Classes.Add("muted");
                DockPanel.SetDock(label, Avalonia.Controls.Dock.Right);
                header.Children.Add(label);
            }

            header.Children.Add(new TextBlock { Text = item.Title, VerticalAlignment = VerticalAlignment.Center });
            var entry = new MenuItem { Header = header, IsEnabled = item.Enabled, Command = shell.CommandFor(item.Command) };
            AutomationProperties.SetName(entry, item.Title);
            if (item.KeyLabel is { } accelerator)
            {
                AutomationProperties.SetAcceleratorKey(entry, accelerator);
            }

            top.Items.Add(entry);
        }
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
