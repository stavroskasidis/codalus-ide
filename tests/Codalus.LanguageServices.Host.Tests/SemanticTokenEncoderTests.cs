using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Tests;

public class SemanticTokenEncoderTests
{
    [Fact]
    public void Flatten_splits_container_around_nested_span()
    {
        // "a\nb" string literal with an escape sequence in the middle.
        var outer = new TokenSpan(0, 6, Type: 1, Modifiers: 0);
        var inner = new TokenSpan(2, 2, Type: 2, Modifiers: 0);

        var flat = SemanticTokenEncoder.Flatten([inner, outer]);

        Assert.Equal(
            [new TokenSpan(0, 2, 1, 0), new TokenSpan(2, 2, 2, 0), new TokenSpan(4, 2, 1, 0)],
            flat);
    }

    [Fact]
    public void Flatten_keeps_adjacent_spans_unchanged()
    {
        var flat = SemanticTokenEncoder.Flatten([new TokenSpan(5, 3, 1, 0), new TokenSpan(0, 5, 2, 1)]);

        Assert.Equal([new TokenSpan(0, 5, 2, 1), new TokenSpan(5, 3, 1, 0)], flat);
    }

    [Fact]
    public void Encode_uses_lsp_relative_positions()
    {
        var text = SourceText.From("int x;\nint y;");
        int[] data = SemanticTokenEncoder.Encode(text, [new TokenSpan(0, 3, 7, 0), new TokenSpan(4, 1, 8, 1), new TokenSpan(7, 3, 7, 0)]);

        Assert.Equal(
            [
                0, 0, 3, 7, 0, // "int" at (0,0)
                0, 4, 1, 8, 1, // "x"   at (0,4)
                1, 0, 3, 7, 0, // "int" at (1,0)
            ],
            data);
    }

    [Fact]
    public void Encode_splits_multiline_tokens_per_line()
    {
        var text = SourceText.From("/* a\r\n  b */ x");
        int[] data = SemanticTokenEncoder.Encode(text, [new TokenSpan(0, 12, 17, 0)]);

        Assert.Equal(
            [
                0, 0, 4, 17, 0, // "/* a"
                1, 0, 6, 17, 0, // "  b */"
            ],
            data);
    }
}
