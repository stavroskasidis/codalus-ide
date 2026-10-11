using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DaedalusStudio.Workbench.Diagnostics;

namespace DaedalusStudio.Shell.Controls;

/// <summary>
/// A value over time (M18, the Diagnostic Tools window): a filled line of the last <see cref="WindowSeconds"/> of
/// <see cref="Points"/>, scaled to <see cref="Maximum"/> or to the largest value, with event marks along the top (GCs,
/// exceptions) and the caption and latest value in the corner.
/// </summary>
public sealed class TimeSeriesChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<CounterPoint>?> PointsProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IReadOnlyList<CounterPoint>?>(nameof(Points));

    public static readonly StyledProperty<IReadOnlyList<double>?> MarksProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IReadOnlyList<double>?>(nameof(Marks));

    public static readonly StyledProperty<double> WindowSecondsProperty = AvaloniaProperty.Register<TimeSeriesChart, double>(nameof(WindowSeconds), 60);

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<TimeSeriesChart, double>(nameof(Maximum), double.NaN);

    public static readonly StyledProperty<string?> CaptionProperty = AvaloniaProperty.Register<TimeSeriesChart, string?>(nameof(Caption));

    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<TimeSeriesChart, string?>(nameof(Unit));

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> MarkBrushProperty = AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(nameof(MarkBrush));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(nameof(GridBrush));

    private static readonly Typeface s_typeface = new(FontFamily.Default);

    static TimeSeriesChart()
    {
        AffectsRender<TimeSeriesChart>(PointsProperty, MarksProperty, WindowSecondsProperty, MaximumProperty, CaptionProperty, UnitProperty, StrokeProperty, MarkBrushProperty, ForegroundProperty, GridBrushProperty);
    }

    public IReadOnlyList<CounterPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>Times (seconds) of events to mark.</summary>
    public IReadOnlyList<double>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    /// <summary>How much history shows (0: all of it).</summary>
    public double WindowSeconds
    {
        get => GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    /// <summary>The top of the scale (NaN: the largest value shown).</summary>
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? MarkBrush
    {
        get => GetValue(MarkBrushProperty);
        set => SetValue(MarkBrushProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, 80);

    /// <summary>The visible points and the time range they span.</summary>
    internal (IReadOnlyList<CounterPoint> Points, double From, double To) Visible()
    {
        var points = Points ?? [];
        if (points.Count == 0)
        {
            return ([], 0, Math.Max(1, WindowSeconds));
        }

        var to = Math.Max(points[^1].At, WindowSeconds > 0 ? WindowSeconds : 1);
        var from = WindowSeconds > 0 ? to - WindowSeconds : 0;
        return ([.. points.Where(p => p.At >= from)], from, to);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds);
        var foreground = Foreground ?? Brushes.Gray;
        var stroke = Stroke ?? Brushes.SteelBlue;
        if (GridBrush is { } grid)
        {
            context.DrawRectangle(new Pen(grid, 1), bounds.Deflate(0.5));
        }

        var (points, from, to) = Visible();
        var plot = new Rect(1, 16, Math.Max(1, bounds.Width - 2), Math.Max(1, bounds.Height - 18));
        var top = double.IsNaN(Maximum) ? Math.Max(1e-9, points.Count == 0 ? 1 : points.Max(p => p.Value) * 1.15) : Maximum;
        double X(double at) => plot.X + ((at - from) / Math.Max(1e-9, to - from) * plot.Width);
        double Y(double value) => plot.Bottom - (Math.Clamp(value / top, 0, 1) * plot.Height);

        if (points.Count >= 2)
        {
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var g = line.Open())
            using (var a = area.Open())
            {
                a.BeginFigure(new Point(X(points[0].At), plot.Bottom), isFilled: true);
                g.BeginFigure(new Point(X(points[0].At), Y(points[0].Value)), isFilled: false);
                foreach (var point in points)
                {
                    g.LineTo(new Point(X(point.At), Y(point.Value)));
                    a.LineTo(new Point(X(point.At), Y(point.Value)));
                }

                a.LineTo(new Point(X(points[^1].At), plot.Bottom));
                a.EndFigure(isClosed: true);
                g.EndFigure(isClosed: false);
            }

            context.DrawGeometry(stroke is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.18) : null, null, area);
            context.DrawGeometry(null, new Pen(stroke, 1.5), line);
        }

        if (Marks is { Count: > 0 } marks)
        {
            var markPen = new Pen(MarkBrush ?? Brushes.Orange, 1);
            foreach (var at in marks)
            {
                if (at >= from && at <= to)
                {
                    var x = X(at);
                    context.DrawLine(markPen, new Point(x, plot.Y), new Point(x, plot.Y + 5));
                }
            }
        }

        var latest = points.Count > 0 ? Format(points[^1].Value) + (Unit is { Length: > 0 } unit ? " " + unit : string.Empty) : "—";
        var caption = new FormattedText($"{Caption}  {latest}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, s_typeface, 11, foreground);
        context.DrawText(caption, new Point(4, 1));
    }

    internal static string Format(double value) =>
        Math.Abs(value) >= 100 ? value.ToString("N0", CultureInfo.CurrentCulture) : value.ToString("0.##", CultureInfo.CurrentCulture);
}

/// <summary>
/// The CPU samples over a trace (the profiling report): a bar per time slice, and a time range selected by dragging
/// (<see cref="SelectionChanged"/> when the drag ends; a click clears it), which filters the report's views.
/// </summary>
public sealed class SampleTimeline : Control
{
    public static readonly StyledProperty<IReadOnlyList<int>?> SamplesProperty = AvaloniaProperty.Register<SampleTimeline, IReadOnlyList<int>?>(nameof(Samples));

    public static readonly StyledProperty<double> SelectionStartProperty = AvaloniaProperty.Register<SampleTimeline, double>(nameof(SelectionStart), double.NaN);

    public static readonly StyledProperty<double> SelectionEndProperty = AvaloniaProperty.Register<SampleTimeline, double>(nameof(SelectionEnd), double.NaN);

    public static readonly StyledProperty<IBrush?> BarBrushProperty = AvaloniaProperty.Register<SampleTimeline, IBrush?>(nameof(BarBrush));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty = AvaloniaProperty.Register<SampleTimeline, IBrush?>(nameof(SelectionBrush));

    private double? _dragFrom;

    static SampleTimeline()
    {
        AffectsRender<SampleTimeline>(SamplesProperty, SelectionStartProperty, SelectionEndProperty, BarBrushProperty, SelectionBrushProperty);
        FocusableProperty.OverrideDefaultValue<SampleTimeline>(true);
    }

    public IReadOnlyList<int>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>The selection's start, 0 to 1 across the trace (NaN: none).</summary>
    public double SelectionStart
    {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public double SelectionEnd
    {
        get => GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>The user selected a range (start and end, 0 to 1) or cleared it (both NaN).</summary>
    public event EventHandler<(double Start, double End)>? SelectionChanged;

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, 56);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds);
        if (Samples is { Count: > 0 } samples)
        {
            var max = Math.Max(1, samples.Max());
            var width = bounds.Width / samples.Count;
            var brush = BarBrush ?? Brushes.SteelBlue;
            for (var i = 0; i < samples.Count; i++)
            {
                var height = samples[i] / (double)max * (bounds.Height - 2);
                if (height > 0)
                {
                    context.FillRectangle(brush, new Rect(i * width, bounds.Height - height, Math.Max(1, width - 1), height));
                }
            }
        }

        if (!double.IsNaN(SelectionStart) && !double.IsNaN(SelectionEnd))
        {
            var from = Math.Min(SelectionStart, SelectionEnd) * bounds.Width;
            var to = Math.Max(SelectionStart, SelectionEnd) * bounds.Width;
            var selection = SelectionBrush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.25) : new SolidColorBrush(Colors.DodgerBlue, 0.25);
            context.FillRectangle(selection, new Rect(from, 0, Math.Max(1, to - from), bounds.Height));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        _dragFrom = Fraction(e.GetPosition(this).X);
        SelectionStart = _dragFrom.Value;
        SelectionEnd = _dragFrom.Value;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not null)
        {
            SelectionEnd = Fraction(e.GetPosition(this).X);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is not { } from)
        {
            return;
        }

        _dragFrom = null;
        e.Pointer.Capture(null);
        var to = Fraction(e.GetPosition(this).X);
        Select(from, to);
    }

    /// <summary>Selects a range (a click, under 1% wide, clears it) and says so.</summary>
    public void Select(double from, double to)
    {
        if (Math.Abs(to - from) < 0.01)
        {
            SelectionStart = SelectionEnd = double.NaN;
            SelectionChanged?.Invoke(this, (double.NaN, double.NaN));
        }
        else
        {
            SelectionStart = Math.Min(from, to);
            SelectionEnd = Math.Max(from, to);
            SelectionChanged?.Invoke(this, (SelectionStart, SelectionEnd));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Select(0, 0);
            e.Handled = true;
        }
    }

    private double Fraction(double x) => Bounds.Width <= 0 ? 0 : Math.Clamp(x / Bounds.Width, 0, 1);
}
