namespace Codalus.LanguageServices.Client;

/// <summary>
/// What FsAutoComplete will list for an F# file, from the text alone, so the editor keeps their room from the first
/// frame (as <see cref="CSharpLensScanner"/> does for C#):
/// <list type="bullet">
/// <item>Its CodeLens (signature and references, on the same lines): every <c>let</c> of a module or a class (not
/// those in a body), every <c>member</c>, <c>override</c>, <c>default</c>, <c>val</c> and <c>new</c> of a type, and the
/// <c>and</c> of a <c>let rec</c> group, whose head is a name, an operator or an active pattern (not a tuple, <c>_</c>,
/// <c>()</c> or another pattern). <c>abstract</c> and <c>do</c> have none, and neither do types.</item>
/// <item>Whether it declares a type: a <c>type</c> or an <c>exception</c>, which FsAutoComplete lists as classes.</item>
/// </list>
/// A light lexer (comments, every kind of string, characters and type parameters, <c>#if</c> branches) and the
/// offside rule by columns: a declaration indented under a <c>let</c> or <c>member</c> (or an expression) is local.
/// <c>#if</c> branches are taken as a Debug build of a project takes them (<c>DEBUG</c>, <c>TRACE</c>); a script's
/// (<c>.fsx</c>), with none of those defined, as FsAutoComplete checks it.
/// </summary>
public static class FSharpDeclarationScanner
{
    /// <summary>The offsets of the names that get a CodeLens, in order.</summary>
    public static IReadOnlyList<int> Lenses(string text, bool script)
    {
        var tokens = new Lexer(text, script).All();
        var lenses = new List<int>();
        var stack = new List<(int Column, bool Body)>();
        var lineColumn = 0;
        var afterLet = false; // the last declaration was a let: an "and" continues its group
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.First)
            {
                while (stack.Count > 0 && stack[^1].Column >= token.Column)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                lineColumn = token.Column;
            }

            var local = stack.Exists(s => s.Body);
            var word = token.Kind == Kind.Word ? text.AsSpan(token.Start, token.Length) : default;
            switch (word)
            {
                case "let" or "member" or "override" or "default" or "val":
                    if (!local)
                    {
                        if (Head(text, tokens, i + 1) is { } head)
                        {
                            lenses.Add(head);
                        }

                        afterLet = word is "let";
                        stack.Add((lineColumn, true));
                    }

                    continue;
                case "and" when !local:
                    if (afterLet && Head(text, tokens, i + 1) is { } next)
                    {
                        lenses.Add(next);
                    }

                    stack.Add((lineColumn, afterLet));
                    continue;
                case "new" when !local && token.First && i + 1 < tokens.Count && IsPunct(text, tokens[i + 1], '('):
                    lenses.Add(token.Start); // a constructor
                    stack.Add((lineColumn, true));
                    continue;
                case "abstract" or "do" when !local:
                    stack.Add((lineColumn, true));
                    continue;
                case "type" or "exception" or "module" or "namespace":
                    afterLet = false;
                    break;
            }

            if (token.First)
            {
                // A line that starts a declaration's part (an attribute, a union case, a record's fields, interface
                // implementations) or anything else, an expression, whose own lets are local.
                stack.Add((token.Column, !IsDeclarationLine(text, tokens, i)));
            }
        }

        return lenses;
    }

    /// <summary>Whether the text declares a type (or an exception); it stops at the first.</summary>
    public static bool DeclaresType(string text, bool script)
    {
        var lexer = new Lexer(text, script);
        while (lexer.Next() is { Kind: not Kind.End } token)
        {
            if (token.Kind == Kind.Word && text.AsSpan(token.Start, token.Length) is "type" or "exception")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDeclarationLine(string text, List<Token> tokens, int i)
    {
        var token = tokens[i];
        if (token.Kind == Kind.Word)
        {
            return text.AsSpan(token.Start, token.Length) is "type" or "module" or "namespace" or "open" or "exception" or "interface"
                or "inherit" or "static" or "private" or "internal" or "public" or "inline" or "with" or "end" or "class" or "struct"
                or "extern" or "let" or "member" or "override" or "default" or "val" or "new" or "abstract" or "do" or "and";
        }

        return IsPunct(text, token, '|') && (token.Start + 1 >= text.Length || text[token.Start + 1] is not ('>' or '|' or ']' or '}')) // a union case, not |> or ||
            || IsPunct(text, token, '{')
            || IsPunct(text, token, '[') && i + 1 < tokens.Count && IsPunct(text, tokens[i + 1], '<'); // [<Attribute>]
    }

    /// <summary>The offset of a binding's name after its keyword, or null when its head isn't one (a pattern, <c>_</c>, <c>()</c>).</summary>
    private static int? Head(string text, List<Token> tokens, int i)
    {
        while (i < tokens.Count)
        {
            var token = tokens[i];
            if (token.Kind == Kind.Word && text.AsSpan(token.Start, token.Length) is "rec" or "inline" or "private" or "internal" or "public" or "mutable" or "static" or "val")
            {
                i++;
            }
            else if (IsPunct(text, token, '[') && i + 1 < tokens.Count && IsPunct(text, tokens[i + 1], '<'))
            {
                // An attribute: [<Literal>]
                i += 2;
                while (i < tokens.Count && !(IsPunct(text, tokens[i], '>') && i + 1 < tokens.Count && IsPunct(text, tokens[i + 1], ']')))
                {
                    i++;
                }

                i += 2;
            }
            else
            {
                break;
            }
        }

        if (i >= tokens.Count)
        {
            return null;
        }

        var head = tokens[i];
        var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
        if (head.Kind == Kind.Quoted)
        {
            return head.Start;
        }

        if (head.Kind == Kind.Word)
        {
            if (IsPunct(text, next, '.'))
            {
                // A member's self identifier: this.Name, _.Name
                return i + 2 < tokens.Count && tokens[i + 2].Kind is Kind.Word or Kind.Quoted ? tokens[i + 2].Start : null;
            }

            return text.AsSpan(head.Start, head.Length) is "_" || IsPunct(text, next, ',') ? null : head.Start;
        }

        if (IsPunct(text, head, '(') && next.Kind == Kind.Punct && !IsPunct(text, next, ')') && !IsPunct(text, next, '('))
        {
            return next.Start; // an active pattern (|Even|Odd|) or an operator (+.)
        }

        return null;
    }

    private static bool IsPunct(string text, Token token, char c) => token.Kind == Kind.Punct && text[token.Start] == c;

    private enum Kind
    {
        End,
        Word,
        Quoted,
        Punct,
        Literal,
    }

    /// <summary>A token, its column, and whether it's the first of its line.</summary>
    private readonly record struct Token(Kind Kind, int Start, int Length, int Column, bool First);

    private sealed class Lexer(string text, bool script)
    {
        private readonly Stack<bool> _conditions = new(); // whether each open #if's taken branch is being read
        private int _position;
        private int _lineStart;
        private bool _first = true;

        public List<Token> All()
        {
            var tokens = new List<Token>();
            while (Next() is { Kind: not Kind.End } token)
            {
                tokens.Add(token);
            }

            return tokens;
        }

        public Token Next()
        {
            while (_position < text.Length)
            {
                var c = text[_position];
                if (c == '\n')
                {
                    _position++;
                    _lineStart = _position;
                    _first = true;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    _position++;
                    continue;
                }

                if (c == '#' && _first)
                {
                    Directive();
                    continue;
                }

                if (c == '/' && Peek(1) == '/')
                {
                    SkipLine();
                    continue;
                }

                if (c == '(' && Peek(1) == '*' && Peek(2) != ')')
                {
                    SkipComment();
                    continue;
                }

                var start = _position;
                var kind = Read(c);
                var token = new Token(kind, start, _position - start, start - _lineStart, _first);
                _first = false;
                return token;
            }

            return default;
        }

        private char Peek(int ahead) => _position + ahead < text.Length ? text[_position + ahead] : '\0';

        private Kind Read(char c)
        {
            if (c == '`' && Peek(1) == '`')
            {
                var close = text.IndexOf("``", _position + 2, StringComparison.Ordinal);
                _position = close < 0 ? text.Length : close + 2;
                return Kind.Quoted;
            }

            if (c is '"' or '@' or '$' && StringStart() is { } quote)
            {
                var verbatim = text.AsSpan(_position, quote - _position).Contains('@');
                _position = quote;
                SkipString(verbatim);
                return Kind.Literal;
            }

            if (c == '\'')
            {
                // A character ('a', '\n', 'A') or a type parameter's quote ('T).
                if (Peek(1) == '\\')
                {
                    var close = text.IndexOf('\'', _position + 3);
                    _position = close < 0 || close - _position > 12 ? _position + 1 : close + 1;
                    return close < 0 ? Kind.Punct : Kind.Literal;
                }

                if (Peek(2) == '\'' && Peek(1) != '\'')
                {
                    _position += 3;
                    return Kind.Literal;
                }

                _position++;
                return Kind.Punct;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] is '_' or '\''))
                {
                    _position++;
                }

                return Kind.Word;
            }

            if (char.IsAsciiDigit(c))
            {
                while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_' || text[_position] == '.' && char.IsAsciiDigit(Peek(1))))
                {
                    _position++;
                }

                return Kind.Literal;
            }

            _position += c == '(' && Peek(1) == '*' && Peek(2) == ')' ? 3 : 1; // (*) is the operator
            return Kind.Punct;
        }

        /// <summary>Where a string's opening quote is, after its <c>$</c> and <c>@</c> prefixes, or null.</summary>
        private int? StringStart()
        {
            var i = _position;
            while (i < text.Length && text[i] is '$' or '@')
            {
                i++;
            }

            return i < text.Length && text[i] == '"' ? i : null;
        }

        /// <summary>From an opening quote at the position: past its closing one (and a byte string's <c>B</c>).</summary>
        private void SkipString(bool verbatim)
        {
            if (string.CompareOrdinal(text, _position, "\"\"\"", 0, 3) == 0)
            {
                var close = text.IndexOf("\"\"\"", _position + 3, StringComparison.Ordinal);
                _position = close < 0 ? text.Length : close + 3;
            }
            else
            {
                _position++;
                while (_position < text.Length)
                {
                    var c = text[_position];
                    if (c == '\\' && !verbatim)
                    {
                        _position += 2;
                        continue;
                    }

                    if (c == '"')
                    {
                        if (verbatim && Peek(1) == '"')
                        {
                            _position += 2;
                            continue;
                        }

                        _position++;
                        break;
                    }

                    _position++;
                }
            }

            if (_position < text.Length && text[_position] == 'B')
            {
                _position++;
            }

            _position = Math.Min(_position, text.Length);
        }

        private void SkipLine()
        {
            var end = text.IndexOf('\n', _position);
            _position = end < 0 ? text.Length : end;
        }

        /// <summary>A block comment, nested ones and strings in it included.</summary>
        private void SkipComment()
        {
            var depth = 0;
            while (_position < text.Length)
            {
                if (text[_position] == '(' && Peek(1) == '*' && Peek(2) != ')')
                {
                    depth++;
                    _position += 2;
                }
                else if (text[_position] == '*' && Peek(1) == ')')
                {
                    _position += 2;
                    if (--depth == 0)
                    {
                        return;
                    }
                }
                else if (text[_position] == '"')
                {
                    SkipString(verbatim: false);
                }
                else
                {
                    _position++;
                }
            }
        }

        /// <summary>A line starting with <c>#</c>: <c>#if</c>, <c>#else</c> and <c>#endif</c> choose the branches read; others (<c>#r</c>, <c>#load</c>, <c>#nowarn</c>) are skipped.</summary>
        private void Directive()
        {
            var end = text.IndexOf('\n', _position);
            end = end < 0 ? text.Length : end;
            var line = text.AsSpan(_position + 1, end - _position - 1).Trim();
            _position = end;
            if (line.StartsWith("if") && (line.Length == 2 || !char.IsLetterOrDigit(line[2])))
            {
                var taken = PreprocessorCondition.Evaluate(line[2..], script ? IsDefinedInScript : IsDefinedInProject);
                _conditions.Push(taken);
                if (!taken)
                {
                    SkipBranch();
                }
            }
            else if (line.StartsWith("else") && _conditions.Count > 0)
            {
                if (_conditions.Peek())
                {
                    SkipBranch(); // the #if's branch was read: the #else's isn't
                }
            }
            else if (line.StartsWith("endif") && _conditions.Count > 0)
            {
                _conditions.Pop();
            }
        }

        /// <summary>Lines up to this branch's <c>#else</c> (which is read) or <c>#endif</c>, nested ones skipped whole.</summary>
        private void SkipBranch()
        {
            var nested = 0;
            while (_position < text.Length)
            {
                var lineStart = _position + 1;
                var lineEnd = text.IndexOf('\n', lineStart);
                lineEnd = lineEnd < 0 ? text.Length : lineEnd;
                var line = text.AsSpan(lineStart, lineEnd - lineStart).Trim();
                _position = lineEnd;
                if (line.StartsWith("#if"))
                {
                    nested++;
                }
                else if (line.StartsWith("#endif"))
                {
                    if (nested-- == 0)
                    {
                        _conditions.Pop();
                        return;
                    }
                }
                else if (line.StartsWith("#else") && nested == 0)
                {
                    if (!_conditions.Peek())
                    {
                        _conditions.Pop();
                        _conditions.Push(true);
                        return;
                    }
                }
            }
        }

        private static bool IsDefinedInProject(ReadOnlySpan<char> symbol) => symbol is "DEBUG" or "TRACE" || PreprocessorCondition.DotNetDebug(symbol);

        private static bool IsDefinedInScript(ReadOnlySpan<char> symbol) => symbol is "INTERACTIVE" or "EDITING";
    }
}
