using System.Text.RegularExpressions;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>Find in Files' line walk numbers lines as Split('\n') does, and its prefilter never hides a match.</summary>
public sealed class FindLineWalkTests
{
    private static List<(int Index, string Line)> Lines(string text)
    {
        var lines = new List<(int, string)>();
        FindResultsPage.ForEachLine(text, (i, line) =>
        {
            lines.Add((i, line.ToString()));
            return true;
        });
        return lines;
    }

    [Theory]
    [InlineData("one\r\ntwo\r\n")]
    [InlineData("one\ntwo")]
    [InlineData("\n\n  indented\r\r\n")]
    [InlineData("")]
    [InlineData("a\u2028b\rc\u0085d")]
    public void Lines_are_those_of_split_on_newlines_without_their_carriage_returns(string text)
    {
        Assert.Equal(text.Split('\n').Select((l, i) => (i, l.TrimEnd('\r'))), Lines(text));
    }

    [Fact]
    public void The_walk_stops_when_asked()
    {
        var seen = 0;
        FindResultsPage.ForEachLine("a\nb\nc\nd", (_, _) => ++seen < 2);
        Assert.Equal(2, seen);
    }

    [Fact]
    public void Only_a_search_of_words_skips_a_file_without_them_and_matches_stay_where_they_were()
    {
        var words = new FindInFilesOptions("Count", WholeWord: true).CreateRegex();
        Assert.False(FindResultsPage.MayMatch(words, "int counter = 1;\nCounts", isRegex: false));
        Assert.True(FindResultsPage.MayMatch(words, "x\n  return Count;", isRegex: false));
        Assert.True(FindResultsPage.MayMatch(new Regex("a\\sb"), "nothing", isRegex: true)); // a pattern is searched line by line

        var row = FindResultsPage.MatchLine("f.cs", 2, "    return Count;", words);
        Assert.NotNull(row);
        Assert.Equal(11, row!.Column); // in the untrimmed line
        Assert.Equal("return Count;", row.Text);
        Assert.Equal([7, 8, 9, 10, 11], row.Highlights);
        Assert.Null(FindResultsPage.MatchLine("f.cs", 1, "nothing here", words));
    }
}
