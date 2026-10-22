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
}
