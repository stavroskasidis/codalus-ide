using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace Codalus.Shell.Editor;

/// <summary>
/// A miniature of the document beside the editor: two pixels per line, words drawn as blocks in their token colors
/// (where the language service, or a diff's grammar, has colored them, otherwise the text color), the lines its source
/// marks (a diff's added and removed lines) on a band with a stripe at the left edge, and the visible area shaded. When the
/// document is taller than the minimap, the minimap scrolls along with the editor. A click centers the editor on the
/// line clicked; the visible area drags like a scroll bar's thumb. Only the lines shown are drawn, so cost doesn't grow
/// with the document.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A control: its bitmap is disposed when it leaves the window (OnDetachedFromVisualTree).")]
public sealed class Minimap : Control
{
    public const double LineHeight = 2;
    private const double CharWidth = 1;
    private const int MaxColumns = 120;

    private readonly TextEditor _editor;
    private readonly IMinimapColors _colors;
    private readonly DispatcherTimer _redraw;
    private bool _dragging;

    /// <summary>Where the pointer holds the visible area while dragging (from its top), or null until it's known.</summary>
    private double? _grab;

    public Minimap(TextEditor editor, IMinimapColors colors)
    {
        _editor = editor;
        _colors = colors;
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

    /// <summary>The band under an added line (a diff's <c>Shell.Diff.Added.Line</c>).</summary>
    public static readonly StyledProperty<IBrush?> AddedLineProperty = AvaloniaProperty.Register<Minimap, IBrush?>(nameof(AddedLine));

    /// <summary>The band under a removed line (a diff's <c>Shell.Diff.Removed.Line</c>).</summary>
    public static readonly StyledProperty<IBrush?> RemovedLineProperty = AvaloniaProperty.Register<Minimap, IBrush?>(nameof(RemovedLine));

    /// <summary>
    /// The stripe at the left edge of an added line (<c>Shell.Diff.Added</c>): the mark that shows in every theme, on
    /// High contrast's black bands too.
    /// </summary>
    public static readonly StyledProperty<IBrush?> AddedMarkProperty = AvaloniaProperty.Register<Minimap, IBrush?>(nameof(AddedMark));

    /// <summary>The stripe at the left edge of a removed line (<c>Shell.Diff.Removed</c>).</summary>
    public static readonly StyledProperty<IBrush?> RemovedMarkProperty = AvaloniaProperty.Register<Minimap, IBrush?>(nameof(RemovedMark));

    /// <summary>The width of a marked line's stripe.</summary>
    public const double MarkWidth = 3;

    static Minimap() => AffectsRender<Minimap>(ForegroundProperty, AddedLineProperty, RemovedLineProperty, AddedMarkProperty, RemovedMarkProperty);

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? AddedLine
    {
        get => GetValue(AddedLineProperty);
        set => SetValue(AddedLineProperty, value);
    }

    public IBrush? RemovedLine
    {
        get => GetValue(RemovedLineProperty);
        set => SetValue(RemovedLineProperty, value);
    }

    public IBrush? AddedMark
    {
        get => GetValue(AddedMarkProperty);
        set => SetValue(AddedMarkProperty, value);
    }

    public IBrush? RemovedMark
    {
        get => GetValue(RemovedMarkProperty);
        set => SetValue(RemovedMarkProperty, value);
    }

    /// <summary>
    /// Draws the minimap again from its source: what it drew of each line is kept until the text or the source's
    /// <see cref="IMinimapColors.Version"/> changes, so a source calls this after its version did.
    /// </summary>
    public void Refresh() => ScheduleRedraw();

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

    /// <summary>The shaded part of the minimap: the lines the editor shows (null before its first layout).</summary>
    internal Rect? VisibleArea
    {
        get
        {
            var view = _editor.TextArea.TextView;
            if (!view.VisualLinesValid || view.VisualLines.Count == 0)
            {
                return null;
            }

            var top = view.VisualLines[0].FirstDocumentLine.LineNumber - 1 - FirstLine;
            var count = view.VisualLines[^1].LastDocumentLine.LineNumber - view.VisualLines[0].FirstDocumentLine.LineNumber + 1;
            return new Rect(0, top * LineHeight, Bounds.Width, Math.Max(4, count * LineHeight));
        }
    }

    /// <summary>The words of a line drawn: columns and color (null: the text color).</summary>
    private readonly record struct Word(int Start, int Length, IBrush? Brush);

    /// <summary>
    /// The words of the lines last drawn (zero-based), kept while the text and its tokens stay the same: scrolling reads
    /// only the lines it brings in. Each frame read every character shown, one at a time from the document's rope.
    /// </summary>
    private Dictionary<int, Word[]> _words = [];
    private Dictionary<int, Word[]> _spareWords = [];
    private (ITextSourceVersion? Text, int Tokens) _wordsOf;

    /// <summary>
    /// The miniature, painted pixel by pixel and drawn as one image. It was a rectangle per word, a few thousand a frame,
    /// each with a brush of its own: a tenth of the UI thread's time while the 100k-line file scrolled, and as many
    /// shapes for the compositor to rasterize.
    /// </summary>
    private WriteableBitmap? _bitmap;
    private readonly Dictionary<IBrush, uint> _drawn = [];

    /// <summary>The lines whose words were read (tests: scrolling reads only the lines it brings in).</summary>
    internal int LinesRead { get; private set; }

    public override void Render(DrawingContext context)
    {
        // A control is hit only where it draws: without this, a click where no word is drawn (beside short lines, below
        // the end of the file) went to the document behind. Transparent, so its column keeps the card's background.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var size = new PixelSize((int)Math.Ceiling(Bounds.Width * scale), (int)Math.Ceiling(Bounds.Height * scale));
        if (size.Width > 0 && size.Height > 0)
        {
            if (_bitmap is null || _bitmap.PixelSize != size || Math.Abs(_bitmap.Dpi.X - (96 * scale)) > 0.001)
            {
                // The last frame's may still be drawn: it's left to the garbage collector, not disposed.
                _bitmap = new WriteableBitmap(size, new Vector(96 * scale, 96 * scale), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }

            Paint(_bitmap, scale);
            context.DrawImage(_bitmap, new Rect(0, 0, size.Width / scale, size.Height / scale));
        }

        if (VisibleArea is { } area)
        {
            context.DrawRectangle(ViewportBrush, null, area);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _bitmap?.Dispose();
        _bitmap = null;
        _words.Clear();
    }

    private unsafe void Paint(WriteableBitmap bitmap, double scale)
    {
        // The text's version changes with every edit and is another document's when the document is.
        if (_wordsOf.Text != Document.Version || _wordsOf.Tokens != _colors.Version)
        {
            _wordsOf = (Document.Version, _colors.Version);
            _words.Clear();
        }

        _drawn.Clear();
        var foreground = Foreground is ISolidColorBrush solid ? Premultiplied(solid.Color, 0.6) : Premultiplied(Colors.Gray, 0.6);
        var addedLine = Solid(AddedLine);
        var removedLine = Solid(RemovedLine);
        var addedMark = Solid(AddedMark);
        var removedMark = Solid(RemovedMark);
        var first = FirstLine;
        var last = Math.Min(Document.LineCount, first + Capacity);
        using var buffer = bitmap.Lock();
        var stride = buffer.RowBytes / sizeof(uint);
        var pixels = new Span<uint>((void*)buffer.Address, stride * buffer.Size.Height);
        pixels.Clear();
        var shown = _spareWords;
        for (var line = first; line < last; line++)
        {
            if (!_words.TryGetValue(line, out var words))
            {
                words = Words(line);
            }

            shown[line] = words;
            var top = (line - first) * LineHeight * scale;
            var bottom = top + ((LineHeight - 0.5) * scale);
            if (_colors.MarkOf(line) is var mark and not MinimapMark.None)
            {
                // The band takes the line's whole height, its gap too, so a run of changed lines reads as one block. Its
                // edges are on whole pixels: at 125% a line is 2.5 px, and half-covered rows shared by two lines' bands
                // left a lighter seam between them.
                var (band, stripe) = mark == MinimapMark.Added ? (addedLine, addedMark) : (removedLine, removedMark);
                var bandTop = Math.Round(top);
                var bandBottom = Math.Round(top + (LineHeight * scale));
                if (band != 0)
                {
                    Fill(pixels, stride, buffer.Size, 0, buffer.Size.Width, bandTop, bandBottom, band);
                }

                if (stripe != 0)
                {
                    Fill(pixels, stride, buffer.Size, 0, Math.Round(MarkWidth * scale), bandTop, bandBottom, stripe);
                }
            }

            foreach (var word in words)
            {
                var color = word.Brush is null ? foreground : WordColor(word.Brush, foreground);
                Fill(pixels, stride, buffer.Size, ((word.Start * CharWidth) + 4) * scale, ((word.Start + word.Length) * CharWidth + 4) * scale, top, bottom, color);
            }
        }

        _words.Clear();
        (_words, _spareWords) = (shown, _words);
    }

    private Word[] Words(int line)
    {
        LinesRead++;
        var documentLine = Document.GetLineByNumber(line + 1);
        var text = Document.GetText(documentLine.Offset, Math.Min(documentLine.Length, MaxColumns));
        var words = new List<Word>();
        var column = 0;
        while (column < text.Length)
        {
            while (column < text.Length && char.IsWhiteSpace(text[column]))
            {
                column++;
            }

            var start = column;
            while (column < text.Length && !char.IsWhiteSpace(text[column]))
            {
                column++;
            }

            if (column > start)
            {
                words.Add(new Word(start, column - start, _colors.WordBrush(line, start)));
            }
        }

        return [.. words];
    }

    /// <summary>A word's color, as drawn: its brush's, or the text color for a brush that isn't a solid color.</summary>
    private uint WordColor(IBrush brush, uint foreground)
    {
        if (!_drawn.TryGetValue(brush, out var color))
        {
            _drawn[brush] = color = brush is ISolidColorBrush token ? Premultiplied(token.Color, 0.8) : foreground;
        }

        return color;
    }

    /// <summary>A band's or a stripe's color, as drawn (0: none).</summary>
    private static uint Solid(IBrush? brush) => brush is ISolidColorBrush solid ? Premultiplied(solid.Color, solid.Opacity) : 0;

    private static uint Premultiplied(Color color, double opacity)
    {
        var alpha = color.A / 255.0 * opacity;
        return Pack(alpha, color.R / 255.0 * alpha, color.G / 255.0 * alpha, color.B / 255.0 * alpha);
    }

    private static uint Pack(double a, double r, double g, double b) =>
        ((uint)Math.Round(a * 255) << 24) | ((uint)Math.Round(r * 255) << 16) | ((uint)Math.Round(g * 255) << 8) | (uint)Math.Round(b * 255);

    /// <summary>
    /// Fills a rectangle in device pixels, its edges' pixels in proportion to how much of them it covers, as a vector
    /// rectangle is drawn, over what's drawn there already (a marked line's band and stripe, under its words). Words
    /// never overlap each other (whitespace and the gap between lines part them), but they do lie on bands: written
    /// over instead of blended, a word's soft edges and its 80% color let the background through holes in the band.
    /// </summary>
    private static void Fill(Span<uint> pixels, int stride, PixelSize size, double left, double right, double top, double bottom, uint color)
    {
        right = Math.Min(right, size.Width);
        bottom = Math.Min(bottom, size.Height);
        for (var y = (int)top; y < bottom; y++)
        {
            var rowCoverage = Math.Min(bottom, y + 1) - Math.Max(top, y);
            var row = pixels.Slice(y * stride, size.Width);
            for (var x = (int)left; x < right; x++)
            {
                var coverage = rowCoverage * (Math.Min(right, x + 1) - Math.Max(left, x));
                var source = coverage >= 0.999 ? color : Scale(color, coverage);
                row[x] = row[x] == 0 || source >> 24 == 0xFF ? source : Over(source, row[x]);
            }
        }
    }

    private static uint Scale(uint color, double coverage) =>
        Pack((color >> 24) / 255.0 * coverage, ((color >> 16) & 0xFF) / 255.0 * coverage, ((color >> 8) & 0xFF) / 255.0 * coverage, (color & 0xFF) / 255.0 * coverage);

    /// <summary>A premultiplied color drawn over another (source over).</summary>
    internal static uint Over(uint source, uint destination)
    {
        var keep = 255 - (source >> 24);
        uint Channel(int shift) => Math.Min(255u, ((source >> shift) & 0xFF) + ((((destination >> shift) & 0xFF) * keep) + 127) / 255);
        return (Channel(24) << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        var y = e.GetPosition(this).Y;
        if (VisibleArea is { } area && y >= area.Top && y < area.Bottom)
        {
            // On the visible area: it's held where it was pressed, as a scroll bar's thumb.
            _grab = y - area.Top;
        }
        else
        {
            // Elsewhere: the editor jumps there, and the visible area is held where it lands once laid out.
            ScrollTo(y);
            _grab = null;
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
        {
            return;
        }

        var y = e.GetPosition(this).Y;
        if (_grab is null && VisibleArea is { } area)
        {
            _grab = Math.Clamp(y - area.Top, 0, area.Height);
        }

        if (_grab is { } grab)
        {
            DragTo(y - grab);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        _grab = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
        _grab = null;
    }

    /// <summary>Centers the editor on the line at <paramref name="y"/>.</summary>
    private void ScrollTo(double y)
    {
        var view = _editor.TextArea.TextView;
        var line = Math.Clamp(FirstLine + (int)(y / LineHeight), 0, Document.LineCount - 1);
        var lineHeight = view.DefaultLineHeight;
        var offset = (line * lineHeight) - (view.Bounds.Height / 2);
        _editor.ScrollToY(offset);
    }

    /// <summary>
    /// Scrolls the editor so the visible area's top is at <paramref name="top"/>. When the minimap scrolls with the
    /// editor, the visible area's top moves from 0 to the minimap's capacity less the editor's lines as the editor
    /// scrolls from its top to its bottom (see <see cref="FirstLine"/>), so the pointer keeps its place on it.
    /// </summary>
    private void DragTo(double top)
    {
        var view = _editor.TextArea.TextView;
        var scrollable = Math.Max(0, view.DocumentHeight - view.Bounds.Height);
        var lineHeight = Math.Max(1, view.DefaultLineHeight);
        double offset;
        if (Document.LineCount <= Capacity)
        {
            offset = top / LineHeight * lineHeight;
        }
        else
        {
            var travel = Math.Max(LineHeight, (Capacity - (view.Bounds.Height / lineHeight)) * LineHeight);
            offset = Math.Clamp(top / travel, 0, 1) * scrollable;
        }

        _editor.ScrollToY(offset);
    }
}
