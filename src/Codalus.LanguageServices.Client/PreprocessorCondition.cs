namespace Codalus.LanguageServices.Client;

/// <summary>
/// A <c>#if</c> condition (<c>!</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>==</c>, <c>!=</c>, parentheses, <c>true</c>,
/// <c>false</c>) over the symbols a scanner takes as defined (C#'s and F#'s estimates).
/// </summary>
internal ref struct PreprocessorCondition(ReadOnlySpan<char> text, PreprocessorCondition.Defined isDefined)
{
    private readonly ReadOnlySpan<char> _text = text;
    private readonly Defined _isDefined = isDefined;
    private int _position;

    /// <summary>Whether a symbol is defined.</summary>
    public delegate bool Defined(ReadOnlySpan<char> symbol);

    /// <summary>The symbols a project on current .NET in Debug defines (<c>DEBUG</c>, <c>TRACE</c>, <c>NET</c>, <c>NETCOREAPP</c>, the <c>_OR_GREATER</c> ones).</summary>
    public static bool DotNetDebug(ReadOnlySpan<char> symbol) =>
        symbol is "true" or "DEBUG" or "TRACE" or "NET" or "NETCOREAPP"
        || symbol.EndsWith("_OR_GREATER") && (symbol.StartsWith("NETCOREAPP") || symbol.StartsWith("NET") && symbol.Length > 3 && char.IsAsciiDigit(symbol[3]));

    public static bool Evaluate(ReadOnlySpan<char> condition, Defined isDefined)
    {
        var comment = condition.IndexOf("//");
        var parser = new PreprocessorCondition(comment < 0 ? condition : condition[..comment], isDefined);
        return parser.Or();
    }

    private bool Or()
    {
        var value = And();
        while (Accept("||"))
        {
            value |= And();
        }

        return value;
    }

    private bool And()
    {
        var value = Equality();
        while (Accept("&&"))
        {
            value &= Equality();
        }

        return value;
    }

    private bool Equality()
    {
        var value = Unary();
        while (true)
        {
            if (Accept("=="))
            {
                value = value == Unary();
            }
            else if (Accept("!="))
            {
                value = value != Unary();
            }
            else
            {
                return value;
            }
        }
    }

    private bool Unary()
    {
        if (Accept("!"))
        {
            return !Unary();
        }

        if (Accept("("))
        {
            var value = Or();
            Accept(")");
            return value;
        }

        SkipSpaces();
        var start = _position;
        while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_'))
        {
            _position++;
        }

        if (_position == start)
        {
            _position = _text.Length; // something else: the rest can't be read
            return false;
        }

        return _isDefined(_text[start.._position]);
    }

    private bool Accept(string symbol)
    {
        SkipSpaces();
        if (_text[_position..].StartsWith(symbol) && !(symbol == "!" && _text[_position..].StartsWith("!=")))
        {
            _position += symbol.Length;
            return true;
        }

        return false;
    }

    private void SkipSpaces()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
        {
            _position++;
        }
    }
}
