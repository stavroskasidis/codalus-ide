using System.Text;
using System.Text.RegularExpressions;

namespace Codalus.Workbench.Ai;

/// <summary>
/// The rules of one ignore file in <c>.gitignore</c>'s syntax (<c>.gitignore</c>, <c>.codalusignore</c>,
/// <c>.git/info/exclude</c>, or the <c>ai.exclude</c> setting): blank lines and <c>#</c> comments are skipped, <c>!</c>
/// re-includes, a trailing <c>/</c> matches only folders, a <c>/</c> elsewhere anchors the pattern to the file's folder,
/// and <c>*</c>, <c>?</c>, <c>[…]</c> and <c>**</c> work as in git.
/// </summary>
public sealed class IgnoreRules
{
    private readonly List<Rule> _rules;

    private IgnoreRules(List<Rule> rules) => _rules = rules;

    /// <summary>No rules.</summary>
    public static IgnoreRules Empty { get; } = new([]);

    public bool IsEmpty => _rules.Count == 0;

    public static IgnoreRules Parse(string text) => Parse(text.Split('\n'));

    public static IgnoreRules Parse(IEnumerable<string> lines)
    {
        var rules = new List<Rule>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            // Trailing spaces are ignored unless escaped.
            var end = line.Length;
            while (end > 0 && line[end - 1] == ' ' && !(end > 1 && line[end - 2] == '\\'))
            {
                end--;
            }

            line = line[..end];
            var negate = false;
            if (line.StartsWith('!'))
            {
                negate = true;
                line = line[1..];
            }
            else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
            {
                line = line[1..];
            }

            var directoryOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0)
            {
                continue;
            }

            var anchored = line.Contains('/');
            line = line.TrimStart('/');
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                rules.Add(new Rule(new Regex(ToRegex(line, anchored), RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? RegexOptions.IgnoreCase : RegexOptions.None)), negate, directoryOnly));
            }
            catch (ArgumentException)
            {
                // A pattern git would also not understand: skip it.
            }
        }

        return new IgnoreRules(rules);
    }

    /// <summary>
    /// Whether this file's rules decide about <paramref name="relativePath"/> (forward slashes, relative to the ignore
    /// file's folder): true for ignored, false for re-included, null when no rule matches.
    /// </summary>
    public bool? Decide(string relativePath, bool isDirectory)
    {
        bool? decision = null;
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }

            if (rule.Pattern.IsMatch(relativePath))
            {
                decision = !rule.Negate;
            }
        }

        return decision;
    }

    /// <summary>Whether <paramref name="relativePath"/>, or a folder above it, is ignored by these rules alone.</summary>
    public bool IsIgnored(string relativePath)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            if (Decide(string.Join('/', parts, 0, i), i < parts.Length) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static string ToRegex(string pattern, bool anchored)
    {
        var sb = new StringBuilder("^");
        if (!anchored)
        {
            sb.Append("(?:.*/)?");
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    {
                        var atStart = i == 0 || pattern[i - 1] == '/';
                        var slashAfter = i + 2 < pattern.Length && pattern[i + 2] == '/';
                        var atEnd = i + 2 == pattern.Length;
                        if (atStart && slashAfter)
                        {
                            sb.Append("(?:.*/)?");
                            i += 2;
                        }
                        else if (atStart && atEnd)
                        {
                            sb.Append(".*");
                            i += 1;
                        }
                        else
                        {
                            sb.Append("[^/]*");
                            i += 1;
                        }

                        break;
                    }

                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                    {
                        var close = pattern.IndexOf(']', i + 1);
                        if (close < 0)
                        {
                            sb.Append("\\[");
                            break;
                        }

                        var set = pattern[(i + 1)..close];
                        sb.Append('[');
                        if (set.StartsWith('!'))
                        {
                            sb.Append('^');
                            set = set[1..];
                        }

                        sb.Append(set.Replace("\\", "\\\\", StringComparison.Ordinal));
                        sb.Append(']');
                        i = close;
                        break;
                    }

                case '\\' when i + 1 < pattern.Length:
                    sb.Append(Regex.Escape(pattern[++i].ToString()));
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return sb.ToString();
    }

    private sealed record Rule(Regex Pattern, bool Negate, bool DirectoryOnly);
}
