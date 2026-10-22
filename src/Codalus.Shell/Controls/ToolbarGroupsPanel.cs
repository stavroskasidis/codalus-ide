using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Controls;

/// <summary>
/// The standard toolbar's row (0.6.0): its groups (the items between <c>Border.toolbarSeparator</c>s) on one line that
/// never wraps. As the window narrows, like the classic IDE's toolbars, items fold from the right: the last group's last
/// items first, and so on leftwards. A group with folded items ends in a "…" drop-down that lists them (buttons by their
/// names, combo boxes as submenus of their choices, as <see cref="ToolbarPanel"/>'s overflow does); a group folded whole is
/// just its "…". Folded items are arranged outside the row, which clips, and leave the tab order.
/// </summary>
public sealed class ToolbarGroupsPanel : Panel
{
    /// <summary>The "…" drop-downs, one per group that has folded items, reused as the row changes.</summary>
    private readonly List<Button> _overflows = [];

    private readonly HashSet<Control> _folded = [];

    /// <summary>Folded items this panel took out of the keyboard's tab order (and gives back when they show).</summary>
    private readonly HashSet<Control> _untabbed = [];

    /// <summary>The groups' items (no separators) in order, from the last measure.</summary>
    private readonly List<List<Control>> _groups = [];

    public ToolbarGroupsPanel()
    {
        ClipToBounds = true;
    }

    /// <summary>The items folded into the drop-downs (tests).</summary>
    public IReadOnlyCollection<Control> Folded => _folded;

    /// <summary>The "…" drop-downs on the row, left to right (tests).</summary>
    public IEnumerable<Button> Overflows => _overflows.Where(o => o.IsVisible);

    private static bool IsSeparator(Control child) => child is Separator || child.Classes.Contains("toolbarSeparator");

    private Button Overflow(int group)
    {
        while (_overflows.Count <= group)
        {
            var overflow = new Button { Content = new Icon { Kind = "more" }, IsVisible = false, Flyout = new MenuFlyout { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft } };
            overflow.Classes.Add("toolbar");
            overflow.Classes.Add("overflow");
            var name = Localizer.Get("msg.toolbarGroupsPanel.moreCommands", "More commands");
            ToolTip.SetTip(overflow, name);
            AutomationProperties.SetName(overflow, name);
            var index = _overflows.Count;
            ((MenuFlyout)overflow.Flyout).Opening += (_, _) => Fill((MenuFlyout)overflow.Flyout!, overflow, index);
            _overflows.Add(overflow);

            // Not one of Children: the toolbar rebuilds those (Tools → Customize), and these stay.
            LogicalChildren.Add(overflow);
            VisualChildren.Add(overflow);
        }

        return _overflows[group];
    }

    /// <summary>A group's drop-down: its folded items, in order.</summary>
    private void Fill(MenuFlyout flyout, Button overflow, int group)
    {
        flyout.Items.Clear();
        if (group >= _groups.Count)
        {
            return;
        }

        foreach (var child in _groups[group].Where(_folded.Contains))
        {
            if (ToolbarPanel.Entry(child, overflow) is { } entry and not Separator)
            {
                flyout.Items.Add(entry);
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _groups.Clear();
        _groups.Add([]);
        var widths = new Dictionary<Control, double>();
        var height = 0.0;
        var total = 0.0;
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
            if (!child.IsVisible)
            {
                continue;
            }

            if (IsSeparator(child))
            {
                if (_groups[^1].Count > 0)
                {
                    _groups.Add([]);
                }
            }
            else
            {
                _groups[^1].Add(child);
            }

            widths[child] = child.DesiredSize.Width;
            total += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        if (_groups[^1].Count == 0 && _groups.Count > 1)
        {
            _groups.RemoveAt(_groups.Count - 1);
        }

        var overflowWidth = 0.0;
        for (var g = 0; g < _groups.Count; g++)
        {
            var overflow = Overflow(g);
            overflow.Measure(Size.Infinity);
            overflowWidth = Math.Max(overflowWidth, overflow.DesiredSize.Width);
        }

        // Fold items from the right until the row fits; a group's first folded item brings its "…".
        _folded.Clear();
        if (!double.IsInfinity(availableSize.Width))
        {
            for (var g = _groups.Count - 1; g >= 0 && total > availableSize.Width + 0.5; g--)
            {
                for (var i = _groups[g].Count - 1; i >= 0 && total > availableSize.Width + 0.5; i--)
                {
                    if (i == _groups[g].Count - 1)
                    {
                        total += overflowWidth;
                    }

                    _folded.Add(_groups[g][i]);
                    total -= widths[_groups[g][i]];
                }
            }
        }

        for (var g = 0; g < _overflows.Count; g++)
        {
            var shown = g < _groups.Count && _groups[g].Any(_folded.Contains);
            _overflows[g].IsVisible = shown;
            if (shown)
            {
                _overflows[g].Measure(Size.Infinity);
                height = Math.Max(height, _overflows[g].DesiredSize.Height);
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? total : Math.Min(total, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        var group = 0;
        void PlaceOverflow(int g)
        {
            if (g < _overflows.Count && _overflows[g].IsVisible)
            {
                var size = _overflows[g].DesiredSize;
                _overflows[g].Arrange(new Rect(x, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
                x += size.Width;
            }
        }

        var hasItems = false; // the current group has had an item (shown or folded), as measure counts them
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            var separator = IsSeparator(child);
            if (separator)
            {
                // The end of a group: its "…" before the separator.
                if (hasItems)
                {
                    PlaceOverflow(group);
                    group++;
                    hasItems = false;
                }
            }
            else
            {
                hasItems = true;
                if (_folded.Contains(child))
                {
                    // Outside the row, which clips: not drawn, not hit; and out of the tab order, since its drop-down has it.
                    child.Arrange(new Rect(finalSize.Width + 10000, 0, child.DesiredSize.Width, child.DesiredSize.Height));
                    if (_untabbed.Add(child))
                    {
                        KeyboardNavigation.SetIsTabStop(child, false);
                    }

                    continue;
                }
            }

            if (_untabbed.Remove(child))
            {
                child.ClearValue(KeyboardNavigation.IsTabStopProperty);
            }

            var height = separator ? finalSize.Height : Math.Min(finalSize.Height, child.DesiredSize.Height);
            child.Arrange(new Rect(x, (finalSize.Height - height) / 2, child.DesiredSize.Width, height));
            x += child.DesiredSize.Width;
        }

        PlaceOverflow(group);
        return finalSize;
    }
}
