using Codalus.Shell.Editor;
using Tint;

namespace Codalus.Shell.Tests;

/// <summary>
/// Every editor, diff and viewer takes its own tokenizer from the one registry, from its own thread (an editor's
/// worker on the thread pool, the diff's tokenizing, the UI thread's single lines): the grammar they share is compiled
/// once and must give each the tokens it gives alone. (AvaloniaEdit.TextMate shared one tokenizer between its worker and
/// the UI thread, and TextMateSharp 2.0.3 gave a line merged or mis-scoped tokens then: <c>{ "a": 1 }</c>'s number read
/// as its key, docs/ci-flakes.md.)
/// </summary>
public sealed class GrammarTokenizerThreadTests
{
    [Fact]
    public void Tokenizers_made_from_threads_at_once_get_the_tokens_one_gets_alone()
    {
        const string line = "{ \"a\": 1, \"b\": [true, \"c\"] }";
        var alone = Describe(new TextMateSupport(new Workbench.Languages.LanguageRegistry()).TokenizerFor("json"), line);

        for (var round = 0; round < 50; round++)
        {
            // A registry no grammar was compiled in yet: the threads compile it at once.
            var textMate = new TextMateSupport(new Workbench.Languages.LanguageRegistry());
            using var start = new Barrier(4);
            var results = new string?[4];
            var errors = new Exception?[4];
            var threads = Enumerable.Range(0, 4).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    results[i] = Describe(textMate.TokenizerFor("json"), line);
                }
                catch (Exception ex)
                {
                    errors[i] = ex;
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            for (var i = 0; i < 4; i++)
            {
                Assert.True(errors[i] is null, $"Round {round}: tokenizing threw {errors[i]}");
                Assert.True(results[i] == alone, $"Round {round}: {results[i]}\nalone: {alone}");
            }
        }
    }

    private static string Describe(Tokenizer? tokenizer, string line)
    {
        Assert.NotNull(tokenizer);
        var description = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            description.Clear();
            foreach (var token in tokenizer.TokenizeLine(line, StateStack.Initial, out _))
            {
                description.Add($"{token.Start} {string.Join(' ', token.Scopes.GetScopeNames())}");
            }
        }

        return string.Join(" | ", description);
    }
}
