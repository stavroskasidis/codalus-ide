using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Codalus.Shell.Controls;

/// <summary>
/// A tool window's toolbar row (M34d): one line that never wraps. Its items keep their order from the left; a text box
/// (a search or filter box) takes the width that is left, down to <see cref="MinStretchWidth"/>. When the pane is too
/// narrow, the items that don't fit move into the overflow menu at the right end (»): buttons become entries with
/// their names, toggle buttons checkable entries, combo boxes submenus of their choices. Text boxes and labels never
/// overflow.
/// </summary>
public sealed class ToolbarPanel : Panel
{
    /// <summary>The narrowest a stretching text box gets before the items before it overflow.</summary>
    public static readonly StyledProperty<double> MinStretchWidth =
        AvaloniaProperty.Register<ToolbarPanel, double>(nameof(MinStretchWidth), 120);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ToolbarPanel, double>(nameof(Spacing), 2);

    private readonly Button _overflow;
    private readonly HashSet<Control> _hidden = [];

    /// <summary>Hidden items this panel took out of the keyboard's tab order (and gives back when they show).</summary>
    private readonly HashSet<Control> _untabbed = [];

    static ToolbarPanel()
    {
        AffectsMeasure<ToolbarPanel>(SpacingProperty, MinStretchWidth);
    }

    public ToolbarPanel()
    {
        _overflow = new Button
        {
            Content = new Icon { Kind = "chevron-double-right" },
            IsVisible = false,
        };
        _overflow.Classes.Add("toolbar");
        _overflow.Classes.Add("overflow");
        ToolTip.SetTip(_overflow, "More");
        AutomationProperties.SetName(_overflow, "More toolbar commands");
        _overflow.Click += (_, _) => ShowOverflow();
        // It is the panel's first child but sits at the right end: last in the tab order too, so focusing a tool window
        // doesn't land on it (its focus border showed in the gallery on the Mac).
        Avalonia.Input.KeyboardNavigation.SetTabIndex(_overflow, int.MaxValue);
        Children.Add(_overflow); // XAML adds the items after it; it is always arranged at the right end
        ClipToBounds = true;
        VerticalAlignment = VerticalAlignment.Top;
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double MinStretch
    {
        get => GetValue(MinStretchWidth);
        set => SetValue(MinStretchWidth, value);
    }

    /// <summary>The items in the overflow menu (tests).</summary>
    public IReadOnlyCollection<Control> Overflowed => _hidden;

    private IEnumerable<Control> Items => Children.Where(c => c != _overflow && c.IsVisible);

    private static bool Stretches(Control child) => child is TextBox || child is AutoCompleteBox;

    private static bool CanOverflow(Control child) => child is Button or ToggleButton or ComboBox or SegmentedControl or Separator || child.Classes.Contains("toolbarSeparator");

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items.ToList();
        var height = 0.0;
        foreach (var child in items)
        {
            child.Measure(Size.Infinity);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        _overflow.Measure(Size.Infinity);
        var width = items.Sum(c => c.DesiredSize.Width) + Spacing * Math.Max(0, items.Count - 1);
        if (!double.IsInfinity(availableSize.Width))
        {
            width = Math.Min(width, availableSize.Width);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var items = Items.ToList();
        var stretch = items.LastOrDefault(Stretches);
        var widths = items.Select(c => Stretches(c) ? Math.Max(MinStretch, Math.Min(c.DesiredSize.Width, 200)) : c.DesiredSize.Width).ToList();
        var total = widths.Sum() + Spacing * Math.Max(0, items.Count - 1);

        // Hide overflowable items from the right until the rest fits next to the overflow button.
        _hidden.Clear();
        if (total > finalSize.Width + 0.5)
        {
            var available = finalSize.Width - _overflow.DesiredSize.Width - Spacing;
            for (var i = items.Count - 1; i >= 0 && total > available + 0.5; i--)
            {
                if (CanOverflow(items[i]))
                {
                    _hidden.Add(items[i]);
                    total -= widths[i] + Spacing;
                }
            }
        }

        // Separators next to the overflow or to another hidden item would be stray lines.
        var shown = items.Where(c => !_hidden.Contains(c)).ToList();
        while (shown.Count > 0 && IsSeparator(shown[^1]))
        {
            _hidden.Add(shown[^1]);
            shown.RemoveAt(shown.Count - 1);
        }

        var reserved = _hidden.Count > 0 ? _overflow.DesiredSize.Width + Spacing : 0;
        var used = shown.Where(c => c != stretch).Sum(c => c.DesiredSize.Width) + Spacing * Math.Max(0, shown.Count - 1);
        var x = 0.0;
        foreach (var child in items)
        {
            if (_hidden.Contains(child))
            {
                // Outside the panel, which clips: not drawn, not hit, and its minimum size can't overlap the others.
                child.Arrange(new Rect(finalSize.Width + 10000, 0, child.DesiredSize.Width, child.DesiredSize.Height));
                // Nor reached with Tab (M34e): the overflow menu has it.
                if (_untabbed.Add(child))
                {
                    Avalonia.Input.KeyboardNavigation.SetIsTabStop(child, false);
                }

                continue;
            }

            if (_untabbed.Remove(child))
            {
                child.ClearValue(Avalonia.Input.KeyboardNavigation.IsTabStopProperty);
            }

            var w = child == stretch ? Math.Max(0, finalSize.Width - reserved - used) : child.DesiredSize.Width;
            var h = Math.Min(finalSize.Height, child.DesiredSize.Height);
            child.Arrange(new Rect(x, (finalSize.Height - h) / 2, w, h));
            x += w + Spacing;
        }

        _overflow.IsVisible = _hidden.Count > 0;
        if (_overflow.IsVisible)
        {
            var size = _overflow.DesiredSize;
            _overflow.Arrange(new Rect(finalSize.Width - size.Width, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
        }

        return finalSize;
    }

    private static bool IsSeparator(Control child) => child is Separator || child.Classes.Contains("toolbarSeparator");

    private void ShowOverflow()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var child in Children.Where(_hidden.Contains))
        {
            if (Entry(child, _overflow) is { } entry)
            {
                menu.Items.Add(entry);
            }
        }

        menu.ShowAt(_overflow);
    }

    /// <summary>The overflow menu's entry for a hidden toolbar item.</summary>
    internal static Control? Entry(Control child, Control anchor)
    {
        if (IsSeparator(child))
        {
            return new Separator();
        }

        var name = AutomationProperties.GetName(child) ?? (ToolTip.GetTip(child) as string) ?? child.Name ?? string.Empty;
        switch (child)
        {
            case ToggleButton toggle:
            {
                var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = toggle.IsChecked == true, IsEnabled = toggle.IsEffectivelyEnabled, Icon = IconOf(toggle) };
                item.Click += (_, _) => toggle.IsChecked = toggle.IsChecked != true;
                return item;
            }

            case Button button:
            {
                var item = new MenuItem { Header = name, IsEnabled = button.IsEffectivelyEnabled, Icon = IconOf(button) };
                item.Click += (_, _) =>
                {
                    if (button.Command is { } command)
                    {
                        if (command.CanExecute(button.CommandParameter))
                        {
                            command.Execute(button.CommandParameter);
                        }
                    }
                    else if (button.Flyout is { } flyout)
                    {
                        flyout.ShowAt(anchor);
                    }
                    else
                    {
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                    }
                };
                return item;
            }

            // A combo box, or a segmented control (M34h): a submenu of its choices.
            case SelectingItemsControl combo:
            {
                var item = new MenuItem { Header = name, IsEnabled = combo.IsEffectivelyEnabled };
                var index = 0;
                foreach (var choice in combo.Items)
                {
                    var at = index++;
                    var label = choice is ContentControl { Content: { } content } ? content.ToString() : choice?.ToString();
                    var sub = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = combo.SelectedIndex == at };
                    sub.Click += (_, _) => combo.SelectedIndex = at;
                    item.Items.Add(sub);
                }

                return item;
            }

            default:
                return null;
        }
    }

    private static Icon? IconOf(ContentControl control) =>
        control.Content switch
        {
            Icon icon => Copy(icon),
            Panel panel when panel.Children.OfType<Icon>().FirstOrDefault() is { } icon => Copy(icon),
            _ when ButtonIcon.GetKind(control) is { Length: > 0 } kind => new Icon { Kind = kind }, // the control set's icon buttons (M34h)
            _ => null,
        };

    private static Icon Copy(Icon icon)
    {
        var copy = new Icon { Kind = icon.Kind };
        copy.Classes.AddRange(icon.Classes.Where(c => !c.StartsWith(':')));
        return copy;
    }
}
