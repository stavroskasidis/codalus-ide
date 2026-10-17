using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.Editor;

/// <summary>
/// Colors of the debugger's editor adornments: the theme's breakpoint glyph, breakpoint line and current statement line
/// (<c>Shell.Editor.Breakpoint</c>, <c>.Breakpoint.Line</c>, <c>Shell.Editor.CurrentStatement</c>, M45), and the arrow.
/// </summary>
internal static class DebugColors
{
    private static readonly IBrush s_breakpoint = new ImmutableSolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00));
    private static readonly IBrush s_breakpointLine = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0xE5, 0x14, 0x00));
    private static readonly IBrush s_currentLine = new ImmutableSolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xCC, 0x00));
    private static readonly Dictionary<Color, IPen> s_outlines = [];
    public static readonly IBrush Current = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
    public static readonly IPen ArrowOutline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x80, 0x60, 0x00)), 1);

    public static IBrush Breakpoint(Control control) => Find(control, "Shell.Editor.Breakpoint") ?? s_breakpoint;

    public static IBrush BreakpointLine(Control control) => Find(control, "Shell.Editor.Breakpoint.Line") ?? s_breakpointLine;

    public static IBrush CurrentLine(Control control) => Find(control, "Shell.Editor.CurrentStatement") ?? s_currentLine;

    /// <summary>The breakpoint glyph's outline, in its color.</summary>
    public static IPen Outline(Control control)
    {
        var color = (Breakpoint(control) as ISolidColorBrush)?.Color ?? Colors.Red;
        if (!s_outlines.TryGetValue(color, out var pen))
        {
            pen = new ImmutablePen(new ImmutableSolidColorBrush(color), 1.5);
            s_outlines[color] = pen;
        }

        return pen;
    }

    private static IBrush? Find(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var value) ? value as IBrush : null;
}

/// <summary>
/// The breakpoint margin (ADR 0007): a filled circle per breakpoint, hollow when disabled or not verified by the
/// debugger, a diamond for logpoints, a dot inside for conditions and hit counts; an arrow on the current statement.
/// Clicking toggles a breakpoint; Ctrl+click runs to the line while paused (Run to Click); right-clicking a breakpoint
/// opens its menu. With <paramref name="relocate"/>, breakpoints follow edits to the document.
/// </summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private const double Size = 16;

    private readonly Func<string> _path;
    private readonly BreakpointStore _store;
    private readonly Func<int?> _currentLine;
    private readonly bool _relocate;
    private readonly Dictionary<int, TextAnchor> _anchors = [];
    private TextDocument? _document;

    public BreakpointMargin(Func<string> path, BreakpointStore store, Func<int?> currentLine, bool relocate)
    {
        _path = path;
        _store = store;
        _currentLine = currentLine;
        _relocate = relocate;
        Cursor = new Cursor(StandardCursorType.Hand);
        store.Changed += OnStoreChanged;
        store.StatesChanged += OnStoreChanged;
    }

    /// <summary>Run to Click: runs to the line, or returns null when that isn't possible now (the program isn't paused).</summary>
    public Func<int, Task?>? RunToLine { get; init; }

    /// <summary>Opens the context menu of the breakpoint on a line.</summary>
    public Action<int, Avalonia.Controls.Control>? ShowMenu { get; init; }

    /// <summary>Stops listening to the store (the editor closed).</summary>
    public void Detach()
    {
        _store.Changed -= OnStoreChanged;
        _store.StatesChanged -= OnStoreChanged;
        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
        }
    }

    private void OnStoreChanged(object? sender, string? path)
    {
        if (path is null || Workbench.Projects.SolutionService.PathComparer.Equals(path, _path()))
        {
            Ui.Post(() =>
            {
                RebuildAnchors();
                InvalidateVisual();
                TextView?.InvalidateLayer(KnownLayer.Background);
            });
        }
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }

        InvalidateVisual();
    }

    protected override void OnDocumentChanged(TextDocument? oldDocument, TextDocument? newDocument)
    {
        if (oldDocument is not null)
        {
            oldDocument.Changed -= OnDocumentChanged;
        }

        base.OnDocumentChanged(oldDocument, newDocument);
        _document = newDocument;
        if (newDocument is not null && _relocate)
        {
            newDocument.Changed += OnDocumentChanged;
        }

        RebuildAnchors();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void RebuildAnchors()
    {
        _anchors.Clear();
        if (!_relocate || _document is not { } document)
        {
            return;
        }

        foreach (var breakpoint in _store.For(_path()))
        {
            if (breakpoint.Line >= 1 && breakpoint.Line <= document.LineCount)
            {
                var anchor = document.CreateAnchor(document.GetLineByNumber(breakpoint.Line).Offset);
                anchor.MovementType = AnchorMovementType.BeforeInsertion;
                anchor.SurviveDeletion = true;
                _anchors[breakpoint.Line] = anchor;
            }
        }
    }

    /// <summary>Breakpoints move with their lines (and stay on the line that survives a deletion).</summary>
    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_anchors.Count == 0 || _anchors.All(a => a.Value.Line == a.Key))
        {
            return;
        }

        var map = _anchors.ToDictionary(a => a.Key, a => a.Value.Line);
        _store.Relocate(_path(), line => map.TryGetValue(line, out var moved) ? moved : line);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, 0);

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (view is null || !view.VisualLinesValid)
        {
            return;
        }

        var breakpoints = _store.For(_path()).ToDictionary(b => b.Line);
        var current = _currentLine();
        foreach (var line in view.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
            var height = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineBottom) - view.VerticalOffset - top;
            var center = new Point(Size / 2, top + (height / 2));
            var radius = Math.Min(Size, height) / 2 - 2;
            if (breakpoints.TryGetValue(number, out var breakpoint))
            {
                var verified = _store.StateOf(breakpoint)?.Verified ?? true;
                var filled = breakpoint.Enabled && verified;
                if (breakpoint.IsLogpoint)
                {
                    var diamond = new PolylineGeometry(
                        [new Point(center.X, center.Y - radius), new Point(center.X + radius, center.Y), new Point(center.X, center.Y + radius), new Point(center.X - radius, center.Y)],
                        isFilled: true);
                    context.DrawGeometry(filled ? DebugColors.Breakpoint(this) : null, DebugColors.Outline(this), diamond);
                }
                else
                {
                    context.DrawEllipse(filled ? DebugColors.Breakpoint(this) : null, DebugColors.Outline(this), center, radius, radius);
                }

                if (breakpoint.Condition is not null || breakpoint.HitCondition is not null)
                {
                    context.DrawEllipse(Brushes.White, null, center, radius / 3, radius / 3);
                }
            }

            if (number == current)
            {
                var arrow = new PolylineGeometry(
                    [new Point(2, center.Y - 4), new Point(8, center.Y - 4), new Point(8, center.Y - 7), new Point(14, center.Y), new Point(8, center.Y + 7), new Point(8, center.Y + 4), new Point(2, center.Y + 4)],
                    isFilled: true);
                context.DrawGeometry(DebugColors.Current, DebugColors.ArrowOutline, arrow);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (TextView is not { } view || !(properties.IsLeftButtonPressed || properties.IsRightButtonPressed))
        {
            return;
        }

        var y = e.GetPosition(view).Y + view.VerticalOffset;
        if (view.GetVisualLineFromVisualTop(y) is not { } line)
        {
            return;
        }

        var number = line.FirstDocumentLine.LineNumber;
        if (properties.IsRightButtonPressed)
        {
            if (_store.At(_path(), number) is not null && ShowMenu is { } menu)
            {
                menu(number, this);
                e.Handled = true;
            }

            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && RunToLine?.Invoke(number) is not null)
        {
            e.Handled = true;
            return;
        }

        _store.Toggle(_path(), number);
        e.Handled = true;
    }
}

/// <summary>Line backgrounds: breakpoints (red) and the current statement (yellow, drawn last so it wins).</summary>
public sealed class DebugLineRenderer(Func<string> path, BreakpointStore store, Func<int?> currentLine) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid)
        {
            return;
        }

        var lines = store.For(path()).Where(b => b.Enabled && !b.IsLogpoint).Select(b => b.Line).ToHashSet();
        var current = currentLine();
        if (lines.Count == 0 && current is null)
        {
            return;
        }

        foreach (var line in textView.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var brush = number == current ? DebugColors.CurrentLine(textView) : lines.Contains(number) ? DebugColors.BreakpointLine(textView) : null;
            if (brush is null)
            {
                continue;
            }

            var top = line.VisualTop - textView.VerticalOffset;
            drawingContext.DrawRectangle(brush, null, new Rect(0, top, textView.Bounds.Width, line.Height));
        }
    }
}
