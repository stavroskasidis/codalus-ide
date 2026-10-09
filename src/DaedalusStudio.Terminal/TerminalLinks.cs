using System.Globalization;
using System.Text.RegularExpressions;

namespace DaedalusStudio.Terminal;

public enum TerminalLinkKind
{
    Url,
    File,
}

/// <summary>
/// A link found in a line of output: <see cref="Start"/>/<see cref="Length"/> index the line's text. For files,
/// <see cref="Target"/> is the path as written (relative paths are resolved by the caller against the working directory)
/// and <see cref="Line"/>/<see cref="Column"/> are one-based (0 when absent).
/// </summary>
public sealed record TerminalLink(int Start, int Length, TerminalLinkKind Kind, string Target, int Line = 0, int Column = 0);

/// <summary>Finds URLs and <c>file:line:col</c> references (compiler, test and stack-trace formats) in terminal text.</summary>
public static partial class TerminalLinks
{
    // https://… up to whitespace or a quote/bracket that likely closes it.
    [GeneratedRegex("""\b(?:https?|file)://[^\s"'<>`\]\)\}]+""", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    // C:\a\b.cs, /a/b.cs, ./a/b.cs, a/b.cs, src\x.cs — followed by :line[:col], (line,col) or (line).
    [GeneratedRegex("""(?<path>(?:[A-Za-z]:[\\/]|\\\\|/|\.{1,2}[\\/]|~[\\/])?(?:[\w.\-+@$]+[\\/])*[\w\-+@$]+\.[A-Za-z0-9]{1,10})(?:(?::(?<line>\d+)(?::(?<col>\d+))?)|(?:\((?<line2>\d+)(?:,(?<col2>\d+))?\)))?""", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    public static IReadOnlyList<TerminalLink> Find(string text)
    {
        var links = new List<TerminalLink>();
        var taken = new bool[text.Length];
        foreach (Match match in UrlPattern().Matches(text))
        {
            var value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            links.Add(new TerminalLink(match.Index, value.Length, TerminalLinkKind.Url, value));
            Array.Fill(taken, true, match.Index, value.Length);
        }

        foreach (Match match in PathPattern().Matches(text))
        {
            if (taken[match.Index])
            {
                continue;
            }

            var path = match.Groups["path"].Value;
            var hasLocation = match.Groups["line"].Success || match.Groups["line2"].Success;
            var hasDirectory = path.Contains('/', StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal);

            // A bare "name.ext" is only a link with a location (Program.cs:12); otherwise too many words match.
            if (!hasDirectory && !hasLocation)
            {
                continue;
            }

            // Version numbers and domains aren't files (1.2.3, nuget.org).
            if (!hasDirectory && char.IsDigit(path[0]))
            {
                continue;
            }

            int Number(string a, string b) => match.Groups[a].Success ? int.Parse(match.Groups[a].Value, CultureInfo.InvariantCulture)
                : match.Groups[b].Success ? int.Parse(match.Groups[b].Value, CultureInfo.InvariantCulture) : 0;
            links.Add(new TerminalLink(match.Index, match.Length, TerminalLinkKind.File, path, Number("line", "line2"), Number("col", "col2")));
        }

        links.Sort((a, b) => a.Start.CompareTo(b.Start));
        return links;
    }
}
