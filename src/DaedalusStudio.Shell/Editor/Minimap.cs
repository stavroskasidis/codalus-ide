using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace DaedalusStudio.Shell.Editor;

/// <summary>
/// A miniature of the document beside the editor: two pixels per line, words drawn as blocks in their token colors
/// (where the language service has colored them, otherwise the text color), with the visible area shaded. When the
/// document is taller than the minimap, the minimap scrolls along with the editor. Click or drag to scroll. Only the
/// lines shown are drawn, so cost doesn't grow with the document.
/// </summary>
public sealed class Minimap : Control
{
    public const double LineHeight = 2;
    private const double CharWidth = 1;
    private const int MaxColumns = 120;

    private readonly TextEditor _editor;
    private readonly SemanticColorizer _colorizer;
    private readonly DispatcherTimer _redraw;
    private bool _dragging;

    public Minimap(TextEditor editor, SemanticColorizer colorizer)
    {
        _editor = editor;
        _colorizer = colorizer;
        Width = 96;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
        _redraw = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _redraw.Tick += (_, _) =>
        {
            _redraw.Stop();
            InvalidateVisual();
        };
        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        editor.TextArea.TextView.VisualLinesChanged += (_, _) => ScheduleRedraw();
        editor.Document.Changed += (_, _) => ScheduleRedraw();
        AutomationProperties.SetName(this, "Minimap");
    }

    public static readonly StyledProperty<IBrush> ForegroundProperty = AvaloniaProperty.Register<Minimap, IBrush>(nameof(Foreground), Brushes.Gray);

    static Minimap() => AffectsRender<Minimap>(ForegroundProperty);

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush ViewportBrush { get; set; } = new ImmutableSolidColorBrush(0x30808080);

    private void ScheduleRedraw()
    {
        if (!_redraw.IsEnabled)
        {
            _redraw.Start();
        }
    }

    private TextDocument Document => _editor.Document;

    /// <summary>The first document line (zero-based) shown at the top of the minimap.</summary>
    public int FirstLine
    {
        get
        {
            var capacity = Capacity;
            var total = Document.LineCount;
            if (total <= capacity)
            {
                return 0;
            }

            var view = _editor.TextArea.TextView;
            var scrollable = Math.Max(1, view.DocumentHeight - view.Bounds.Height);
            var fraction = Math.Clamp(view.VerticalOffset / scrollable, 0, 1);
            return (int)Math.Round(fraction * (total - capacity));
        }
    }

    private int Capacity => Math.Max(1, (int)(Bounds.Height / LineHeight));

    public override void Render(DrawingContext context)
    {
        var view = _editor.TextArea.TextView;
        var first = FirstLine;
        var last = Math.Min(Document.LineCount, first + Capacity);
        var foreground = Foreground is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, 0.6) : Foreground;
        for (var line = first; line < last; line++)
        {
            var documentLine = Document.GetLineByNumber(line + 1);
            var y = (line - first) * LineHeight;
            var length = Math.Min(documentLine.Length, MaxColumns);
            var column = 0;
            while (column < length)
            {
                while (column < length && char.IsWhiteSpace(Document.GetCharAt(documentLine.Offset + column)))
                {
                    column++;
                }

                var start = column;
                while (column < length && !char.IsWhiteSpace(Document.GetCharAt(documentLine.Offset + column)))
                {
                    column++;
                }

                if (column > start)
                {
                    var type = _colorizer.TokenAt(line, start);
                    var brush = type >= 0 && _colorizer.Theme.Token(type) is ISolidColorBrush token ? new ImmutableSolidColorBrush(token.Color, 0.8) : foreground;
                    context.DrawRectangle(brush, null, new Rect(start * CharWidth + 4, y, (column - start) * CharWidth, LineHeight - 0.5));
                }
            }
        }

        // The visible area.
        if (view.VisualLinesValid && view.VisualLines.Count > 0)
        {
            var top = view.VisualLines[0].FirstDocumentLine.LineNumber - 1 - first;
            var count = view.VisualLines[^1].LastDocumentLine.LineNumber - view.VisualLines[0].FirstDocumentLine.LineNumber + 1;
            context.DrawRectangle(ViewportBrush, null, new Rect(0, top * LineHeight, Bounds.Width, Math.Max(4, count * LineHeight)));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        ScrollTo(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            ScrollTo(e.GetPosition(this).Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>Centers the editor on the line at <paramref name="y"/>.</summary>
    private void ScrollTo(double y)
    {
        var view = _editor.TextArea.TextView;
        var line = Math.Clamp(FirstLine + (int)(y / LineHeight), 0, Document.LineCount - 1);
        var lineHeight = view.DefaultLineHeight;
        var offset = (line * lineHeight) - (view.Bounds.Height / 2);
        _editor.ScrollToVerticalOffset(Math.Clamp(offset, 0, Math.Max(0, view.DocumentHeight - view.Bounds.Height)));
    }
}
