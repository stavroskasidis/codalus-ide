using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace Codalus.Shell.Controls;

/// <summary>
/// The tabs of a tool window group: one row that fits the pane. When the tabs are wider than the pane, the widest ones
/// shrink first, down to a shared width, and their titles end in an ellipsis (as a classic IDE's tool window tabs do);
/// a tab with no room left for a readable title shows only its icon (<see cref="IconOnlyProperty"/>), and the
/// tooltip has the whole name. A tab's title is the <see cref="TextBlock"/> with the class <c>tabTitle</c>, whose
/// <c>MaxWidth</c> binds to <see cref="TitleMaxWidthProperty"/> on its tab; its icon is the <see cref="Icon"/> with the
/// class <c>tabIcon</c>.
/// </summary>
public sealed class FittingTabPanel : Panel
{
    /// <summary>The width the panel gives a tab's title; infinite while every tab fits.</summary>
    public static readonly AttachedProperty<double> TitleMaxWidthProperty =
        AvaloniaProperty.RegisterAttached<FittingTabPanel, Control, double>("TitleMaxWidth", double.PositiveInfinity);

    /// <summary>A tab whose title would be narrower than this shows only its icon, when it has one.</summary>
    public static readonly AttachedProperty<bool> IconOnlyProperty =
        AvaloniaProperty.RegisterAttached<FittingTabPanel, Control, bool>("IconOnly");

    /// <summary>The narrowest a tab with a title gets.</summary>
    public const double MinTabWidth = 40;

    /// <summary>The narrowest a title gets before its tab shows only the icon.</summary>
    public const double MinTitleWidth = 24;

    public static bool GetIconOnly(Control control) => control.GetValue(IconOnlyProperty);

    public static void SetIconOnly(Control control, bool value) => control.SetValue(IconOnlyProperty, value);

    public static double GetTitleMaxWidth(Control control) => control.GetValue(TitleMaxWidthProperty);

    public static void SetTitleMaxWidth(Control control, double value) => control.SetValue(TitleMaxWidthProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children.Where(c => c.IsVisible).ToList();
        var naturals = new double[children.Count];
        var overheads = new double[children.Count];
        var hasIcon = new bool[children.Count];
        var margins = new double[children.Count];
        var iconWidths = new double[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(Size.Infinity);
            var title = Title(child);
            var titleNow = title is { IsVisible: true } ? title.DesiredSize.Width : 0;
            // The tab's other parts without the icon, which shows only when the tab is collapsed: a collapsed tab's width
            // is those and the icon (counted whether or not it shows now; the Mac's narrow panes cut the last icon).
            var icon = child.GetVisualDescendants().OfType<Icon>().FirstOrDefault(c => c.Classes.Contains("tabIcon"));
            var iconNow = icon is { IsVisible: true } ? icon.DesiredSize.Width : 0;
            overheads[i] = Math.Max(0, child.DesiredSize.Width - titleNow - iconNow);
            naturals[i] = overheads[i] + (title is null ? titleNow : NaturalWidth(title));
            margins[i] = title is null ? 0 : title.Margin.Left + title.Margin.Right;
            hasIcon[i] = title is not null && icon?.Kind is not null;
            iconWidths[i] = icon is null ? 0 : (double.IsNaN(icon.Width) ? 16 : icon.Width) + icon.Margin.Left + icon.Margin.Right;
        }

        // Tabs that would keep fewer than MinTitleWidth of their title show only their icon; the width they free goes
        // to the others, so repeat until no more tabs change.
        var iconOnly = new bool[children.Count];
        var widths = (double[])naturals.Clone();
        var cap = Cap(widths, availableSize.Width);
        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i < children.Count; i++)
            {
                // The share the tab gets, not the text minimum: tabs that would only fit below it collapse to their icons.
                if (!iconOnly[i] && hasIcon[i] && naturals[i] > cap && cap - overheads[i] - margins[i] < MinTitleWidth)
                {
                    iconOnly[i] = true;
                    widths[i] = overheads[i] + iconWidths[i];
                    changed = true;
                }
            }

            cap = Cap(widths, availableSize.Width);
        }
        double width = 0, height = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var titleMax = !iconOnly[i] && naturals[i] > cap ? Math.Max(0, Math.Max(cap, MinTabWidth) - overheads[i] - margins[i]) : double.PositiveInfinity; // MaxWidth leaves out the margin
            if (!SameWidth(GetTitleMaxWidth(child), titleMax) || GetIconOnly(child) != iconOnly[i])
            {
                SetTitleMaxWidth(child, titleMax);
                SetIconOnly(child, iconOnly[i]);
                child.Measure(Size.Infinity);
            }

            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // When even collapsed tabs don't fit, whole tabs are left out from the end (never the selected one) rather than
        // cut at the edge; their windows stay in the View menu.
        var children = Children.Where(c => c.IsVisible).ToList();
        var shown = new HashSet<Control>(children);
        var total = children.Sum(c => c.DesiredSize.Width);
        for (var i = children.Count - 1; i >= 0 && total > finalSize.Width + 0.5; i--)
        {
            if (children[i] is Avalonia.Controls.Primitives.TabStripItem { IsSelected: true })
            {
                continue;
            }

            shown.Remove(children[i]);
            total -= children[i].DesiredSize.Width;
        }

        double x = 0;
        foreach (var child in children)
        {
            if (!shown.Contains(child))
            {
                child.Arrange(default);
                continue;
            }

            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }

        return finalSize;
    }

    /// <summary>
    /// The widest a tab may be so that all fit in <paramref name="available"/>: tabs narrower than it keep their width,
    /// the rest share what is left equally. Infinite when everything fits.
    /// </summary>
    public static double Cap(IReadOnlyList<double> naturals, double available)
    {
        if (double.IsInfinity(available) || naturals.Sum() <= available)
        {
            return double.PositiveInfinity;
        }

        var sorted = naturals.OrderBy(w => w).ToArray();
        var left = available;
        for (var i = 0; i < sorted.Length; i++)
        {
            var share = left / (sorted.Length - i);
            if (sorted[i] > share)
            {
                return share;
            }

            left -= sorted[i];
        }

        return double.PositiveInfinity;
    }

    private static TextBlock? Title(Control tab) =>
        tab.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("tabTitle"));

    /// <summary>The title's width without a limit: its text laid out in its own font, plus its padding and margin.</summary>
    private static double NaturalWidth(TextBlock title)
    {
        if (string.IsNullOrEmpty(title.Text))
        {
            return title.DesiredSize.Width;
        }

        using var layout = new TextLayout(title.Text, new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch), title.FontSize, null);
        return Math.Ceiling(layout.WidthIncludingTrailingWhitespace) + title.Padding.Left + title.Padding.Right + title.Margin.Left + title.Margin.Right;
    }

    private static bool SameWidth(double a, double b) =>
        (double.IsInfinity(a) && double.IsInfinity(b)) || Math.Abs(a - b) < 0.5;
}
