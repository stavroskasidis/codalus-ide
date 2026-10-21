using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Codalus.Shell.Controls;

/// <summary>
/// One look for every menu entry (the maintainer's 0.5 review: the Git Changes window's menu had no icons and didn't line
/// up with the others). The command registry's entries (<see cref="Views.MenuBuilder"/>) and a view's own are made the
/// same way: the icon in its palette color, the title, and the shortcut on the right, muted. In XAML a view gives an
/// entry its icon with <c>controls:MenuEntry.Icon="compare"</c>; in code it makes the entry with <see cref="Create"/>.
/// ShellStyles.axaml keeps the 16 px icon column on every entry, so text lines up whether an entry has an icon or not,
/// 24 px rows, and separators that start where the text does, as Visual Studio's menus do.
/// </summary>
public static class MenuEntry
{
    /// <summary>The entry's icon: an <see cref="Controls.Icon"/> kind.</summary>
    public static readonly AttachedProperty<string?> IconProperty = AvaloniaProperty.RegisterAttached<MenuItem, string?>("Icon", typeof(MenuEntry));

    /// <summary>The entry's icon: the one the menus give this command (<see cref="CommandIcons"/>), in its color.</summary>
    public static readonly AttachedProperty<string?> CommandIconProperty = AvaloniaProperty.RegisterAttached<MenuItem, string?>("CommandIcon", typeof(MenuEntry));

    static MenuEntry()
    {
        IconProperty.Changed.AddClassHandler<MenuItem>((item, e) => item.Icon = e.NewValue is string kind ? IconOf(kind) : null);
        CommandIconProperty.Changed.AddClassHandler<MenuItem>((item, e) => item.Icon = e.NewValue is string command ? CommandIcons.Create(command) : null);
    }

    public static string? GetIcon(MenuItem item) => item.GetValue(IconProperty);

    public static void SetIcon(MenuItem item, string? value) => item.SetValue(IconProperty, value);

    public static string? GetCommandIcon(MenuItem item) => item.GetValue(CommandIconProperty);

    public static void SetCommandIcon(MenuItem item, string? value) => item.SetValue(CommandIconProperty, value);

    /// <summary>A menu's 16 px icon of <paramref name="kind"/>, in its palette color.</summary>
    public static Icon IconOf(string kind) => new() { Kind = kind, Width = 16, Height = 16 };

    /// <summary>
    /// A view's menu entry: <paramref name="title"/> with its <paramref name="icon"/> (a kind, or null for none) and its
    /// shortcut (<paramref name="keys"/>, as the menus write it), running <paramref name="command"/>, or
    /// <paramref name="click"/> when it's chosen.
    /// </summary>
    public static MenuItem Create(string title, string? icon, ICommand? command = null, object? parameter = null, string? keys = null, Action? click = null)
    {
        var item = new MenuItem
        {
            Header = keys is null ? title : Header(title, keys),
            Icon = icon is null ? null : IconOf(icon),
            Command = command,
            CommandParameter = parameter,
        };
        AutomationProperties.SetName(item, title);
        if (keys is not null)
        {
            AutomationProperties.SetAcceleratorKey(item, keys);
        }

        if (click is not null)
        {
            item.Click += (_, _) => click();
        }

        return item;
    }

    /// <summary>A submenu: <paramref name="title"/> with its <paramref name="icon"/> (or none), and <paramref name="items"/>.</summary>
    public static MenuItem Submenu(string title, string? icon, params IEnumerable<object> items)
    {
        var menu = Create(title, icon);
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>A muted line naming the entries under it (Implementations, Overrides…); it can't be chosen.</summary>
    public static MenuItem Caption(string title)
    {
        var item = Create(title, null);
        item.IsEnabled = false;
        return item;
    }

    /// <summary>An entry's header: its title, and its shortcut on the right in muted text.</summary>
    public static Control Header(string title, string? keys)
    {
        var header = new DockPanel { LastChildFill = true };
        if (keys is not null)
        {
            var label = new TextBlock { Text = keys, Margin = new Thickness(32, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            label.Classes.Add("muted");
            DockPanel.SetDock(label, Avalonia.Controls.Dock.Right);
            header.Children.Add(label);
        }

        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        return header;
    }
}
