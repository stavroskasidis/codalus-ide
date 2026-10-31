namespace Codalus.LanguageServices.Client;

/// <summary>
/// Whether a JavaScript or TypeScript file declares a class, an interface or an enum where the TypeScript server's
/// document symbols list them as such: at the top level, or in a <c>namespace</c>, a <c>module</c> or a
/// <c>declare global</c> block (a class in a function or an expression, <c>const A = class {}</c>, is no type there).
/// In JavaScript, a top-level function whose <c>prototype</c> the top level assigns (<c>F.prototype = {…}</c>,
/// <c>F.prototype.m = …</c>) is a class too. The editor keeps the navigation bar's room from the first frame when they will come. A light lexer: comments,
/// strings, template literals with their holes, and regular expressions told from division by the token before them.
/// </summary>
public static class JavaScriptTypeScanner
{
    /// <summary>Whether the text declares a type; it stops at the first. <paramref name="javaScript"/>: JavaScript, not TypeScript (classes from prototypes).</summary>
    public static bool DeclaresType(string text, bool javaScript = false)
    {
        var lexer = new Lexer(text);
        var blocks = new List<bool>(); // each open bracket: whether it's a namespace's body
        var namespacePending = false;
        HashSet<string>? functions = null, prototypes = null; // JavaScript's top-level functions, and those whose prototype the top level assigns
        Token previous = default, beforePrevious = default, beforeThat = default;
        var token = lexer.Next();
        var next = lexer.Next();
        var afterNext = lexer.Next();
        while (token.Kind != Kind.End)
        {
            var word = token.Kind == Kind.Word ? text.AsSpan(token.Start, token.Length) : default;
            if (token.Kind == Kind.Punct)
            {
                switch (text[token.Start])
                {
                    case '{':
                        blocks.Add(namespacePending);
                        namespacePending = false;
                        break;
                    case '(' or '[':
                        blocks.Add(false);
                        break;
                    case '}' or ')' or ']' when blocks.Count > 0:
                        blocks.RemoveAt(blocks.Count - 1);
                        break;
                    case ';':
                        namespacePending = false;
                        break;
                }
            }
            else if (word is "namespace" or "module" && next.Kind is Kind.Word or Kind.Literal
                && (StartsStatement(text, previous, token) || IsWord(text, previous, "export") || IsWord(text, previous, "declare")))
            {
                namespacePending = true; // namespace A.B {, declare module "m" {
            }
            else if (word is "global" && IsWord(text, previous, "declare") && IsPunct(text, next, '{'))
            {
                namespacePending = true;
            }
            else if (blocks.TrueForAll(b => b) && word is "class" or "interface" or "enum")
            {
                var declaration = StartsStatement(text, previous, token)
                    || IsWord(text, previous, "export") || IsWord(text, previous, "declare")
                    || word is "class" && (IsWord(text, previous, "default") || IsWord(text, previous, "abstract")
                        || IsPunct(text, previous, ')') || previous.Kind == Kind.Word && (IsPunct(text, beforePrevious, '@') || IsPunct(text, beforeThat, '@'))) // decorators
                    || word is "enum" && IsWord(text, previous, "const");
                var named = next.Kind == Kind.Word || word is "class" && IsPunct(text, next, '{'); // export default class {
                if (declaration && named)
                {
                    return true;
                }
            }
            else if (javaScript && blocks.Count == 0 && token.Kind == Kind.Word && !IsPunct(text, previous, '.'))
            {
                if (word is "function" && next.Kind == Kind.Word)
                {
                    (functions ??= []).Add(text.Substring(next.Start, next.Length)); // function F(
                }
                else if (word is "var" or "let" or "const" && next.Kind == Kind.Word && IsPunct(text, afterNext, '='))
                {
                    var name = text.Substring(next.Start, next.Length);
                    var value = lexer.PeekToken();
                    if (value.Kind == Kind.Word && text.AsSpan(value.Start, value.Length) is "function")
                    {
                        (functions ??= []).Add(name); // var F = function (
                    }
                }
                else if (IsPunct(text, next, '.') && IsWord(text, afterNext, "prototype") && lexer.PeekToken() is var after
                    && (IsPunct(text, after, '=') && IsPunct(text, lexer.PeekToken(2), '{') || IsPunct(text, after, '.')))
                {
                    (prototypes ??= []).Add(text.Substring(token.Start, token.Length)); // F.prototype = {…}, F.prototype.m = …
                }
            }

            beforeThat = beforePrevious;
            beforePrevious = previous;
            previous = token;
            token = next;
            next = afterNext;
            afterNext = lexer.Next();
        }

        return functions is not null && prototypes is not null && functions.Overlaps(prototypes);
    }

    /// <summary>Whether a token starts a statement: the first of the text, after <c>;</c> or a brace, or first on its line after a token that can end one.</summary>
    private static bool StartsStatement(string text, Token previous, Token token) =>
        previous.Kind == Kind.End
        || previous.Kind == Kind.Punct && text[previous.Start] is ';' or '{' or '}'
        || token.First && (previous.Kind is Kind.Word or Kind.Literal || previous.Kind == Kind.Punct && text[previous.Start] is ')' or ']');

    private static bool IsWord(string text, Token token, string word) =>
        token.Kind == Kind.Word && text.AsSpan(token.Start, token.Length).SequenceEqual(word);

    private static bool IsPunct(string text, Token token, char c) => token.Kind == Kind.Punct && text[token.Start] == c;

    private enum Kind
    {
        End,
        Word,
        Punct,
        Literal,
    }

    /// <summary>A token, and whether it's the first of its line.</summary>
    private readonly record struct Token(Kind Kind, int Start, int Length, bool First);

    private sealed class Lexer(string text)
    {
        private static readonly HashSet<string> s_beforeExpression = ["return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw", "case", "do", "else", "yield", "await"];

        private readonly Stack<int> _holes = new(); // the brace depth at which each open template hole closes
        private int _position;
        private int _depth;
        private bool _first = true;
        private Token _previous;

        /// <summary>The token <paramref name="ahead"/> from here (1: the next), without taking any.</summary>
        public Token PeekToken(int ahead = 1)
        {
            var (position, depth, first, previous) = (_position, _depth, _first, _previous);
            var holes = _holes.Count > 0 ? _holes.ToArray() : null;
            var token = Next();
            for (var i = 1; i < ahead; i++)
            {
                token = Next();
            }

            (_position, _depth, _first, _previous) = (position, depth, first, previous);
            if (holes is not null || _holes.Count > 0)
            {
                _holes.Clear();
                foreach (var hole in (holes ?? []).Reverse())
                {
                    _holes.Push(hole);
                }
            }

            return token;
        }

        public Token Next()
        {
            while (_position < text.Length)
            {
                var c = text[_position];
                if (c == '\n')
                {
                    _position++;
                    _first = true;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    _position++;
                    continue;
                }

                if (c == '/' && Peek(1) == '/')
                {
                    var end = text.IndexOf('\n', _position);
                    _position = end < 0 ? text.Length : end;
                    continue;
                }

                if (c == '/' && Peek(1) == '*')
                {
                    var end = text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                    _first |= end >= 0 && text.AsSpan(_position, end - _position).Contains('\n');
                    _position = end < 0 ? text.Length : end + 2;
                    continue;
                }

                if (c == '}' && _holes.Count > 0 && _holes.Peek() == _depth)
                {
                    // A template literal's hole ends: the template goes on.
                    _holes.Pop();
                    _position++;
                    Template();
                    continue;
                }

                var start = _position;
                var kind = Read(c);
                _previous = new Token(kind, start, _position - start, _first);
                _first = false;
                return _previous;
            }

            return default;
        }

        private char Peek(int ahead) => _position + ahead < text.Length ? text[_position + ahead] : '\0';

        private Kind Read(char c)
        {
            switch (c)
            {
                case '\'' or '"':
                    _position++;
                    while (_position < text.Length && text[_position] != c && text[_position] != '\n')
                    {
                        _position += text[_position] == '\\' ? 2 : 1;
                    }

                    _position = Math.Min(_position + 1, text.Length);
                    return Kind.Literal;
                case '`':
                    _position++;
                    Template();
                    return Kind.Literal;
                case '/' when RegexAllowed():
                    Regex();
                    return Kind.Literal;
                case '{':
                    _depth++;
                    _position++;
                    return Kind.Punct;
                case '}':
                    _depth--;
                    _position++;
                    return Kind.Punct;
            }

            if (char.IsLetter(c) || c is '_' or '$')
            {
                while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] is '_' or '$'))
                {
                    _position++;
                }

                return Kind.Word;
            }

            if (char.IsAsciiDigit(c))
            {
                while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] is '_' or '.'))
                {
                    _position++;
                }

                return Kind.Literal;
            }

            _position++;
            return Kind.Punct;
        }

        /// <summary>A template literal's text from the position: to its closing backtick, or into a hole (<c>${</c>), whose tokens follow.</summary>
        private void Template()
        {
            while (_position < text.Length)
            {
                var c = text[_position];
                if (c == '\\')
                {
                    _position += 2;
                    continue;
                }

                if (c == '`')
                {
                    _position++;
                    return;
                }

                if (c == '$' && Peek(1) == '{')
                {
                    _position += 2;
                    _holes.Push(_depth);
                    return;
                }

                _position++;
            }
        }

        /// <summary>Whether a <c>/</c> here starts a regular expression: not after a value (a name, a literal, a closing bracket).</summary>
        private bool RegexAllowed() =>
            _previous.Kind switch
            {
                Kind.End => true,
                Kind.Literal => false,
                Kind.Word => s_beforeExpression.Contains(text.Substring(_previous.Start, _previous.Length)),
                _ => text[_previous.Start] is not (')' or ']' or '}'),
            };

        private void Regex()
        {
            _position++;
            var inClass = false;
            while (_position < text.Length && text[_position] != '\n')
            {
                var c = text[_position];
                if (c == '\\')
                {
                    _position += 2;
                    continue;
                }

                _position++;
                if (c == '[')
                {
                    inClass = true;
                }
                else if (c == ']')
                {
                    inClass = false;
                }
                else if (c == '/' && !inClass)
                {
                    break;
                }
            }

            while (_position < text.Length && char.IsLetter(text[_position]))
            {
                _position++; // flags
            }

            _position = Math.Min(_position, text.Length);
        }
    }
}
