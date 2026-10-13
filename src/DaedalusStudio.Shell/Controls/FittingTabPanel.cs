using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace DaedalusStudio.Shell.Controls;

/// <summary>
/// The tabs of a tool window group: one row that fits the pane. When the tabs are wider than the pane, the widest ones
/// shrink first, down to a shared width, and their titles end in an ellipsis (as a classic IDE's tool window tabs do);
/// the title's tooltip has the whole name. A tab's title is the <see cref="TextBlock"/> with the class
/// <c>tabTitle</c>, whose <c>MaxWidth</c> binds to <see cref="TitleMaxWidthProperty"/> on its tab.
/// </summary>
public sealed class FittingTabPanel : Panel
{
    /// <summary>The width the panel gives a tab's title; infinite while every tab fits.</summary>
    public static readonly AttachedProperty<double> TitleMaxWidthProperty =
        AvaloniaProperty.RegisterAttached<FittingTabPanel, Control, double>("TitleMaxWidth", double.PositiveInfinity);

    /// <summary>The narrowest a tab gets: its icon and a few letters.</summary>
    public const double MinTabWidth = 40;

    public static double GetTitleMaxWidth(Control control) => control.GetValue(TitleMaxWidthProperty);

    public static void SetTitleMaxWidth(Control control, double value) => control.SetValue(TitleMaxWidthProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children.Where(c => c.IsVisible).ToList();
        var naturals = new double[children.Count];
        var overheads = new double[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(Size.Infinity);
            var title = Title(child);
            var titleNow = title?.DesiredSize.Width ?? 0;
            overheads[i] = Math.Max(0, child.DesiredSize.Width - titleNow);
            naturals[i] = overheads[i] + (title is null ? titleNow : NaturalWidth(title));
        }

        var cap = Cap(naturals, availableSize.Width);
        double width = 0, height = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var titleMax = naturals[i] > cap ? Math.Max(0, Math.Max(cap, MinTabWidth) - overheads[i]) : double.PositiveInfinity;
            if (!SameWidth(GetTitleMaxWidth(child), titleMax))
            {
                SetTitleMaxWidth(child, titleMax);
                child.Measure(Size.Infinity);
            }

            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children.Where(c => c.IsVisible))
        {
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
