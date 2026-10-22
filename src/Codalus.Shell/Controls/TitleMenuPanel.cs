using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Codalus.Shell.Controls;

/// <summary>
/// The menu bar's items panel (0.6.0): one row when the title row has room for it, otherwise the items wrap onto more
/// rows, as the classic IDE's menu does in a narrow window. The last row keeps <see cref="TrailingSpace"/> free at its end
/// for the search box, which <see cref="TitleBarPanel"/> puts there. It is the menu's navigable container, so Left and
/// Right go from item to item across the wrap, and round from the last to the first.
/// </summary>
public sealed class TitleMenuPanel : Panel, INavigableContainer
{
    /// <summary>The room kept free at the end of the last row (the search box shrunk to its icon).</summary>
    public static readonly StyledProperty<double> TrailingSpaceProperty =
        AvaloniaProperty.Register<TitleMenuPanel, double>(nameof(TrailingSpace));

    /// <summary>The least height of each row once the items wrap, so the search box fits at the last row's end.</summary>
    public static readonly StyledProperty<double> MinRowHeightProperty =
        AvaloniaProperty.Register<TitleMenuPanel, double>(nameof(MinRowHeight), 26);

    /// <summary>The least height of the row when the items fit on one (the theme's menu bar height).</summary>
    public static readonly StyledProperty<double> SingleRowHeightProperty =
        AvaloniaProperty.Register<TitleMenuPanel, double>(nameof(SingleRowHeight), 32);

    /// <summary>Each row's first child index, its width and height, from the last measure.</summary>
    private readonly List<(int Start, double Width, double Height)> _rows = [];

    static TitleMenuPanel()
    {
        AffectsMeasure<TitleMenuPanel>(TrailingSpaceProperty, MinRowHeightProperty, SingleRowHeightProperty);
    }

    public double TrailingSpace
    {
        get => GetValue(TrailingSpaceProperty);
        set => SetValue(TrailingSpaceProperty, value);
    }

    public double MinRowHeight
    {
        get => GetValue(MinRowHeightProperty);
        set => SetValue(MinRowHeightProperty, value);
    }

    public double SingleRowHeight
    {
        get => GetValue(SingleRowHeightProperty);
        set => SetValue(SingleRowHeightProperty, value);
    }

    /// <summary>The number of rows the items take.</summary>
    public int RowCount => _rows.Count;

    /// <summary>The first row's width (its items' widths).</summary>
    public double FirstRowWidth => _rows.Count > 0 ? _rows[0].Width : 0;

    /// <summary>The first row's height.</summary>
    public double FirstRowHeight => _rows.Count > 0 ? _rows[0].Height : 0;

    /// <summary>Where the last row's items end, from the panel's left.</summary>
    public double LastRowEnd => _rows.Count > 0 ? _rows[^1].Width : 0;

    /// <summary>The last row's top, from the panel's top.</summary>
    public double LastRowTop => _rows.Take(_rows.Count - 1).Sum(r => r.Height);

    /// <summary>The last row's height.</summary>
    public double LastRowHeight => _rows.Count > 0 ? _rows[^1].Height : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = availableSize.Width;
        _rows.Clear();
        var start = 0;
        var x = 0.0;
        var height = 0.0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(Size.Infinity);
            var size = child.DesiredSize;
            if (x > 0 && size.Width > 0 && x + size.Width > limit + 0.5)
            {
                _rows.Add((start, x, height));
                (start, x, height) = (i, 0, 0);
            }

            x += size.Width;
            height = Math.Max(height, size.Height);
        }

        _rows.Add((start, x, height));

        // The last row keeps room for the search box at its end: its last item moves to a row of its own if needed.
        if (!double.IsInfinity(limit) && TrailingSpace > 0 && _rows[^1] is var last && last.Width + TrailingSpace > limit + 0.5)
        {
            var split = Children.Count - 1;
            while (split > last.Start && Children[split].DesiredSize.Width <= 0)
            {
                split--; // hidden items take no room
            }

            if (split > last.Start)
            {
                var moved = Children.Skip(split).Sum(c => c.DesiredSize.Width);
                _rows[^1] = (last.Start, last.Width - moved, Children.Skip(last.Start).Take(split - last.Start).Max(c => c.DesiredSize.Height));
                _rows.Add((split, moved, Children.Skip(split).Max(c => c.DesiredSize.Height)));
            }
        }

        var least = _rows.Count > 1 ? MinRowHeight : SingleRowHeight;
        for (var row = 0; row < _rows.Count; row++)
        {
            _rows[row] = _rows[row] with { Height = Math.Max(_rows[row].Height, least) };
        }

        return new Size(_rows.Max(r => r.Width), _rows.Sum(r => r.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        for (var row = 0; row < _rows.Count; row++)
        {
            var (start, _, height) = _rows[row];
            var end = row + 1 < _rows.Count ? _rows[row + 1].Start : Children.Count;
            var x = 0.0;
            for (var i = start; i < end && i < Children.Count; i++)
            {
                var child = Children[i];
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, height));
                x += child.DesiredSize.Width;
            }

            y += height;
        }

        return finalSize;
    }

    /// <summary>Left and Right (and Previous, Next, First, Last) go along the items in order, across rows.</summary>
    IInputElement? INavigableContainer.GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var items = Children.Where(c => c.IsVisible).ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var index = from is Control control ? items.IndexOf(control) : -1;
        int next;
        switch (direction)
        {
            case NavigationDirection.First:
                return items[0];
            case NavigationDirection.Last:
                return items[^1];
            case NavigationDirection.Right or NavigationDirection.Next:
                next = index + 1;
                if (next >= items.Count)
                {
                    return wrap ? items[0] : null;
                }

                return items[next];
            case NavigationDirection.Left or NavigationDirection.Previous:
                next = index < 0 ? items.Count - 1 : index - 1;
                if (next < 0)
                {
                    return wrap ? items[^1] : null;
                }

                return items[next];
            default:
                return null;
        }
    }
}
