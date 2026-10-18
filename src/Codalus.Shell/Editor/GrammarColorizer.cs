using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.LanguageServices.Protocol;
using TextMateSharp.Grammars;

namespace Codalus.Shell.Editor;

/// <summary>
/// Colors a read-only text with the editor's syntax colors without a language service (M34h, Add New Item's preview):
/// the language's TextMate grammar splits each line into scopes, and each scope takes the semantic token type the C#
/// service would give it (a keyword, a type's name, a string, a comment…), drawn in the theme's <see cref="EditorTheme"/>,
/// the colors the editor gives C# and Visual Basic in that theme. The text is tokenized once, when it is set.
/// </summary>
public sealed class GrammarColorizer : DocumentColorizingTransformer
{
    /// <summary>Scope prefixes, most specific first, and the token type each stands for.</summary>
    private static readonly (string Scope, string Type)[] s_scopes =
    [
        ("comment.block.documentation", "xmlDocComment"),
        ("comment", "comment"),
        ("constant.character.escape", "stringEscape"),
        ("string", "string"),
        ("constant.numeric", "number"),
        ("constant.language", "keyword"),
        ("keyword.control.directive", "preprocessor"),
        ("meta.preprocessor", "preprocessor"),
        ("keyword.preprocessor", "preprocessor"),
        ("keyword.control", "controlKeyword"),
        ("keyword.operator", "operator"),
        ("keyword", "keyword"),
        ("storage.modifier", "modifier"),
        ("storage.type", "keyword"),
        ("entity.name.type.namespace", "namespace"),
        ("entity.name.type.interface", "interface"),
        ("entity.name.type.struct", "struct"),
        ("entity.name.type.enum", "enum"),
        ("entity.name.type.delegate", "delegate"),
        ("entity.name.type.type-parameter", "typeParameter"),
        ("entity.name.type", "class"),
        ("support.type", "type"),
        ("entity.name.function", "method"),
        ("entity.name.tag", "markupElement"),
        ("entity.other.attribute-name", "markupAttribute"),
        ("entity.name.variable.parameter", "parameter"),
        ("variable.parameter", "parameter"),
        ("entity.name.variable.local", "local"),
        ("entity.name.variable.field", "field"),
        ("entity.name.variable.property", "property"),
        ("entity.name.variable.enum-member", "enumMember"),
        ("punctuation.definition.tag", "markupTagDelimiter"),
        ("punctuation", "punctuation"),
    ];

    private static readonly Dictionary<string, int> s_typeIndex = TokenTypes.All.Select((name, index) => (name, index)).ToDictionary(t => t.name, t => t.index, StringComparer.Ordinal);

    private (int Start, int Length, int Type)[][] _lines = [];

    public EditorTheme Theme { get; set; } = EditorTheme.Dark;

    /// <summary>Tokenizes <paramref name="document"/> with <paramref name="grammar"/> (a line that takes too long is colored as far as it got).</summary>
    public void Tokenize(TextDocument document, IGrammar grammar)
    {
        var lines = new (int Start, int Length, int Type)[document.LineCount][];
        IStateStack? state = null;
        for (var number = 1; number <= document.LineCount; number++)
        {
            var line = document.GetLineByNumber(number);
            var text = document.GetText(line.Offset, line.Length);
            var result = grammar.TokenizeLine(text, state, TimeSpan.FromMilliseconds(200));
            state = result.RuleStack;
            var tokens = new List<(int, int, int)>();
            foreach (var token in result.Tokens)
            {
                var start = Math.Min(token.StartIndex, text.Length);
                var end = Math.Min(token.EndIndex, text.Length);
                if (end > start && TypeOf(token.Scopes) is { } type)
                {
                    tokens.Add((start, end - start, type));
                }
            }

            lines[number - 1] = [.. tokens];
        }

        _lines = lines;
    }

    /// <summary>Forgets the tokens (a text without a grammar).</summary>
    public void Clear() => _lines = [];

    /// <summary>The token type of a TextMate token: its innermost scope that names one.</summary>
    public static int? TypeOf(IEnumerable<string> scopes)
    {
        foreach (var scope in scopes.Reverse())
        {
            foreach (var (prefix, type) in s_scopes)
            {
                if (scope.Equals(prefix, StringComparison.Ordinal) || scope.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    return s_typeIndex[type];
                }
            }
        }

        return null;
    }

    /// <summary>The token type at a zero-based line and character, or -1 (tests).</summary>
    public int TokenAt(int line, int character) =>
        line < _lines.Length && _lines[line].FirstOrDefault(t => character >= t.Start && character < t.Start + t.Length) is { Length: > 0 } token ? token.Type : -1;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.LineNumber - 1 >= _lines.Length)
        {
            return;
        }

        foreach (var (start, length, type) in _lines[line.LineNumber - 1])
        {
            if (Theme.Token(type) is not { } brush || start + length > line.Length)
            {
                continue;
            }

            ChangeLinePart(line.Offset + start, line.Offset + start + length, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
