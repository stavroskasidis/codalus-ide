using Avalonia;
using Avalonia.Controls;

namespace Codalus.Shell.Controls;

/// <summary>
/// A row of columns whose widths are shared by every row and the header (the References window, 0.6): set
/// <see cref="WidthsProperty"/> once on the list and each row's panel inherits it. The first column takes the width the
/// others leave (down to <see cref="MinFirstWidth"/>); the others are the widths given, in pixels, so dragging a
/// header's splitter resizes a column in every row at once. A child sits in <see cref="ColumnProperty"/> and spans
/// <see cref="ColumnSpanProperty"/> columns.
/// </summary>
public sealed class ColumnsPanel : Panel
{
    /// <summary>The narrowest the first column gets.</summary>
    public const double MinFirstWidth = 120;

    public static readonly AttachedProperty<IReadOnlyList<double>?> WidthsProperty =
        AvaloniaProperty.RegisterAttached<ColumnsPanel, Control, IReadOnlyList<double>?>("Widths", inherits: true);

    public static readonly AttachedProperty<int> ColumnProperty =
        AvaloniaProperty.RegisterAttached<ColumnsPanel, Control, int>("Column");

    public static readonly AttachedProperty<int> ColumnSpanProperty =
        AvaloniaProperty.RegisterAttached<ColumnsPanel, Control, int>("ColumnSpan", 1);

    static ColumnsPanel()
    {
        AffectsMeasure<ColumnsPanel>(WidthsProperty);
        AffectsParentMeasure<ColumnsPanel>(ColumnProperty, ColumnSpanProperty);
    }

    public static IReadOnlyList<double>? GetWidths(Control control) => control.GetValue(WidthsProperty);

    public static void SetWidths(Control control, IReadOnlyList<double>? value) => control.SetValue(WidthsProperty, value);

    public static int GetColumn(Control control) => control.GetValue(ColumnProperty);

    public static void SetColumn(Control control, int value) => control.SetValue(ColumnProperty, value);

    public static int GetColumnSpan(Control control) => control.GetValue(ColumnSpanProperty);

    public static void SetColumnSpan(Control control, int value) => control.SetValue(ColumnSpanProperty, value);

    /// <summary>The columns' left edges and widths for a row <paramref name="width"/> wide.</summary>
    public static (double Left, double Width)[] Layout(IReadOnlyList<double> widths, double width)
    {
        var columns = new (double Left, double Width)[widths.Count];
        var others = widths.Skip(1).Sum();
        var first = double.IsInfinity(width) ? Math.Max(MinFirstWidth, widths.Count > 0 ? widths[0] : 0) : Math.Max(MinFirstWidth, width - others);

        // Too narrow for every column: the first keeps its minimum (or 40% of a row narrower still) and the others share
        // the rest in proportion, as the classic IDE squeezes its columns, so none runs past the row's edge.
        var scale = 1.0;
        if (!double.IsInfinity(width) && first + others > width)
        {
            first = Math.Min(MinFirstWidth, width * 0.4);
            scale = others > 0 ? Math.Max(0, width - first) / others : 0;
        }

        var left = 0.0;
        for (var i = 0; i < widths.Count; i++)
        {
            columns[i] = (left, i == 0 ? first : widths[i] * scale);
            left += columns[i].Width;
        }

        return columns;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Layout(GetWidths(this) ?? [availableSize.Width], availableSize.Width);
        var height = 0.0;
        foreach (var child in Children)
        {
            var (_, width) = Cell(columns, child);
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var total = columns.Length == 0 ? 0 : columns[^1].Left + columns[^1].Width;
        return new Size(double.IsInfinity(availableSize.Width) ? total : Math.Min(total, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Layout(GetWidths(this) ?? [finalSize.Width], finalSize.Width);
        foreach (var child in Children)
        {
            var (left, width) = Cell(columns, child);
            child.Arrange(new Rect(left, 0, width, finalSize.Height));
        }

        return finalSize;
    }

    private static (double Left, double Width) Cell((double Left, double Width)[] columns, Control child)
    {
        if (columns.Length == 0)
        {
            return (0, 0);
        }

        var column = Math.Clamp(GetColumn(child), 0, columns.Length - 1);
        var last = Math.Clamp(column + Math.Max(1, GetColumnSpan(child)) - 1, column, columns.Length - 1);
        return (columns[column].Left, columns[last].Left + columns[last].Width - columns[column].Left);
    }
}
