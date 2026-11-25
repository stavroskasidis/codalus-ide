using System.Text.RegularExpressions;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>The Task List's scan: rows and lines as before, files and lines without a token skipped.</summary>
public sealed class TaskListScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-tasks-" + Guid.NewGuid().ToString("N"));

    public TaskListScanTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static (Regex Line, Regex Any) Regexes(params string[] tokens)
    {
        var words = string.Join('|', tokens.Select(t => Regex.Escape(t.Trim())));
        return (new Regex(@"(?://+|/\*+|#|<!--|'|\*)\s*(" + words + @")\b[:\s-]*(.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
            new Regex("(" + words + @")\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Rows_have_their_tokens_descriptions_and_lines()
    {
        var crlf = Write("a.cs", "class A\r\n{\r\n    // todo: first thing\r\n    /* HACK around it */\r\n}\r\n");
        var plain = Write("b.cs", "var todo = 1; // TODO\n<!-- UNDONE later -->\n");
        var none = Write("c.cs", "nothing here\n");
        var unsaved = Write("d.cs", "// TODO on disk\n");
        var (line, any) = Regexes("TODO", "HACK", "UNDONE");
        var rows = TaskListViewModel.Scan([crlf, plain, none, unsaved], new Dictionary<string, string> { [unsaved] = "\n// TODO unsaved" }, line, any, CancellationToken.None);
        Assert.Equal(
            [("TODO", "first thing", crlf, 3), ("HACK", "around it", crlf, 4), ("TODO", "(no description)", plain, 1), ("UNDONE", "later", plain, 2), ("TODO", "unsaved", unsaved, 2)],
            rows.Select(r => (r.Token, r.Text, r.Path, r.Line)));
    }

    [Fact]
    public void A_custom_token_and_the_cut_at_five_thousand()
    {
        var many = Write("many.cs", string.Concat(Enumerable.Range(0, 5_010).Select(i => $"// Review {i}\n")));
        var (line, any) = Regexes("REVIEW");
        var rows = TaskListViewModel.Scan([many, many], new Dictionary<string, string>(), line, any, CancellationToken.None);
        Assert.Equal(5_000, rows.Count);
        Assert.Equal(("REVIEW", "4999", 5_000), (rows[^1].Token, rows[^1].Text, rows[^1].Line));
    }
}
