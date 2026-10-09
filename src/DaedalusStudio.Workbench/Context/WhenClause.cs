using System.Globalization;
using System.Text.RegularExpressions;

namespace DaedalusStudio.Workbench.Context;

/// <summary>
/// A boolean expression over context keys, as used by <c>when</c> and <c>enablement</c> in key bindings, menus
/// and command contributions. Grammar (precedence low → high):
/// <code>
/// expr    := and ('||' and)*
/// and     := unary ('&amp;&amp;' unary)*
/// unary   := '!' unary | primary
/// primary := '(' expr ')' | 'true' | 'false' | key [op value]
/// op      := '==' | '!=' | '=~' | '&lt;' | '&lt;=' | '&gt;' | '&gt;='
/// value   := 'quoted' | "quoted" | /regex/ | bare-word | number
/// </code>
/// A bare key is true when its value is <c>true</c>, a non-empty string or a non-zero number.
/// </summary>
public sealed class WhenClause
{
    private readonly Node _root;

    private WhenClause(string text, Node root)
    {
        Text = text;
        _root = root;
    }

    public static WhenClause Always { get; } = new("true", new Constant(true));

    public string Text { get; }

    public static WhenClause Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Always;
        }

        var parser = new Parser(text);
        var root = parser.ParseExpression();
        parser.ExpectEnd();
        return new WhenClause(text.Trim(), root);
    }

    public static bool TryParse(string? text, out WhenClause clause, out string? error)
    {
        try
        {
            clause = Parse(text);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            clause = Always;
            error = ex.Message;
            return false;
        }
    }

    public bool Evaluate(IContextKeyReader context) => _root.Evaluate(context);

    public override string ToString() => Text;

    // ------------------------------------------------------------------ AST

    private abstract class Node
    {
        public abstract bool Evaluate(IContextKeyReader context);
    }

    private sealed class Constant(bool value) : Node
    {
        public override bool Evaluate(IContextKeyReader context) => value;
    }

    private sealed class Not(Node inner) : Node
    {
        public override bool Evaluate(IContextKeyReader context) => !inner.Evaluate(context);
    }

    private sealed class And(Node left, Node right) : Node
    {
        public override bool Evaluate(IContextKeyReader context) => left.Evaluate(context) && right.Evaluate(context);
    }

    private sealed class Or(Node left, Node right) : Node
    {
        public override bool Evaluate(IContextKeyReader context) => left.Evaluate(context) || right.Evaluate(context);
    }

    private sealed class Truthy(string key) : Node
    {
        public override bool Evaluate(IContextKeyReader context) => context.GetValue(key) switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            double d => d != 0,
            int i => i != 0,
            _ => true,
        };
    }

    private sealed class Compare(string key, string op, string value) : Node
    {
        private readonly Regex? _regex = op == "=~" ? new Regex(value, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) : null;

        public override bool Evaluate(IContextKeyReader context)
        {
            var actual = context.GetValue(key);
            var text = actual switch
            {
                null => string.Empty,
                bool b => b ? "true" : "false",
                double d => d.ToString(CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                _ => actual.ToString() ?? string.Empty,
            };
            switch (op)
            {
                case "==":
                    return string.Equals(text, value, StringComparison.Ordinal);
                case "!=":
                    return !string.Equals(text, value, StringComparison.Ordinal);
                case "=~":
                    return _regex!.IsMatch(text);
                default:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
                        || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
                    {
                        return false;
                    }

                    return op switch
                    {
                        "<" => left < right,
                        "<=" => left <= right,
                        ">" => left > right,
                        _ => left >= right,
                    };
            }
        }
    }

    // ------------------------------------------------------------------ parser

    private sealed class Parser(string text)
    {
        private int _pos;

        public Node ParseExpression()
        {
            var left = ParseAnd();
            while (Match("||"))
            {
                left = new Or(left, ParseAnd());
            }

            return left;
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_pos < text.Length)
            {
                throw Error($"unexpected '{text[_pos]}'");
            }
        }

        private Node ParseAnd()
        {
            var left = ParseUnary();
            while (Match("&&"))
            {
                left = new And(left, ParseUnary());
            }

            return left;
        }

        private Node ParseUnary()
        {
            SkipWhitespace();
            if (Peek('!') && !PeekAt(1, '='))
            {
                _pos++;
                return new Not(ParseUnary());
            }

            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipWhitespace();
            if (Match("("))
            {
                var inner = ParseExpression();
                if (!Match(")"))
                {
                    throw Error("expected ')'");
                }

                return inner;
            }

            var key = ReadWord();
            if (key.Length == 0)
            {
                throw Error("expected a context key");
            }

            if (key == "true")
            {
                return new Constant(true);
            }

            if (key == "false")
            {
                return new Constant(false);
            }

            SkipWhitespace();
            foreach (var op in new[] { "==", "!=", "=~", "<=", ">=", "<", ">" })
            {
                if (Match(op))
                {
                    return new Compare(key, op, ReadValue(op == "=~"));
                }
            }

            return new Truthy(key);
        }

        private string ReadValue(bool regex)
        {
            SkipWhitespace();
            if (_pos < text.Length && (text[_pos] is '\'' or '"' || (regex && text[_pos] == '/')))
            {
                var quote = text[_pos++];
                var start = _pos;
                while (_pos < text.Length && text[_pos] != quote)
                {
                    if (text[_pos] == '\\' && _pos + 1 < text.Length)
                    {
                        _pos++;
                    }

                    _pos++;
                }

                if (_pos >= text.Length)
                {
                    throw Error("unterminated string");
                }

                return text[start.._pos++];
            }

            var word = ReadWord();
            return word.Length > 0 ? word : throw Error("expected a value");
        }

        private string ReadWord()
        {
            SkipWhitespace();
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '.' or '_' or '-' or ':' or '/'))
            {
                _pos++;
            }

            return text[start.._pos];
        }

        private bool Match(string token)
        {
            SkipWhitespace();
            if (string.CompareOrdinal(text, _pos, token, 0, token.Length) == 0)
            {
                _pos += token.Length;
                return true;
            }

            return false;
        }

        private bool Peek(char c) => _pos < text.Length && text[_pos] == c;

        private bool PeekAt(int offset, char c) => _pos + offset < text.Length && text[_pos + offset] == c;

        private void SkipWhitespace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        private FormatException Error(string message) => new($"Invalid when-clause '{text}' at {_pos}: {message}.");
    }
}
