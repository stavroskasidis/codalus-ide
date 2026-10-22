namespace Codalus.Workbench.Git;

/// <summary>
/// A row of a diff document: a line on either side (null where that side has none: a filler row opposite), and its
/// kind: <c>' '</c> (unchanged), <c>'-'</c> (removed), <c>'+'</c> (added) or <c>'~'</c> (a removed line paired with
/// the added line that replaces it). Lines are 1-based.
/// </summary>
public sealed record DiffRow(int? OldLine, int? NewLine, char Kind)
{
    public bool IsChange => Kind != ' ';
}

/// <summary>
/// One line of a diff document's pane (#86): the row it belongs to, the line of each file it shows (0 for none) and
/// how it is drawn: <c>' '</c> unchanged, <c>'-'</c> removed, <c>'+'</c> added, <c>'f'</c> a filler row where the
/// other side has a line, <c>'z'</c> a folded run of unchanged rows (Summary).
/// </summary>
public readonly record struct DiffPaneLine(int Row, int OldLine, int NewLine, char Kind);

/// <summary>Which file a pane shows: the old one, the new one, or both (inline).</summary>
public enum DiffPaneSide
{
    Old,
    New,
    Inline,
}

/// <summary>A pane's composed text and its lines (one per line of the text).</summary>
public sealed record DiffPane(DiffPaneSide Side, string Text, IReadOnlyList<DiffPaneLine> Lines)
{
    public static DiffPane Empty { get; } = new(DiffPaneSide.New, string.Empty, []);

    /// <summary>
    /// The file line a 0-based pane line shows: whether it is the old file's, and its 1-based number (0 for a filler or
    /// folded line, or past the end).
    /// </summary>
    public (bool Old, int Line) SourceOf(int index)
    {
        if ((uint)index >= (uint)Lines.Count || Lines[index].Kind is 'f' or 'z')
        {
            return (Side == DiffPaneSide.Old, 0);
        }

        var line = Lines[index];
        return Side switch
        {
            DiffPaneSide.Old => (true, line.OldLine),
            DiffPaneSide.New => (false, line.NewLine),
            _ => line.Kind == '-' ? (true, line.OldLine) : (false, line.NewLine),
        };
    }
}

/// <summary>Rows, changes and the panes' texts of a diff document (#86).</summary>
public static class DiffRows
{
    /// <summary>
    /// Pairs the removed and added lines of each run of an edit script line by line (<c>'~'</c> rows), so the sides line
    /// up; the rest of the longer side gets filler opposite.
    /// </summary>
    public static IReadOnlyList<DiffRow> Align(IReadOnlyList<DiffOp> ops)
    {
        var rows = new List<DiffRow>(ops.Count);
        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == ' ')
            {
                rows.Add(new DiffRow(ops[i].OldIndex + 1, ops[i].NewIndex + 1, ' '));
                i++;
                continue;
            }

            var removed = new List<int>();
            var added = new List<int>();
            while (i < ops.Count && ops[i].Kind != ' ')
            {
                if (ops[i].Kind == '-')
                {
                    removed.Add(ops[i].OldIndex + 1);
                }
                else
                {
                    added.Add(ops[i].NewIndex + 1);
                }

                i++;
            }

            for (var k = 0; k < Math.Max(removed.Count, added.Count); k++)
            {
                int? oldLine = k < removed.Count ? removed[k] : null;
                int? newLine = k < added.Count ? added[k] : null;
                rows.Add(new DiffRow(oldLine, newLine, oldLine is null ? '+' : newLine is null ? '-' : '~'));
            }
        }

        return rows;
    }

    /// <summary>
    /// The two panes of the side-by-side layout: each row is a line on both sides, the old file's line on the left and
    /// the new file's on the right, and an empty filler line where a side has none.
    /// </summary>
    public static (DiffPane Old, DiffPane New) SideBySide(IReadOnlyList<DiffRow> rows, IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var left = new System.Text.StringBuilder();
        var right = new System.Text.StringBuilder();
        var leftLines = new List<DiffPaneLine>(rows.Count);
        var rightLines = new List<DiffPaneLine>(rows.Count);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var o = row.OldLine ?? 0;
            var n = row.NewLine ?? 0;
            left.Append(o > 0 ? oldLines[o - 1] : string.Empty).Append('\n');
            right.Append(n > 0 ? newLines[n - 1] : string.Empty).Append('\n');
            leftLines.Add(new DiffPaneLine(r, o, n, o == 0 ? 'f' : row.Kind is '-' or '~' ? '-' : ' '));
            rightLines.Add(new DiffPaneLine(r, o, n, n == 0 ? 'f' : row.Kind is '+' or '~' ? '+' : ' '));
        }

        return (new DiffPane(DiffPaneSide.Old, left.ToString(), leftLines), new DiffPane(DiffPaneSide.New, right.ToString(), rightLines));
    }

    /// <summary>
    /// The inline layout's pane: unchanged rows once, and each change as its removed lines, then its added lines.
    /// </summary>
    public static DiffPane Inline(IReadOnlyList<DiffRow> rows, IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var text = new System.Text.StringBuilder();
        var lines = new List<DiffPaneLine>(rows.Count);
        var r = 0;
        while (r < rows.Count)
        {
            if (!rows[r].IsChange)
            {
                var row = rows[r];
                text.Append(newLines[row.NewLine!.Value - 1]).Append('\n');
                lines.Add(new DiffPaneLine(r, row.OldLine!.Value, row.NewLine.Value, ' '));
                r++;
                continue;
            }

            var end = r;
            while (end < rows.Count && rows[end].IsChange)
            {
                end++;
            }

            for (var k = r; k < end; k++)
            {
                if (rows[k].OldLine is { } o)
                {
                    text.Append(oldLines[o - 1]).Append('\n');
                    lines.Add(new DiffPaneLine(k, o, 0, '-'));
                }
            }

            for (var k = r; k < end; k++)
            {
                if (rows[k].NewLine is { } n)
                {
                    text.Append(newLines[n - 1]).Append('\n');
                    lines.Add(new DiffPaneLine(k, 0, n, '+'));
                }
            }

            r = end;
        }

        return new DiffPane(DiffPaneSide.Inline, text.ToString(), lines);
    }
}

/// <summary>
/// The changed words of a pair of lines (#86, D45 question 14): a character diff of the two lines, each changed
/// character widened to the whole word it is in, so a changed name is marked whole (AddSingleton → AddScoped).
/// </summary>
public static class WordDiff
{
    /// <summary>Lines longer than this aren't compared character by character: they're marked as whole lines only.</summary>
    public const int MaxLineLength = 2000;

    /// <summary>The changed ranges (start, length) of each line, in order, merged where they touch.</summary>
    public static (IReadOnlyList<(int Start, int Length)> Old, IReadOnlyList<(int Start, int Length)> New) Ranges(string oldLine, string newLine)
    {
        if (oldLine == newLine || oldLine.Length > MaxLineLength || newLine.Length > MaxLineLength)
        {
            return ([], []);
        }

        var ops = LineDiff.Diff(Characters(oldLine), Characters(newLine));
        var oldChanged = new bool[oldLine.Length];
        var newChanged = new bool[newLine.Length];
        foreach (var op in ops)
        {
            if (op.Kind == '-')
            {
                oldChanged[op.OldIndex] = true;
            }
            else if (op.Kind == '+')
            {
                newChanged[op.NewIndex] = true;
            }
        }

        return (Widen(oldLine, oldChanged), Widen(newLine, newChanged));
    }

    private static string[] Characters(string line)
    {
        var characters = new string[line.Length];
        for (var i = 0; i < line.Length; i++)
        {
            characters[i] = line[i].ToString();
        }

        return characters;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Each changed character's whole word, then the ranges, merged where they touch.</summary>
    private static List<(int Start, int Length)> Widen(string line, bool[] changed)
    {
        var marked = (bool[])changed.Clone();
        for (var i = 0; i < line.Length; i++)
        {
            if (!changed[i] || !IsWord(line[i]))
            {
                continue;
            }

            for (var j = i - 1; j >= 0 && IsWord(line[j]); j--)
            {
                marked[j] = true;
            }

            for (var j = i + 1; j < line.Length && IsWord(line[j]); j++)
            {
                marked[j] = true;
            }
        }

        var ranges = new List<(int Start, int Length)>();
        for (var i = 0; i < line.Length; i++)
        {
            if (!marked[i])
            {
                continue;
            }

            var start = i;
            while (i < line.Length && marked[i])
            {
                i++;
            }

            ranges.Add((start, i - start));
        }

        return ranges;
    }
}
