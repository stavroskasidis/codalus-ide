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
