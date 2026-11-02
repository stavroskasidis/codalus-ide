using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Shell.Docking;
using Codalus.Workbench.Localization;
using Dock.Model.Core;

namespace Codalus.Shell.Controls;

/// <summary>A tab in a <see cref="TabList"/>: its dockable and how its row shows it.</summary>
public sealed record TabListEntry(IDockable Dockable, string Title, string? Icon, bool IsActive, bool IsPreview, string? ToolTip)
{
    /// <summary>The title, which typing in the list searches.</summary>
    public override string ToString() => Title;

    /// <summary>A group's tabs by name (as a classic IDE's tab list), its active one marked.</summary>
    public static List<TabListEntry> For(Dock.Model.Controls.IDocumentDock dock) =>
    [
        .. (dock.VisibleDockables ?? []).OfType<IShellTab>().Cast<IDockable>()
            .Select(d => new TabListEntry(d, d.Title, ((IShellTab)d).TabIcon, ReferenceEquals(d, dock.ActiveDockable), ((IShellTab)d).IsPreview, ((IShellTab)d).TabToolTip))
            .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
    ];
}

/// <summary>
/// A document group's tab list (the strip's <c>…</c>, <see cref="TabStripButtons"/>): every tab of the group by name, the
/// active one bold and selected, with a close button on the row under the pointer or selected. Clicking a row, or Enter,
/// opens its tab (<see cref="Activate"/>); typing selects by name.
/// </summary>
public sealed class TabList : Border
{
    private readonly ListBox _list;

    public TabList()
    {
        _list = new ListBox
        {
            MaxHeight = 560,
            MinWidth = 220,
            IsTextSearchEnabled = true,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<TabListEntry>((entry, _) => entry is null ? new Panel() : Row(entry)),
        };
        _list.Classes.Add("tabList");
        AutomationProperties.SetName(_list, Localizer.Get("msg.tabStripButtons.list", "Open Documents"));
        _list.AddHandler(PointerReleasedEvent, OnReleased, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _list.KeyDown += OnKeyDown;
        Child = _list;
    }

    /// <summary>Opens a tab (a row clicked, or Enter).</summary>
    public event EventHandler<IDockable>? Activate;

    /// <summary>Closes a tab (its row's close button).</summary>
    public event EventHandler<IDockable>? Close;

    /// <summary>The rows shown.</summary>
    public IReadOnlyList<TabListEntry> Entries => _list.ItemsSource as IReadOnlyList<TabListEntry> ?? [];

    /// <summary>The list, for keyboard focus and tests.</summary>
    public ListBox List => _list;

    /// <summary>Shows <paramref name="entries"/>, the active one selected.</summary>
    public void Show(IReadOnlyList<TabListEntry> entries)
    {
        _list.ItemsSource = entries;
        _list.SelectedItem = entries.FirstOrDefault(e => e.IsActive) ?? (entries.Count > 0 ? entries[0] : null);
        if (_list.SelectedItem is { } selected)
        {
            _list.ScrollIntoView(selected);
        }
    }

    private DockPanel Row(TabListEntry entry)
    {
        var close = new Button { Width = 16, Height = 16, MinWidth = 16, MinHeight = 16, Padding = default, Focusable = false, Margin = new Thickness(8, 0, 0, 0) };
        close.Classes.Add("toolbar");
        close.Classes.Add("icon");
        close.Classes.Add("tabListClose");
        ButtonIcon.SetKind(close, "close");
        var closeName = Localizer.Get("msg.tabStripButtons.close", "Close");
        ToolTip.SetTip(close, closeName);
        AutomationProperties.SetName(close, closeName);
        close.Click += (_, e) =>
        {
            e.Handled = true;
            Close?.Invoke(this, entry.Dockable);
        };
        DockPanel.SetDock(close, Avalonia.Controls.Dock.Right);

        var row = new DockPanel { Background = Brushes.Transparent };
        ToolTip.SetTip(row, entry.ToolTip);
        row.Children.Add(close);
        if (entry.Icon is { } icon)
        {
            var image = new Icon { Kind = icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(image, Avalonia.Controls.Dock.Left);
            row.Children.Add(image);
        }

        row.Children.Add(new TextBlock
        {
            Text = entry.Title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = entry.IsActive ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = entry.IsPreview ? FontStyle.Italic : FontStyle.Normal,
        });
        return row;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left
            && e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: TabListEntry entry })
        {
            Activate?.Invoke(this, entry.Dockable);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _list.SelectedItem is TabListEntry entry)
        {
            e.Handled = true;
            Activate?.Invoke(this, entry.Dockable);
        }
        else if (e.Key == Key.Delete && _list.SelectedItem is TabListEntry selected)
        {
            e.Handled = true;
            Close?.Invoke(this, selected.Dockable);
        }
    }
}
