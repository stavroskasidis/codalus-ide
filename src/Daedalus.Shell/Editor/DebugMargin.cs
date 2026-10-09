using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Daedalus.Shell.Infrastructure;
using Daedalus.Workbench.Debugging;

namespace Daedalus.Shell.Editor;

/// <summary>Colors of the debugger's editor adornments (readable on light, dark and high-contrast backgrounds).</summary>
internal static class DebugColors
{
    public static readonly IBrush Breakpoint = new ImmutableSolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00));
    public static readonly IBrush BreakpointLine = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0xE5, 0x14, 0x00));
    public static readonly IBrush Current = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
    public static readonly IBrush CurrentLine = new ImmutableSolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xCC, 0x00));
    public static readonly IPen Outline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00)), 1.5);
    public static readonly IPen ArrowOutline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x80, 0x60, 0x00)), 1);
}

/// <summary>
/// The breakpoint margin (ADR 0007): a filled circle per breakpoint, hollow when disabled or not verified by the
/// debugger, a diamond for logpoints, a dot inside for conditions and hit counts; an arrow on the current statement.
/// Clicking toggles a breakpoint. With <paramref name="relocate"/>, breakpoints follow edits to the document.
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
    }

    /// <summary>Stops listening to the store (the editor closed).</summary>
    public void Detach()
    {
        _store.Changed -= OnStoreChanged;
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
                    context.DrawGeometry(filled ? DebugColors.Breakpoint : null, DebugColors.Outline, diamond);
                }
                else
                {
                    context.DrawEllipse(filled ? DebugColors.Breakpoint : null, DebugColors.Outline, center, radius, radius);
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
        if (TextView is not { } view || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var y = e.GetPosition(view).Y + view.VerticalOffset;
        if (view.GetVisualLineFromVisualTop(y) is { } line)
        {
            _store.Toggle(_path(), line.FirstDocumentLine.LineNumber);
            e.Handled = true;
        }
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
            var brush = number == current ? DebugColors.CurrentLine : lines.Contains(number) ? DebugColors.BreakpointLine : null;
            if (brush is null)
            {
                continue;
            }

            var top = line.VisualTop - textView.VerticalOffset;
            drawingContext.DrawRectangle(brush, null, new Rect(0, top, textView.Bounds.Width, line.Height));
        }
    }
}
