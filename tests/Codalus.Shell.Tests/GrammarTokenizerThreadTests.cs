using Codalus.Shell.Editor;
using TextMateSharp.Grammars;
using TextMateSharp.Model;

namespace Codalus.Shell.Tests;

/// <summary>
/// An editor's grammar tokenizes from two threads at once: AvaloniaEdit.TextMate's model tokenizes the document on its
/// worker, and the lines in view on the UI thread at each scroll, both through the editor's one tokenizer and grammar.
/// TextMateSharp 2.0.3 didn't serialize them, and a line came out with tokens merged or mis-scoped: <c>{ "a": 1 }</c>'s
/// number read as its key (Linux CI, PR #328's run 43168967815, in
/// <c>EditorLargeTextTests.Hidden_editors_stop_their_grammar_thread_and_grammars_are_read_once</c>). Alone, about 7 rounds
/// in 10 went wrong with 2.0.3; 2.0.5 serializes them (docs/ci-flakes.md).
/// </summary>
public sealed class GrammarTokenizerThreadTests
{
    [Fact]
    public void A_line_tokenized_from_two_threads_at_once_gets_the_tokens_it_gets_alone()
    {
        var textMate = new TextMateSupport(new Workbench.Languages.LanguageRegistry());
        var grammar = textMate.GrammarFor("json");
        Assert.NotNull(grammar);
        var line = new LineText("{ \"a\": 1, \"b\": [true, \"c\"] }\n");
        var alone = Describe(new Tokenizer(grammar).Tokenize(line, null, 0, 10_000, TimeSpan.FromSeconds(3)).Tokens);

        for (var round = 0; round < 500; round++)
        {
            // As one editor: its worker and the UI thread share its tokenizer.
            var tokenizer = new Tokenizer(grammar);
            using var start = new Barrier(2);
            var results = new string?[2];
            var errors = new Exception?[2];
            var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    results[i] = Describe(tokenizer.Tokenize(line, null, 0, 10_000, TimeSpan.FromSeconds(3)).Tokens);
                }
                catch (Exception ex)
                {
                    errors[i] = ex;
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            for (var i = 0; i < 2; i++)
            {
                Assert.True(errors[i] is null, $"Round {round}: tokenizing threw {errors[i]}");
                Assert.True(results[i] == alone, $"Round {round}: {results[i]}\nalone: {alone}");
            }
        }
    }

    private static string Describe(List<TMToken> tokens) =>
        string.Join(" | ", tokens.Select(t => $"{t.StartIndex} {string.Join(' ', t.Scopes)}"));
}
