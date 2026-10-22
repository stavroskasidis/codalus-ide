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
    /// The line numbers a 0-based pane line shows (0 for none): a side's own file's number (none on filler and folded
    /// lines); in the inline view, the old file's and the new file's columns.
    /// </summary>
    public (int Old, int New) NumbersOf(int index)
    {
        if ((uint)index >= (uint)Lines.Count || Lines[index].Kind is 'f' or 'z')
        {
            return (0, 0);
        }

        var line = Lines[index];
        return Side switch
        {
            DiffPaneSide.Old => (line.OldLine, 0),
            DiffPaneSide.New => (0, line.NewLine),
            _ => (line.Kind == '+' ? 0 : line.OldLine, line.Kind == '-' ? 0 : line.NewLine),
        };
    }

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

/// <summary>
/// A change of a diff document (D45 question 7): a run of changed rows, first and last inclusive. F8 stops at it, the
/// toolbar counts it, and Stage Change stages it.
/// </summary>
public readonly record struct DiffChange(int FirstRow, int LastRow)
{
    public bool Contains(int row) => row >= FirstRow && row <= LastRow;
}

/// <summary>Rows, changes and the panes' texts of a diff document (#86).</summary>
public static class DiffRows
{
    /// <summary>The changes: each run of changed rows.</summary>
    public static IReadOnlyList<DiffChange> Changes(IReadOnlyList<DiffRow> rows)
    {
        var changes = new List<DiffChange>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].IsChange)
            {
                continue;
            }

            var first = i;
            while (i + 1 < rows.Count && rows[i + 1].IsChange)
            {
                i++;
            }

            changes.Add(new DiffChange(first, i));
        }

        return changes;
    }

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

/// <summary>
/// A patch made from a diff document's rows (#86, D45 question 7): the chosen changed rows over the exact bytes of the
/// old text (the index, or HEAD) and the new text (the saved file, or the index), so what is staged is what is drawn,
/// whatever git's own hunks or <c>diff.algorithm</c> would make of the same edit. Applied forward (staging, to the
/// index), a removal not chosen stays as context and an addition not chosen is left out; applied in reverse
/// (unstaging, discarding), the other way around. Line endings, a byte order mark and a last line without a newline
/// pass unchanged.
/// </summary>
public static class DiffPatch
{
    private const int Context = 3;

    /// <summary>
    /// A line of the patch: <c>' '</c>, <c>'-'</c> or <c>'+'</c>, and its bytes with the line ending, if it has one; an
    /// unchanged line also has the other side's bytes (the same text, maybe with another line ending).
    /// </summary>
    private readonly record struct Entry(char Kind, byte[] Line, byte[]? Other = null)
    {
        public bool HasNewline => Line.Length > 0 && Line[^1] == (byte)'\n';

        public Entry WithNewline() => HasNewline ? this : this with { Line = [.. Line, (byte)'\n'] };
    }

    /// <summary>A text's lines as bytes, each with its line ending (the last one without, when the text doesn't end in one).</summary>
    public static IReadOnlyList<byte[]> Lines(ReadOnlySpan<byte> text)
    {
        var lines = new List<byte[]>();
        while (text.Length > 0)
        {
            var end = text.IndexOf((byte)'\n');
            var length = end < 0 ? text.Length : end + 1;
            lines.Add(text[..length].ToArray());
            text = text[length..];
        }

        return lines;
    }

    /// <summary>
    /// The patch for the chosen rows, or null when none of them is a changed row. <paramref name="oldPath"/> and
    /// <paramref name="newPath"/> are relative to the repository, with <c>/</c>; <paramref name="oldExists"/> is false for
    /// a file the old side doesn't have (an untracked file, staged as new).
    /// </summary>
    public static byte[]? Build(
        string oldPath, string newPath, bool oldExists, ReadOnlySpan<byte> oldText, ReadOnlySpan<byte> newText,
        IReadOnlyList<DiffRow> rows, IReadOnlySet<int> chosen, bool reverse)
    {
        var oldLines = Lines(oldText);
        var newLines = Lines(newText);
        var entries = new List<Entry>();
        var i = 0;
        while (i < rows.Count)
        {
            if (!rows[i].IsChange)
            {
                var row = rows[i];
                var (oldLine, newLine) = (oldLines[row.OldLine!.Value - 1], newLines[row.NewLine!.Value - 1]);
                entries.Add(reverse ? new Entry(' ', newLine, oldLine) : new Entry(' ', oldLine, newLine));
                i++;
                continue;
            }

            var end = i;
            while (end < rows.Count && rows[end].IsChange)
            {
                end++;
            }

            for (var k = i; k < end; k++)
            {
                if (rows[k].OldLine is { } o)
                {
                    if (chosen.Contains(k))
                    {
                        entries.Add(new Entry('-', oldLines[o - 1]));
                    }
                    else if (!reverse)
                    {
                        entries.Add(new Entry(' ', oldLines[o - 1]));
                    }
                }
            }

            for (var k = i; k < end; k++)
            {
                if (rows[k].NewLine is { } n)
                {
                    if (chosen.Contains(k))
                    {
                        entries.Add(new Entry('+', newLines[n - 1]));
                    }
                    else if (reverse)
                    {
                        entries.Add(new Entry(' ', newLines[n - 1]));
                    }
                }
            }

            i = end;
        }

        if (!entries.Any(e => e.Kind != ' '))
        {
            return null;
        }

        FixLastLines(entries);
        var patch = new MemoryStream();
        void Write(string text) => patch.Write(System.Text.Encoding.UTF8.GetBytes(text));
        Write($"diff --git a/{oldPath} b/{newPath}\n");
        if (!oldExists && !reverse)
        {
            Write("new file mode 100644\n");
        }

        Write(oldExists || reverse ? $"--- a/{oldPath}\n" : "--- /dev/null\n");
        Write($"+++ b/{newPath}\n");
        foreach (var (first, last) in Hunks(entries))
        {
            int oldStart = 1, newStart = 1;
            for (var k = 0; k < first; k++)
            {
                oldStart += entries[k].Kind != '+' ? 1 : 0;
                newStart += entries[k].Kind != '-' ? 1 : 0;
            }

            var oldCount = 0;
            var newCount = 0;
            for (var k = first; k <= last; k++)
            {
                oldCount += entries[k].Kind != '+' ? 1 : 0;
                newCount += entries[k].Kind != '-' ? 1 : 0;
            }

            // An empty side's start is the line before it (git's convention for a range of no lines).
            Write(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"@@ -{(oldCount == 0 ? oldStart - 1 : oldStart)},{oldCount} +{(newCount == 0 ? newStart - 1 : newStart)},{newCount} @@\n"));
            for (var k = first; k <= last; k++)
            {
                var entry = entries[k];
                patch.WriteByte((byte)entry.Kind);
                patch.Write(entry.Line);
                if (!entry.HasNewline)
                {
                    Write("\n\\ No newline at end of file\n");
                }
            }
        }

        return patch.ToArray();
    }

    /// <summary>
    /// A last line without a newline that isn't the last on both sides of the patch: an unchanged one gets a newline on
    /// the side that goes on, so the patch says "this line, then more" there and "the end, no newline" on the other.
    /// </summary>
    private static void FixLastLines(List<Entry> entries)
    {
        for (var k = 0; k < entries.Count; k++)
        {
            var entry = entries[k];
            if (entry.Kind != ' ' || entry.HasNewline)
            {
                continue;
            }

            var moreAdded = entries.Skip(k + 1).Any(e => e.Kind == '+');
            var moreRemoved = entries.Skip(k + 1).Any(e => e.Kind == '-');
            if (moreAdded && !moreRemoved)
            {
                // The old side ends here; the new side goes on, with this line's ending on the new side.
                entries[k] = entry with { Kind = '-' };
                entries.Insert(k + 1, new Entry('+', entry.Other is { } other && other.Length > 0 && other[^1] == (byte)'\n' ? other : entry.WithNewline().Line));
            }
            else if (moreRemoved && !moreAdded)
            {
                // The new side ends here; the old side goes on: the line, with a newline, is removed, and comes back without.
                var removals = k + 1;
                while (removals < entries.Count && entries[removals].Kind == '-')
                {
                    removals++;
                }

                entries[k] = new Entry('-', entry.Other is { } other && other.Length > 0 && other[^1] == (byte)'\n' ? other : entry.WithNewline().Line);
                entries.Insert(removals, new Entry('+', entry.Line));
            }
        }
    }

    /// <summary>The hunks: each changed entry with up to three lines of context, hunks that touch merged.</summary>
    private static List<(int First, int Last)> Hunks(List<Entry> entries)
    {
        var hunks = new List<(int First, int Last)>();
        for (var k = 0; k < entries.Count; k++)
        {
            if (entries[k].Kind == ' ')
            {
                continue;
            }

            var first = Math.Max(0, k - Context);
            var last = k;
            while (last + 1 < entries.Count)
            {
                // The next change within twice the context joins this hunk.
                var next = last + 1;
                var gap = 0;
                while (next < entries.Count && entries[next].Kind == ' ')
                {
                    next++;
                    gap++;
                }

                if (next < entries.Count && gap <= 2 * Context)
                {
                    last = next;
                }
                else
                {
                    break;
                }
            }

            var end = Math.Min(entries.Count - 1, last + Context);
            if (hunks.Count > 0 && first <= hunks[^1].Last + 1)
            {
                hunks[^1] = (hunks[^1].First, end);
            }
            else
            {
                hunks.Add((first, end));
            }

            k = end;
        }

        return hunks;
    }
}
