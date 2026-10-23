using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Input;
using Codalus.Workbench.Menus;
using MenuItem = Avalonia.Controls.MenuItem;

namespace Codalus.Shell.Views;

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
        // A document tab's context menu is ours (M34e): the tab becomes the active document, then the menu opens,
        // before Dock's own (tunneling, so it sees the request first).
        window.AddHandler(Control.ContextRequestedEvent, (_, e) => OpenDocumentTabMenu(shell, e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (shell.Workbench.Platform == PlatformKind.MacOS)
        {
            menuBar.IsVisible = false;
            AttachApplicationMenu(shell);
            window.Opened += (_, _) => AttachApplicationMenu(shell); // again, should Avalonia install its default after us
            var native = new NativeMenu();
            NativeMenu.SetMenu(window, native);
            var nativeShown = string.Empty;
            void RebuildNative() => nativeShown = BuildNative(native, shell);
            RebuildNative();
            menus.Changed += (_, _) => Ui.Post(RebuildNative);
            RebuildOnActiveDocument(shell, () => nativeShown, RebuildNative);
            return;
        }

        var shown = string.Empty;
        void Rebuild() => shown = Build(menuBar, shell);
        Rebuild();
        menus.Changed += (_, _) => Ui.Post(Rebuild);
        RebuildOnActiveDocument(shell, () => shown, Rebuild);
    }

    /// <summary>
    /// Menus whose items all depend on the active document (the Windows Forms designer's Format menu) come and go with it:
    /// the bar is rebuilt when the active document's kind changes the menus it shows, and only then (their items are
    /// filled as each menu opens). Rebuilt at every change of kind, opening the WCF Test Client from the code editor
    /// spent 200 ms of its 220 ms on the UI thread rebuilding a bar that showed the same menus.
    /// </summary>
    private static void RebuildOnActiveDocument(ShellHost shell, Func<string> built, Action rebuild) =>
        shell.Workbench.Context.Changed += (_, key) =>
        {
            if (key == "activeDocumentKind")
            {
                Ui.Post(() =>
                {
                    if (Shown(shell) != built())
                    {
                        rebuild();
                    }
                });
            }
        };

    /// <summary>The ids of the menu bar's menus that have items in the current context.</summary>
    private static string Shown(ShellHost shell) =>
        string.Join('\n', shell.Workbench.Menus.MenuBar.Where(m => shell.Workbench.Menus.GetItems(MenuLocations.MenuBar(m.Id)).Count > 0).Select(m => m.Id));

    private static void OpenDocumentTabMenu(ShellHost shell, ContextRequestedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual source
            || source.FindAncestorOfType<Dock.Avalonia.Controls.DocumentTabStripItem>(includeSelf: true) is not { DataContext: Docking.ShellDocument document } tab)
        {
            return;
        }

        e.Handled = true;
        shell.Layout.OpenDocument(document, focus: true);
        if (Context(shell, MenuLocations.DocumentTabContext) is { } menu)
        {
            menu.Open(tab);
        }
    }

    /// <summary>
    /// macOS's application menu: our About and Settings entries before the ones the system adds (Services, Hide, Quit).
    /// Without a menu of its own, Avalonia's default shows "About Avalonia" (found on the Mac).
    /// </summary>
    private static void AttachApplicationMenu(ShellHost shell)
    {
        if (Avalonia.Application.Current is not { } app)
        {
            return;
        }

        // Avalonia may have installed its default (About Avalonia, Hide Others on ⌥⌘Q) already: edit that one, so the
        // system's own entries stay, or make ours (found on the Mac: skipping when a menu existed left Avalonia's).
        var menu = NativeMenu.GetMenu(app);
        if (menu is null)
        {
            menu = new NativeMenu();
            NativeMenu.SetMenu(app, menu);
        }

        foreach (var item in menu.Items.OfType<NativeMenuItem>().ToList())
        {
            if (item.Header?.StartsWith("About ", StringComparison.Ordinal) == true || item.Header == "Settings…")
            {
                menu.Items.Remove(item);
            }
            else if (item.Header == "Hide Others")
            {
                item.Gesture = new KeyGesture(Key.H, Avalonia.Input.KeyModifiers.Meta | Avalonia.Input.KeyModifiers.Alt); // macOS's ⌥⌘H
            }
        }

        // macOS adds its own separator before Services: none of ours at the end of our part (the Mac showed two in a row).
        while (menu.Items.Count > 0 && menu.Items[0] is NativeMenuItemSeparator)
        {
            menu.Items.RemoveAt(0);
        }

        menu.Items.Insert(0, new NativeMenuItem("About " + ShellInfo.ProductName) { Command = shell.CommandFor("help.about") });
        menu.Items.Insert(1, new NativeMenuItemSeparator());
        menu.Items.Insert(2, new NativeMenuItem("Settings…") { Command = shell.CommandFor("workbench.openOptions"), Gesture = new KeyGesture(Key.OemComma, Avalonia.Input.KeyModifiers.Meta) });
    }

    /// <summary>
    /// A context menu of <paramref name="location"/>'s items, or null when none is visible. <paramref name="commandFor"/>
    /// makes each entry's command (the editor passes the document and caret); <paramref name="titleFor"/> gives an entry a
    /// title of the moment (the Windows Forms designer's "Select 'groupBox1'"), or null to keep its own.
    /// </summary>
    public static ContextMenu? Context(ShellHost shell, string location, Func<string, ICommand>? commandFor = null, Func<string, string?>? titleFor = null) =>
        Context(shell, location, new MenuShape(commandFor ?? (c => shell.CommandFor(c))) { TitleFor = titleFor is null ? null : item => titleFor(item.Command) });

    /// <summary>
    /// A context menu of <paramref name="location"/>'s items in <paramref name="shape"/>'s scope (keys set for this menu
    /// only, D38), with the titles and keys it gives; null when none is visible.
    /// </summary>
    public static ContextMenu? Context(ShellHost shell, string location, MenuShape shape)
    {
        var scope = shape.Scope ?? shell.Workbench.Context;
        if (shell.Workbench.Menus.GetItems(location, scope).Count == 0)
        {
            return null;
        }

        var menu = new ContextMenu();
        Fill(menu.Items, location, shell, shape with { Scope = scope });
        return menu;
    }

    /// <summary>
    /// How a context menu is made: each entry's command (the editor passes the document and caret), the context its
    /// when-clauses read (the global context with a menu's own keys, D38), and, for menus whose entries pass arguments,
    /// each entry's title (<c>Merge 'x' into 'y'</c>) and keys (only the rule's own: D38, question 13).
    /// </summary>
    public sealed record MenuShape(Func<string, ICommand> CommandFor)
    {
        public Workbench.Context.IContextKeyReader? Scope { get; init; }

        public Func<Workbench.Menus.MenuItem, string?>? TitleFor { get; init; }

        public Func<Workbench.Menus.MenuItem, string?>? KeyFor { get; init; }
    }

    /// <summary>Builds the in-window menu bar; returns the ids of the menus it shows (<see cref="Shown"/>).</summary>
    private static string Build(Menu menuBar, ShellHost shell)
    {
        menuBar.Items.Clear();
        var shown = new List<string>();
        foreach (var menu in shell.Workbench.Menus.MenuBar)
        {
            var location = MenuLocations.MenuBar(menu.Id);
            if (shell.Workbench.Menus.GetItems(location).Count == 0)
            {
                continue;
            }

            shown.Add(menu.Id);
            var top = new MenuItem { Header = menu.Title };
            AutomationProperties.SetName(top, menu.Title.Replace("_", string.Empty, StringComparison.Ordinal));

            // Its items are made as it opens, in the context of then: until then a placeholder makes it a menu that opens.
            // Making every menu's entries here (about 600, each with its icon and tooltip) took 200 ms of the UI thread.
            top.Items.Add(new Separator());
            top.SubmenuOpened += (_, e) =>
            {
                if (e.Source == top)
                {
                    Fill(top.Items, location, shell, new MenuShape(c => shell.CommandFor(c)));
                }
            };
            menuBar.Items.Add(top);
        }

        return string.Join('\n', shown);
    }

    private static void Fill(ItemCollection items, string location, ShellHost shell, MenuShape shape)
    {
        items.Clear();
        string? group = null;
        foreach (var listed in shape.Scope is { } scope ? shell.Workbench.Menus.GetItems(location, scope) : shell.Workbench.Menus.GetItems(location))
        {
            var item = shape.TitleFor?.Invoke(listed) is { } title ? listed with { Title = title } : listed;
            if (shape.KeyFor is { } keyFor)
            {
                item = item with { KeyLabel = keyFor(listed) };
            }

            if (group is not null && item.Group != group)
            {
                items.Add(new Separator());
            }

            group = item.Group;
            if (item.Submenu is { } submenu)
            {
                var child = new MenuItem { Header = item.Title };
                AutomationProperties.SetName(child, item.Title);
                Fill(child.Items, submenu, shell, shape);
                items.Add(child);
                continue;
            }

            items.Add(Entry(item, shell, shape.CommandFor(item.Command)));
        }
    }

    /// <summary>
    /// An entry: its icon, title, shortcut and tooltip. A check entry has no icon, checked or not: the check takes the icon's
    /// column (ui-guidelines.md's menus; the designer's Lock Controls, D40).
    /// </summary>
    private static MenuItem Entry(Workbench.Menus.MenuItem item, ShellHost shell, ICommand command)
    {
        var entry = new MenuItem
        {
            Header = Controls.MenuEntry.Header(item.Title, item.KeyLabel),
            IsEnabled = item.Enabled,
            Command = command,
            Icon = item.IsChecked is null ? CommandIcons.Create(item.Command, id => shell.Workbench.ToolWindows.Find(id)?.Icon) : null,
        };
        if (item.IsChecked is { } isChecked)
        {
            entry.ToggleType = MenuItemToggleType.CheckBox;
            entry.IsChecked = isChecked;
        }

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

    private static string BuildNative(NativeMenu native, ShellHost shell)
    {
        native.Items.Clear();
        var shown = new List<string>();
        foreach (var menu in shell.Workbench.Menus.MenuBar)
        {
            var location = MenuLocations.MenuBar(menu.Id);
            if (shell.Workbench.Menus.GetItems(location).Count == 0)
            {
                continue;
            }

            shown.Add(menu.Id);

            var top = new NativeMenuItem(menu.Title.Replace("_", string.Empty, StringComparison.Ordinal)) { Menu = new NativeMenu() };
            FillNative(top.Menu, location, shell);
            top.Menu.Opening += (_, _) => FillNative(top.Menu, location, shell);
            native.Items.Add(top);
        }

        return string.Join('\n', shown);
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

            // The binding, or the keys the controls handle themselves (Edit's ⌘Z, ⌘C…), which macOS users expect shown.
            var binding = item.Keys;
            menu.Items.Add(new NativeMenuItem(item.Title)
            {
                IsEnabled = item.Enabled,
                Command = shell.CommandFor(item.Command),

                // The native menu can only show single-chord gestures.
                Gesture = binding is { Chords.Count: 1 } ? KeyMapper.ToGesture(binding.Chords[0]) : null,

                // A check entry (Format → Lock Controls) as macOS draws one.
                ToggleType = item.IsChecked is null ? MenuItemToggleType.None : MenuItemToggleType.CheckBox,
                IsChecked = item.IsChecked == true,
            });
        }
    }
}
