namespace Codalus.LanguageServices.Client;

/// <summary>
/// Where the C# host's CodeLens will be, found from the text alone: the start of the identifier of every type,
/// delegate, method, constructor, property, indexer (its <c>this</c>) and event with accessors declared in a type, a
/// namespace or the file, as <c>textDocument/codeLens</c> lists them from the syntax tree (fields, field-like events,
/// operators, destructors, enum members and top-level statements' local functions have none). The editor keeps the
/// rows' room from the first frame, as the classic IDE does from its in-process syntax tree, instead of moving the code
/// down when the host answers. A light lexer (comments, every kind of string, preprocessor directives) and a walk of
/// declarations that skips member bodies. <c>#if</c> branches are taken as a project on current .NET in Debug would
/// take them (<c>DEBUG</c>, <c>TRACE</c>, <c>NET</c>, <c>NETCOREAPP</c> and the <c>_OR_GREATER</c> symbols of .NET
/// defined); the host's list replaces the estimate when it arrives.
/// </summary>
public static class CSharpLensScanner
{
    /// <summary>The offsets of the identifiers that get a CodeLens, in order.</summary>
    public static IReadOnlyList<int> Scan(string text)
    {
        var parser = new Parser(text);
        parser.Container(Level.File);
        return parser.Lenses;
    }

    /// <summary>
    /// Whether the text declares a class, struct, interface, enum, record or union, as <see cref="Scan"/> finds them: the
    /// editor keeps the navigation bar's room (its types and members) from the first frame when the host's document
    /// symbols will list a type. It stops at the first one.
    /// </summary>
    public static bool DeclaresType(string text)
    {
        var parser = new Parser(text) { StopAtType = true };
        parser.Container(Level.File);
        return parser.FoundType;
    }

    private enum Level
    {
        File,
        Namespace,
        Type,
    }

    private enum Kind
    {
        End,
        Word,
        Punct,
        Literal,
    }

    private readonly record struct Token(Kind Kind, int Start, int Length);

    private sealed class Parser(string text)
    {
        private readonly Lexer _lexer = new(text);

        public List<int> Lenses { get; } = [];

        /// <summary>Whether the walk ends at the first type declaration (<see cref="DeclaresType"/>).</summary>
        public bool StopAtType { get; init; }

        public bool FoundType { get; private set; }

        private Token Peek(int ahead = 0) => _lexer.Peek(ahead);

        private Token Take() => _lexer.Take();

        private bool IsWord(Token token, string word) =>
            token.Kind == Kind.Word && token.Length == word.Length && string.CompareOrdinal(text, token.Start, word, 0, word.Length) == 0;

        private bool IsPunct(Token token, char c) => token.Kind == Kind.Punct && token.Length == 1 && text[token.Start] == c;

        private bool IsArrow(Token token) => token.Kind == Kind.Punct && token.Length == 2 && text[token.Start] == '=';

        private bool IsModifier(Token token) =>
            token.Kind == Kind.Word && text.AsSpan(token.Start, token.Length) is
                "public" or "private" or "protected" or "internal" or "static" or "readonly" or "sealed" or "abstract"
                or "virtual" or "override" or "extern" or "unsafe" or "new" or "partial" or "async" or "const" or "volatile"
                or "fixed" or "required" or "file" or "ref";

        /// <summary>Declarations until the container's closing brace (taken) or the end of the text.</summary>
        public void Container(Level level)
        {
            while (!FoundType && Peek() is { Kind: not Kind.End } token)
            {
                if (IsPunct(token, '}'))
                {
                    Take();
                    if (level != Level.File)
                    {
                        return;
                    }

                    continue;
                }

                Declaration(level);
            }
        }

        private void Declaration(Level level)
        {
            var token = Peek();
            if (IsPunct(token, ';'))
            {
                Take();
                return;
            }

            if (IsPunct(token, '[') || IsPunct(token, '{'))
            {
                // An attribute list, or a top-level statement's block.
                SkipBalanced();
                return;
            }

            if (IsWord(token, "namespace"))
            {
                Namespace();
                return;
            }

            if (level != Level.Type && (IsWord(token, "using") && !IsPunct(Peek(1), '(') || IsWord(token, "global") && IsWord(Peek(1), "using")
                || IsWord(token, "extern") && IsWord(Peek(1), "alias")))
            {
                SkipToSemicolon();
                return;
            }

            while (IsModifier(Peek()))
            {
                Take();
            }

            token = Peek();
            if (IsWord(token, "class") || IsWord(token, "struct") || IsWord(token, "interface") || IsWord(token, "enum")
                || (IsWord(token, "record") || IsWord(token, "union")) && Peek(1).Kind == Kind.Word)
            {
                TypeDeclaration();
            }
            else if (IsWord(token, "delegate") && !IsPunct(Peek(1), '*'))
            {
                Take();
                SkipType();
                AddLens(Peek());
                SkipMember();
            }
            else if (level == Level.File)
            {
                // Top-level statements, their local functions included: no CodeLens.
                Statement();
            }
            else
            {
                Member();
            }
        }

        private void Namespace()
        {
            Take();
            while (Peek() is { Kind: Kind.Word or Kind.Punct } part && !IsPunct(part, ';') && !IsPunct(part, '{') && !IsPunct(part, '}'))
            {
                Take();
            }

            if (IsPunct(Peek(), '{'))
            {
                Take();
                Container(Level.Namespace);
            }
            else if (IsPunct(Peek(), ';'))
            {
                Take(); // file-scoped: what follows is its content
            }
        }

        /// <summary>A class, struct, interface, enum, record or union (C# 15): its name, its header up to the body, then its members.</summary>
        private void TypeDeclaration()
        {
            if (StopAtType)
            {
                FoundType = true;
                return;
            }

            var isEnum = IsWord(Take(), "enum");
            if (IsWord(Peek(), "struct") || IsWord(Peek(), "class"))
            {
                Take(); // record struct, record class
            }

            AddLens(Peek());
            while (Peek() is { Kind: not Kind.End } token && !IsPunct(token, '}'))
            {
                if (IsPunct(token, ';'))
                {
                    Take();
                    return;
                }

                if (IsPunct(token, '{'))
                {
                    if (isEnum)
                    {
                        SkipBalanced();
                    }
                    else
                    {
                        Take();
                        Container(Level.Type);
                    }

                    return;
                }

                if (IsPunct(token, '(') || IsPunct(token, '['))
                {
                    SkipBalanced(); // a primary constructor's parameters, a base's arguments
                }
                else
                {
                    Take();
                }
            }
        }

        /// <summary>A member of a type (or, in code that doesn't compile, of a namespace), after its modifiers.</summary>
        private void Member()
        {
            var token = Peek();
            if (IsWord(token, "event"))
            {
                Take();
                SkipType();
                var eventName = MemberName();
                if (eventName is { } name && IsPunct(Peek(), '{'))
                {
                    AddLens(name); // accessors: a field-like event has none
                }

                SkipMember();
                return;
            }

            if (IsWord(token, "extension") && (IsPunct(Peek(1), '(') || IsPunct(Peek(1), '<')))
            {
                // C# 14's extension block: its members are the type's.
                Take();
                while (Peek() is { Kind: not Kind.End } part && !IsPunct(part, '{') && !IsPunct(part, ';') && !IsPunct(part, '}'))
                {
                    if (IsPunct(part, '(') || IsPunct(part, '['))
                    {
                        SkipBalanced();
                    }
                    else
                    {
                        Take();
                    }
                }

                if (IsPunct(Peek(), '{'))
                {
                    Take();
                    Container(Level.Type);
                }

                return;
            }

            if (IsWord(token, "implicit") || IsWord(token, "explicit") || IsPunct(token, '~'))
            {
                SkipMember(); // a conversion operator, a destructor
                return;
            }

            if (token.Kind == Kind.Word && IsPunct(Peek(1), '('))
            {
                AddLens(token); // a constructor
                Take();
                SkipMember();
                return;
            }

            if (!SkipType())
            {
                SkipMember();
                return;
            }

            if (IsWord(Peek(), "operator"))
            {
                SkipMember();
                return;
            }

            if (MemberName() is { } member && (IsPunct(Peek(), '(') || IsPunct(Peek(), '{') || IsArrow(Peek()) || IsWord(member, "this")))
            {
                AddLens(member); // a method, a property, an indexer
            }

            SkipMember();
        }

        /// <summary>
        /// A member's name after its type, through an explicit interface (<c>IList&lt;T&gt;.Count</c>) and a method's
        /// type parameters: its last identifier, or <c>this</c> for an indexer. Null for an operator or no name.
        /// </summary>
        private Token? MemberName()
        {
            if (Peek().Kind != Kind.Word || IsWord(Peek(), "operator"))
            {
                return null;
            }

            var name = Take();
            while (true)
            {
                if (IsPunct(Peek(), '<'))
                {
                    SkipAngles();
                }
                else if (IsPunct(Peek(), '.') && Peek(1).Kind == Kind.Word && !IsWord(Peek(1), "operator"))
                {
                    Take();
                    name = Take();
                }
                else
                {
                    return IsPunct(Peek(), '.') ? null : name;
                }
            }
        }

        /// <summary>A type: a name (qualified, generic, an alias's), a tuple, a function pointer; then <c>?</c>, <c>*</c> and array ranks.</summary>
        private bool SkipType()
        {
            var token = Peek();
            if (IsPunct(token, '('))
            {
                SkipBalanced();
            }
            else if (IsWord(token, "delegate") && IsPunct(Peek(1), '*'))
            {
                Take();
                Take();
                if (Peek().Kind == Kind.Word)
                {
                    Take(); // managed, unmanaged
                }

                if (IsPunct(Peek(), '['))
                {
                    SkipBalanced();
                }

                if (IsPunct(Peek(), '<'))
                {
                    SkipAngles();
                }
            }
            else if (token.Kind == Kind.Word && !IsWord(token, "this") && !IsWord(token, "operator"))
            {
                Take();
                while (true)
                {
                    if (IsPunct(Peek(), '<'))
                    {
                        SkipAngles();
                    }
                    else if ((IsPunct(Peek(), '.') || IsDoubleColon(Peek())) && Peek(1).Kind == Kind.Word && !IsWord(Peek(1), "this") && !IsWord(Peek(1), "operator")
                        && (IsPunct(Peek(2), '.') || IsPunct(Peek(2), '<') || IsDoubleColon(Peek(2)) || IsPunct(Peek(2), '?') || IsPunct(Peek(2), '[') || IsPunct(Peek(2), '*') || Peek(2).Kind == Kind.Word))
                    {
                        // A qualified name, when a name still follows it (else the part after the dot is the member's).
                        Take();
                        Take();
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else
            {
                return false;
            }

            while (IsPunct(Peek(), '?') || IsPunct(Peek(), '*') || IsPunct(Peek(), '[') && (IsPunct(Peek(1), ']') || IsPunct(Peek(1), ',')))
            {
                if (IsPunct(Peek(), '['))
                {
                    SkipBalanced();
                }
                else
                {
                    Take();
                }
            }

            return true;
        }

        private bool IsDoubleColon(Token token) => token.Kind == Kind.Punct && token.Length == 2 && text[token.Start] == ':';

        /// <summary>The rest of a member: to its <c>;</c>, or through its body (and a property's initializer after it).</summary>
        private void SkipMember()
        {
            var depth = 0;
            while (Peek() is { Kind: not Kind.End } token)
            {
                if (depth == 0)
                {
                    if (IsPunct(token, ';'))
                    {
                        Take();
                        return;
                    }

                    if (IsPunct(token, '}'))
                    {
                        return; // the container's end
                    }

                    if (IsArrow(token))
                    {
                        SkipToSemicolon();
                        return;
                    }

                    if (IsPunct(token, '{'))
                    {
                        SkipBalanced();
                        if (IsPunct(Peek(), '='))
                        {
                            SkipToSemicolon(); // a property's initializer
                        }

                        return;
                    }
                }

                if (IsPunct(token, '(') || IsPunct(token, '['))
                {
                    depth++;
                }
                else if ((IsPunct(token, ')') || IsPunct(token, ']')) && depth > 0)
                {
                    depth--;
                }

                Take();
            }
        }

        /// <summary>A top-level statement: to its <c>;</c> or the end of its block.</summary>
        private void Statement()
        {
            if (IsPunct(Peek(), ')') || IsPunct(Peek(), ']'))
            {
                Take(); // a stray closing bracket
                return;
            }

            var depth = 0;
            while (Peek() is { Kind: not Kind.End } token)
            {
                if (depth == 0 && IsPunct(token, ';'))
                {
                    Take();
                    return;
                }

                if (IsPunct(token, '(') || IsPunct(token, '[') || IsPunct(token, '{'))
                {
                    depth++;
                }
                else if (IsPunct(token, ')') || IsPunct(token, ']') || IsPunct(token, '}'))
                {
                    if (depth == 0)
                    {
                        return;
                    }

                    depth--;
                    if (depth == 0 && IsPunct(token, '}'))
                    {
                        Take();
                        return;
                    }
                }

                Take();
            }
        }

        /// <summary>To the next <c>;</c> outside brackets (taken), or the container's end (not taken).</summary>
        private void SkipToSemicolon()
        {
            var depth = 0;
            while (Peek() is { Kind: not Kind.End } token)
            {
                if (depth == 0 && IsPunct(token, ';'))
                {
                    Take();
                    return;
                }

                if (IsPunct(token, '(') || IsPunct(token, '[') || IsPunct(token, '{'))
                {
                    depth++;
                }
                else if (IsPunct(token, ')') || IsPunct(token, ']') || IsPunct(token, '}'))
                {
                    if (depth == 0)
                    {
                        return;
                    }

                    depth--;
                }

                Take();
            }
        }

        /// <summary>An opening bracket through its matching closing one.</summary>
        private void SkipBalanced()
        {
            var depth = 0;
            while (Peek() is { Kind: not Kind.End } token)
            {
                Take();
                if (IsPunct(token, '(') || IsPunct(token, '[') || IsPunct(token, '{'))
                {
                    depth++;
                }
                else if ((IsPunct(token, ')') || IsPunct(token, ']') || IsPunct(token, '}')) && --depth <= 0)
                {
                    return;
                }
            }
        }

        /// <summary>Type arguments or parameters, <c>&lt;</c> through its matching <c>&gt;</c>; stops before anything a type can't hold.</summary>
        private void SkipAngles()
        {
            var depth = 0;
            while (Peek() is { Kind: not Kind.End } token)
            {
                if (IsPunct(token, ';') || IsPunct(token, '{') || IsPunct(token, '}') || IsArrow(token))
                {
                    return;
                }

                if (IsPunct(token, '(') || IsPunct(token, '['))
                {
                    SkipBalanced();
                    continue;
                }

                Take();
                if (IsPunct(token, '<'))
                {
                    depth++;
                }
                else if (IsPunct(token, '>') && --depth <= 0)
                {
                    return;
                }
            }
        }

        private void AddLens(Token token)
        {
            if (token.Kind == Kind.Word)
            {
                Lenses.Add(token.Start);
            }
        }
    }

    /// <summary>
    /// Words, punctuation (<c>=&gt;</c> and <c>::</c> as one) and literals, without whitespace, comments, preprocessor
    /// lines and the text of <c>#if</c> branches not taken.
    /// </summary>
    private sealed class Lexer(string text)
    {
        private readonly Token[] _ahead = new Token[4]; // a ring: the parser looks at most three tokens ahead
        private readonly Stack<bool> _conditions = new(); // per open #if: whether a branch was taken
        private int _first;
        private int _count;
        private int _position;
        private bool _lineStart = true;

        public Token Peek(int ahead)
        {
            while (_count <= ahead)
            {
                _ahead[(_first + _count++) & 3] = Read();
            }

            return _ahead[(_first + ahead) & 3];
        }

        public Token Take()
        {
            var token = Peek(0);
            _first = (_first + 1) & 3;
            _count--;
            return token;
        }

        private char At(int index) => index < text.Length ? text[index] : '\0';

        private Token Read()
        {
            while (_position < text.Length)
            {
                var c = text[_position];
                if (c == '\n')
                {
                    _lineStart = true;
                    _position++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    _position++;
                    continue;
                }

                if (c == '#' && _lineStart)
                {
                    Directive();
                    continue;
                }

                _lineStart = false;
                var start = _position;
                var next = At(_position + 1);
                if (c == '/' && next == '/')
                {
                    SkipLine();
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    var end = text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                    _position = end < 0 ? text.Length : end + 2;
                    continue;
                }

                if (c == '"' || c == '$' && (next == '"' || next == '$' || next == '@') || c == '@' && (next == '"' || next == '$'))
                {
                    SkipString();
                    return new Token(Kind.Literal, start, _position - start);
                }

                if (c == '\'')
                {
                    SkipCharacter();
                    return new Token(Kind.Literal, start, _position - start);
                }

                if (IsWordStart(c) || c == '@' && IsWordStart(next))
                {
                    _position++;
                    while (_position < text.Length && IsWordPart(text[_position]))
                    {
                        _position++;
                    }

                    return new Token(Kind.Word, start, _position - start);
                }

                if (char.IsAsciiDigit(c) || c == '.' && char.IsAsciiDigit(next))
                {
                    _position++;
                    while (_position < text.Length && (IsWordPart(text[_position]) || text[_position] == '.' && char.IsAsciiDigit(At(_position + 1))))
                    {
                        _position++;
                    }

                    return new Token(Kind.Literal, start, _position - start);
                }

                _position += c == '=' && next == '>' || c == ':' && next == ':' ? 2 : 1;
                return new Token(Kind.Punct, start, _position - start);
            }

            return new Token(Kind.End, text.Length, 0);
        }

        private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_' || c > 127 && char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.LetterNumber;

        private static bool IsWordPart(char c) =>
            char.IsLetterOrDigit(c) || c == '_' || c > 127 && char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.ConnectorPunctuation
                or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LetterNumber;

        private void SkipLine()
        {
            var end = text.IndexOf('\n', _position);
            _position = end < 0 ? text.Length : end;
        }

        private void SkipCharacter()
        {
            _position++;
            while (_position < text.Length && text[_position] is not ('\'' or '\n'))
            {
                _position += text[_position] == '\\' ? 2 : 1;
            }

            if (_position < text.Length && text[_position] == '\'')
            {
                _position++;
            }
        }

        /// <summary>A string of any kind: regular, verbatim, raw, each interpolated or not (holes lexed as code).</summary>
        private void SkipString()
        {
            var dollars = 0;
            var verbatim = false;
            while (_position < text.Length && text[_position] is '$' or '@')
            {
                if (text[_position] == '$')
                {
                    dollars++;
                }
                else
                {
                    verbatim = true;
                }

                _position++;
            }

            var quotes = 0;
            while (At(_position + quotes) == '"')
            {
                quotes++;
            }

            if (quotes >= 3 && !verbatim)
            {
                _position += quotes;
                SkipRawString(quotes, dollars);
                return;
            }

            _position++; // the opening quote
            if (quotes == 2)
            {
                _position++; // "" (a verbatim string starting with a quote has three or more)
                return;
            }

            while (_position < text.Length)
            {
                var c = text[_position];
                if (c == '"')
                {
                    if (verbatim && At(_position + 1) == '"')
                    {
                        _position += 2;
                        continue;
                    }

                    _position++;
                    return;
                }

                if (c == '\\' && !verbatim)
                {
                    _position += 2;
                    continue;
                }

                if (c == '\n' && !verbatim)
                {
                    return; // unterminated
                }

                if (dollars > 0 && c == '{')
                {
                    if (At(_position + 1) == '{')
                    {
                        _position += 2;
                        continue;
                    }

                    _position++;
                    SkipHole(1);
                    continue;
                }

                _position++;
            }
        }

        private void SkipRawString(int quotes, int dollars)
        {
            while (_position < text.Length)
            {
                var c = text[_position];
                if (c == '"')
                {
                    var run = 0;
                    while (At(_position + run) == '"')
                    {
                        run++;
                    }

                    _position += run;
                    if (run >= quotes)
                    {
                        return;
                    }

                    continue;
                }

                if (dollars > 0 && c == '{')
                {
                    var run = 0;
                    while (At(_position + run) == '{')
                    {
                        run++;
                    }

                    _position += run;
                    if (run >= dollars)
                    {
                        SkipHole(dollars);
                    }

                    continue;
                }

                _position++;
            }
        }

        /// <summary>An interpolation hole's code, through the <paramref name="braces"/> closing braces that end it.</summary>
        private void SkipHole(int braces)
        {
            var depth = 0;
            while (_position < text.Length)
            {
                var c = text[_position];
                var next = At(_position + 1);
                if (c == '}' && depth == 0)
                {
                    var run = 0;
                    while (run < braces && At(_position + run) == '}')
                    {
                        run++;
                    }

                    _position += run;
                    return;
                }

                if (c == '"' || c == '$' && (next == '"' || next == '$' || next == '@') || c == '@' && (next == '"' || next == '$'))
                {
                    SkipString();
                }
                else if (c == '\'')
                {
                    SkipCharacter();
                }
                else if (c == '/' && next == '*')
                {
                    var end = text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                    _position = end < 0 ? text.Length : end + 2;
                }
                else if (c == '/' && next == '/')
                {
                    SkipLine();
                }
                else
                {
                    depth += c is '(' or '[' or '{' ? 1 : c is ')' or ']' or '}' ? -1 : 0;
                    _position++;
                }
            }
        }

        /// <summary>A preprocessor line; for <c>#if</c>, <c>#elif</c> and <c>#else</c>, the branches not taken are skipped.</summary>
        private void Directive()
        {
            var lineEnd = text.IndexOf('\n', _position);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var line = text.AsSpan(_position + 1, lineEnd - _position - 1).Trim();
            var nameLength = 0;
            while (nameLength < line.Length && char.IsAsciiLetter(line[nameLength]))
            {
                nameLength++;
            }

            var name = line[..nameLength];
            var rest = line[nameLength..];
            _position = lineEnd;
            switch (name)
            {
                case "if":
                    var taken = PreprocessorCondition.Evaluate(rest, PreprocessorCondition.DotNetDebug);
                    _conditions.Push(taken);
                    if (!taken)
                    {
                        SkipBranch();
                    }

                    break;
                case "elif" or "else" when _conditions.Count > 0:
                    if (_conditions.Peek() || name is "elif" && !PreprocessorCondition.Evaluate(rest, PreprocessorCondition.DotNetDebug))
                    {
                        SkipBranch();
                    }
                    else
                    {
                        _conditions.Pop();
                        _conditions.Push(true);
                    }

                    break;
                case "endif" when _conditions.Count > 0:
                    _conditions.Pop();
                    break;
            }
        }

        /// <summary>
        /// The lines of a branch not taken, up to (not through) the <c>#elif</c>, <c>#else</c> or <c>#endif</c> that
        /// ends it: that directive is read next. Nested <c>#if</c>s in it are skipped whole; its text isn't lexed (as
        /// the compiler doesn't).
        /// </summary>
        private void SkipBranch()
        {
            var nested = 0;
            while (_position < text.Length)
            {
                var lineStart = _position < text.Length && text[_position] == '\n' ? _position + 1 : _position;
                var lineEnd = text.IndexOf('\n', lineStart);
                if (lineEnd < 0)
                {
                    lineEnd = text.Length;
                }

                var line = text.AsSpan(lineStart, lineEnd - lineStart).TrimStart();
                if (line.StartsWith("#"))
                {
                    var directive = line[1..].TrimStart();
                    if (directive.StartsWith("if") && (directive.Length == 2 || !char.IsAsciiLetterOrDigit(directive[2])))
                    {
                        nested++;
                    }
                    else if (directive.StartsWith("endif"))
                    {
                        if (nested == 0)
                        {
                            _position = lineStart;
                            _lineStart = true;
                            return;
                        }

                        nested--;
                    }
                    else if (nested == 0 && (directive.StartsWith("elif") || directive.StartsWith("else")))
                    {
                        _position = lineStart;
                        _lineStart = true;
                        return;
                    }
                }

                _position = lineEnd;
            }
        }
    }
}
