using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views.PropertyEditors;

/// <summary>Small helpers the drop-downs share: themed brushes, captions, automation names.</summary>
internal static class DropDownParts
{
    public static T Named<T>(T control, string name)
        where T : Control
    {
        Avalonia.Automation.AutomationProperties.SetName(control, name);
        return control;
    }

    public static void Brush(Control control, AvaloniaProperty property, string key) => control.Bind(property, control.GetResourceObservable(key));

    public static Border Swatch(Color color, double width, double height)
    {
        var swatch = new Border { Width = width, Height = height, BorderThickness = new Thickness(1), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
        Brush(swatch, Border.BorderBrushProperty, "Shell.Border");
        return swatch;
    }

    public static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        Brush(block, TextBlock.ForegroundProperty, "Shell.MutedForeground");
        return block;
    }
}

/// <summary>
/// A color's drop-down (#68, D41 question 18): Custom (the 48 basic colors and the grid's 16 slots; a right-click on a
/// slot puts Define Color in the grid's place until OK or Cancel), Web and System lists, each a swatch and a name. The
/// three tabs are one size; the tab holding the value opens first; a pick commits the name, or "R, G, B".
/// </summary>
internal sealed class ColorDropDown : Border, IPropertyDropDown
{
    private const double ContentHeight = 208;

    private readonly TabStrip _tabs = new() { Margin = new Thickness(4, 0) };
    private readonly Panel _content = new() { Height = ContentHeight, Width = 214 };
    private readonly ListBox _web = ColorList();
    private readonly ListBox _system = ColorList();
    private readonly WrapPanel _custom = new() { Margin = new Thickness(10, 10, 6, 4), ItemWidth = 24, ItemHeight = 24 };
    private readonly StackPanel _define = new() { Spacing = 6, Margin = new Thickness(10) };
    private readonly Border _defineSpectrum = new() { Height = 62, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
    private readonly Slider _brightness = new() { Minimum = 0, Maximum = 1, Value = 0.5, Height = 24 };
    private readonly TextBox _red = Channel("R");
    private readonly TextBox _green = Channel("G");
    private readonly TextBox _blue = Channel("B");
    private readonly Border _preview = new() { Width = 28, Height = 22, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
    private PropertyRowViewModel? _row;
    private PropertyGridViewModel? _grid;
    private Action? _close;
    private int _defining = -1;
    private bool _syncing;
    private double _hue;
    private double _saturation = 1;

    public ColorDropDown()
    {
        Padding = new Thickness(0, 0, 0, 4);
        foreach (var tab in new[] { PropertyColors.CustomTab, PropertyColors.WebTab, PropertyColors.SystemTab })
        {
            _tabs.Items.Add(new TabStripItem { Content = TabTitle(tab), Tag = tab, FontSize = 12, MinHeight = 28, Padding = new Thickness(9, 0) });
        }

        _tabs.SelectionChanged += (_, _) => ShowTab();
        DropDownParts.Named(_tabs, Localizer.Get("msg.propertyGrid.colorTabs", "Color tabs"));
        foreach (var list in new[] { _web, _system })
        {
            list.AddHandler(PointerReleasedEvent, (_, _) => Pick(list), RoutingStrategies.Bubble);
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Pick(list);
                    e.Handled = true;
                }
            };
        }

        DropDownParts.Named(_web, Localizer.Get("msg.propertyGrid.webColors", "Web colors"));
        DropDownParts.Named(_system, Localizer.Get("msg.propertyGrid.systemColors", "System colors"));
        BuildDefine();
        _content.Children.Add(_custom);
        _content.Children.Add(_web);
        _content.Children.Add(_system);
        _content.Children.Add(_define);
        Child = new StackPanel { Children = { _tabs, _content } };
    }

    private static string TabTitle(string tab) => tab switch
    {
        PropertyColors.CustomTab => Localizer.Get("msg.propertyGrid.customTab", "Custom"),
        PropertyColors.WebTab => Localizer.Get("msg.propertyGrid.webTab", "Web"),
        _ => Localizer.Get("msg.propertyGrid.systemTab", "System"),
    };

    private static ListBox ColorList()
    {
        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(4) };
        PropertyDropDowns.CompactRows(list);
        list.ItemTemplate = new FuncDataTemplate<PropertyColorChoice>((choice, _) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { DropDownParts.Swatch(choice?.Color ?? Colors.Transparent, 22, 12), new TextBlock { Text = choice?.Name, VerticalAlignment = VerticalAlignment.Center } },
        });
        return list;
    }

    private static TextBox Channel(string name) => DropDownParts.Named(new TextBox { Width = 40, MinWidth = 0, Padding = new Thickness(4, 1) }, name);

    /// <summary>The tab shown (tests).</summary>
    public string Tab => (_tabs.SelectedItem as TabStripItem)?.Tag as string ?? PropertyColors.CustomTab;

    /// <summary>Whether Define Color is in the grid's place (tests).</summary>
    public bool IsDefining => _defining >= 0;

    public IReadOnlyList<PropertyColorChoice> WebColors => _web.ItemsSource as IReadOnlyList<PropertyColorChoice> ?? [];

    public IReadOnlyList<PropertyColorChoice> SystemColors => _system.ItemsSource as IReadOnlyList<PropertyColorChoice> ?? [];

    public void Open(PropertyRowViewModel row, PropertyGridViewModel grid, Action close)
    {
        _row = row;
        _grid = grid;
        _close = close;
        _defining = -1;
        var names = row.Options.Select(o => o.Value).ToList();
        _web.ItemsSource = PropertyColors.WebColors(names);
        _system.ItemsSource = PropertyColors.SystemColors(names);
        _web.SelectedItem = WebColors.FirstOrDefault(c => string.Equals(c.Name, row.Value, StringComparison.OrdinalIgnoreCase));
        _system.SelectedItem = SystemColors.FirstOrDefault(c => string.Equals(c.Name, row.Value, StringComparison.OrdinalIgnoreCase));
        BuildCustom();
        var tab = PropertyColors.TabOf(row.Value);
        _tabs.SelectedItem = _tabs.Items.OfType<TabStripItem>().First(t => (string)t.Tag! == tab);
        ShowTab();
    }

    public void SelectTab(string tab) => _tabs.SelectedItem = _tabs.Items.OfType<TabStripItem>().First(t => (string)t.Tag! == tab);

    private void ShowTab()
    {
        var tab = Tab;
        _custom.IsVisible = tab == PropertyColors.CustomTab && _defining < 0;
        _define.IsVisible = tab == PropertyColors.CustomTab && _defining >= 0;
        _web.IsVisible = tab == PropertyColors.WebTab;
        _system.IsVisible = tab == PropertyColors.SystemTab;
        if (_web.IsVisible && _web.SelectedItem is { } web)
        {
            _web.ScrollIntoView(web);
        }

        if (_system.IsVisible && _system.SelectedItem is { } system)
        {
            _system.ScrollIntoView(system);
        }
    }

    private void BuildCustom()
    {
        _custom.Children.Clear();
        var current = _row is null ? null : PropertyRowViewModel.ParseColor(_row.Value);
        foreach (var color in PropertyColors.Basic)
        {
            _custom.Children.Add(Cell(color, -1, current == color));
        }

        for (var slot = 0; slot < PropertyColors.CustomSlots; slot++)
        {
            var color = _grid?.CustomColors[slot];
            _custom.Children.Add(Cell(color, slot, color is { } c && current == c));
        }
    }

    private Button Cell(Color? color, int slot, bool selected)
    {
        var cell = new Button
        {
            Width = 20,
            Height = 20,
            MinWidth = 20,
            MinHeight = 20,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Background = color is { } c ? new SolidColorBrush(c) : Brushes.White,
            Margin = new Thickness(0, slot == 0 || (slot > 0 && slot < 8) ? 6 : 0, 0, 0),
            Tag = slot,
        };
        DropDownParts.Brush(cell, Button.BorderBrushProperty, selected ? "Shell.Focus" : "Shell.Border");
        var name = color is { } named ? PropertyColors.Text(named) : Localizer.Get("msg.propertyGrid.customSlot", "Custom color (right-click to define)");
        DropDownParts.Named(cell, name);
        ToolTip.SetTip(cell, name);
        cell.Click += (_, _) =>
        {
            if (color is { } picked)
            {
                Commit(PropertyColors.Text(picked));
            }
            else if (slot >= 0)
            {
                Define(slot);
            }
        };
        if (slot >= 0)
        {
            cell.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Right)
                {
                    Define(slot);
                    e.Handled = true;
                }
            }, RoutingStrategies.Tunnel);
            cell.ContextRequested += (_, e) =>
            {
                Define(slot);
                e.Handled = true;
            };
        }

        return cell;
    }

    // ------------------------------------------------------------------ Define Color

    private void BuildDefine()
    {
        var title = new TextBlock { Text = Localizer.Get("msg.propertyGrid.defineColor", "Define Color"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var header = new DockPanel { Children = { title } };
        DockPanel.SetDock(title, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(_preview, Avalonia.Controls.Dock.Right);
        header.Children.Insert(0, _preview);
        DropDownParts.Brush(_preview, Border.BorderBrushProperty, "Shell.Border");

        // The spectrum: hue across, saturation down; a click picks both. The slider is the brightness.
        _defineSpectrum.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 1d / 6), new GradientStop(Colors.Lime, 2d / 6), new GradientStop(Colors.Cyan, 3d / 6), new GradientStop(Colors.Blue, 4d / 6), new GradientStop(Colors.Magenta, 5d / 6), new GradientStop(Colors.Red, 1) },
        };
        var fade = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 128, 128, 128), 0), new GradientStop(Color.FromRgb(128, 128, 128), 1) },
            },
        };
        _defineSpectrum.Child = fade;
        DropDownParts.Brush(_defineSpectrum, Border.BorderBrushProperty, "Shell.Border");
        DropDownParts.Named(_defineSpectrum, Localizer.Get("msg.propertyGrid.spectrum", "Hue and saturation"));
        _defineSpectrum.PointerPressed += (_, e) => PickSpectrum(e.GetPosition(_defineSpectrum));
        _defineSpectrum.PointerMoved += (_, e) =>
        {
            if (e.GetCurrentPoint(_defineSpectrum).Properties.IsLeftButtonPressed)
            {
                PickSpectrum(e.GetPosition(_defineSpectrum));
            }
        };
        DropDownParts.Named(_brightness, Localizer.Get("msg.propertyGrid.brightness", "Brightness"));
        _brightness.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncing)
            {
                FromHsb();
            }
        };
        foreach (var box in new[] { _red, _green, _blue })
        {
            box.TextChanged += (_, _) =>
            {
                if (!_syncing)
                {
                    UpdatePreview();
                }
            };
        }

        var channels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (label, box) in new[] { ("R", _red), ("G", _green), ("B", _blue) })
        {
            channels.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(label == "R" ? 0 : 4, 0, 0, 0) });
            channels.Children.Add(box);
        }

        var ok = DropDownParts.Named(new Button { Content = Localizer.Get("msg.propertyGrid.ok", "OK"), MinWidth = 64, Classes = { "accent" } }, Localizer.Get("msg.propertyGrid.ok", "OK"));
        var cancel = DropDownParts.Named(new Button { Content = Localizer.Get("msg.propertyGrid.cancel", "Cancel"), MinWidth = 64 }, Localizer.Get("msg.propertyGrid.cancel", "Cancel"));
        ok.Click += (_, _) => AcceptDefine();
        cancel.Click += (_, _) => EndDefine();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        if (OperatingSystem.IsMacOS())
        {
            buttons.Children.Move(0, 1); // the OS's order: the default button last
        }

        _define.Children.Add(header);
        _define.Children.Add(_defineSpectrum);
        _define.Children.Add(_brightness);
        _define.Children.Add(channels);
        _define.Children.Add(buttons);
    }

    /// <summary>A right-click on a custom slot: Define Color in the grid's place, until OK or Cancel.</summary>
    public void Define(int slot)
    {
        _defining = slot;
        var start = _grid?.CustomColors[slot] ?? (_row is null ? null : PropertyRowViewModel.ParseColor(_row.Value)) ?? Colors.White;
        SetChannels(start);
        ShowTab();
        _red.Focus();
    }

    /// <summary>Define Color's channels (tests).</summary>
    public void SetChannels(Color color)
    {
        _syncing = true;
        _red.Text = color.R.ToString(CultureInfo.InvariantCulture);
        _green.Text = color.G.ToString(CultureInfo.InvariantCulture);
        _blue.Text = color.B.ToString(CultureInfo.InvariantCulture);
        var hsv = color.ToHsv();
        _hue = hsv.H;
        _saturation = hsv.S;
        _brightness.Value = hsv.V;
        _syncing = false;
        UpdatePreview();
    }

    private Color? Channels() =>
        byte.TryParse(_red.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
        && byte.TryParse(_green.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)
        && byte.TryParse(_blue.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
            ? Color.FromRgb(r, g, b)
            : null;

    private void UpdatePreview() => _preview.Background = Channels() is { } color ? new SolidColorBrush(color) : Brushes.Transparent;

    private void PickSpectrum(Point at)
    {
        var width = Math.Max(1, _defineSpectrum.Bounds.Width);
        var height = Math.Max(1, _defineSpectrum.Bounds.Height);
        _hue = Math.Clamp(at.X / width, 0, 1) * 360;
        _saturation = 1 - Math.Clamp(at.Y / height, 0, 1);
        FromHsb();
    }

    private void FromHsb()
    {
        var color = HsvColor.ToRgb(_hue % 360, _saturation, _brightness.Value);
        _syncing = true;
        _red.Text = color.R.ToString(CultureInfo.InvariantCulture);
        _green.Text = color.G.ToString(CultureInfo.InvariantCulture);
        _blue.Text = color.B.ToString(CultureInfo.InvariantCulture);
        _syncing = false;
        UpdatePreview();
    }

    /// <summary>OK: the slot takes the color and the row gets it as "R, G, B".</summary>
    public void AcceptDefine()
    {
        if (_defining >= 0 && Channels() is { } color && _grid is { } grid)
        {
            grid.CustomColors[_defining] = color;
            _defining = -1;
            Commit(PropertyColors.Text(color));
        }
    }

    private void EndDefine()
    {
        _defining = -1;
        BuildCustom();
        ShowTab();
    }

    // ------------------------------------------------------------------ picks

    private void Pick(ListBox list)
    {
        if (list.SelectedItem is PropertyColorChoice choice)
        {
            Commit(choice.Name);
        }
    }

    /// <summary>A pick: the cell takes the name (or "R, G, B") and the drop-down closes.</summary>
    public void Commit(string value)
    {
        _row?.Text = value;
        _close?.Invoke();
    }

    /// <summary>Picks a list's color by name (tests).</summary>
    public void PickNamed(string name)
    {
        var list = Tab == PropertyColors.SystemTab ? _system : _web;
        list.SelectedItem = (list.ItemsSource as IReadOnlyList<PropertyColorChoice>)?.FirstOrDefault(c => c.Name == name);
        Pick(list);
    }

    public void Closed() => _defining = -1;

    public void FocusFirst() => _tabs.Focus();
}

/// <summary>A ContentAlignment's 3×3 picker (#68): nine toggles, the current one checked, each named for screen readers; a pick commits.</summary>
internal sealed class ContentAlignmentDropDown : Border, IPropertyDropDown
{
    private static readonly string[] s_values = ["TopLeft", "TopCenter", "TopRight", "MiddleLeft", "MiddleCenter", "MiddleRight", "BottomLeft", "BottomCenter", "BottomRight"];

    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("40,40,40"), RowDefinitions = new RowDefinitions("28,28,28"), Margin = new Thickness(8) };
    private readonly List<ToggleButton> _cells = [];
    private PropertyRowViewModel? _row;
    private Action? _close;

    public ContentAlignmentDropDown()
    {
        for (var i = 0; i < s_values.Length; i++)
        {
            var value = s_values[i];
            var mark = new Border
            {
                Width = 14,
                Height = 4,
                CornerRadius = new CornerRadius(1),
                HorizontalAlignment = (i % 3) switch { 0 => HorizontalAlignment.Left, 1 => HorizontalAlignment.Center, _ => HorizontalAlignment.Right },
                VerticalAlignment = (i / 3) switch { 0 => VerticalAlignment.Top, 1 => VerticalAlignment.Center, _ => VerticalAlignment.Bottom },
            };
            DropDownParts.Brush(mark, Border.BackgroundProperty, "Shell.Foreground");
            var cell = new ToggleButton
            {
                Content = mark,
                Margin = new Thickness(2),
                Padding = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Tag = value,
            };
            DropDownParts.Named(cell, NameOf(value));
            ToolTip.SetTip(cell, NameOf(value));
            cell.Click += (_, _) => Pick(value);
            Grid.SetColumn(cell, i % 3);
            Grid.SetRow(cell, i / 3);
            _grid.Children.Add(cell);
            _cells.Add(cell);
        }

        Child = _grid;
    }

    private static string NameOf(string value) => value switch
    {
        "TopLeft" => Localizer.Get("msg.propertyGrid.alignTopLeft", "Top Left"),
        "TopCenter" => Localizer.Get("msg.propertyGrid.alignTopCenter", "Top Center"),
        "TopRight" => Localizer.Get("msg.propertyGrid.alignTopRight", "Top Right"),
        "MiddleLeft" => Localizer.Get("msg.propertyGrid.alignMiddleLeft", "Middle Left"),
        "MiddleCenter" => Localizer.Get("msg.propertyGrid.alignMiddleCenter", "Middle Center"),
        "MiddleRight" => Localizer.Get("msg.propertyGrid.alignMiddleRight", "Middle Right"),
        "BottomLeft" => Localizer.Get("msg.propertyGrid.alignBottomLeft", "Bottom Left"),
        "BottomCenter" => Localizer.Get("msg.propertyGrid.alignBottomCenter", "Bottom Center"),
        _ => Localizer.Get("msg.propertyGrid.alignBottomRight", "Bottom Right"),
    };

    public void Open(PropertyRowViewModel row, PropertyGridViewModel grid, Action close)
    {
        _row = row;
        _close = close;
        foreach (var cell in _cells)
        {
            cell.IsChecked = string.Equals((string)cell.Tag!, row.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A pick commits the value's name and closes.</summary>
    public void Pick(string value)
    {
        _row?.Text = value;
        _close?.Invoke();
    }

    public void Closed()
    {
    }

    public void FocusFirst() => (_cells.FirstOrDefault(c => c.IsChecked == true) ?? _cells[4]).Focus();
}

/// <summary>
/// Multi-line text (#68, D41 question 16): a text box that takes Enter, written with CRLF as the classic editor writes
/// it; it commits when it closes or on Ctrl+Enter, and Esc cancels.
/// </summary>
internal sealed class MultilineTextDropDown : Border, IPropertyDropDown
{
    private readonly TextBox _box = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 78, Width = 220, NewLine = "\r\n" };
    private PropertyRowViewModel? _row;
    private Action? _close;
    private bool _cancelled;

    public MultilineTextDropDown()
    {
        Padding = new Thickness(8);
        DropDownParts.Named(_box, Localizer.Get("msg.propertyGrid.multilineText", "Text"));
        _box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _close?.Invoke();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _cancelled = true;
                _close?.Invoke();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        Child = new StackPanel
        {
            Spacing = 6,
            Children = { _box, DropDownParts.Muted(Localizer.Get("msg.propertyGrid.multilineHint", "Enter adds a line · Ctrl+Enter applies · Esc cancels")) },
        };
    }

    /// <summary>The text box (tests).</summary>
    public TextBox Box => _box;

    public void Open(PropertyRowViewModel row, PropertyGridViewModel grid, Action close)
    {
        _row = row;
        _close = close;
        _cancelled = false;
        _box.Text = row.IsMixed ? string.Empty : row.Value;
    }

    /// <summary>Closing commits the text with CRLF, unless Esc cancelled it.</summary>
    public void Closed()
    {
        if (!_cancelled && _row is { } row && PropertyText.WithCrLf(_box.Text ?? string.Empty) is var text && text != row.Value)
        {
            row.Text = text;
        }
    }

    public void FocusFirst()
    {
        _box.Focus();
        _box.CaretIndex = _box.Text?.Length ?? 0;
    }
}

/// <summary>A menu item's shortcut keys (#68): Ctrl, Shift and Alt, a key from KeysConverter's values, and Reset; it commits "Ctrl+Shift+S" when it closes.</summary>
internal sealed class ShortcutKeysDropDown : Border, IPropertyDropDown
{
    private readonly CheckBox _ctrl = new() { Content = "Ctrl" };
    private readonly CheckBox _shift = new() { Content = "Shift" };
    private readonly CheckBox _alt = new() { Content = "Alt" };
    private readonly ComboBox _key = new() { Width = 96 };
    private PropertyRowViewModel? _row;
    private Action? _close;
    private string? _initial;

    public ShortcutKeysDropDown()
    {
        Padding = new Thickness(10);
        DropDownParts.Named(_ctrl, "Ctrl");
        DropDownParts.Named(_shift, "Shift");
        DropDownParts.Named(_alt, "Alt");
        DropDownParts.Named(_key, Localizer.Get("msg.propertyGrid.key", "Key"));
        var reset = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new Controls.Icon { Kind = "undo" }, new TextBlock { Text = Localizer.Get("msg.propertyGrid.reset", "Reset") } } } };
        DropDownParts.Named(reset, Localizer.Get("msg.propertyGrid.reset", "Reset"));
        reset.Click += (_, _) =>
        {
            _row?.Text = "None";
            _initial = "None";
            _close?.Invoke();
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 8, ColumnSpacing = 10 };
        var modifiers = new TextBlock { Text = Localizer.Get("msg.propertyGrid.modifiers", "Modifiers:"), VerticalAlignment = VerticalAlignment.Center };
        var key = new TextBlock { Text = Localizer.Get("msg.propertyGrid.keyLabel", "Key:"), VerticalAlignment = VerticalAlignment.Center };
        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _ctrl, _shift, _alt } };
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _key, reset } };
        Grid.SetColumn(checks, 1);
        Grid.SetRow(key, 1);
        Grid.SetRow(keyRow, 1);
        Grid.SetColumn(keyRow, 1);
        grid.Children.Add(modifiers);
        grid.Children.Add(checks);
        grid.Children.Add(key);
        grid.Children.Add(keyRow);
        Child = grid;
    }

    /// <summary>The value the drop-down would commit now.</summary>
    public string Composed => ShortcutKeysText.Compose(_ctrl.IsChecked == true, _shift.IsChecked == true, _alt.IsChecked == true, _key.SelectedItem as string);

    /// <summary>Sets the modifiers and the key (tests).</summary>
    public void Set(bool ctrl, bool shift, bool alt, string key)
    {
        _ctrl.IsChecked = ctrl;
        _shift.IsChecked = shift;
        _alt.IsChecked = alt;
        _key.SelectedItem = (_key.ItemsSource as IReadOnlyList<string>)?.FirstOrDefault(k => k == key) ?? key;
    }

    public void Open(PropertyRowViewModel row, PropertyGridViewModel grid, Action close)
    {
        _row = row;
        _close = close;
        List<string> keys = [.. row.Options.Select(o => o.Value).Where(k => k is not ("None" or "Control" or "Shift" or "Alt"))];
        var (ctrl, shift, alt, key) = ShortcutKeysText.Parse(row.Value);
        if (key != "None" && !keys.Contains(key))
        {
            keys.Insert(0, key);
        }

        _key.ItemsSource = keys;
        _ctrl.IsChecked = ctrl;
        _shift.IsChecked = shift;
        _alt.IsChecked = alt;
        _key.SelectedItem = key == "None" ? null : key;
        _initial = Composed;
    }

    public void Closed()
    {
        if (_row is { } row && Composed is var composed && composed != _initial)
        {
            row.Text = composed;
        }
    }

    public void FocusFirst() => _ctrl.Focus();
}

/// <summary>
/// A date (#68, D41 question 20): a month calendar under the cell, the value's day selected, the days of the months
/// around it muted, and Today below; picking a day commits it with the value's time, and the cell still takes typing.
/// Drawn here at the grid's size (26 px days, 22 px rows) rather than as the theme's full-size calendar.
/// </summary>
internal sealed class DateDropDown : Border, IPropertyDropDown
{
    private readonly TextBlock _month = new() { FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly UniformGrid _days = new() { Columns = 7, Rows = 7 };
    private readonly Button _today = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 3) };
    private PropertyRowViewModel? _row;
    private Action? _close;
    private DateTime _shown = DateTime.Today;
    private DateTime? _selected;

    public DateDropDown()
    {
        Padding = new Thickness(6);
        Width = 196;
        Button Arrow(string icon, string name, int months)
        {
            var button = DropDownParts.Named(new Button { Content = new Controls.Icon { Kind = icon }, Classes = { "toolbar" }, Padding = new Thickness(2) }, name);
            button.Click += (_, _) =>
            {
                _shown = _shown.AddMonths(months);
                Draw();
            };
            return button;
        }

        var previous = Arrow("chevron-left", Localizer.Get("msg.propertyGrid.previousMonth", "Previous month"), -1);
        var next = Arrow("chevron-right", Localizer.Get("msg.propertyGrid.nextMonth", "Next month"), 1);
        var header = new DockPanel { Height = 26, Margin = new Thickness(0, 0, 0, 2), Children = { previous, next, _month } };
        DockPanel.SetDock(previous, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(next, Avalonia.Controls.Dock.Right);
        DropDownParts.Named(_days, Localizer.Get("msg.propertyGrid.calendar", "Calendar"));
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 4, 0, 0), Child = _today };
        DropDownParts.Brush(footer, Border.BorderBrushProperty, "Shell.Border");
        _today.Click += (_, _) => Pick(DateTime.Today);
        Child = new StackPanel { Children = { header, _days, footer } };
    }

    private void Draw()
    {
        _month.Text = _shown.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        _days.Children.Clear();
        var culture = CultureInfo.CurrentCulture;
        var first = culture.DateTimeFormat.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
        {
            var day = DropDownParts.Muted(culture.DateTimeFormat.ShortestDayNames[((int)first + i) % 7]);
            day.HorizontalAlignment = HorizontalAlignment.Center;
            day.VerticalAlignment = VerticalAlignment.Center;
            day.TextWrapping = TextWrapping.NoWrap;
            _days.Children.Add(new Border { Height = 22, Child = day });
        }

        var start = new DateTime(_shown.Year, _shown.Month, 1);
        start = start.AddDays(-(((int)start.DayOfWeek - (int)first + 7) % 7));
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var selected = _selected?.Date == date;
            var cell = new Button
            {
                Content = date.Day.ToString(CultureInfo.CurrentCulture),
                Height = 22,
                MinHeight = 22,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Classes = { selected ? "accent" : "toolbar" },
                FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
                Tag = date,
            };
            if (date.Month != _shown.Month)
            {
                DropDownParts.Brush(cell, Button.ForegroundProperty, "Shell.MutedForeground");
            }

            DropDownParts.Named(cell, date.ToString("D", CultureInfo.CurrentCulture));
            cell.Click += (_, _) => Pick(date);
            _days.Children.Add(cell);
        }
    }

    public void Open(PropertyRowViewModel row, PropertyGridViewModel grid, Action close)
    {
        _row = row;
        _close = close;
        _selected = PropertyDates.Parse(row.Value);
        _shown = _selected ?? DateTime.Today;
        var today = Localizer.Format("msg.propertyGrid.today", "Today: {0}", DateTime.Today.ToString("d", CultureInfo.CurrentCulture));
        _today.Content = today;
        DropDownParts.Named(_today, today);
        Draw();
    }

    /// <summary>A day picked: committed with the value's time; the drop-down closes.</summary>
    public void Pick(DateTime day)
    {
        if (_row is { } row)
        {
            row.Text = PropertyDates.WithDay(row.Value, day);
        }

        _close?.Invoke();
    }

    public void Closed()
    {
    }

    public void FocusFirst() => (_days.Children.OfType<Button>().FirstOrDefault(b => b.Tag is DateTime d && d == _selected?.Date) ?? _days.Children.OfType<Button>().FirstOrDefault())?.Focus();
}
