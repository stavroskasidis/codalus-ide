using System.Globalization;

namespace Codalus.Workbench.Projects;

/// <summary>
/// The template engine's C++-style conditions (<c>(InteractivityPlatform != "None")</c>,
/// <c>(HostIdentifier != "dotnetcli" &amp;&amp; HostIdentifier != "dotnetcli-preview")</c>): identifiers, quoted strings,
/// <c>true</c>/<c>false</c>, numbers, <c>==</c>, <c>!=</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>!</c> and parentheses. New
/// Project evaluates them to enable a template's options and to choose the files to open, as an IDE host:
/// <c>HostIdentifier</c> is <see cref="IdeHostIdentifier"/> unless the values say otherwise. A template's computed
/// symbols (<c>UseWebAssembly</c>) are evaluated from their own conditions. Text that doesn't parse, and an unknown symbol
/// tested on its own, count as true, so an option is never disabled by a condition Codalus can't read; an unknown name
/// compared with <c>==</c> is the literal text (<c>XUnitVersion == v3</c>).
/// </summary>
public static class TemplateCondition
{
    /// <summary>The host identifier of the classic IDE, for which templates write their IDE-only conditions.</summary>
    public const string IdeHostIdentifier = "vs";

    public static bool Evaluate(string? condition, IReadOnlyDictionary<string, string?> values, IReadOnlyDictionary<string, string>? computed = null)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        try
        {
            var parser = new Parser(Tokenize(condition), values, computed ?? new Dictionary<string, string>(StringComparer.Ordinal), 0);
            var result = parser.ParseExpression();
            return parser.AtEnd ? result.AsBool() : true;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    private enum TokenKind
    {
        Identifier,
        String,
        Number,
        LeftParen,
        RightParen,
        Not,
        And,
        Or,
        Equal,
        NotEqual,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c is '"' or '\'')
            {
                var end = text.IndexOf(c, i + 1);
                if (end < 0)
                {
                    throw new FormatException("An unterminated string.");
                }

                tokens.Add(new Token(TokenKind.String, text[(i + 1)..end]));
                i = end + 1;
            }
            else if (c == '(')
            {
                tokens.Add(new Token(TokenKind.LeftParen, "("));
                i++;
            }
            else if (c == ')')
            {
                tokens.Add(new Token(TokenKind.RightParen, ")"));
                i++;
            }
            else if (Match(text, i, "&&"))
            {
                tokens.Add(new Token(TokenKind.And, "&&"));
                i += 2;
            }
            else if (Match(text, i, "||"))
            {
                tokens.Add(new Token(TokenKind.Or, "||"));
                i += 2;
            }
            else if (Match(text, i, "=="))
            {
                tokens.Add(new Token(TokenKind.Equal, "=="));
                i += 2;
            }
            else if (Match(text, i, "!="))
            {
                tokens.Add(new Token(TokenKind.NotEqual, "!="));
                i += 2;
            }
            else if (c == '!')
            {
                tokens.Add(new Token(TokenKind.Not, "!"));
                i++;
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '.' or '-')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.' or '-'))
                {
                    i++;
                }

                var word = text[start..i];
                tokens.Add(new Token(double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? TokenKind.Number : TokenKind.Identifier, word));
            }
            else
            {
                throw new FormatException($"Unexpected '{c}'.");
            }
        }

        return tokens;
    }

    private static bool Match(string text, int index, string token) => string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    /// <summary>A value: text with a known value, or an unknown name (true on its own, its own text when compared).</summary>
    private readonly record struct Value(string Text, bool Known)
    {
        public bool AsBool() => !Known || !bool.TryParse(Text, out var parsed) || parsed;

        public static Value Of(bool value) => new(value ? "true" : "false", true);
    }

    private sealed class Parser(List<Token> tokens, IReadOnlyDictionary<string, string?> values, IReadOnlyDictionary<string, string> computed, int depth)
    {
        private int _position;

        public bool AtEnd => _position >= tokens.Count;

        public Value ParseExpression()
        {
            var left = ParseAnd();
            while (Accept(TokenKind.Or))
            {
                var right = ParseAnd();
                left = Value.Of(left.AsBool() || right.AsBool());
            }

            return left;
        }

        private Value ParseAnd()
        {
            var left = ParseUnary();
            while (Accept(TokenKind.And))
            {
                var right = ParseUnary();
                left = Value.Of(left.AsBool() && right.AsBool());
            }

            return left;
        }

        private Value ParseUnary()
        {
            if (Accept(TokenKind.Not))
            {
                var operand = ParseUnary();
                return operand.Known ? Value.Of(!operand.AsBool()) : operand; // !unknown stays unknown: true
            }

            var left = ParsePrimary();
            if (Accept(TokenKind.Equal))
            {
                return Value.Of(Same(left, ParsePrimary()));
            }

            if (Accept(TokenKind.NotEqual))
            {
                return Value.Of(!Same(left, ParsePrimary()));
            }

            return left;
        }

        private Value ParsePrimary()
        {
            if (AtEnd)
            {
                throw new FormatException("The condition ends early.");
            }

            var token = tokens[_position++];
            switch (token.Kind)
            {
                case TokenKind.LeftParen:
                    var inner = ParseExpression();
                    if (!Accept(TokenKind.RightParen))
                    {
                        throw new FormatException("A ')' is missing.");
                    }

                    return inner;
                case TokenKind.String:
                case TokenKind.Number:
                    return new Value(token.Text, true);
                case TokenKind.Identifier when token.Text.Equals("true", StringComparison.OrdinalIgnoreCase) || token.Text.Equals("false", StringComparison.OrdinalIgnoreCase):
                    return new Value(token.Text.ToLowerInvariant(), true);
                case TokenKind.Identifier:
                    return Symbol(token.Text);
                default:
                    throw new FormatException($"Unexpected '{token.Text}'.");
            }
        }

        private Value Symbol(string name)
        {
            if (TryValue(name, out var text))
            {
                return new Value(text, true);
            }

            if (name == "HostIdentifier")
            {
                return new Value(IdeHostIdentifier, true);
            }

            if (depth < 8 && Find(computed, name) is { } expression)
            {
                var parser = new Parser(Tokenize(expression), values, computed, depth + 1);
                var result = parser.ParseExpression();
                return parser.AtEnd ? Value.Of(result.AsBool()) : new Value(name, false);
            }

            return new Value(name, false);
        }

        private bool TryValue(string name, out string text)
        {
            if (values.TryGetValue(name, out var exact) && exact is not null)
            {
                text = exact;
                return true;
            }

            foreach (var (key, value) in values)
            {
                if (value is not null && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    text = value;
                    return true;
                }
            }

            text = string.Empty;
            return false;
        }

        private static string? Find(IReadOnlyDictionary<string, string> map, string name) =>
            map.TryGetValue(name, out var exact) ? exact : map.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

        private static bool Same(Value left, Value right) => string.Equals(left.Text, right.Text, StringComparison.OrdinalIgnoreCase);

        private bool Accept(TokenKind kind)
        {
            if (!AtEnd && tokens[_position].Kind == kind)
            {
                _position++;
                return true;
            }

            return false;
        }
    }
}
