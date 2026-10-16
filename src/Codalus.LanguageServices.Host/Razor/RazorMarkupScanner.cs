using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Razor;

/// <summary>
/// Razor's markup, as tokens of Razor's own types: transitions (<c>@</c>), directives, Razor and HTML comments, and tags
/// with their delimiters, names, attributes and values. The C# the generator copied (its mapped spans) is skipped: Roslyn
/// classifies it. One linear pass over the file, keeping the tokens in the requested range.
/// TODO(issue): a scanner, not Razor's syntax tree: <c>&lt;text&gt;</c> tags aren't transitions, and an element a tag helper
/// doesn't bind is a tag helper when it comes where the generator's next tag helper scope of that name is expected.
/// </summary>
internal sealed class RazorMarkupScanner
{
    public static readonly int Transition = TokenTypes.IndexOf("razorTransition");
    public static readonly int Directive = TokenTypes.IndexOf("razorDirective");
    public static readonly int DirectiveAttribute = TokenTypes.IndexOf("razorDirectiveAttribute");
    public static readonly int DirectiveColon = TokenTypes.IndexOf("razorDirectiveColon");
    public static readonly int ComponentElement = TokenTypes.IndexOf("razorComponentElement");
    public static readonly int ComponentAttribute = TokenTypes.IndexOf("razorComponentAttribute");
    public static readonly int TagHelperElement = TokenTypes.IndexOf("razorTagHelperElement");
    public static readonly int TagHelperAttribute = TokenTypes.IndexOf("razorTagHelperAttribute");
    public static readonly int Comment = TokenTypes.IndexOf("razorComment");
    public static readonly int CommentTransition = TokenTypes.IndexOf("razorCommentTransition");
    public static readonly int CommentStar = TokenTypes.IndexOf("razorCommentStar");
    public static readonly int TagDelimiter = TokenTypes.IndexOf("markupTagDelimiter");
    public static readonly int Element = TokenTypes.IndexOf("markupElement");
    public static readonly int Attribute = TokenTypes.IndexOf("markupAttribute");
    public static readonly int AttributeQuote = TokenTypes.IndexOf("markupAttributeQuote");
    public static readonly int AttributeValue = TokenTypes.IndexOf("markupAttributeValue");
    public static readonly int Operator = TokenTypes.IndexOf("markupOperator");
    public static readonly int MarkupComment = TokenTypes.IndexOf("markupComment");
    public static readonly int MarkupCommentPunctuation = TokenTypes.IndexOf("markupCommentPunctuation");
    public static readonly int StringLiteral = TokenTypes.IndexOf("string");

    private static readonly HashSet<string> s_directives = new(StringComparer.Ordinal)
    {
        "page", "code", "using", "inject", "inherits", "implements", "layout", "attribute", "rendermode", "typeparam",
        "namespace", "preservewhitespace", "functions", "model", "addTagHelper", "removeTagHelper", "tagHelperPrefix", "section",
    };

    private static readonly System.Buffers.SearchValues<char> s_interesting = System.Buffers.SearchValues.Create("@<})");

    private readonly string _s;
    private readonly TextSpan[] _code;
    private readonly RazorBoundTags _bound;
    private readonly TextSpan _range;
    private readonly List<TokenSpan> _tokens = [];
    private readonly Dictionary<string, Stack<int>> _open = new(StringComparer.OrdinalIgnoreCase);
    private int _codeIndex;
    private int _braces;
    private int _parens;
    private int _tagHelpers;

    private RazorMarkupScanner(string text, TextSpan[] code, RazorBoundTags bound, TextSpan range)
    {
        _s = text;
        _code = code;
        _bound = bound;
        _range = range;
    }

    /// <summary>
    /// The markup tokens of <paramref name="text"/> in <paramref name="range"/>, sorted and not overlapping.
    /// <paramref name="code"/> are the C# spans (sorted).
    /// </summary>
    public static List<TokenSpan> Scan(string text, TextSpan[] code, RazorBoundTags bound, TextSpan range)
    {
        var scanner = new RazorMarkupScanner(text, code, bound, range);
        scanner.Run();
        scanner._tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        return scanner._tokens;
    }

    private void Run()
    {
        var n = _s.Length;
        var i = 0;
        while (i < n)
        {
            if (CodeEnd(i) is var end and >= 0)
            {
                i = end;
                continue;
            }

            var stop = NextCodeStart(i);
            var next = _s.AsSpan(i, stop - i).IndexOfAny(s_interesting);
            if (next < 0)
            {
                i = stop;
                continue;
            }

            i += next;
            switch (_s[i])
            {
                case '@':
                    i = At(i);
                    break;
                case '<' when IsAt(i, "<!--"):
                    i = HtmlComment(i);
                    break;
                case '<' when StartsTag(i):
                    i = Tag(i);
                    break;
                case '}' when _braces > 0:
                    Add(i, 1, Transition);
                    _braces--;
                    i++;
                    break;
                case ')' when _parens > 0:
                    Add(i, 1, Transition);
                    _parens--;
                    i++;
                    break;
                default:
                    i++;
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ transitions and directives

    /// <summary>An <c>@</c>: an escaped <c>@@</c>, an e-mail address, a Razor comment, or a transition (to a directive, a block, an expression).</summary>
    private int At(int i)
    {
        var n = _s.Length;
        if (i + 1 >= n)
        {
            return i + 1;
        }

        var next = _s[i + 1];
        if (next == '@')
        {
            return i + 2;
        }

        if (next == '*')
        {
            return RazorComment(i);
        }

        if (i > 0 && char.IsLetterOrDigit(_s[i - 1]))
        {
            return i + 1; // name@example.com
        }

        switch (next)
        {
            case '{':
                Add(i, 2, Transition);
                _braces++;
                return i + 2;
            case '(':
                Add(i, 2, Transition);
                _parens++;
                return i + 2;
            case ':':
                Add(i, 2, Transition);
                return i + 2;
            case '<':
                Add(i, 1, Transition); // a template: @<p>…</p>
                return i + 1;
        }

        if (!char.IsLetter(next) && next != '_')
        {
            return i + 1;
        }

        var end = i + 1;
        while (end < n && (char.IsLetterOrDigit(_s[end]) || _s[end] == '_'))
        {
            end++;
        }

        var word = _s[(i + 1)..end];
        Add(i, 1, Transition);
        if (!s_directives.Contains(word) || !AtLineStart(i) || (word == "using" && NextNonSpace(end) == '('))
        {
            return i + 1; // an expression or a statement (@if, @foreach): the generator copied it, Roslyn colors it
        }

        Add(i + 1, word.Length, Directive);
        switch (word)
        {
            case "code" or "functions":
                return OpenBlock(end);
            case "section":
            {
                var name = SkipSpaces(end);
                while (name < n && (char.IsLetterOrDigit(_s[name]) || _s[name] == '_'))
                {
                    name++;
                }

                return OpenBlock(name);
            }

            case "page":
            {
                var quote = SkipSpaces(end);
                if (quote < n && _s[quote] == '"')
                {
                    var close = _s.IndexOf('"', quote + 1);
                    var lineEnd = LineEnd(quote);
                    if (close >= 0 && close < lineEnd)
                    {
                        Add(quote, close + 1 - quote, StringLiteral);
                    }
                }

                break;
            }
        }

        // A directive's line has no markup (its C# is mapped): "@typeparam T where T : IComparable<T>" isn't a tag.
        return LineEnd(end);
    }

    /// <summary>The brace that opens a directive's block (<c>@code {</c>), with its closing brace counted.</summary>
    private int OpenBlock(int position)
    {
        var brace = position;
        while (brace < _s.Length && char.IsWhiteSpace(_s[brace]))
        {
            brace++;
        }

        if (brace < _s.Length && _s[brace] == '{')
        {
            Add(brace, 1, Transition);
            _braces++;
            return brace + 1;
        }

        return position;
    }

    private int RazorComment(int i)
    {
        var close = _s.IndexOf("*@", i + 2, StringComparison.Ordinal);
        Add(i, 1, CommentTransition);
        Add(i + 1, 1, CommentStar);
        if (close < 0)
        {
            Add(i + 2, _s.Length - (i + 2), Comment);
            return _s.Length;
        }

        Add(i + 2, close - (i + 2), Comment);
        Add(close, 1, CommentStar);
        Add(close + 1, 1, CommentTransition);
        return close + 2;
    }

    // ------------------------------------------------------------------ HTML

    private int HtmlComment(int i)
    {
        var close = _s.IndexOf("-->", i + 4, StringComparison.Ordinal);
        Add(i, 4, MarkupCommentPunctuation);
        if (close < 0)
        {
            Add(i + 4, _s.Length - (i + 4), MarkupComment);
            return _s.Length;
        }

        Add(i + 4, close - (i + 4), MarkupComment);
        Add(close, 3, MarkupCommentPunctuation);
        return close + 3;
    }

    private bool StartsTag(int i)
    {
        if (i + 1 >= _s.Length)
        {
            return false;
        }

        var next = _s[i + 1];
        return char.IsLetter(next) || (next is '/' or '!' && i + 2 < _s.Length && char.IsLetter(_s[i + 2]));
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.';

    /// <summary>A start or end tag: its delimiters, name, attributes and values.</summary>
    private int Tag(int i)
    {
        var n = _s.Length;
        var p = i + 1;
        var closing = _s[p] == '/';
        if (_s[p] is '/' or '!')
        {
            p++;
        }

        var nameStart = p;
        while (p < n && IsNameChar(_s[p]))
        {
            p++;
        }

        var name = _s[nameStart..p];
        Add(i, nameStart - i, TagDelimiter);

        int kind;
        if (closing)
        {
            kind = _open.TryGetValue(name, out var open) && open.Count > 0 ? open.Pop() : _bound.IsComponent(name) ? ComponentElement : Element;
        }
        else if (_bound.IsComponent(name))
        {
            kind = ComponentElement;
        }
        else if (_tagHelpers < _bound.TagHelperElements.Count && string.Equals(_bound.TagHelperElements[_tagHelpers], name, StringComparison.OrdinalIgnoreCase))
        {
            // The generator writes tag helpers in document order.
            _tagHelpers++;
            kind = TagHelperElement;
        }
        else
        {
            kind = Element;
        }

        Add(nameStart, name.Length, kind);

        var selfClosing = false;
        while (p < n)
        {
            while (p < n && char.IsWhiteSpace(_s[p]))
            {
                p++;
            }

            if (p >= n)
            {
                break;
            }

            if (CodeEnd(p) is var codeEnd and >= 0 && !(kind == ComponentElement && IsNameStart(p)))
            {
                p = codeEnd;
                continue;
            }

            var c = _s[p];
            if (c == '>')
            {
                Add(p, 1, TagDelimiter);
                p++;
                break;
            }

            if (c == '/' && p + 1 < n && _s[p + 1] == '>')
            {
                Add(p, 2, TagDelimiter);
                p += 2;
                selfClosing = true;
                break;
            }

            if (c == '<')
            {
                break; // an unterminated tag; the next one starts here
            }

            if (c == '@' && p + 1 < n && (char.IsLetter(_s[p + 1]) || _s[p + 1] == '_'))
            {
                p = DirectiveAttributeName(p);
            }
            else if (c == '@')
            {
                p = At(p); // @* … *@ or @(…) where an attribute could be
                continue;
            }
            else if (c is '"' or '\'' or '=' or '/')
            {
                p++;
                continue;
            }
            else
            {
                var start = p;
                while (p < n && !char.IsWhiteSpace(_s[p]) && _s[p] is not ('=' or '>' or '"' or '\'') && !(_s[p] == '/' && p + 1 < n && _s[p + 1] == '>'))
                {
                    p++;
                }

                Add(start, p - start, AttributeKind(kind, _s[start..p], mapped: CodeEnd(start) >= 0));
            }

            var equals = p;
            while (equals < n && char.IsWhiteSpace(_s[equals]))
            {
                equals++;
            }

            if (equals < n && _s[equals] == '=')
            {
                Add(equals, 1, Operator);
                p = Value(equals + 1);
            }
        }

        if (closing || selfClosing)
        {
            return p;
        }

        if (!_open.TryGetValue(name, out var stack))
        {
            _open[name] = stack = new Stack<int>();
        }

        stack.Push(kind);

        // Script and style content is JavaScript and CSS: their grammar colors it, and its '<' isn't a tag.
        if (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            var end = _s.IndexOf("</" + name, p, StringComparison.OrdinalIgnoreCase);
            return end < 0 ? n : end;
        }

        return p;
    }

    private bool IsNameStart(int p) => char.IsLetter(_s[p]) && (p == 0 || char.IsWhiteSpace(_s[p - 1]));

    private int AttributeKind(int element, string name, bool mapped)
    {
        if (element == ComponentElement)
        {
            // A parameter (the generator maps its name), or a capitalized name (Context, a type parameter); else an HTML
            // attribute the component passes on.
            return mapped || (name.Length > 0 && char.IsUpper(name[0])) ? ComponentAttribute : Attribute;
        }

        return element == TagHelperElement && _bound.TagHelperAttributes.Contains(name) ? TagHelperAttribute : Attribute;
    }

    /// <summary><c>@onclick</c>, <c>@bind-Value</c>, <c>@bind:event</c>, <c>@onclick:preventDefault</c>.</summary>
    private int DirectiveAttributeName(int p)
    {
        Add(p, 1, Transition);
        p++;
        var start = p;
        while (p < _s.Length && (char.IsLetterOrDigit(_s[p]) || _s[p] is '-' or '_' or '.'))
        {
            p++;
        }

        Add(start, p - start, DirectiveAttribute);
        if (p < _s.Length && _s[p] == ':')
        {
            Add(p, 1, DirectiveColon);
            p++;
            start = p;
            while (p < _s.Length && (char.IsLetterOrDigit(_s[p]) || _s[p] is '-' or '_' or '.'))
            {
                p++;
            }

            Add(start, p - start, DirectiveAttribute);
        }

        return p;
    }

    /// <summary>An attribute's value: quotes, and the markup between them; its C# and transitions are left to their own tokens.</summary>
    private int Value(int p)
    {
        var n = _s.Length;
        while (p < n && char.IsWhiteSpace(_s[p]))
        {
            p++;
        }

        if (p >= n)
        {
            return p;
        }

        char? quote = _s[p] is '"' or '\'' ? _s[p] : null;
        if (quote is not null)
        {
            Add(p, 1, AttributeQuote);
            p++;
        }

        var segment = p;
        while (p < n)
        {
            var c = _s[p];
            if (quote is { } q ? c == q : char.IsWhiteSpace(c) || c == '>' || (c == '/' && p + 1 < n && _s[p + 1] == '>'))
            {
                break;
            }

            if (CodeEnd(p) is var codeEnd and >= 0)
            {
                Add(segment, p - segment, AttributeValue);
                p = codeEnd;
                segment = p;
            }
            else if (c == '@')
            {
                Add(segment, p - segment, AttributeValue);
                p = At(p);
                segment = p;
            }
            else if (c == ')' && _parens > 0)
            {
                Add(segment, p - segment, AttributeValue);
                Add(p, 1, Transition);
                _parens--;
                p++;
                segment = p;
            }
            else
            {
                p++;
            }
        }

        Add(segment, Math.Min(p, n) - segment, AttributeValue);
        if (quote is not null && p < n)
        {
            Add(p, 1, AttributeQuote);
            p++;
        }

        return p;
    }

    // ------------------------------------------------------------------ text

    private bool IsAt(int i, string value) => string.CompareOrdinal(_s, i, value, 0, value.Length) == 0;

    private bool AtLineStart(int i)
    {
        for (var k = i - 1; k >= 0; k--)
        {
            if (_s[k] == '\n')
            {
                return true;
            }

            if (_s[k] is not (' ' or '\t' or '﻿'))
            {
                return false;
            }
        }

        return true;
    }

    private int SkipSpaces(int i)
    {
        while (i < _s.Length && _s[i] is ' ' or '\t')
        {
            i++;
        }

        return i;
    }

    private char NextNonSpace(int i) => SkipSpaces(i) is var k && k < _s.Length ? _s[k] : '\0';

    private int LineEnd(int i)
    {
        var end = _s.IndexOf('\n', i);
        return end < 0 ? _s.Length : end;
    }

    /// <summary>The end of the C# span at <paramref name="position"/>, or -1 in markup.</summary>
    private int CodeEnd(int position)
    {
        Seek(position);
        return _codeIndex < _code.Length && _code[_codeIndex].Start <= position ? _code[_codeIndex].End : -1;
    }

    /// <summary>The start of the next C# span after <paramref name="position"/>, or the end of the text.</summary>
    private int NextCodeStart(int position)
    {
        Seek(position);
        return _codeIndex < _code.Length ? Math.Max(position, _code[_codeIndex].Start) : _s.Length;
    }

    /// <summary>Moves to the first C# span that ends after <paramref name="position"/> (positions mostly only grow).</summary>
    private void Seek(int position)
    {
        while (_codeIndex > 0 && _code[_codeIndex - 1].End > position)
        {
            _codeIndex--;
        }

        while (_codeIndex < _code.Length && _code[_codeIndex].End <= position)
        {
            _codeIndex++;
        }
    }

    private void Add(int start, int length, int type)
    {
        if (length > 0 && type >= 0 && start < _range.End && start + length > _range.Start)
        {
            _tokens.Add(new TokenSpan(start, length, type, 0));
        }
    }
}
