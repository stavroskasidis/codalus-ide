using System.Text.RegularExpressions;

namespace DaedalusStudio.Workbench.Languages;

/// <summary>What scoped CSS completes at a caret: class names, element ids, or <c>::deep</c>.</summary>
public enum ScopedCssCompletion
{
    None,
    ClassName,
    Id,
    PseudoElement,
}

/// <summary>
/// Scoped CSS (ADR 0027): <c>Component.razor.css</c> styles <c>Component.razor</c> only (and <c>Page.cshtml.css</c> its
/// page). In its selectors the editor completes the class names and ids the component's markup uses, and Blazor's
/// <c>::deep</c>, next to the CSS server's own completion.
/// </summary>
public static partial class ScopedCss
{
    /// <summary>Whether <paramref name="path"/> is a component's scoped CSS (<c>.razor.css</c>, <c>.cshtml.css</c>).</summary>
    public static bool IsScoped(string path) =>
        path.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml.css", StringComparison.OrdinalIgnoreCase);

    /// <summary>The component a scoped CSS file styles: the file without <c>.css</c>.</summary>
    public static string Component(string path) => path[..^".css".Length];

    /// <summary>
    /// What to complete at <paramref name="offset"/> in <paramref name="css"/>, and where the word being completed
    /// starts: in a selector (outside a declaration block and a comment), after <c>.</c>, <c>#</c> or <c>::</c>.
    /// </summary>
    public static (ScopedCssCompletion Kind, int WordStart) At(string css, int offset)
    {
        if (offset < 0 || offset > css.Length || InDeclarationOrComment(css, offset))
        {
            return (ScopedCssCompletion.None, offset);
        }

        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(css[start - 1]) || css[start - 1] is '-' or '_'))
        {
            start--;
        }

        var before = start > 0 ? css[start - 1] : '\0';
        return before switch
        {
            '.' => (ScopedCssCompletion.ClassName, start),
            '#' => (ScopedCssCompletion.Id, start),
            ':' when start > 1 && css[start - 2] == ':' => (ScopedCssCompletion.PseudoElement, start),
            _ => (ScopedCssCompletion.None, start),
        };
    }

    /// <summary>Whether <paramref name="offset"/> is inside a <c>{ … }</c> block of declarations or a comment.</summary>
    private static bool InDeclarationOrComment(string css, int offset)
    {
        var depth = 0;
        var i = 0;
        while (i < offset)
        {
            if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0 || close + 2 > offset)
                {
                    return true;
                }

                i = close + 2;
                continue;
            }

            if (css[i] == '{')
            {
                depth++;
            }
            else if (css[i] == '}')
            {
                depth = Math.Max(0, depth - 1);
            }

            i++;
        }

        // A block holds declarations; at-rule blocks (@media) hold rules, which nest one level deeper.
        return depth > AtRuleDepth(css, offset);
    }

    /// <summary>How many of the blocks open at <paramref name="offset"/> are at-rules (<c>@media</c>, <c>@supports</c>), which hold selectors.</summary>
    private static int AtRuleDepth(string css, int offset)
    {
        var stack = new Stack<bool>();
        var headerStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (css[i] == '{')
            {
                stack.Push(css.AsSpan(headerStart, i - headerStart).Trim().StartsWith("@"));
                headerStart = i + 1;
            }
            else if (css[i] == '}')
            {
                if (stack.Count > 0)
                {
                    stack.Pop();
                }

                headerStart = i + 1;
            }
            else if (css[i] == ';')
            {
                headerStart = i + 1;
            }
        }

        return stack.Count(isAtRule => isAtRule);
    }

    /// <summary>The class names and ids in a component's markup (its C# blanked first, so expressions don't count).</summary>
    public static (IReadOnlyList<string> Classes, IReadOnlyList<string> Ids) Names(string component)
    {
        var html = RazorHtmlProjection.Project(component);
        var classes = new SortedSet<string>(StringComparer.Ordinal);
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Attribute().Matches(html))
        {
            var target = match.Groups["name"].Value.Equals("class", StringComparison.OrdinalIgnoreCase) ? classes : ids;
            foreach (var name in match.Groups["value"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (CssName().IsMatch(name))
                {
                    target.Add(name);
                }
            }
        }

        return ([.. classes], [.. ids]);
    }

    [GeneratedRegex("""\b(?<name>class|id)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"^-?[A-Za-z_][\w-]*$")]
    private static partial Regex CssName();
}
