using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views.PropertyEditors;

/// <summary>A row's drop-down (#68): one control per kind and grid, given the row each time it opens.</summary>
internal interface IPropertyDropDown
{
    /// <summary>Shows <paramref name="row"/>'s value; <paramref name="close"/> closes the drop-down (after a pick).</summary>
    void Open(PropertyRowViewModel row, PropertyGridViewModel grid, Action close);

    /// <summary>The drop-down closed (light dismiss, Esc, a pick): an editor that commits on close commits here.</summary>
    void Closed();

    void FocusFirst();
}

/// <summary>The drop-downs' kinds and their factory: a control is made the first time a grid opens one of its kind.</summary>
internal static class PropertyDropDowns
{
    /// <summary>A drop-down's list: 20 px rows in the UI font, as the mockup draws them (the shell's lists are taller).</summary>
    public static void CompactRows(ListBox list)
    {
        list.FontSize = 12;
        list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(6, 0)),
                new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 20d),
                new Avalonia.Styling.Setter(ListBoxItem.HeightProperty, 22d),
            },
        });
    }

    public static string KindOf(PropertyRowViewModel row) => row.Kind switch
    {
        PropertyRowKind.Dock or PropertyRowKind.Anchor => "dockAnchor",
        PropertyRowKind.Flags => "flags",
        PropertyRowKind.Color => "color",
        PropertyRowKind.ContentAlignment => "alignment",
        PropertyRowKind.Multiline => "multiline",
        PropertyRowKind.ShortcutKeys => "shortcutKeys",
        PropertyRowKind.Date => "date",
        _ => "choice",
    };

    public static Control Create(string kind) => kind switch
    {
        "dockAnchor" => new DockAnchorDropDown(),
        "flags" => new FlagsDropDown(),
        "color" => new ColorDropDown(),
        "alignment" => new ContentAlignmentDropDown(),
        "multiline" => new MultilineTextDropDown(),
        "shortcutKeys" => new ShortcutKeysDropDown(),
        "date" => new DateDropDown(),
        _ => new ChoiceDropDown(),
    };
}

/// <summary>A choice's list (#68): an exclusive list (AcceptButton's "(none)" and buttons) or one under a cell that takes typing (Cursor). A pick commits and closes.</summary>
internal sealed class ChoiceDropDown : Border, IPropertyDropDown
{
    private readonly ListBox _list = new() { MaxHeight = 224, MinWidth = 120, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private PropertyRowViewModel? _row;
    private Action? _close;

    public ChoiceDropDown()
    {
        Padding = new Thickness(4);
        PropertyDropDowns.CompactRows(_list);
        _list.ItemTemplate = new FuncDataTemplate<PropertyOption>((option, _) => Item(option));
        _list.AddHandler(PointerReleasedEvent, (_, _) => Pick(), Avalonia.Interactivity.RoutingStrategies.Bubble);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Pick();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _close?.Invoke();
                e.Handled = true;
            }
        };
        Child = _list;
        Avalonia.Automation.AutomationProperties.SetName(_list, Workbench.Localization.Localizer.Get("msg.propertyGrid.choices", "Choices"));
    }

    /// <summary>The list (tests).</summary>
    public ListBox List => _list;

    private Control Item(PropertyOption? option)
    {
        var text = new TextBlock { Text = option?.Label, VerticalAlignment = VerticalAlignment.Center };
        if (_row is { IsCursor: true } && option is not null)
        {
            // Each cursor beside its picture (D41 question 13).
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new CursorPicture { CursorName = option.Value, VerticalAlignment = VerticalAlignment.Center }, text } };
        }

        return text;
    }

    public void Open(PropertyRowViewModel row, PropertyGridViewModel grid, Action close)
    {
        _row = row;
        _close = close;
        _list.ItemsSource = null;
        _list.ItemsSource = row.Options;
        _list.SelectedItem = row.SelectedOption ?? row.Options.FirstOrDefault(o => string.Equals(o.Value, row.Text, StringComparison.OrdinalIgnoreCase));
        if (_list.SelectedItem is { } selected)
        {
            _list.ScrollIntoView(selected);
        }
    }

    /// <summary>Commits the list's selection and closes (a click, Enter).</summary>
    public void Pick()
    {
        if (_row is { } row && _list.SelectedItem is PropertyOption option)
        {
            if (row.IsTextEditor)
            {
                row.Text = option.Value;
            }
            else
            {
                row.SelectedOption = option;
            }
        }

        _close?.Invoke();
    }

    public void Closed()
    {
    }

    public void FocusFirst() => (_list.ContainerFromItem(_list.SelectedItem ?? new object()) ?? (Control)_list).Focus();
}

/// <summary>A flags enum's names, each a check box; a change commits the checked names ("None" when none is).</summary>
internal sealed class FlagsDropDown : Border, IPropertyDropDown
{
    private readonly StackPanel _panel = new() { Spacing = 2, Margin = new Thickness(8, 6) };

    public FlagsDropDown()
    {
        Child = new ScrollViewer { Content = _panel, MaxHeight = 260 };
    }

    public void Open(PropertyRowViewModel row, PropertyGridViewModel grid, Action close)
    {
        _panel.Children.Clear();
        foreach (var flag in row.Flags)
        {
            var box = new CheckBox { Content = flag.Name, IsChecked = flag.IsChecked };
            box.IsCheckedChanged += (_, _) => flag.IsChecked = box.IsChecked == true;
            Avalonia.Automation.AutomationProperties.SetName(box, flag.Name);
            _panel.Children.Add(box);
        }
    }

    public void Closed()
    {
    }

    public void FocusFirst() => _panel.Children.FirstOrDefault()?.Focus();
}
