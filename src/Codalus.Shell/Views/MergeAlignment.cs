using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Views;

/// <summary>
/// How a merge editor pane lines up with the other two (the maintainer's report, 2026-11-13: an empty side showed a thin
/// band and the panes drifted apart past a conflict; the classic IDE pads the shorter side). Every pane has as many rows
/// as the others: filler rows go where another pane has lines this one hasn't, and a conflict's block is as tall in every
/// pane as its tallest side.
/// </summary>
/// <param name="Before">The filler rows before each line (0-based), and after the last line at <c>[LineCount]</c>.</param>
/// <param name="Blocks">Each conflict's lines in this pane, [Start, End).</param>
/// <param name="BlockRows">The filler rows each conflict's block takes: after its last line, or where an empty block is.</param>
internal sealed record MergePaneLayout(int[] Before, (int Start, int End)[] Blocks, int[] BlockRows)
{
    private int[]? _rowsAbove;

    public int LineCount => Before.Length - 1;

    /// <summary>Filler rows above the first line (in its visual line, before its text).</summary>
    public int Lead => Before[0];

    /// <summary>Filler rows after a 0-based line's text, in its visual line.</summary>
    public int After(int line) => (uint)(line + 1) < (uint)Before.Length ? Before[line + 1] : 0;

    /// <summary>The rows above a 0-based line's visual line (its own lead rows aren't above it), up to <see cref="LineCount"/>.</summary>
    public int RowsAbove(int line)
    {
        if (_rowsAbove is null)
        {
            var rows = new int[Before.Length];
            for (var i = 1; i < rows.Length; i++)
            {
                rows[i] = rows[i - 1] + 1 + (i == 1 ? Before[0] : 0) + Before[i];
            }

            _rowsAbove = rows;
        }

        return _rowsAbove[Math.Clamp(line, 0, _rowsAbove.Length - 1)];
    }

    /// <summary>The 0-based line whose visual line holds row <paramref name="row"/>, and the row's place in it.</summary>
    public (int Line, double RowInLine) LineAtRow(double row)
    {
        int low = 0, high = LineCount - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (RowsAbove(middle) <= row)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return (Math.Max(0, low), Math.Max(0, row - RowsAbove(low)));
    }

    public bool SameAs(MergePaneLayout? other) =>
        other is not null && Before.AsSpan().SequenceEqual(other.Before) && BlockRows.AsSpan().SequenceEqual(other.BlockRows) && Blocks.AsSpan().SequenceEqual(other.Blocks);

    /// <summary>A layout with no filler (a pane whose blocks can't be placed).</summary>
    public static MergePaneLayout Empty(int lineCount, int blocks) => new(new int[lineCount + 1], new (int, int)[blocks], new int[blocks]);
}

/// <summary>Works out the merge editor's <see cref="MergePaneLayout"/>s.</summary>
internal static class MergeAlignment
{
    /// <summary>A pane's lines and each conflict's lines in it, [Start, End), in order.</summary>
    public readonly record struct Pane(string[] Lines, (int Start, int End)[] Blocks);

    /// <summary>
    /// Lines the panes up. Between conflicts, each pane is diffed against the last (the result): a line every pane has
    /// lines up across all of them, and between two such lines each pane gets filler up to the tallest one's lines. Each
    /// conflict's block gets filler up to the tallest side's block.
    /// </summary>
    public static MergePaneLayout[] Align(IReadOnlyList<Pane> panes)
    {
        var count = panes.Count;
        var blocks = panes[0].Blocks.Length;
        var before = new int[count][];
        var blockRows = new int[count][];
        for (var p = 0; p < count; p++)
        {
            before[p] = new int[panes[p].Lines.Length + 1];
            blockRows[p] = new int[blocks];
        }

        if (panes.Any(p => p.Blocks.Length != blocks || !Ordered(p)))
        {
            return [.. panes.Select(p => MergePaneLayout.Empty(p.Lines.Length, p.Blocks.Length))];
        }

        var from = new int[count];
        var to = new int[count];
        for (var k = 0; k <= blocks; k++)
        {
            for (var p = 0; p < count; p++)
            {
                to[p] = k < blocks ? panes[p].Blocks[k].Start : panes[p].Lines.Length;
            }

            AlignStretch(panes, from, to, before);
            if (k == blocks)
            {
                break;
            }

            var tallest = 0;
            for (var p = 0; p < count; p++)
            {
                tallest = Math.Max(tallest, panes[p].Blocks[k].End - panes[p].Blocks[k].Start);
            }

            for (var p = 0; p < count; p++)
            {
                var (start, end) = panes[p].Blocks[k];
                blockRows[p][k] = tallest - (end - start);
                before[p][end] += blockRows[p][k];
                from[p] = end;
            }
        }

        return [.. Enumerable.Range(0, count).Select(p => new MergePaneLayout(before[p], panes[p].Blocks, blockRows[p]))];
    }

    private static bool Ordered(Pane pane)
    {
        var previous = 0;
        foreach (var (start, end) in pane.Blocks)
        {
            if (start < previous || end < start || end > pane.Lines.Length)
            {
                return false;
            }

            previous = end;
        }

        return true;
    }

    /// <summary>Lines up the panes' lines [from, to) between two conflicts, with the last pane as the one the others are diffed against.</summary>
    private static void AlignStretch(IReadOnlyList<Pane> panes, int[] from, int[] to, int[][] before)
    {
        var count = panes.Count;
        var pivot = count - 1;
        var pivotLength = to[pivot] - from[pivot];
        var pivotLines = new ArraySegment<string>(panes[pivot].Lines, from[pivot], pivotLength);
        var match = new int[count][];
        for (var p = 0; p < pivot; p++)
        {
            match[p] = new int[pivotLength];
            Array.Fill(match[p], -1);
            foreach (var op in LineDiff.Diff(new ArraySegment<string>(panes[p].Lines, from[p], to[p] - from[p]), pivotLines))
            {
                if (op.Kind == ' ')
                {
                    match[p][op.NewIndex] = op.OldIndex;
                }
            }
        }

        var position = new int[count];
        var at = new int[count];
        for (var line = 0; line <= pivotLength; line++)
        {
            var shared = true;
            for (var p = 0; p < pivot && shared && line < pivotLength; p++)
            {
                shared = match[p][line] >= 0;
            }

            if (!shared)
            {
                continue;
            }

            // A line every pane has (or the stretch's end): each pane's lines since the last one line up, padded to the tallest.
            var tallest = 0;
            for (var p = 0; p < count; p++)
            {
                at[p] = p == pivot ? line : line < pivotLength ? match[p][line] : to[p] - from[p];
                tallest = Math.Max(tallest, at[p] - position[p]);
            }

            for (var p = 0; p < count; p++)
            {
                before[p][from[p] + at[p]] += tallest - (at[p] - position[p]);
                position[p] = at[p] + 1;
            }
        }
    }

    /// <summary>A pane's lines, and each conflict's lines in it from its characters (whole lines: an edited block ending inside a line takes that line).</summary>
    public static Pane PaneOf(TextDocument document, string[]? lines, IEnumerable<(int Start, int Length)> ranges)
    {
        lines ??= [.. document.Lines.Select(document.GetText)];
        var blocks = new List<(int Start, int End)>();
        var previous = 0;
        foreach (var (start, length) in ranges)
        {
            if (start + length > document.TextLength)
            {
                return new Pane(lines, []);
            }

            var first = Math.Max(previous, document.GetLineByOffset(start).LineNumber - 1);
            var end = length == 0 ? first : Math.Max(first, document.GetLineByOffset(start + length - 1).LineNumber);
            blocks.Add((first, end));
            previous = end;
        }

        return new Pane(lines, [.. blocks]);
    }
}

/// <summary>
/// A merge editor pane's filler rows (<see cref="MergePaneLayout"/>): a line's rows after its text go at the line's end,
/// the first line's lead rows before its text. They're not part of the text, and the caret doesn't stop in them.
/// </summary>
internal sealed class MergeFillerGenerator(Func<MergePaneLayout?> layout) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (layout() is not { } current)
        {
            return -1;
        }

        var line = CurrentContext.VisualLine.LastDocumentLine;
        var (lead, trail) = Rows(current, line);
        return lead > 0 && startOffset <= line.Offset ? line.Offset
            : trail > 0 && startOffset <= line.EndOffset ? line.EndOffset
            : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (layout() is not { } current)
        {
            return null;
        }

        var line = CurrentContext.Document.GetLineByOffset(offset);
        var (lead, trail) = Rows(current, line);
        if (offset == line.Offset && lead > 0)
        {
            return new MergeFillerElement(lead, offset == line.EndOffset ? trail : 0); // an empty line: its rows in one element
        }

        return offset == line.EndOffset && trail > 0 ? new MergeFillerElement(0, trail) : null;
    }

    private static (int Lead, int Trail) Rows(MergePaneLayout layout, DocumentLine line) =>
        line.LineNumber > layout.LineCount ? (0, 0) : (line.LineNumber == 1 ? layout.Lead : 0, layout.After(line.LineNumber - 1));
}

/// <summary>
/// Filler rows in a visual line: each a zero-width space in the code's font (as tall as a line) and a line break, before
/// the line's text (<paramref name="lead"/>) or after it (<paramref name="trail"/>). The caret's one stop is at the text's side.
/// </summary>
internal sealed class MergeFillerElement(int lead, int trail) : VisualLineElement(2 * (lead + trail), 0)
{
    private const string ZeroWidthSpace = "​";

    public int Lead => lead;

    public int Trail => trail;

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var at = startVisualColumn - VisualColumn;
        var row = at < 2 * lead ? at % 2 == 0 : (at - (2 * lead)) % 2 == 1;
        return row ? new TextCharacters(ZeroWidthSpace, TextRunProperties) : new TextEndOfLine(1);
    }

    // The line's start (after lead rows) or end (before trail rows) is the element's caret stop, and the line's own.
    public override bool HandlesLineBorders => true;

    public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, CaretPositioningMode mode)
    {
        var stop = VisualColumn + (2 * lead);
        return direction == AvaloniaEdit.Document.LogicalDirection.Forward ? (visualColumn < stop ? stop : -1) : (visualColumn > stop ? stop : -1);
    }

    public override bool IsWhitespace(int visualColumn) => false;
}

/// <summary>Draws the hatching on a merge editor pane's filler rows (<c>Shell.Diff.Filler</c>), as the diff view does.</summary>
internal static class MergeFillerHatch
{
    private const double Spacing = 6;

    /// <summary>Diagonal stripes over <paramref name="rect"/>, anchored to the document so a run of rows draws one pattern.</summary>
    public static void Draw(DrawingContext context, IPen pen, Avalonia.Rect rect, double scrollY, double width)
    {
        if (rect.Height <= 0)
        {
            return;
        }

        using (context.PushClip(rect))
        {
            var phase = (rect.Top + scrollY) % Spacing;
            for (var x = -rect.Height - phase; x < width; x += Spacing)
            {
                context.DrawLine(pen, new Avalonia.Point(x, rect.Bottom), new Avalonia.Point(x + rect.Height, rect.Top));
            }
        }
    }
}
