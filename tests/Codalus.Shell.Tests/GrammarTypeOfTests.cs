using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Tests;

/// <summary>A TextMate token's type: its innermost scope with an entry, read by index, whole scope names only.</summary>
public sealed class GrammarTypeOfTests
{
    private static string? Type(params string[] scopes) => GrammarColorizer.TypeOf(scopes) is { } type ? TokenTypes.All[type] : null;

    private static string? TypeAsSequence(params string[] scopes) => GrammarColorizer.TypeOf(scopes.Select(s => s)) is { } type ? TokenTypes.All[type] : null;

    [Theory]
    [InlineData("comment", "source.cs", "comment.line.double-slash.cs")]
    [InlineData("string", "source.cs", "string.quoted.double.cs", "punctuation.definition.string.begin.cs")]
    [InlineData("stringEscape", "source.cs", "string.quoted.double.cs", "constant.character.escape.cs")]
    [InlineData("controlKeyword", "source.cs", "keyword.control.flow.return.cs")]
    [InlineData("keyword", "source.cs", "keyword.operator.expression.typeof.cs")]
    [InlineData("operator", "source.cs", "keyword.operator.assignment.cs")]
    [InlineData("xmlDocComment", "source.cs", "comment.block.documentation.cs")]
    [InlineData("comment", "source.cs", "comment", "punctuation.definition.comment.cs")]
    [InlineData(null, "source.cs", "stringx.other", "commentary")]
    [InlineData(null, "source.cs")]
    public void The_innermost_scope_with_an_entry_names_the_type(string? expected, params string[] scopes)
    {
        Assert.Equal(expected, Type(scopes));
        Assert.Equal(expected, TypeAsSequence(scopes));
    }
}
