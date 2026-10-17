using System.Globalization;
using System.Text;

namespace Codalus.Debugging.Host.NetFx;

// The expressions the desktop-CLR adapter evaluates (M43b, ADR 0076): a subset of C# expressions, parsed here and
// evaluated by ClrEvaluator against the stopped program. Literals, names, this, member access, indexers, calls,
// unary and binary operators (with C#'s precedence), ?:, ?? and ?., and casts to primitive types.

internal abstract record Expr;

/// <summary>null, bool, char, string, int, uint, long, ulong, float, double or decimal.</summary>
internal sealed record LiteralExpr(object? Value) : Expr;

internal sealed record NameExpr(string Name) : Expr;

/// <summary><c>target.Name</c>; <see cref="Conditional"/> for <c>target?.Name</c>.</summary>
internal sealed record MemberExpr(Expr Target, string Name, bool Conditional = false) : Expr;

internal sealed record IndexExpr(Expr Target, IReadOnlyList<Expr> Arguments) : Expr;

/// <summary>A call: <see cref="Callee"/> is a <see cref="NameExpr"/> or a <see cref="MemberExpr"/>.</summary>
internal sealed record CallExpr(Expr Callee, IReadOnlyList<Expr> Arguments) : Expr;

internal sealed record UnaryExpr(string Operator, Expr Operand) : Expr;

internal sealed record BinaryExpr(string Operator, Expr Left, Expr Right) : Expr;

internal sealed record ConditionalExpr(Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr;

/// <summary><c>(int)x</c>: <see cref="TypeName"/> as written.</summary>
internal sealed record CastExpr(string TypeName, Expr Operand) : Expr;

/// <summary>An expression that isn't valid, or isn't supported, with the message to show.</summary>
internal sealed class ExpressionException(string message) : InvalidOperationException(message);

internal static class ExpressionParser
{
    /// <summary>C#'s keywords for primitive types, and their runtime types.</summary>
    public static readonly IReadOnlyDictionary<string, string> Keywords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bool"] = "System.Boolean",
        ["char"] = "System.Char",
        ["sbyte"] = "System.SByte",
        ["byte"] = "System.Byte",
        ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16",
        ["int"] = "System.Int32",
        ["uint"] = "System.UInt32",
        ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64",
        ["float"] = "System.Single",
        ["double"] = "System.Double",
        ["decimal"] = "System.Decimal",
        ["string"] = "System.String",
        ["object"] = "System.Object",
    };

    public static Expr Parse(string text)
    {
        var parser = new Parser(Tokenize(text));
        var expression = parser.ParseExpression();
        if (!parser.AtEnd)
        {
            throw new ExpressionException($"Unexpected '{parser.Current.Text}' in the expression.");
        }

        return expression;
    }

    private enum Kind
    {
        Identifier,
        Number,
        String,
        Char,
        Operator,
        End,
    }

    private readonly record struct Token(Kind Kind, string Text, object? Value = null);

    private static readonly string[] s_operators =
    [
        "?.", "??", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||",
        "+", "-", "*", "/", "%", "!", "~", "<", ">", "&", "|", "^", "?", ":", ".", ",", "(", ")", "[", "]",
    ];

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
                continue;
            }

            if (char.IsLetter(c) || c == '_' || (c == '@' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_')))
            {
                var start = c == '@' ? i + 1 : i;
                i = start;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(Kind.Identifier, text[start..i]));
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                var (value, length) = ReadNumber(text, i);
                tokens.Add(new Token(Kind.Number, text.Substring(i, length), value));
                i += length;
                continue;
            }

            if (c == '"' || (c == '@' && i + 1 < text.Length && text[i + 1] == '"'))
            {
                var (value, length) = c == '@' ? ReadVerbatim(text, i) : ReadQuoted(text, i, '"');
                tokens.Add(new Token(Kind.String, text.Substring(i, length), value));
                i += length;
                continue;
            }

            if (c == '\'')
            {
                var (value, length) = ReadQuoted(text, i, '\'');
                if (value.Length != 1)
                {
                    throw new ExpressionException("A character literal holds one character.");
                }

                tokens.Add(new Token(Kind.Char, text.Substring(i, length), value[0]));
                i += length;
                continue;
            }

            var op = s_operators.FirstOrDefault(o => string.CompareOrdinal(text, i, o, 0, o.Length) == 0)
                ?? throw new ExpressionException($"Unexpected '{c}' in the expression.");
            tokens.Add(new Token(Kind.Operator, op));
            i += op.Length;
        }

        tokens.Add(new Token(Kind.End, string.Empty));
        return tokens;
    }

    private static (object Value, int Length) ReadNumber(string text, int start)
    {
        var i = start;
        var hex = i + 1 < text.Length && text[i] == '0' && (text[i + 1] is 'x' or 'X');
        var binary = i + 1 < text.Length && text[i] == '0' && (text[i + 1] is 'b' or 'B');
        var digits = new StringBuilder();
        var real = false;
        if (hex || binary)
        {
            i += 2;
            while (i < text.Length && (Uri.IsHexDigit(text[i]) || text[i] == '_') && !(binary && text[i] is not ('0' or '1' or '_')))
            {
                if (text[i] != '_')
                {
                    digits.Append(text[i]);
                }

                i++;
            }
        }
        else
        {
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_' || (text[i] == '.' && !real && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))))
            {
                real |= text[i] == '.';
                if (text[i] != '_')
                {
                    digits.Append(text[i]);
                }

                i++;
            }

            if (i < text.Length && text[i] is 'e' or 'E')
            {
                real = true;
                digits.Append('e');
                i++;
                if (i < text.Length && text[i] is '+' or '-')
                {
                    digits.Append(text[i++]);
                }

                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    digits.Append(text[i++]);
                }
            }
        }

        var suffix = new StringBuilder();
        while (i < text.Length && char.IsLetter(text[i]))
        {
            suffix.Append(char.ToLowerInvariant(text[i++]));
        }

        if (digits.Length == 0)
        {
            throw new ExpressionException($"'{text[start..i]}' isn't a number.");
        }

        var s = digits.ToString();
        var kind = suffix.ToString();
        try
        {
            object value = kind switch
            {
                "f" => float.Parse(s, CultureInfo.InvariantCulture),
                "d" => double.Parse(s, CultureInfo.InvariantCulture),
                "m" => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
                _ when real => kind.Length == 0 ? double.Parse(s, CultureInfo.InvariantCulture) : throw new ExpressionException($"'{text[start..i]}' isn't a number."),
                _ => Integer(hex ? ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : binary ? Convert.ToUInt64(s, 2) : ulong.Parse(s, CultureInfo.InvariantCulture), kind, text[start..i]),
            };
            return (value, i - start);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new ExpressionException($"'{text[start..i]}' isn't a number C# accepts.");
        }
    }

    /// <summary>An integer literal's type, as C# picks it: the first of int, uint, long, ulong that holds it (narrowed by its suffix).</summary>
    private static object Integer(ulong value, string suffix, string text) => suffix switch
    {
        "" => value <= int.MaxValue ? (int)value : value <= uint.MaxValue ? (object)(uint)value : value <= long.MaxValue ? (object)(long)value : value,
        "u" => value <= uint.MaxValue ? (object)(uint)value : value,
        "l" => value <= long.MaxValue ? (object)(long)value : value,
        "ul" or "lu" => value,
        _ => throw new ExpressionException($"'{text}' isn't a number C# accepts."),
    };

    private static (string Value, int Length) ReadQuoted(string text, int start, char quote)
    {
        var builder = new StringBuilder();
        var i = start + 1;
        while (i < text.Length && text[i] != quote)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                switch (text[i])
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case '0': builder.Append('\0'); break;
                    case 'a': builder.Append('\a'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'v': builder.Append('\v'); break;
                    case 'u' when i + 4 < text.Length && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                        builder.Append((char)code);
                        i += 4;
                        break;
                    default: builder.Append(text[i]); break;
                }

                i++;
                continue;
            }

            builder.Append(text[i++]);
        }

        if (i >= text.Length)
        {
            throw new ExpressionException(quote == '"' ? "A string literal isn't closed." : "A character literal isn't closed.");
        }

        return (builder.ToString(), i - start + 1);
    }

    private static (string Value, int Length) ReadVerbatim(string text, int start)
    {
        var builder = new StringBuilder();
        var i = start + 2;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    builder.Append('"');
                    i += 2;
                    continue;
                }

                return (builder.ToString(), i - start + 1);
            }

            builder.Append(text[i++]);
        }

        throw new ExpressionException("A string literal isn't closed.");
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _position;

        public Token Current => tokens[_position];

        public bool AtEnd => Current.Kind == Kind.End;

        private bool Is(string op) => Current.Kind == Kind.Operator && Current.Text == op;

        private bool Accept(string op)
        {
            if (Is(op))
            {
                _position++;
                return true;
            }

            return false;
        }

        private void Expect(string op)
        {
            if (!Accept(op))
            {
                throw new ExpressionException(AtEnd ? $"'{op}' is missing at the end of the expression." : $"'{op}' was expected, not '{Current.Text}'.");
            }
        }

        public Expr ParseExpression()
        {
            if (AtEnd)
            {
                throw new ExpressionException("The expression is empty.");
            }

            var condition = ParseCoalesce();
            if (Accept("?"))
            {
                var whenTrue = ParseExpression();
                Expect(":");
                return new ConditionalExpr(condition, whenTrue, ParseExpression());
            }

            return condition;
        }

        private Expr ParseCoalesce()
        {
            var left = ParseBinary(0);
            return Accept("??") ? new BinaryExpr("??", left, ParseCoalesce()) : left;
        }

        private static readonly string[][] s_levels =
        [
            ["||"],
            ["&&"],
            ["|"],
            ["^"],
            ["&"],
            ["==", "!="],
            ["<", ">", "<=", ">="],
            ["<<", ">>"],
            ["+", "-"],
            ["*", "/", "%"],
        ];

        private Expr ParseBinary(int level)
        {
            if (level == s_levels.Length)
            {
                return ParseUnary();
            }

            var left = ParseBinary(level + 1);
            while (Current.Kind == Kind.Operator && s_levels[level].Contains(Current.Text))
            {
                var op = Current.Text;
                _position++;
                left = new BinaryExpr(op, left, ParseBinary(level + 1));
            }

            return left;
        }

        private Expr ParseUnary()
        {
            foreach (var op in (string[])["!", "-", "+", "~"])
            {
                if (Accept(op))
                {
                    return new UnaryExpr(op, ParseUnary());
                }
            }

            if (Is("(") && TryCast() is { } cast)
            {
                return cast;
            }

            return ParsePostfix(ParsePrimary());
        }

        /// <summary>A cast to a primitive keyword type (<c>(int)x</c>), or to a dotted type name when what follows can only be an operand.</summary>
        private CastExpr? TryCast()
        {
            var start = _position;
            _position++;
            if (Current.Kind == Kind.Identifier)
            {
                var name = new StringBuilder(Current.Text);
                _position++;
                while (Is(".") && tokens[_position + 1].Kind == Kind.Identifier)
                {
                    name.Append('.').Append(tokens[_position + 1].Text);
                    _position += 2;
                }

                if (Accept(")"))
                {
                    var keyword = Keywords.ContainsKey(name.ToString());
                    var operandFollows = Current.Kind is Kind.Identifier or Kind.Number or Kind.String or Kind.Char || Is("(");
                    var unaryFollows = Current.Kind == Kind.Operator && Current.Text is "-" or "!" or "~" or "+";
                    if (operandFollows || (keyword && unaryFollows))
                    {
                        return new CastExpr(name.ToString(), ParseUnary());
                    }
                }
            }

            _position = start;
            return null;
        }

        private Expr ParsePrimary()
        {
            var token = Current;
            switch (token.Kind)
            {
                case Kind.Number or Kind.String or Kind.Char:
                    _position++;
                    return new LiteralExpr(token.Value);
                case Kind.Identifier:
                    _position++;
                    return token.Text switch
                    {
                        "true" => new LiteralExpr(true),
                        "false" => new LiteralExpr(false),
                        "null" => new LiteralExpr(null),
                        "new" or "typeof" or "sizeof" or "default" or "await" or "is" or "as" => throw new ExpressionException($"'{token.Text}' isn't supported in expressions here."),
                        _ => new NameExpr(token.Text),
                    };
                case Kind.Operator when token.Text == "(":
                    _position++;
                    var inner = ParseExpression();
                    Expect(")");
                    return inner;
                case Kind.End:
                    throw new ExpressionException("The expression ends too early.");
                default:
                    throw new ExpressionException($"Unexpected '{token.Text}' in the expression.");
            }
        }

        private Expr ParsePostfix(Expr target)
        {
            while (true)
            {
                if (Is(".") || Is("?."))
                {
                    var conditional = Current.Text == "?.";
                    _position++;
                    if (Current.Kind != Kind.Identifier)
                    {
                        throw new ExpressionException("A member name was expected after '.'.");
                    }

                    target = new MemberExpr(target, Current.Text, conditional);
                    _position++;
                }
                else if (Accept("("))
                {
                    if (target is not (NameExpr or MemberExpr))
                    {
                        throw new ExpressionException("Only methods can be called.");
                    }

                    target = new CallExpr(target, Arguments(")"));
                }
                else if (Accept("["))
                {
                    target = new IndexExpr(target, Arguments("]"));
                }
                else
                {
                    return target;
                }
            }
        }

        private List<Expr> Arguments(string close)
        {
            var arguments = new List<Expr>();
            if (Accept(close))
            {
                return arguments;
            }

            do
            {
                arguments.Add(ParseExpression());
            }
            while (Accept(","));

            Expect(close);
            return arguments;
        }
    }
}
