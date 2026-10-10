using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using DaedalusStudio.Workbench.Git;

namespace DaedalusStudio.Shell.Controls;

/// <summary>
/// One row of the history graph (M13): the lines entering the commit's dot from above, leaving it below, the lanes
/// passing by, and the dot (filled for HEAD). Rows stack without gaps, so the lines join up into the graph.
/// </summary>
public sealed class GitGraphCell : Control
{
    public const double LaneWidth = 14;

    public static readonly StyledProperty<GraphRow?> RowProperty = AvaloniaProperty.Register<GitGraphCell, GraphRow?>(nameof(Row));

    public static readonly StyledProperty<int> LanesProperty = AvaloniaProperty.Register<GitGraphCell, int>(nameof(Lanes), 1);

    public static readonly StyledProperty<bool> IsHeadProperty = AvaloniaProperty.Register<GitGraphCell, bool>(nameof(IsHead));

    /// <summary>Lane colors, repeating; readable on light and dark backgrounds.</summary>
    private static readonly IBrush[] s_colors =
    [
        new ImmutableSolidColorBrush(Color.Parse("#0E70C0")),
        new ImmutableSolidColorBrush(Color.Parse("#C2410C")),
        new ImmutableSolidColorBrush(Color.Parse("#15803D")),
        new ImmutableSolidColorBrush(Color.Parse("#9333EA")),
        new ImmutableSolidColorBrush(Color.Parse("#B45309")),
        new ImmutableSolidColorBrush(Color.Parse("#0F766E")),
        new ImmutableSolidColorBrush(Color.Parse("#BE185D")),
        new ImmutableSolidColorBrush(Color.Parse("#4D7C0F")),
    ];

    static GitGraphCell()
    {
        AffectsRender<GitGraphCell>(RowProperty, IsHeadProperty);
        AffectsMeasure<GitGraphCell>(LanesProperty);
    }

    public GraphRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>The graph's width in lanes, the same for every row.</summary>
    public int Lanes
    {
        get => GetValue(LanesProperty);
        set => SetValue(LanesProperty, value);
    }

    public bool IsHead
    {
        get => GetValue(IsHeadProperty);
        set => SetValue(IsHeadProperty, value);
    }

    public static IBrush ColorOf(int lane) => s_colors[lane % s_colors.Length];

    protected override Size MeasureOverride(Size availableSize) => new(Math.Max(1, Math.Min(Lanes, 24)) * LaneWidth + 4, 20);

    public override void Render(DrawingContext context)
    {
        if (Row is not { } row)
        {
            return;
        }

        var height = Bounds.Height;
        var middle = height / 2;
        static double X(int lane) => (lane * LaneWidth) + (LaneWidth / 2) + 2;

        foreach (var lane in row.Passing)
        {
            context.DrawLine(new Pen(ColorOf(lane), 1.5), new Point(X(lane), 0), new Point(X(lane), height));
        }

        foreach (var (from, to) in row.Top)
        {
            context.DrawLine(new Pen(ColorOf(from), 1.5), new Point(X(from), 0), new Point(X(to), middle));
        }

        foreach (var (from, to) in row.Bottom)
        {
            context.DrawLine(new Pen(ColorOf(to), 1.5), new Point(X(from), middle), new Point(X(to), height));
        }

        var color = ColorOf(row.Lane);
        var center = new Point(X(row.Lane), middle);
        context.DrawEllipse(IsHead ? color : Brushes.White, new Pen(color, 2), center, 4, 4);
    }
}
