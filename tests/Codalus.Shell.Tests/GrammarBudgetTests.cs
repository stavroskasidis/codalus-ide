using System.Diagnostics;
using AvaloniaEdit.Document;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Tests;

/// <summary>
/// A line that runs out of its time budget (200 ms) is colored as far as it got. Tint then hands on the state it stopped
/// in (inside a string, say), where TextMateSharp handed on the line's own: the next line must start as the cut one did,
/// or every line after it takes the string's color (the diff's panes and the code viewer, as the editor's TintColorizer).
/// </summary>
public sealed class GrammarBudgetTests
{
    /// <summary>
    /// A template literal spans lines, and each of its escapes is a search of its own: millions of them take far longer
    /// than the budget on any machine (Tint checks it before each search), so the first line is cut inside the template.
    /// </summary>
    private static readonly string s_text = "`" + string.Concat(Enumerable.Repeat("\\n", 4_000_000)) + "`;\nvar y = 2;\n";

    [Fact]
    public async Task The_diffs_tokens_start_the_line_after_a_cut_one_as_it_started()
    {
        var tokenizer = new GrammarTokenizer(new TextMateSupport(new Workbench.Languages.LanguageRegistry())) { MaxLineLength = int.MaxValue };
        var watch = Stopwatch.StartNew();
        var tokens = await tokenizer.TokenizeAsync(s_text, "javascript", "dark", TestContext.Current.CancellationToken);

        Assert.NotNull(tokens);
        var first = tokens.Line(0).ToArray();
        var second = tokens.Line(1).ToArray();
        string Describe(SourceToken[] line) => string.Join(", ", line.Take(5).Select(t => $"{t.Start}+{t.Length}:{t.Argb:X8}"));
        Assert.True(first is [{ Start: 0 }, ..] && first[^1].Start + first[^1].Length < s_text.IndexOf('\n', StringComparison.Ordinal) - 1,
            $"line 1 is colored from its backtick and cut before its end ({watch.ElapsedMilliseconds} ms): {Describe(first)} … {first.Length} tokens");
        Assert.True(
            second is [{ Start: 0, Length: 3 } var, ..] && var.Argb != first[0].Argb,
            $"line 2's 'var' must not take the template's color: line 1 {Describe(first)}; line 2 {Describe(second)}");
    }

    [Fact]
    public void The_viewers_tokens_start_the_line_after_a_cut_one_as_it_started()
    {
        var textMate = new TextMateSupport(new Workbench.Languages.LanguageRegistry());
        var colorizer = new GrammarColorizer();
        var document = new TextDocument(s_text);
        colorizer.Tokenize(document, textMate.TokenizerFor("javascript")!);

        Assert.Equal(Array.IndexOf(TokenTypes.All, "string"), colorizer.TokenAt(0, 0)); // the backtick
        Assert.Equal(-1, colorizer.TokenAt(0, document.GetLineByNumber(1).Length - 2)); // cut before the template's end
        Assert.Equal(Array.IndexOf(TokenTypes.All, "keyword"), colorizer.TokenAt(1, 0)); // "var", not the template's string
    }
}
