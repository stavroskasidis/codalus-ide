using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Codalus.Shell.Editor;

/// <summary>
/// Backgrounds behind the text: references of the symbol at the caret (reads and writes, from the language service)
/// and the matching pair of a bracket next to the caret.
/// </summary>
public sealed class HighlightRenderer(TextDocument document) : IBackgroundRenderer
{
    private readonly TextSegmentCollection<HighlightSegment> _segments = new(document);

    public EditorTheme Theme { get; set; } = EditorTheme.Dark;

    public KnownLayer Layer => KnownLayer.Background;

    public enum Kind
    {
        Read,
        Write,
        Bracket,
    }

    public sealed class HighlightSegment(Kind kind) : TextSegment
    {
        public Kind Kind { get; } = kind;
    }

    public int Count(Kind kind) => _segments.Count(s => s.Kind == kind);

    public void Set(Kind kind, IEnumerable<(int Start, int End)> ranges)
    {
        foreach (var segment in _segments.Where(s => s.Kind == kind).ToList())
        {
            _segments.Remove(segment);
        }

        foreach (var (start, end) in ranges)
        {
            if (end > start && end <= document.TextLength)
            {
                _segments.Add(new HighlightSegment(kind) { StartOffset = start, EndOffset = end });
            }
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0 || _segments.Count == 0)
        {
            return;
        }

        var start = textView.VisualLines[0].FirstDocumentLine.Offset;
        var end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (var segment in _segments.FindOverlappingSegments(start, end - start))
        {
            var brush = segment.Kind switch
            {
                Kind.Write => Theme.HighlightWrite,
                Kind.Bracket => Theme.Bracket,
                _ => Theme.HighlightRead,
            };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                drawingContext.DrawRectangle(brush, null, new RoundedRect(rect, 2));
            }
        }
    }

    /// <summary>
    /// The bracket next to <paramref name="caret"/> and its match, scanning at most <paramref name="limit"/>
    /// characters. Brackets inside strings and comments are not told apart (good enough for highlighting).
    /// </summary>
    public static (int Open, int Close)? MatchBracket(TextDocument document, int caret, int limit = 20_000)
    {
        const string opening = "([{";
        const string closing = ")]}";
        foreach (var offset in new[] { caret, caret - 1 })
        {
            if (offset < 0 || offset >= document.TextLength)
            {
                continue;
            }

            var c = document.GetCharAt(offset);
            if (opening.IndexOf(c, StringComparison.Ordinal) is var open and >= 0)
            {
                var match = Scan(document, offset, +1, c, closing[open], limit);
                return match is { } m ? (offset, m) : null;
            }

            if (closing.IndexOf(c, StringComparison.Ordinal) is var close and >= 0)
            {
                var match = Scan(document, offset, -1, c, opening[close], limit);
                return match is { } m ? (m, offset) : null;
            }
        }

        return null;
    }

    private static int? Scan(TextDocument document, int from, int direction, char self, char other, int limit)
    {
        var depth = 0;
        for (int i = from, steps = 0; i >= 0 && i < document.TextLength && steps < limit; i += direction, steps++)
        {
            var c = document.GetCharAt(i);
            if (c == self)
            {
                depth++;
            }
            else if (c == other && --depth == 0)
            {
                return i;
            }
        }

        return null;
    }
}
