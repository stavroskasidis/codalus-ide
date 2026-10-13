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

    public void AddCaret(int offset)
    {
        offset = Math.Clamp(offset, 0, Document.TextLength);
        if (All.Any(s => s.Caret == offset))
        {
            return;
        }

        _extra.Add(CaretSelection.At(offset));
        Update();
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

        var content = Document.Text;
        do
        {
            var from = All.Max(s => s.End);
            var next = content.IndexOf(text, from, StringComparison.Ordinal);
            if (next < 0)
            {
                next = content.IndexOf(text, 0, StringComparison.Ordinal); // wrap around
            }

            if (next < 0 || All.Any(s => s.Start == next))
            {
                break;
            }

            _extra.Add(new CaretSelection(next, next + text.Length));
        }
        while (all);

        Update();
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

        _applying = true;
        try
        {
            using (Document.RunUpdate())
            {
                foreach (var (offset, length, text) in change.Edits)
                {
                    Document.Replace(offset, length, text);
                }
            }
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

        // An edit made by something else (undo, a refactoring): keep the extra carets where the text moved them.
        for (var i = 0; i < _extra.Count; i++)
        {
            var s = _extra[i];
            _extra[i] = new CaretSelection(e.GetNewOffset(s.Anchor, AnchorMovementType.Default), e.GetNewOffset(s.Caret, AnchorMovementType.Default));
        }
    }

    private void Update()
    {
        _area.TextView.InvalidateLayer(KnownLayer.Caret);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_extra.Count == 0 || !textView.VisualLinesValid)
        {
            return;
        }

        var caretBrush = _area.Caret.CaretBrush ?? textView.GetValue(TextView.NonPrintableCharacterBrushProperty) ?? Brushes.Gray;
        var selectionBrush = _area.SelectionBrush ?? Brushes.LightBlue;
        foreach (var selection in _extra)
        {
            if (selection.Length > 0)
            {
                var segment = new TextSegment { StartOffset = selection.Start, EndOffset = selection.End };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    drawingContext.DrawRectangle(selectionBrush, null, rect);
                }
            }

            var position = new TextViewPosition(Document.GetLocation(selection.Caret));
            var top = textView.GetVisualPosition(position, VisualYPosition.LineTop) - textView.ScrollOffset;
            var bottom = textView.GetVisualPosition(position, VisualYPosition.LineBottom) - textView.ScrollOffset;
            drawingContext.DrawRectangle(caretBrush, null, new Rect(top.X, top.Y, 1.5, bottom.Y - top.Y));
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
