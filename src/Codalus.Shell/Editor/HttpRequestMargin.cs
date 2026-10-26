using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Codalus.Workbench.Http;

namespace Codalus.Shell.Editor;

/// <summary>
/// The send glyphs of a <c>.http</c> file (M15a): a green triangle on each request line; clicking it sends the request
/// (the classic IDE shows a "Send request" CodeLens there). Empty in other documents.
/// </summary>
public sealed class HttpRequestMargin : AbstractMargin
{
    private const double Size = 16;
    private static readonly IBrush s_brush = new SolidColorBrush(Color.FromRgb(0x38, 0x8A, 0x34));

    private readonly Func<bool> _enabled;
    private readonly Func<int, Task> _send;
    private TextDocument? _document;

    public HttpRequestMargin(Func<bool> enabled, Func<int, Task> send)
    {
        _enabled = enabled;
        _send = send;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Send request (Ctrl+Alt+R)");
    }

    /// <summary>The request lines (1-based) of the document, when it is a .http file.</summary>
    public IReadOnlyList<int> RequestLines =>
        _enabled() && _document is { } document
            ? [.. HttpFileParser.Parse(document.Text).Requests.Select(r => r.RequestLine)]
            : [];

    protected override void OnDocumentChanged(TextDocument? oldDocument, TextDocument? newDocument)
    {
        if (oldDocument is not null)
        {
            oldDocument.TextChanged -= OnTextChanged;
        }

        base.OnDocumentChanged(oldDocument, newDocument);
        _document = newDocument;
        if (newDocument is not null)
        {
            newDocument.TextChanged += OnTextChanged;
        }

        InvalidateMeasure();
    }

    private void OnTextChanged(object? sender, EventArgs e) => InvalidateVisual();

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

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>The language may change after the margin is created (a file renamed to .http).</summary>
    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(_enabled() ? Size : 0, 0);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (!_enabled() || TextView is not { VisualLinesValid: true } view || _document is not { } document)
        {
            return;
        }

        var lines = RequestLines.ToHashSet();
        foreach (var line in view.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            if (!lines.Contains(number) || number > document.LineCount)
            {
                continue;
            }

            var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
            var height = line.TextLines[0].Height;
            var middle = top + (height / 2);
            var half = Math.Min(10, height) / 2;
            var triangle = new PolylineGeometry([new Point(4, middle - half), new Point(4 + (half * 1.7), middle), new Point(4, middle + half)], isFilled: true);
            context.DrawGeometry(s_brush, null, triangle);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_enabled() || TextView is not { } view || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var y = e.GetPosition(view).Y + view.VerticalOffset;
        if (view.GetVisualLineFromVisualTop(y) is { } line && RequestLines.Contains(line.FirstDocumentLine.LineNumber))
        {
            _ = _send(line.FirstDocumentLine.LineNumber);
            e.Handled = true;
        }
    }
}
