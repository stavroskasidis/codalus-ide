using Codalus.Workbench.Git;

namespace Codalus.Workbench.Tests;

/// <summary>The diff document's model (#86, D45): rows, the panes' texts and the changed words.</summary>
public sealed class DiffModelTests
{
    private static IReadOnlyList<DiffRow> Rows(string[] oldLines, string[] newLines) => DiffRows.Align(LineDiff.Diff(oldLines, newLines));

    [Fact]
    public void Panes_line_up_side_by_side_with_filler_and_inline_shows_each_changes_removed_lines_first()
    {
        string[] old = ["a", "b", "c", "d"];
        string[] now = ["a", "B", "x", "c"];
        var rows = Rows(old, now);
        Assert.Equal(["' '", "'~'", "'+'", "' '", "'-'"], rows.Select(r => $"'{r.Kind}'"));

        var (left, right) = DiffRows.SideBySide(rows, old, now);
        Assert.Equal("a\nb\n\nc\nd\n", left.Text);
        Assert.Equal("a\nB\nx\nc\n\n", right.Text);
        Assert.Equal(" -f -", string.Concat(left.Lines.Select(l => l.Kind)));
        Assert.Equal(" ++ f", string.Concat(right.Lines.Select(l => l.Kind)));
        Assert.Equal((true, 2), left.SourceOf(1));
        Assert.Equal((true, 0), left.SourceOf(2)); // filler
        Assert.Equal((false, 3), right.SourceOf(2));

        // Inline: a change's removed lines, then its added lines (not interleaved row by row).
        var inline = DiffRows.Inline(rows, old, now);
        Assert.Equal("a\nb\nB\nx\nc\nd\n", inline.Text);
        Assert.Equal(" -++ -", string.Concat(inline.Lines.Select(l => l.Kind)));
        Assert.Equal((true, 2), inline.SourceOf(1));
        Assert.Equal((false, 2), inline.SourceOf(2));
        Assert.Equal((false, 4), inline.SourceOf(4)); // an unchanged line shows the new file's line
        Assert.Equal([0, 1, 1, 2, 3, 4], inline.Lines.Select(l => l.Row));
    }

    [Fact]
    public void A_changed_word_is_marked_whole_on_both_lines()
    {
        var (old, now) = WordDiff.Ranges("        services.AddSingleton<IPuzzleSeedReader>(x => x);", "        services.AddScoped<IPuzzleSeedReader>(x => x);");
        Assert.Equal([(17, "AddSingleton".Length)], old);
        Assert.Equal([(17, "AddScoped".Length)], now);

        // Only the word that changed: the extension of "index.html" stays unmarked.
        (old, now) = WordDiff.Ranges("MapFallbackToFile(\"index.html\");", "MapFallbackToFile(\"app.html\");");
        Assert.Equal([(19, 5)], old);
        Assert.Equal([(19, 3)], now);

        // Text added at the end: nothing on the old line, the new words on the new one.
        (old, now) = WordDiff.Ranges("app.UseExceptionHandler(\"/Error\");", "app.UseExceptionHandler(\"/Error\", createScopeForErrors: true);");
        Assert.Empty(old);
        var added = Assert.Single(now);
        Assert.Equal(", createScopeForErrors: true", "app.UseExceptionHandler(\"/Error\", createScopeForErrors: true);".Substring(added.Start, added.Length));

        // Same lines, and lines too long to compare, have none.
        Assert.Equal(([], []), WordDiff.Ranges("same", "same"));
        Assert.Equal(([], []), WordDiff.Ranges(new string('a', WordDiff.MaxLineLength + 1), "b"));
    }

    [Fact]
    public void A_change_is_a_run_of_changed_rows_and_each_side_numbers_its_own_lines()
    {
        // Two edits three lines apart: one git hunk at git's three lines of context, two changes here.
        string[] old = ["a", "b", "c", "d", "e", "f", "g"];
        string[] now = ["a", "x", "b", "c", "d", "E", "f", "g"];
        var rows = Rows(old, now);
        var changes = DiffRows.Changes(rows);
        Assert.Equal([new DiffChange(1, 1), new DiffChange(5, 5)], changes);
        Assert.True(changes[1].Contains(5));
        Assert.False(changes[1].Contains(4));

        // The old side's numbers skip its filler line: "b" is line 2 of the old file on the third pane line.
        var (left, right) = DiffRows.SideBySide(rows, old, now);
        Assert.Equal((0, 0), left.NumbersOf(1)); // filler opposite the added "x"
        Assert.Equal((2, 0), left.NumbersOf(2));
        Assert.Equal((0, 3), right.NumbersOf(2));

        // Inline: two columns, the old file's then the new file's.
        var inline = DiffRows.Inline(rows, old, now);
        Assert.Equal((0, 2), inline.NumbersOf(1)); // the added "x"
        Assert.Equal((2, 3), inline.NumbersOf(2)); // "b", unchanged
        Assert.Equal((5, 0), inline.NumbersOf(5)); // the removed "e"
        Assert.Equal((0, 6), inline.NumbersOf(6)); // the added "E"
    }
}
