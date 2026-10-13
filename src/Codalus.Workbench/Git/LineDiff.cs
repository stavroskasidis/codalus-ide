namespace Codalus.Workbench.Git;

public enum LineChangeKind
{
    Added,
    Modified,

    /// <summary>Lines were removed just above <see cref="LineChange.Line"/> (shown as a marker between lines).</summary>
    Deleted,
}

/// <summary>A change in the new text: <see cref="Line"/> is 1-based; <see cref="Count"/> lines (0 for deletions).</summary>
public sealed record LineChange(LineChangeKind Kind, int Line, int Count);

/// <summary>An edit of a line diff: <see cref="OldIndex"/>/<see cref="NewIndex"/> are 0-based (-1 when absent).</summary>
public readonly record struct DiffOp(char Kind, int OldIndex, int NewIndex);

/// <summary>Myers' O(ND) line diff (with a cost cap), for gutter markers and side-by-side alignment.</summary>
public static class LineDiff
{
    /// <summary>Splits text into lines (without line terminators).</summary>
    public static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    /// <summary>The edit script from <paramref name="oldLines"/> to <paramref name="newLines"/>: ' ' (same), '-' and '+'.</summary>
    public static IReadOnlyList<DiffOp> Diff(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        // Trim the common prefix and suffix: most edits are local.
        var prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix
               && oldLines[oldLines.Count - 1 - suffix] == newLines[newLines.Count - 1 - suffix])
        {
            suffix++;
        }

        var ops = new List<DiffOp>(Math.Max(oldLines.Count, newLines.Count));
        for (var i = 0; i < prefix; i++)
        {
            ops.Add(new DiffOp(' ', i, i));
        }

        Middle(oldLines, newLines, prefix, oldLines.Count - suffix, prefix, newLines.Count - suffix, search: true, ops);
        for (var i = 0; i < suffix; i++)
        {
            ops.Add(new DiffOp(' ', oldLines.Count - suffix + i, newLines.Count - suffix + i));
        }

        return ops;
    }

    private static void Middle(IReadOnlyList<string> a, IReadOnlyList<string> b, int aStart, int aEnd, int bStart, int bEnd, bool search, List<DiffOp> ops)
    {
        var n = aEnd - aStart;
        var m = bEnd - bStart;
        if (n == 0 || m == 0 || !search)
        {
            // Nothing in common to find (or too big to search): all removed, then all added.
            for (var i = aStart; i < aEnd; i++)
            {
                ops.Add(new DiffOp('-', i, -1));
            }

            for (var j = bStart; j < bEnd; j++)
            {
                ops.Add(new DiffOp('+', -1, j));
            }

            return;
        }

        // Each step saves only the diagonals it can read back (-d..d), so memory grows with D² rather than D·(N+M);
        // past MaxEdits the middle is reported as replaced wholesale.
        const int MaxEdits = 3000;
        var max = Math.Min(n + m, MaxEdits);
        var offset = n + m;
        var v = new int[(2 * (n + m)) + 2];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= max && !found; d++)
        {
            trace.Add(v[(offset - d)..(offset + d + 1)]);
            for (var k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            Middle(a, b, aStart, aEnd, bStart, bEnd, search: false, ops);
            return;
        }

        // Backtrack through the saved frontiers (trace[d][k + d] is v[k] before step d).
        var script = new List<DiffOp>();
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            int At(int k) => k < -d || k > d ? 0 : vd[k + d];
            var k = cx - cy;
            int prevK = k == -d || (k != d && At(k - 1) < At(k + 1)) ? k + 1 : k - 1;
            var prevX = d == 0 ? 0 : At(prevK);
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                script.Add(new DiffOp(' ', aStart + cx - 1, bStart + cy - 1));
                cx--;
                cy--;
            }

            if (d > 0)
            {
                if (cx == prevX)
                {
                    script.Add(new DiffOp('+', -1, bStart + cy - 1));
                }
                else
                {
                    script.Add(new DiffOp('-', aStart + cx - 1, -1));
                }

                cx = prevX;
                cy = prevY;
            }
        }

        script.Reverse();
        ops.AddRange(script);
    }

    /// <summary>Gutter markers: changed runs of the new text against the old text.</summary>
    public static IReadOnlyList<LineChange> Changes(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var result = new List<LineChange>();
        var ops = Diff(oldLines, newLines);
        var i = 0;
        var newLine = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == ' ')
            {
                newLine++;
                i++;
                continue;
            }

            int removed = 0, added = 0;
            while (i < ops.Count && ops[i].Kind != ' ')
            {
                if (ops[i].Kind == '-')
                {
                    removed++;
                }
                else
                {
                    added++;
                }

                i++;
            }

            if (added == 0)
            {
                result.Add(new LineChange(LineChangeKind.Deleted, newLine + 1, 0));
            }
            else
            {
                var modified = Math.Min(added, removed);
                if (modified > 0)
                {
                    result.Add(new LineChange(LineChangeKind.Modified, newLine + 1, modified));
                }

                if (added > modified)
                {
                    result.Add(new LineChange(LineChangeKind.Added, newLine + 1 + modified, added - modified));
                }
            }

            newLine += added;
        }

        return result;
    }
}
