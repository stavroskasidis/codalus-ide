using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>
/// The smallest whole-line changes from one text to another (M41a, ADR 0073): the common lines at both ends are kept,
/// and the rest is compared line by line (longest common subsequence), so an edit shows in undo, local history and
/// source control as the lines it changed.
/// </summary>
internal static class LineDiff
{
    /// <summary>Above this many cells the middle is replaced as one change.</summary>
    private const long MaxCells = 4_000_000;

    public static IReadOnlyList<TextChange> Compute(SourceText before, SourceText after)
    {
        var a = Lines(before);
        var b = Lines(after);
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix].Text == b[prefix].Text)
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix].Text == b[b.Count - 1 - suffix].Text)
        {
            suffix++;
        }

        var n = a.Count - prefix - suffix;
        var m = b.Count - prefix - suffix;
        if (n == 0 && m == 0)
        {
            return [];
        }

        int Start(int index) => index < a.Count ? a[index].Start : before.Length;
        if ((long)n * m > MaxCells || n == 0 || m == 0)
        {
            return [new TextChange(TextSpan.FromBounds(Start(prefix), Start(prefix + n)), string.Concat(b.Skip(prefix).Take(m).Select(l => l.Text)))];
        }

        // lengths[i, j]: the LCS of a[prefix + i..] and b[prefix + j..].
        var lengths = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lengths[i, j] = a[prefix + i].Text == b[prefix + j].Text ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var changes = new List<TextChange>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[prefix + x].Text == b[prefix + y].Text)
            {
                x++;
                y++;
                continue;
            }

            var fromX = x;
            var fromY = y;
            while ((x < n || y < m) && !(x < n && y < m && a[prefix + x].Text == b[prefix + y].Text))
            {
                if (y < m && (x == n || lengths[x, y + 1] >= lengths[x + 1, y]))
                {
                    y++;
                }
                else
                {
                    x++;
                }
            }

            changes.Add(new TextChange(
                TextSpan.FromBounds(Start(prefix + fromX), Start(prefix + x)),
                string.Concat(b.Skip(prefix + fromY).Take(y - fromY).Select(l => l.Text))));
        }

        return changes;
    }

    private static List<(int Start, string Text)> Lines(SourceText text) =>
        [.. text.Lines.Where(l => l.Start < text.Length || l.EndIncludingLineBreak > l.Start).Select(l => (l.Start, text.ToString(l.SpanIncludingLineBreak)))];
}
