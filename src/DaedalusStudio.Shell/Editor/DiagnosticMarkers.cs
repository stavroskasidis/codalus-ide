using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.Shell.Editor;

/// <summary>A diagnostic as a text segment, so it moves with edits until the next diagnostics arrive.</summary>
public sealed class DiagnosticSegment(Diagnostic diagnostic) : TextSegment
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>
/// Squiggles under errors (red), warnings (yellow) and information (blue), dots under hints, and faded text for
/// unnecessary code (unused usings). Drawn behind the text; the segments track edits.
/// </summary>
public sealed class DiagnosticMarkers : DocumentColorizingTransformer, IBackgroundRenderer
{
    private readonly TextSegmentCollection<DiagnosticSegment> _segments;
    private readonly TextDocument _document;

    public DiagnosticMarkers(TextDocument document)
    {
        _document = document;
        _segments = new TextSegmentCollection<DiagnosticSegment>(document);
    }

    public EditorTheme Theme { get; set; } = EditorTheme.Dark;

    public KnownLayer Layer => KnownLayer.Selection;

    public IEnumerable<DiagnosticSegment> Segments => _segments;

    /// <summary>Replaces the markers with <paramref name="diagnostics"/> (positions in the current text).</summary>
    public void Set(IReadOnlyList<Diagnostic> diagnostics, Func<Position, int> offsetAt)
    {
        _segments.Clear();
        foreach (var diagnostic in diagnostics)
        {
            var start = offsetAt(diagnostic.Range.Start);
            var end = offsetAt(diagnostic.Range.End);
            if (end <= start)
            {
                // Zero-width diagnostics (a missing semicolon) mark the next character, or the previous one at a line end.
                end = Math.Min(_document.TextLength, start + 1);
                if (end == start || _document.GetCharAt(start) is '\r' or '\n')
                {
                    start = Math.Max(0, start - 1);
                    end = Math.Max(end, start + 1);
                }
            }

            _segments.Add(new DiagnosticSegment(diagnostic) { StartOffset = start, EndOffset = Math.Min(end, _document.TextLength) });
        }
    }

    /// <summary>The diagnostics covering <paramref name="offset"/>, most severe first.</summary>
    public IReadOnlyList<Diagnostic> At(int offset) =>
        [.. _segments.FindSegmentsContaining(offset).Concat(_segments.FindSegmentsContaining(Math.Max(0, offset - 1)))
            .Distinct()
            .Select(s => s.Diagnostic)
            .Where(d => d.Severity != DiagnosticSeverity.Hint || d.Tags is null)
            .OrderBy(d => d.Severity)];

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0)
        {
            return;
        }

        var start = textView.VisualLines[0].FirstDocumentLine.Offset;
        var end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (var segment in _segments.FindOverlappingSegments(start, end - start))
        {
            var diagnostic = segment.Diagnostic;
            if (diagnostic.Tags?.Contains(DiagnosticTag.Unnecessary) == true && diagnostic.Severity == DiagnosticSeverity.Hint)
            {
                continue; // faded instead (ColorizeLine)
            }

            var brush = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => Theme.Error,
                DiagnosticSeverity.Warning => Theme.Warning,
                DiagnosticSeverity.Information => Theme.Info,
                _ => Theme.Hint,
            };
            var pen = new Pen(brush, 1);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                if (diagnostic.Severity == DiagnosticSeverity.Hint)
                {
                    // Three dots under the start, like other IDEs' suggestions.
                    for (var i = 0; i < 3; i++)
                    {
                        drawingContext.DrawRectangle(brush, null, new Rect(rect.Left + (i * 3), rect.Bottom - 1.5, 1.5, 1.5));
                    }

                    break;
                }

                drawingContext.DrawGeometry(null, pen, Squiggle(rect));
            }
        }
    }

    private static StreamGeometry Squiggle(Rect rect)
    {
        const double step = 2.5;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var y = rect.Bottom - 1;
        var width = Math.Max(rect.Width, step * 2);
        context.BeginFigure(new Point(rect.Left, y), isFilled: false);
        var up = true;
        for (var x = rect.Left + step; x <= rect.Left + width; x += step)
        {
            context.LineTo(new Point(x, up ? y - step : y));
            up = !up;
        }

        context.EndFigure(isClosed: false);
        return geometry;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var segment in _segments.FindOverlappingSegments(line.Offset, line.Length))
        {
            if (segment.Diagnostic.Tags?.Contains(DiagnosticTag.Unnecessary) != true)
            {
                continue;
            }

            var start = Math.Max(line.Offset, segment.StartOffset);
            var end = Math.Min(line.EndOffset, segment.EndOffset);
            if (end > start)
            {
                ChangeLinePart(start, end, element =>
                {
                    var color = element.TextRunProperties.ForegroundBrush is ISolidColorBrush solid ? solid.Color : Colors.Gray;
                    element.TextRunProperties.SetForegroundBrush(new ImmutableSolidColorBrush(color, 0.5));
                });
            }
        }
    }
}
