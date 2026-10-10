namespace DaedalusStudio.Workbench.Languages;

/// <summary>
/// The HTML of a Razor file (ADR 0027): its text with the C# and the Razor syntax replaced by spaces, line breaks kept,
/// so every offset and position is the same in both. The HTML language server works on it for HTML and CSS inside
/// <c>.razor</c> and <c>.cshtml</c> files. A scanner, like Razor's own parser in spirit:
/// <list type="bullet">
/// <item>markup stays, with <c>@expression</c>, <c>@(…)</c>, <c>@*…*@</c> and directive attributes (<c>@onclick="…"</c>) blanked;</item>
/// <item>directives (<c>@page</c>, <c>@using</c>, …) and <c>@code</c>/<c>@functions</c> blocks are blanked;</item>
/// <item>in <c>@{…}</c> and in <c>@if</c>, <c>@foreach</c> and the other statements, the C# is blanked and the elements
/// in it (and <c>@:</c> lines) stay, as Razor reads them;</item>
/// <item><c>@@</c> is a literal <c>@</c>.</item>
/// </list>
/// </summary>
public static class RazorHtmlProjection
{
    /// <summary>The HTML's document is the Razor file's path with this added (<c>Counter.razor__virtual.html</c>).</summary>
    public const string VirtualSuffix = "__virtual.html";

    private static readonly HashSet<string> s_lineDirectives = new(StringComparer.Ordinal)
    {
        "page", "using", "inject", "layout", "inherits", "implements", "attribute", "namespace", "typeparam", "rendermode",
        "model", "addTagHelper", "removeTagHelper", "tagHelperPrefix", "preservewhitespace",
    };

    private static readonly HashSet<string> s_codeDirectives = new(StringComparer.Ordinal) { "code", "functions" };

    private static readonly HashSet<string> s_statements = new(StringComparer.Ordinal) { "if", "for", "foreach", "while", "switch", "lock", "using", "try", "do" };

    private static readonly HashSet<string> s_voidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    /// <summary>The HTML of <paramref name="razor"/>, as long as it.</summary>
    public static string Project(string razor)
    {
        var scanner = new Scanner(razor);
        scanner.Markup(0, razor.Length, stopAtBrace: false);
        return new string(scanner.Output);
    }

    /// <summary>
    /// Whether <paramref name="offset"/> (a caret) is in markup: the last character before it that isn't white space
    /// was kept in the HTML (the start of the file counts as markup).
    /// </summary>
    public static bool IsMarkup(string razor, string html, int offset)
    {
        if (html.Length != razor.Length || offset < 0 || offset > razor.Length)
        {
            return false;
        }

        for (var i = offset - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(razor[i]))
            {
                return razor[i] == html[i];
            }
        }

        return true;
    }

    private sealed class Scanner(string text)
    {
        public char[] Output { get; } = text.ToCharArray();

        private void Blank(int start, int end)
        {
            for (var i = Math.Max(0, start); i < Math.Min(end, text.Length); i++)
            {
                if (text[i] is not '\r' and not '\n')
                {
                    Output[i] = ' ';
                }
            }
        }

        private char At(int i) => i >= 0 && i < text.Length ? text[i] : '\0';

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        private int Identifier(int i)
        {
            while (i < text.Length && IsIdentifierPart(text[i]))
            {
                i++;
            }

            return i;
        }

        private int SkipSpace(int i, bool newlines = true)
        {
            while (i < text.Length && (text[i] is ' ' or '\t' || (newlines && text[i] is '\r' or '\n')))
            {
                i++;
            }

            return i;
        }

        private int LineEnd(int i)
        {
            while (i < text.Length && text[i] is not '\r' and not '\n')
            {
                i++;
            }

            return i;
        }

        // ------------------------------------------------------------------ markup

        /// <summary>
        /// Markup from <paramref name="i"/>: copied, with Razor transitions handled. Inside a block (<paramref name="stopAtBrace"/>,
        /// a <c>@section</c>) it ends at the unbalanced <c>}</c>, which is returned.
        /// </summary>
        public int Markup(int i, int end, bool stopAtBrace)
        {
            while (i < end)
            {
                var c = text[i];
                if (c == '@' && !char.IsLetterOrDigit(At(i - 1)))
                {
                    i = Transition(i, end);
                }
                else if (c == '<' && (IsIdentifierStart(At(i + 1)) || At(i + 1) == '/'))
                {
                    i = Tag(i, end, out _, out _);
                }
                else if (c == '<' && At(i + 1) == '!' && At(i + 2) == '-' && At(i + 3) == '-')
                {
                    var close = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = close < 0 ? end : close + 3;
                }
                else if (stopAtBrace && c == '}')
                {
                    return i;
                }
                else
                {
                    i++;
                }
            }

            return i;
        }

        /// <summary>A tag at <paramref name="i"/>: its attributes keep their markup, directive attributes are blanked.</summary>
        private int Tag(int i, int end, out string name, out bool closes)
        {
            var start = i;
            i++;
            closes = At(i) == '/';
            if (closes)
            {
                i++;
            }

            var nameStart = i;
            while (i < end && (IsIdentifierPart(text[i]) || text[i] is '-' or ':' or '.'))
            {
                i++;
            }

            name = text[nameStart..i];
            if (name == "text")
            {
                // <text> is Razor's: its content is markup, the tag isn't.
                var close = text.IndexOf('>', i);
                var tagEnd = close < 0 ? end : close + 1;
                Blank(start, tagEnd);
                return tagEnd;
            }

            while (i < end && text[i] != '>')
            {
                if (text[i] == '@')
                {
                    // A directive attribute (@onclick="…", @bind-Value:event="…") or an expression among the attributes.
                    var attributeStart = i;
                    var j = i + 1;
                    while (j < end && (IsIdentifierPart(text[j]) || text[j] is '-' or ':' or '.'))
                    {
                        j++;
                    }

                    if (j > i + 1 && At(SkipSpace(j)) == '=')
                    {
                        j = AttributeValue(SkipSpace(SkipSpace(j) + 1), end);
                        Blank(attributeStart, j);
                        i = j;
                    }
                    else if (j > i + 1 && (char.IsWhiteSpace(At(j)) || At(j) is '>' or '/'))
                    {
                        // A directive attribute without a value (@onclick:preventDefault).
                        Blank(attributeStart, j);
                        i = j;
                    }
                    else
                    {
                        i = Transition(i, end);
                    }
                }
                else if (text[i] is '"' or '\'')
                {
                    // A quoted value: copied, with its expressions blanked.
                    var quote = text[i];
                    i++;
                    while (i < end && text[i] != quote)
                    {
                        i = text[i] == '@' ? Transition(i, end) : i + 1;
                    }

                    i++;
                }
                else
                {
                    i++;
                }
            }

            return Math.Min(i + 1, end);
        }

        private int AttributeValue(int i, int end)
        {
            if (At(i) is '"' or '\'')
            {
                var quote = text[i];
                var close = text.IndexOf(quote, i + 1);
                return close < 0 ? end : close + 1;
            }

            while (i < end && !char.IsWhiteSpace(text[i]) && text[i] is not '>' and not '/')
            {
                i++;
            }

            return i;
        }

        /// <summary>
        /// An element in code (<c>&lt;p&gt;…&lt;/p&gt;</c> in a <c>@foreach</c>): markup until its end tag, nested
        /// elements counted.
        /// </summary>
        private int Element(int i, int end)
        {
            i = Tag(i, end, out var name, out var closes);
            if (closes || name.Length == 0 || s_voidElements.Contains(name) || text[Math.Max(0, i - 2)] == '/')
            {
                return i;
            }

            var depth = 1;
            while (i < end && depth > 0)
            {
                if (text[i] == '@' && !char.IsLetterOrDigit(At(i - 1)))
                {
                    i = Transition(i, end);
                }
                else if (text[i] == '<' && (IsIdentifierStart(At(i + 1)) || At(i + 1) == '/'))
                {
                    var tagStart = i;
                    i = Tag(i, end, out var inner, out var innerCloses);
                    if (string.Equals(inner, name, StringComparison.OrdinalIgnoreCase))
                    {
                        var selfClosing = text[Math.Max(tagStart, i - 2)] == '/';
                        depth += innerCloses ? -1 : selfClosing ? 0 : 1;
                    }
                }
                else
                {
                    i++;
                }
            }

            return i;
        }

        // ------------------------------------------------------------------ transitions

        /// <summary>A Razor transition at an <c>@</c>; returns where markup resumes.</summary>
        private int Transition(int i, int end)
        {
            var next = At(i + 1);
            if (next == '@')
            {
                Output[i] = ' '; // @@ is a literal @
                return i + 2;
            }

            if (next == '*')
            {
                var close = text.IndexOf("*@", i + 2, StringComparison.Ordinal);
                var commentEnd = close < 0 ? end : close + 2;
                Blank(i, commentEnd);
                return commentEnd;
            }

            if (next == '{')
            {
                var blockEnd = CodeBlock(i + 1, end);
                Blank(i, i + 2);
                return blockEnd;
            }

            if (next == '(')
            {
                var close = Balanced(i + 1, end, '(', ')');
                Blank(i, close);
                return close;
            }

            if (next == ':')
            {
                // A text line in code: markup to the end of the line.
                Blank(i, i + 2);
                return Markup(i + 2, LineEnd(i + 2), stopAtBrace: false);
            }

            if (!IsIdentifierStart(next))
            {
                // An e-mail address or a lone @: text.
                return i + 1;
            }

            var wordEnd = Identifier(i + 1);
            var word = text[(i + 1)..wordEnd];
            if (s_codeDirectives.Contains(word) && At(SkipSpace(wordEnd)) == '{')
            {
                var open = SkipSpace(wordEnd);
                var close = Balanced(open, end, '{', '}');
                Blank(i, close);
                return close;
            }

            if (word == "section" && At(SkipSpace(wordEnd, newlines: false)) is var after && IsIdentifierStart(after))
            {
                // @section Name { markup }
                var open = SkipSpace(Identifier(SkipSpace(wordEnd, newlines: false)));
                if (At(open) == '{')
                {
                    Blank(i, open + 1);
                    var close = Markup(open + 1, end, stopAtBrace: true);
                    Blank(close, close + 1);
                    return Math.Min(close + 1, end);
                }
            }

            if (s_statements.Contains(word) && !(word == "using" && At(SkipSpace(wordEnd, newlines: false)) != '('))
            {
                return Statement(i, wordEnd, end);
            }

            if (s_lineDirectives.Contains(word) && IsLineStart(i))
            {
                var lineEnd = LineEnd(wordEnd);
                Blank(i, lineEnd);
                return lineEnd;
            }

            // An implicit expression: identifiers, member access, calls and indexers, await.
            var j = wordEnd;
            if (word == "await")
            {
                j = Identifier(SkipSpace(j, newlines: false));
            }

            while (j < end)
            {
                if (text[j] == '(')
                {
                    j = Balanced(j, end, '(', ')');
                }
                else if (text[j] == '[')
                {
                    j = Balanced(j, end, '[', ']');
                }
                else if (text[j] is '.' or '?' && IsIdentifierStart(At(j + 1)) || text[j] == '?' && At(j + 1) == '.' && IsIdentifierStart(At(j + 2)))
                {
                    j = Identifier(j + (text[j] == '?' && At(j + 1) == '.' ? 2 : 1));
                }
                else if (text[j] == '!' && At(j + 1) == '.')
                {
                    j++;
                }
                else
                {
                    break;
                }
            }

            Blank(i, j);
            return j;
        }

        private bool IsLineStart(int i)
        {
            for (var j = i - 1; j >= 0 && text[j] is not '\n'; j--)
            {
                if (!char.IsWhiteSpace(text[j]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// <c>@if (…) { … } else { … }</c> and the other statements: the C# blanked, the markup in their blocks kept.
        /// </summary>
        private int Statement(int at, int wordEnd, int end)
        {
            var i = wordEnd;
            while (true)
            {
                // The header: a parenthesized part (none for try, do, else), then the block.
                i = SkipSpace(i);
                if (At(i) == '(')
                {
                    i = Balanced(i, end, '(', ')');
                    i = SkipSpace(i);
                }

                if (At(i) != '{')
                {
                    Blank(at, i);
                    return i;
                }

                Blank(at, i);
                i = CodeBlock(i, end);

                // else, else if, catch, finally, and do's while (…).
                var next = SkipSpace(i);
                var nextWord = IsIdentifierStart(At(next)) ? text[next..Identifier(next)] : string.Empty;
                if (nextWord is "else" or "catch" or "finally")
                {
                    at = next;
                    i = Identifier(next);
                    if (nextWord == "else" && At(SkipSpace(i, newlines: false)) == 'i' && text.AsSpan(SkipSpace(i, newlines: false)).StartsWith("if", StringComparison.Ordinal))
                    {
                        i = SkipSpace(i, newlines: false) + 2;
                    }

                    continue;
                }

                if (nextWord == "while" && text.AsSpan(at).StartsWith("@do", StringComparison.Ordinal))
                {
                    var close = Balanced(SkipSpace(Identifier(next)), end, '(', ')');
                    var semicolon = SkipSpace(close, newlines: false);
                    var statementEnd = At(semicolon) == ';' ? semicolon + 1 : close;
                    Blank(next, statementEnd);
                    return statementEnd;
                }

                return i;
            }
        }

        /// <summary>
        /// A code block at the <c>{</c> at <paramref name="open"/>: C# blanked, with its elements and <c>@:</c> lines kept
        /// as markup. Returns the offset after the closing <c>}</c>.
        /// </summary>
        private int CodeBlock(int open, int end)
        {
            Blank(open, open + 1);
            var i = open + 1;
            var depth = 1;
            var statementStart = true;
            while (i < end)
            {
                var c = text[i];
                if (c is '\r' or '\n')
                {
                    statementStart = true;
                    i++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '<' && statementStart && (IsIdentifierStart(At(i + 1)) || At(i + 1) == '!'))
                {
                    i = Element(i, end);
                    statementStart = true;
                    continue;
                }

                if (c == '@')
                {
                    if (At(i + 1) == ':')
                    {
                        Blank(i, i + 2);
                        i = Markup(i + 2, LineEnd(i + 2), stopAtBrace: false);
                    }
                    else if (At(i + 1) == '*')
                    {
                        i = Transition(i, end);
                    }
                    else if (At(i + 1) == '"' || At(i + 1) == '$')
                    {
                        var stringEnd = SkipString(i + 1, end);
                        Blank(i, stringEnd);
                        i = stringEnd;
                    }
                    else
                    {
                        // A nested Razor statement (@if in a block): the same rules.
                        i = IsIdentifierStart(At(i + 1)) && s_statements.Contains(text[(i + 1)..Identifier(i + 1)])
                            ? Statement(i, Identifier(i + 1), end)
                            : SkipC(i, end);
                    }

                    statementStart = true;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        Blank(i, i + 1);
                        return i + 1;
                    }
                }

                var next = c is '"' or '\'' or '$' ? SkipString(i, end) : c == '/' && At(i + 1) is '/' or '*' ? SkipComment(i, end) : i + 1;
                Blank(i, next);
                statementStart = c is '{' or '}' or ';';
                i = next;
            }

            return end;
        }

        private int SkipC(int i, int end)
        {
            Blank(i, i + 1);
            return i + 1;
        }

        /// <summary>Past a balanced pair from the opener at <paramref name="i"/>, skipping C# strings, characters and comments.</summary>
        private int Balanced(int i, int end, char open, char close)
        {
            var depth = 0;
            while (i < end)
            {
                var c = text[i];
                if (c == open)
                {
                    depth++;
                }
                else if (c == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i + 1;
                    }
                }
                else if (c is '"' or '\'' || (c == '$' || c == '@') && At(i + 1) == '"')
                {
                    i = SkipString(i, end);
                    continue;
                }
                else if (c == '/' && At(i + 1) is '/' or '*')
                {
                    i = SkipComment(i, end);
                    continue;
                }

                i++;
            }

            return end;
        }

        /// <summary>Past a C# string or character literal at <paramref name="i"/> (regular, verbatim, interpolated, raw).</summary>
        private int SkipString(int i, int end)
        {
            var verbatim = false;
            while (i < end && text[i] is '$' or '@')
            {
                verbatim |= text[i] == '@';
                i++;
            }

            if (i >= end || text[i] is not '"' and not '\'')
            {
                return i;
            }

            var quote = text[i];
            if (quote == '"' && At(i + 1) == '"' && At(i + 2) == '"')
            {
                // A raw string: as many quotes close it as opened it.
                var count = 0;
                while (At(i + count) == '"')
                {
                    count++;
                }

                var closer = new string('"', count);
                var close = text.IndexOf(closer, i + count, StringComparison.Ordinal);
                return close < 0 ? end : close + count;
            }

            i++;
            while (i < end)
            {
                if (text[i] == quote)
                {
                    if (verbatim && At(i + 1) == quote)
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                if (text[i] == '\\' && !verbatim)
                {
                    i += 2;
                    continue;
                }

                if (text[i] is '\n' && !verbatim)
                {
                    return i;
                }

                i++;
            }

            return end;
        }

        private int SkipComment(int i, int end)
        {
            if (At(i + 1) == '/')
            {
                return LineEnd(i);
            }

            var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return close < 0 ? end : close + 2;
        }
    }
}
