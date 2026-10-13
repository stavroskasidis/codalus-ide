using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Codalus.Diagnostics.Protocol;

namespace Codalus.Shell.Controls;

/// <summary>
/// The flame graph (M18, ADR 0045): a row per call depth from the top (the root's row first), each frame as wide as its
/// samples. The frames come pruned from the diagnostics host for the zoom root it was asked for; within them the view
/// zooms (mouse wheel, +/-) and pans (drag, Shift+wheel) without asking again, and a double-click or Enter on a frame
/// asks for that frame's subtree (<see cref="FrameActivated"/>). Keyboard: the arrows move between parent, child and
/// neighbours, Backspace zooms out, Home resets the view. User code is warm, framework code (System.*, Microsoft.*) cool.
/// </summary>
public sealed class FlameGraph : Control
{
    public const double RowHeight = 18;
    private const double TextMinWidth = 34;
    private const double ZoomStep = 1.25;

    public static readonly StyledProperty<IReadOnlyList<FlameFrame>?> FramesProperty =
        AvaloniaProperty.Register<FlameGraph, IReadOnlyList<FlameFrame>?>(nameof(Frames));

    public static readonly StyledProperty<int> TotalProperty = AvaloniaProperty.Register<FlameGraph, int>(nameof(Total));

    public static readonly StyledProperty<FlameFrame?> SelectedFrameProperty =
        AvaloniaProperty.Register<FlameGraph, FlameFrame?>(nameof(SelectedFrame), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty = AvaloniaProperty.Register<FlameGraph, IBrush?>(nameof(HighlightBrush));

    private static readonly IBrush s_text = new ImmutableSolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly Typeface s_typeface = new(FontFamily.Default);
    private readonly Dictionary<int, IBrush> _fills = [];
    private double _viewStart;
    private double _viewWidth;
    private Point? _dragFrom;
    private double _dragStart;
    private int _maxDepth;

    static FlameGraph()
    {
        AffectsRender<FlameGraph>(SelectedFrameProperty, HighlightBrushProperty);
        FocusableProperty.OverrideDefaultValue<FlameGraph>(true);
        ClipToBoundsProperty.OverrideDefaultValue<FlameGraph>(true);
    }

    public IReadOnlyList<FlameFrame>? Frames
    {
        get => GetValue(FramesProperty);
        set => SetValue(FramesProperty, value);
    }

    /// <summary>The root's samples: the full width.</summary>
    public int Total
    {
        get => GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    public FlameFrame? SelectedFrame
    {
        get => GetValue(SelectedFrameProperty);
        set => SetValue(SelectedFrameProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    /// <summary>A frame was double-clicked or Enter was pressed on it: show its subtree.</summary>
    public event EventHandler<FlameFrame>? FrameActivated;

    /// <summary>Backspace: back to the previous zoom root.</summary>
    public event EventHandler? ZoomOutRequested;

    /// <summary>The visible range, in samples from the root's left edge (tests).</summary>
    internal (double Start, double Width) View => (_viewStart, _viewWidth);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FramesProperty || change.Property == TotalProperty)
        {
            _maxDepth = Frames is { Count: > 0 } frames ? frames.Max(f => f.Depth) : 0;
            ResetView();
            InvalidateMeasure();
        }
    }

    /// <summary>Shows the whole root again.</summary>
    public void ResetView()
    {
        _viewStart = 0;
        _viewWidth = Math.Max(1, Total);
        InvalidateVisual();
    }

    /// <summary>Zooms the view by <paramref name="factor"/> (above 1: in) around a point <paramref name="at"/> (0 to 1) across the width.</summary>
    public void Zoom(double factor, double at = 0.5)
    {
        var total = Math.Max(1, Total);
        var anchor = _viewStart + (_viewWidth * at);
        _viewWidth = Math.Clamp(_viewWidth / factor, Math.Min(total, 2), total);
        _viewStart = Math.Clamp(anchor - (_viewWidth * at), 0, total - _viewWidth);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        return new Size(width, ((Frames is { Count: > 0 } ? _maxDepth + 1 : 1) * RowHeight) + 2);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds);
        if (Frames is not { Count: > 0 } frames || Total <= 0 || bounds.Width <= 0)
        {
            return;
        }

        var scale = bounds.Width / _viewWidth;
        var selected = SelectedFrame;
        foreach (var frame in frames)
        {
            var x = (frame.Start - _viewStart) * scale;
            var w = frame.Width * scale;
            if (x + w < 0 || x > bounds.Width || w < 0.4)
            {
                continue;
            }

            var left = Math.Max(0, x);
            var right = Math.Min(bounds.Width, x + w);
            var rect = new Rect(left, frame.Depth * RowHeight, Math.Max(0.4, right - left - (w > 3 ? 1 : 0)), RowHeight - 1);
            context.FillRectangle(FillOf(frame), rect);
            if (rect.Width >= TextMinWidth)
            {
                var text = new FormattedText(ShortName(frame.Name), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, s_typeface, 11, s_text)
                {
                    MaxTextWidth = rect.Width - 6,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(text, new Point(rect.X + 3, rect.Y + ((RowHeight - 1 - text.Height) / 2)));
            }
        }

        if (selected is not null && frames.Contains(selected))
        {
            var x = Math.Max(0, (selected.Start - _viewStart) * scale);
            var w = Math.Min(bounds.Width, (selected.Start + selected.Width - _viewStart) * scale) - x;
            if (w > 0)
            {
                context.DrawRectangle(new Pen(HighlightBrush ?? Brushes.DodgerBlue, 2), new Rect(x + 1, (selected.Depth * RowHeight) + 1, Math.Max(1, w - 2), RowHeight - 3));
            }
        }
    }

    /// <summary>The method without its parameter list, which the tooltip has.</summary>
    internal static string ShortName(string name)
    {
        var paren = name.IndexOf('(', StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    private IBrush FillOf(FlameFrame frame)
    {
        if (_fills.TryGetValue(frame.Function, out var brush))
        {
            return brush;
        }

        // A stable color per function: warm for user code, cool for the framework's.
        var hash = (uint)StringComparer.Ordinal.GetHashCode(frame.Name);
        var framework = frame.Name.StartsWith("System.", StringComparison.Ordinal) || frame.Name.StartsWith("Microsoft.", StringComparison.Ordinal) || frame.Name.StartsWith('[');
        var t = (hash % 1000) / 1000.0;
        var color = framework
            ? Color.FromRgb((byte)(150 + (40 * t)), (byte)(175 + (40 * t)), (byte)(205 + (35 * t)))
            : Color.FromRgb((byte)(230 + (25 * t)), (byte)(110 + (110 * t)), (byte)(40 + (40 * t)));
        brush = new ImmutableSolidColorBrush(color);
        _fills[frame.Function] = brush;
        return brush;
    }

    /// <summary>The frame at a point of the control, or null.</summary>
    public FlameFrame? FrameAt(Point point)
    {
        if (Frames is not { Count: > 0 } frames || Bounds.Width <= 0)
        {
            return null;
        }

        var depth = (int)(point.Y / RowHeight);
        var sample = _viewStart + (point.X / Bounds.Width * _viewWidth);
        return frames.FirstOrDefault(f => f.Depth == depth && sample >= f.Start && sample < f.Start + f.Width);
    }

    /// <summary>A frame's description: name, samples and share, self samples.</summary>
    public static string Describe(FlameFrame frame, int total) =>
        string.Create(CultureInfo.CurrentCulture, $"{frame.Name}\n{frame.Width:N0} samples ({Percent(frame.Width, total)}), {frame.Self:N0} self ({Percent(frame.Self, total)})");

    internal static string Percent(int part, int total) =>
        total <= 0 ? "0%" : (100.0 * part / total).ToString("0.##", CultureInfo.CurrentCulture) + "%";

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var frame = FrameAt(point);
        if (e.ClickCount >= 2 && frame is not null)
        {
            FrameActivated?.Invoke(this, frame);
            e.Handled = true;
            return;
        }

        SelectedFrame = frame;
        _dragFrom = point;
        _dragStart = _viewStart;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_dragFrom is { } from && Bounds.Width > 0)
        {
            var total = Math.Max(1, Total);
            _viewStart = Math.Clamp(_dragStart - ((point.X - from.X) / Bounds.Width * _viewWidth), 0, total - _viewWidth);
            InvalidateVisual();
            return;
        }

        var frame = FrameAt(point);
        ToolTip.SetTip(this, frame is null ? null : Describe(frame, Total));
        ToolTip.SetIsOpen(this, frame is not null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetIsOpen(this, false);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Bounds.Width <= 0)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var total = Math.Max(1, Total);
            _viewStart = Math.Clamp(_viewStart - (e.Delta.Y * _viewWidth / 10), 0, total - _viewWidth);
            InvalidateVisual();
        }
        else
        {
            Zoom(Math.Pow(ZoomStep, e.Delta.Y), e.GetPosition(this).X / Bounds.Width);
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var frames = Frames;
        var selected = SelectedFrame;
        switch (e.Key)
        {
            case Key.Enter when selected is not null:
                FrameActivated?.Invoke(this, selected);
                break;
            case Key.Back:
                ZoomOutRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.Home:
                ResetView();
                break;
            case Key.OemPlus or Key.Add:
                Zoom(ZoomStep * ZoomStep);
                break;
            case Key.OemMinus or Key.Subtract:
                Zoom(1 / (ZoomStep * ZoomStep));
                break;
            case Key.Up or Key.Down or Key.Left or Key.Right when frames is { Count: > 0 }:
                SelectedFrame = Move(frames, selected, e.Key);
                EnsureVisible(SelectedFrame);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    internal static FlameFrame? Move(IReadOnlyList<FlameFrame> frames, FlameFrame? from, Key key)
    {
        if (from is null)
        {
            return frames[0];
        }

        return key switch
        {
            Key.Up => frames.FirstOrDefault(f => f.Depth == from.Depth - 1 && f.Start <= from.Start && from.Start < f.Start + f.Width) ?? from,
            Key.Down => frames.Where(f => f.Depth == from.Depth + 1 && f.Start >= from.Start && f.Start < from.Start + from.Width).MinBy(f => f.Start) ?? from,
            Key.Left => frames.Where(f => f.Depth == from.Depth && f.Start < from.Start).MaxBy(f => f.Start) ?? from,
            Key.Right => frames.Where(f => f.Depth == from.Depth && f.Start > from.Start).MinBy(f => f.Start) ?? from,
            _ => from,
        };
    }

    private void EnsureVisible(FlameFrame? frame)
    {
        if (frame is null)
        {
            return;
        }

        if (frame.Start < _viewStart || frame.Start + frame.Width > _viewStart + _viewWidth)
        {
            var total = Math.Max(1, Total);
            _viewWidth = Math.Clamp(Math.Max(_viewWidth, frame.Width), 1, total);
            _viewStart = Math.Clamp(frame.Start - ((_viewWidth - frame.Width) / 2), 0, total - _viewWidth);
        }

        InvalidateVisual();
        this.BringIntoView(new Rect(0, frame.Depth * RowHeight, Bounds.Width, RowHeight));
    }
}
