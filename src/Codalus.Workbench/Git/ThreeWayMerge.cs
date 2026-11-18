using System.Text;

namespace Codalus.Workbench.Git;

/// <summary>
/// The merge editor's own 3-way merge (ADR 0020, amended 2026-11-18), for a file git's merge left in conflict. git's
/// merge-ort aligns the versions with the histogram diff on exact lines: when one side re-indents a block that the other
/// side edits, nothing in the block matches, and git may put the whole rewrite in one conflict with an empty side, or
/// pair unrelated blocks. Here each side is aligned with the base by a Myers line diff that ignores whitespace, so a
/// re-indented line still lines up with its old self; whether a side changed a line is then decided on its exact text,
/// so a re-indent still conflicts with an edit of the same line. The chunks are diff3's: stretches where all three agree,
/// and between them the stretches either side changed. A stretch only one side changed (or both the same way) takes
/// that side; the rest are conflicts, written with git's diff3 markers.
/// </summary>
public static class ThreeWayMerge
{
    /// <summary>
    /// The merge of the three versions, with diff3 conflict markers labeled as <paramref name="workTree"/>'s (git's
    /// conflicted file) and in its line endings; null when <paramref name="workTree"/> has no conflict markers.
    /// </summary>
    public static string? Remerge(string @base, string ours, string theirs, string workTree)
    {
        if (ConflictLabels(workTree) is not var (oursLabel, baseLabel, theirsLabel))
        {
            return null;
        }

        var merged = Merge(@base, ours, theirs, oursLabel, baseLabel ?? "base", theirsLabel);
        return workTree.Contains("\r\n", StringComparison.Ordinal) && !merged.Contains("\r\n", StringComparison.Ordinal)
            ? merged.Replace("\n", "\r\n", StringComparison.Ordinal)
            : merged;
    }

    /// <summary>The merge of the three versions, each conflict written with diff3 markers (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; ours</c>, <c>|||||||</c>, <c>=======</c>, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; theirs</c>).</summary>
    public static string Merge(string @base, string ours, string theirs, string oursLabel = "ours", string baseLabel = "base", string theirsLabel = "theirs")
    {
        var b = Lines(@base);
        var o = Lines(ours);
        var t = Lines(theirs);
        var toOurs = Matches(b, o);
        var toTheirs = Matches(b, t);
        var output = new Output(oursLabel, baseLabel, theirsLabel);
        int i = 0, oi = 0, ti = 0;
        while (i < b.Length || oi < o.Length || ti < t.Length)
        {
            // A stretch all three have (alike but for whitespace): each line on its own, as either side may have
            // re-indented it.
            var stable = 0;
            while (i + stable < b.Length && toOurs[i + stable] == oi + stable && toTheirs[i + stable] == ti + stable)
            {
                stable++;
            }

            if (stable > 0)
            {
                for (var k = 0; k < stable; k++)
                {
                    output.Add([b[i + k]], [o[oi + k]], [t[ti + k]]);
                }

                (i, oi, ti) = (i + stable, oi + stable, ti + stable);
                continue;
            }

            // A stretch either side changed: up to the next base line both sides still have.
            var next = i;
            while (next < b.Length && (toOurs[next] < 0 || toTheirs[next] < 0))
            {
                next++;
            }

            var oursEnd = next < b.Length ? toOurs[next] : o.Length;
            var theirsEnd = next < b.Length ? toTheirs[next] : t.Length;
            output.Add(b[i..next], o[oi..oursEnd], t[ti..theirsEnd]);
            (i, oi, ti) = (next, oursEnd, theirsEnd);
        }

        return output.ToString();
    }

    /// <summary>The labels of the first conflict's markers in <paramref name="text"/> (the base's, when diff3 wrote one); null with no conflict.</summary>
    public static (string Ours, string? Base, string Theirs)? ConflictLabels(string text)
    {
        static string Label(string line) => line.Length > 8 ? line[8..] : string.Empty;

        string? ours = null;
        string? @base = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (ours is null)
            {
                if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
                {
                    ours = Label(line);
                }
            }
            else if (line.StartsWith("|||||||", StringComparison.Ordinal))
            {
                @base = Label(line);
            }
            else if (line.StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                return (ours, @base, Label(line));
            }
        }

        return null;
    }

    /// <summary>The text's lines, each with its line terminator (the last one without, when the text doesn't end with one).</summary>
    private static string[] Lines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return [.. lines];
    }

    /// <summary>A line without its whitespace, which the alignment compares.</summary>
    private static string Key(string line)
    {
        var key = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (!char.IsWhiteSpace(c))
            {
                key.Append(c);
            }
        }

        return key.ToString();
    }

    /// <summary>Each base line's line in <paramref name="side"/> where the two are alike but for whitespace, or -1.</summary>
    private static int[] Matches(string[] @base, string[] side)
    {
        var matches = new int[@base.Length];
        Array.Fill(matches, -1);
        foreach (var op in LineDiff.Diff([.. @base.Select(Key)], [.. side.Select(Key)]))
        {
            if (op.Kind == ' ')
            {
                matches[op.OldIndex] = op.NewIndex;
            }
        }

        return matches;
    }

    /// <summary>The merged text: clean stretches as they are, and consecutive conflicting stretches as one conflict.</summary>
    private sealed class Output(string oursLabel, string baseLabel, string theirsLabel)
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _base = new();
        private readonly StringBuilder _ours = new();
        private readonly StringBuilder _theirs = new();
        private bool _inConflict;

        public void Add(string[] @base, string[] ours, string[] theirs)
        {
            var (b, o, t) = (string.Concat(@base), string.Concat(ours), string.Concat(theirs));
            var clean = o == b ? t : t == b || o == t ? o : null;
            if (clean is null)
            {
                _base.Append(b);
                _ours.Append(o);
                _theirs.Append(t);
                _inConflict = true;
                return;
            }

            if (clean.Length == 0)
            {
                return; // nothing to write: a conflict on either side of it stays one conflict
            }

            Flush();
            _text.Append(clean);
        }

        private void Flush()
        {
            if (!_inConflict)
            {
                return;
            }

            var newline = _ours.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            _text.Append("<<<<<<< ").Append(oursLabel).Append(newline);
            Section(_ours, newline);
            _text.Append("||||||| ").Append(baseLabel).Append(newline);
            Section(_base, newline);
            _text.Append("=======").Append(newline);
            Section(_theirs, newline);
            _text.Append(">>>>>>> ").Append(theirsLabel).Append(newline);
            _base.Clear();
            _ours.Clear();
            _theirs.Clear();
            _inConflict = false;
        }

        /// <summary>A section's text, ending with a line terminator so that the next marker starts a line.</summary>
        private void Section(StringBuilder section, string newline)
        {
            _text.Append(section);
            if (section.Length > 0 && section[^1] != '\n')
            {
                _text.Append(newline);
            }
        }

        public override string ToString()
        {
            Flush();
            return _text.ToString();
        }
    }
}
