using Codalus.Shell.Editor;
using Tint;

namespace Codalus.Shell.Tests;

/// <summary>
/// Lines of code outside an editor (Find Results, M34h) take one tokenizer per language: a tokenizer builds its regular
/// expressions as it reaches its grammar's rules, and a new one for each line made 2,000 Markdown matches take 2.4 s and
/// allocate 4 GiB, against 0.2 s and 150 MiB with one kept (PR 528's review).
/// </summary>
[Collection(CodeColorsConfigurationTests.Name)]
public sealed class CodeColorsTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task A_plugins_grammar_colors_lines_already_shown_without_one()
    {
        await using var f = ShellFixture.Create();
        var languages = f.Shell.Workbench.LanguageDefinitions;
        var changes = 0;
        void OnChanged(object? sender, EventArgs e) => changes++;
        CodeColors.Changed += OnChanged;
        try
        {
            // A Find Results line of a file whose language comes later: plain, and its language, line and tokenizer cached.
            Assert.Null(CodeColors.LanguageOf("Order.ctso"));
            languages.Register(new Workbench.Languages.LanguageDefinition("contoso", "Contoso") { Extensions = [".ctso"] });
            Assert.Equal("contoso", CodeColors.LanguageOf("Order.ctso"));
            Assert.Empty(CodeColors.Tokens("contoso", "begin order"));

            // The plugin's grammar: the language's line is colored, and lines shown are told to color again.
            var grammar = Path.Combine(f.UserData, "contoso.tmLanguage.json");
            await File.WriteAllTextAsync(grammar, """{ "scopeName": "source.contoso", "patterns": [{ "match": "\\bbegin\\b", "name": "keyword.control.contoso" }] }""", TestContext.Current.CancellationToken);
            var before = changes;
            languages.RegisterGrammar(new Workbench.Languages.GrammarDefinition("source.contoso", "contoso", grammar));
            Assert.True(changes > before, "lines shown weren't told to color again");
            Assert.Equal([(0, 5, Array.IndexOf(Codalus.LanguageServices.Protocol.TokenTypes.All, "controlKeyword"))], CodeColors.Tokens("contoso", "begin order"));
        }
        finally
        {
            CodeColors.Changed -= OnChanged;
        }
    }

    [Fact]
    public void Lines_of_a_language_share_its_tokenizer_until_the_configuration_changes()
    {
        var textMate = new TextMateSupport(new Workbench.Languages.LanguageRegistry());
        var asked = new List<string>();
        Tokenizer? TokenizerOf(string id)
        {
            asked.Add(id);
            return textMate.TokenizerFor(id);
        }

        string? LanguageOf(string path) => null;
        CodeColors.Configure(LanguageOf, TokenizerOf, _ => EditorTheme.Dark);
        try
        {
            for (var i = 0; i < 50; i++)
            {
                Assert.NotEmpty(CodeColors.Tokens("typescript", $"const x{i}: string = \"a\";"));
                Assert.Empty(CodeColors.Tokens("plaintext", $"plain {i}"));
            }

            Assert.Equal(["typescript", "plaintext"], asked);

            // A new configuration (another shell's, or the languages again) asks again.
            CodeColors.Configure(LanguageOf, TokenizerOf, _ => EditorTheme.Dark);
            Assert.NotEmpty(CodeColors.Tokens("typescript", "const y = 1;"));
            Assert.Equal(["typescript", "plaintext", "typescript"], asked);
        }
        finally
        {
            CodeColors.Release(LanguageOf);
        }
    }
}

/// <summary>Tests that configure the shell-wide <see cref="CodeColors"/>: alone, since every headless shell configures it too.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CodeColorsConfigurationTests
{
    public const string Name = "Tests that configure CodeColors";
}
