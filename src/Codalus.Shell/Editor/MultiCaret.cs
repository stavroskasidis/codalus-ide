using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Codalus.Shell.Editor;

/// <summary>A selection (or a caret, when empty) as offsets; <see cref="Caret"/> is the end the caret sits at.</summary>
public readonly record struct CaretSelection(int Anchor, int Caret)
{
    public int Start => Math.Min(Anchor, Caret);

    public int End => Math.Max(Anchor, Caret);

    public int Length => End - Start;

    public static CaretSelection At(int offset) => new(offset, offset);
}

/// <summary>
/// The pure part of multiple carets: applying an edit to every selection at once and computing where the carets end
/// up. Overlapping selections merge. Unit-tested without a view.
/// </summary>
public static class CaretEdits
{
    /// <summary>Merges overlapping or touching selections; result sorted by start.</summary>
    public static List<CaretSelection> Normalize(IEnumerable<CaretSelection> selections)
    {
        var result = new List<CaretSelection>();
        foreach (var selection in selections.OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            // Overlapping selections, or two carets at the same place.
            if (result.Count > 0 && (selection.Start < result[^1].End || (selection.Start == result[^1].Start && selection.End == result[^1].End)))
            {
                var last = result[^1];
                result[^1] = new CaretSelection(Math.Min(last.Start, selection.Start), Math.Max(last.End, selection.End));
                continue;
            }

            result.Add(selection);
        }

        return result;
    }

    /// <summary>
    /// The edits (descending, so each is valid when applied in order) that replace every selection — or for empty
    /// selections, the range <paramref name="emptyRange"/> returns (backspace, delete) — with <paramref name="text"/>,
    /// and the carets afterwards.
    /// </summary>
    public static (List<(int Offset, int Length, string Text)> Edits, List<int> Carets) Replace(
        IReadOnlyList<CaretSelection> selections,
        Func<string> textFor,
        Func<int, (int Start, int End)>? emptyRange = null)
    {
        var ranges = Normalize(selections)
            .Select(s => s.Length == 0 && emptyRange is not null ? emptyRange(s.Caret) : (s.Start, s.End))
            .ToList();

        // Ranges widened for backspace/delete may now overlap: merge again.
        ranges.Sort();
        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start < merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        var edits = new List<(int, int, string)>();
        var carets = new List<int>();
        var shift = 0;
        foreach (var (start, end) in merged)
        {
            var text = textFor();
            edits.Add((start, end - start, text));
            carets.Add(start + shift + text.Length);
            shift += text.Length - (end - start);
        }

        edits.Reverse();
        return (edits, carets);
    }
}

/// <summary>
/// Extra carets on top of AvaloniaEdit's single caret (which stays the primary one): Ctrl+Alt+Click,
/// Ctrl+Alt+Up/Down, Shift+Alt+. (next match) and Shift+Alt+; (all matches). Typing, Backspace, Delete, Enter and the
/// arrow keys apply to every caret as one undo step; Escape or a plain click drops the extra carets. Box selection is
/// AvaloniaEdit's own (Alt+drag).
/// </summary>
public sealed class MultiCaretController : IBackgroundRenderer, IDisposable
{
    private readonly TextArea _area;

    /// <summary>The extra selections, in order of start: drawing looks up the visible ones (M56).</summary>
    private readonly List<CaretSelection> _extra = [];
    private bool _applying;

    public MultiCaretController(TextArea area)
    {
        _area = area;
        area.TextView.BackgroundRenderers.Add(this);
        area.Document.Changed += OnDocumentChanged;
    }

    public IReadOnlyList<CaretSelection> ExtraCarets => _extra;

    public KnownLayer Layer => KnownLayer.Caret;

    public event EventHandler? Changed;

    private TextDocument Document => _area.Document;

    private CaretSelection Primary
    {
        get
        {
            var caret = _area.Caret.Offset;
            if (_area.Selection.IsEmpty)
            {
                return CaretSelection.At(caret);
            }

            var segment = _area.Selection.SurroundingSegment;
            return new CaretSelection(caret == segment.Offset ? segment.EndOffset : segment.Offset, caret);
        }
    }

    /// <summary>All selections, the primary first.</summary>
    public IReadOnlyList<CaretSelection> All => [Primary, .. _extra];

    /// <summary>The extra carets drawn by the last frame (tests: only the visible ones are).</summary>
    internal int DrawnCarets { get; private set; }

    public void AddCaret(int offset)
    {
        offset = Math.Clamp(offset, 0, Document.TextLength);
        if (All.Any(s => s.Caret == offset))
        {
            return;
        }

        _extra.Insert(FirstStartingAt(offset), CaretSelection.At(offset));
        Update();
    }

    /// <summary>The index of the first extra selection that starts at or after <paramref name="offset"/>.</summary>
    private int FirstStartingAt(int offset)
    {
        int low = 0, high = _extra.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_extra[middle].Start < offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Adds a caret on the line above (-1) or below (+1) the outermost caret, at the same column.</summary>
    public void AddCaretVertically(int direction)
    {
        var outermost = direction < 0 ? All.MinBy(s => s.Caret) : All.MaxBy(s => s.Caret);
        var location = Document.GetLocation(outermost.Caret);
        var line = location.Line + direction;
        if (line < 1 || line > Document.LineCount)
        {
            return;
        }

        var target = Document.GetLineByNumber(line);
        AddCaret(target.Offset + Math.Min(location.Column - 1, target.Length));
    }

    /// <summary>
    /// Selects the word at the caret, or adds the next occurrence of the selected text as another selection
    /// (<paramref name="all"/>: every occurrence).
    /// </summary>
    public void SelectNextOccurrence(bool all = false)
    {
        var primary = Primary;
        if (primary.Length == 0)
        {
            var (start, end) = WordAt(primary.Caret);
            if (end > start)
            {
                _area.Selection = Selection.Create(_area, start, end);
                _area.Caret.Offset = end;
            }

            if (!all)
            {
                return;
            }

            primary = Primary;
        }

        var text = Document.GetText(primary.Start, primary.Length);
        if (text.Length == 0)
        {
            return;
        }

        // One pass over the text, wrapping around: every occurrence not selected yet (each one was a search from the
        // start and a copy of every selection, which took seconds at 20,000), or the next one after the selection added
        // last, the furthest from the primary going forward.
        var taken = new HashSet<int>(_extra.Select(s => s.Start)) { primary.Start };
        var from = primary.End;
        if (!all && _extra.Count > 0)
        {
            var before = FirstStartingAt(primary.Start);
            from = (before > 0 ? _extra[before - 1] : _extra[^1]).End;
        }

        var added = new List<CaretSelection>();
        foreach (var next in Occurrences(Document.Text, text, from))
        {
            if (taken.Add(next))
            {
                added.Add(new CaretSelection(next, next + text.Length));
                if (!all)
                {
                    break;
                }
            }
        }

        if (added.Count > 0)
        {
            var merged = new List<CaretSelection>(_extra.Count + added.Count);
            merged.AddRange(_extra);
            merged.AddRange(added);
            merged.Sort((a, b) => a.Start.CompareTo(b.Start));
            _extra.Clear();
            _extra.AddRange(merged);
            Update();
        }
    }

    /// <summary>
    /// The starts of <paramref name="text"/>'s occurrences, not overlapping each other, from <paramref name="from"/> to the
    /// end, then from the start of the text to <paramref name="from"/>.
    /// </summary>
    private static IEnumerable<int> Occurrences(string content, string text, int from)
    {
        for (var next = content.IndexOf(text, from, StringComparison.Ordinal); next >= 0; next = content.IndexOf(text, next + text.Length, StringComparison.Ordinal))
        {
            yield return next;
        }

        for (var next = content.IndexOf(text, 0, StringComparison.Ordinal); next >= 0 && next + text.Length <= from; next = content.IndexOf(text, next + text.Length, StringComparison.Ordinal))
        {
            yield return next;
        }
    }

    public void Clear()
    {
        if (_extra.Count > 0)
        {
            _extra.Clear();
            Update();
        }
    }

    /// <summary>Types <paramref name="text"/> at every caret (replacing selections).</summary>
    public void Type(string text) => Apply(CaretEdits.Replace(All, () => text));

    /// <summary>Handles editing and movement keys while there are extra carets. Returns true when handled.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var word = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Back:
                Apply(CaretEdits.Replace(All, () => string.Empty, caret => word ? (WordStartBefore(caret), caret) : (Math.Max(0, caret - PreviousCharLength(caret)), caret)));
                return true;
            case Key.Delete:
                Apply(CaretEdits.Replace(All, () => string.Empty, caret => (caret, Math.Min(Document.TextLength, caret + NextCharLength(caret)))));
                return true;
            case Key.Enter:
                var newline = TextUtilities.GetNewLineFromDocument(Document, _area.Caret.Line);
                Apply(CaretEdits.Replace(All, () => newline));
                return true;
            case Key.Tab when !shift:
                Type(_area.Options.IndentationString);
                return true;
            case Key.Left or Key.Right or Key.Home or Key.End:
                Move(e.Key, shift, word);
                return true;
            case Key.Up or Key.Down:
                Clear(); // AvaloniaEdit moves the primary caret
                return false;
            default:
                return false;
        }
    }

    private void Move(Key key, bool extend, bool word)
    {
        int Target(int caret) => key switch
        {
            Key.Left => word ? WordStartBefore(caret) : Math.Max(0, caret - PreviousCharLength(caret)),
            Key.Right => word ? WordEndAfter(caret) : Math.Min(Document.TextLength, caret + NextCharLength(caret)),
            Key.Home => Document.GetLineByOffset(caret).Offset + LeadingWhitespace(Document.GetLineByOffset(caret)),
            _ => Document.GetLineByOffset(caret).EndOffset,
        };

        var moved = All.Select(s => extend ? s with { Caret = Target(s.Caret) } : CaretSelection.At(s.Length > 0 && key is Key.Left or Key.Right ? (key == Key.Left ? s.Start : s.End) : Target(s.Caret))).ToList();
        SetAll(moved);
    }

    private void Apply((List<(int Offset, int Length, string Text)> Edits, List<int> Carets) change)
    {
        if (_area.ReadOnlySectionProvider is { } readOnly && change.Edits.Any(e => !readOnly.CanInsert(e.Offset)))
        {
            return;
        }

        // Carets near each other are one document change (DocumentEdits): each change ran every change handler.
        _applying = true;
        try
        {
            var ascending = change.Edits.ToList();
            ascending.Reverse();
            DocumentEdits.Apply(Document, ascending);
        }
        finally
        {
            _applying = false;
        }

        SetAll([.. change.Carets.Select(CaretSelection.At)]);
    }

    /// <summary>Makes the first selection the primary (AvaloniaEdit's caret) and the rest extra.</summary>
    private void SetAll(IReadOnlyList<CaretSelection> selections)
    {
        var normalized = CaretEdits.Normalize(selections);
        if (normalized.Count == 0)
        {
            return;
        }

        var primary = normalized[0];
        if (primary.Length > 0)
        {
            _area.Selection = Selection.Create(_area, primary.Anchor, primary.Caret);
        }
        else
        {
            _area.ClearSelection();
        }

        _area.Caret.Offset = primary.Caret;
        _extra.Clear();
        _extra.AddRange(normalized.Skip(1));
        Update();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_applying || _extra.Count == 0)
        {
            return;
        }

        // An edit made by something else (undo, a refactoring): keep the extra carets where the text moved them. Moving
        // keeps their order.
        var offsets = new int[_extra.Count * 2];
        for (var i = 0; i < _extra.Count; i++)
        {
            offsets[2 * i] = _extra[i].Anchor;
            offsets[(2 * i) + 1] = _extra[i].Caret;
        }

        DocumentEdits.Map(e, offsets);
        for (var i = 0; i < _extra.Count; i++)
        {
            _extra[i] = new CaretSelection(offsets[2 * i], offsets[(2 * i) + 1]);
        }
    }

    private void Update()
    {
        _area.TextView.InvalidateLayer(KnownLayer.Caret);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        DrawnCarets = 0;
        if (_extra.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
        {
            return;
        }

        // Only the selections in the visible lines, and only lines already built: a caret's position asked of a line off
        // screen built that line (colorizers and all) at every caret blink, for each of thousands of carets (M56).
        var visibleStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        var visibleEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var first = FirstStartingAt(visibleStart);
        if (first > 0 && _extra[first - 1].End >= visibleStart)
        {
            first--;
        }

        var caretBrush = _area.Caret.CaretBrush ?? textView.GetValue(TextView.NonPrintableCharacterBrushProperty) ?? Brushes.Gray;
        var selectionBrush = _area.SelectionBrush ?? Brushes.LightBlue;
        for (var i = first; i < _extra.Count && _extra[i].Start <= visibleEnd; i++)
        {
            var selection = _extra[i];
            if (selection.Length > 0)
            {
                var segment = new TextSegment { StartOffset = selection.Start, EndOffset = selection.End };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    drawingContext.DrawRectangle(selectionBrush, null, rect);
                }
            }

            if (selection.Caret < visibleStart || selection.Caret > visibleEnd
                || textView.GetVisualLine(Document.GetLineByOffset(selection.Caret).LineNumber) is not { } line)
            {
                continue;
            }

            var column = line.GetVisualColumn(selection.Caret - line.FirstDocumentLine.Offset);
            var top = line.GetVisualPosition(column, VisualYPosition.LineTop) - textView.ScrollOffset;
            var bottom = line.GetVisualPosition(column, VisualYPosition.LineBottom) - textView.ScrollOffset;
            drawingContext.DrawRectangle(caretBrush, null, new Rect(top.X, top.Y, 1.5, bottom.Y - top.Y));
            DrawnCarets++;
        }
    }

    // ------------------------------------------------------------------ text helpers

    private (int Start, int End) WordAt(int offset)
    {
        var start = offset;
        while (start > 0 && IsWordChar(Document.GetCharAt(start - 1)))
        {
            start--;
        }

        var end = offset;
        while (end < Document.TextLength && IsWordChar(Document.GetCharAt(end)))
        {
            end++;
        }

        return (start, end);
    }

    private int WordStartBefore(int offset) => TextUtilities.GetNextCaretPosition(Document, offset, LogicalDirection.Backward, CaretPositioningMode.WordStart) is var p and >= 0 ? p : 0;

    private int WordEndAfter(int offset) => TextUtilities.GetNextCaretPosition(Document, offset, LogicalDirection.Forward, CaretPositioningMode.WordBorder) is var p and >= 0 ? p : Document.TextLength;

    private int PreviousCharLength(int offset) =>
        offset >= 2 && Document.GetCharAt(offset - 1) == '\n' && Document.GetCharAt(offset - 2) == '\r' ? 2
        : offset >= 2 && char.IsLowSurrogate(Document.GetCharAt(offset - 1)) ? 2 : 1;

    private int NextCharLength(int offset) =>
        offset + 1 < Document.TextLength && Document.GetCharAt(offset) == '\r' && Document.GetCharAt(offset + 1) == '\n' ? 2
        : offset + 1 < Document.TextLength && char.IsHighSurrogate(Document.GetCharAt(offset)) ? 2 : 1;

    private int LeadingWhitespace(DocumentLine line)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(Document.GetCharAt(line.Offset + i)))
        {
            i++;
        }

        return i;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public void Dispose()
    {
        _area.TextView.BackgroundRenderers.Remove(this);
        _area.Document.Changed -= OnDocumentChanged;
    }
}
