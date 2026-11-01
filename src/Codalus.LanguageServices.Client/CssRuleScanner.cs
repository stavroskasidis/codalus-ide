namespace Codalus.LanguageServices.Client;

/// <summary>
/// Whether a CSS, SCSS or Less file has a rule the CSS server's document symbols list as a class, which the navigation
/// bar shows as its types: a style rule (<c>.btn {</c>, Sass's <c>%placeholder</c>, a Less guard), <c>@font-face</c>,
/// <c>@keyframes</c> or <c>@at-root</c>, at the top level or in a block that passes its rules through (<c>@media</c>,
/// <c>@supports</c>, <c>@layer</c>, <c>@container</c>, <c>@scope</c>, <c>@starting-style</c>, and Sass's <c>@if</c>,
/// <c>@each</c>, <c>@for</c>, <c>@while</c> and <c>@include</c>). Other blocks hide theirs (<c>@page</c>,
/// <c>@property</c>, <c>@mixin</c>, <c>@function</c>, a Less mixin <c>.m() {</c>, a detached ruleset, a nested
/// property). The editor keeps the bar's room from the first frame when they will come.
/// </summary>
public static class CssRuleScanner
{
    private static readonly HashSet<string> s_passThrough = new(StringComparer.OrdinalIgnoreCase)
    {
        "media", "supports", "layer", "container", "document", "-moz-document", "scope", "starting-style",
        "if", "else", "each", "for", "while", "include",
    };

    private static readonly HashSet<string> s_rules = new(StringComparer.OrdinalIgnoreCase)
    {
        "font-face", "keyframes", "-webkit-keyframes", "-moz-keyframes", "-o-keyframes", "-ms-keyframes", "at-root",
    };

    /// <summary>Whether the text has such a rule; it stops at the first. <paramref name="lineComments"/>: SCSS and Less (<c>//</c>).</summary>
    public static bool DeclaresType(string text, bool lineComments)
    {
        var passing = new List<bool>(); // each open block: whether it passes its rules through
        var prelude = -1; // where the text before the next block starts (its selector or at-rule), or -1
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }

            if (c == '/' && lineComments && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
                continue;
            }

            if (c is '"' or '\'')
            {
                prelude = prelude < 0 ? i : prelude;
                i = SkipString(text, i);
                continue;
            }

            if (c is '#' or '@' && i + 1 < text.Length && text[i + 1] == '{')
            {
                // An interpolation (Sass's #{…}, Less's @{…}): part of a selector or a name.
                prelude = prelude < 0 ? i : prelude;
                i = SkipBraces(text, i + 1);
                continue;
            }

            if ((c is 'u' or 'U') && string.Compare(text, i, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                prelude = prelude < 0 ? i : prelude;
                var close = text.IndexOf(')', i);
                i = close < 0 ? text.Length : close + 1; // url(http://…): not a comment
                continue;
            }

            switch (c)
            {
                case '{':
                    var kind = prelude < 0 ? Block.Other : Classify(text.AsSpan(prelude, i - prelude).Trim());
                    if (kind == Block.Rule && passing.TrueForAll(p => p))
                    {
                        return true;
                    }

                    passing.Add(kind == Block.PassThrough);
                    prelude = -1;
                    break;
                case '}':
                    if (passing.Count > 0)
                    {
                        passing.RemoveAt(passing.Count - 1);
                    }

                    prelude = -1;
                    break;
                case ';':
                    prelude = -1;
                    break;
                default:
                    if (prelude < 0 && !char.IsWhiteSpace(c))
                    {
                        prelude = i;
                    }

                    break;
            }

            i++;
        }

        return false;
    }

    private enum Block
    {
        Rule,
        PassThrough,
        Other,
    }

    private static Block Classify(ReadOnlySpan<char> prelude)
    {
        if (prelude.IsEmpty)
        {
            return Block.Other;
        }

        if (prelude[0] == '@')
        {
            var length = 1;
            while (length < prelude.Length && (char.IsLetterOrDigit(prelude[length]) || prelude[length] is '-' or '_'))
            {
                length++;
            }

            var name = prelude[1..length];
            return s_passThrough.GetAlternateLookup<ReadOnlySpan<char>>().Contains(name) ? Block.PassThrough
                : s_rules.GetAlternateLookup<ReadOnlySpan<char>>().Contains(name) ? Block.Rule
                : Block.Other;
        }

        if (prelude[^1] == ':')
        {
            return Block.Other; // a nested property: font: { family: … }
        }

        if (prelude[0] is '.' or '#')
        {
            // A Less mixin's definition: .m() {, #m(@a; @b: 2) {
            var length = 1;
            while (length < prelude.Length && (char.IsLetterOrDigit(prelude[length]) || prelude[length] is '-' or '_'))
            {
                length++;
            }

            while (length < prelude.Length && char.IsWhiteSpace(prelude[length]))
            {
                length++;
            }

            if (length > 1 && length < prelude.Length && prelude[length] == '(')
            {
                return Block.Other;
            }
        }

        return Block.Rule;
    }

    private static int SkipString(string text, int i)
    {
        var quote = text[i++];
        while (i < text.Length && text[i] != quote && text[i] != '\n')
        {
            i += text[i] == '\\' ? 2 : 1;
        }

        return Math.Min(i + 1, text.Length);
    }

    /// <summary>From an opening brace: past its closing one.</summary>
    private static int SkipBraces(string text, int i)
    {
        var depth = 0;
        while (i < text.Length)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i + 1;
            }

            i++;
        }

        return i;
    }
}
