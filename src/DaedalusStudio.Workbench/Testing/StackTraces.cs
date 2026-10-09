using System.Globalization;
using System.Text.RegularExpressions;

namespace DaedalusStudio.Workbench.Testing;

/// <summary>A line of a stack trace; <see cref="File"/> and <see cref="Line"/> are set when the frame has a location.</summary>
public sealed record StackFrameLine(string Text, string? File, int Line);

/// <summary>Finds source locations in .NET stack traces (<c>at Ns.Type.Method() in C:\src\File.cs:line 42</c>).</summary>
public static partial class StackTraces
{
    public static IReadOnlyList<StackFrameLine> Parse(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
        {
            return [];
        }

        var lines = new List<StackFrameLine>();
        foreach (var raw in stackTrace.Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            if (text.Length == 0)
            {
                continue;
            }

            var match = Location().Match(text);
            lines.Add(match.Success && int.TryParse(match.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line)
                ? new StackFrameLine(text, match.Groups["file"].Value, line)
                : new StackFrameLine(text, null, 0));
        }

        return lines;
    }

    /// <summary>The first frame with a location in one of <paramref name="files"/>, else the first with any location.</summary>
    public static StackFrameLine? FirstUserFrame(IReadOnlyList<StackFrameLine> frames, Func<string, bool> isUserFile) =>
        frames.FirstOrDefault(f => f.File is { } file && isUserFile(file)) ?? frames.FirstOrDefault(f => f.File is not null);

    [GeneratedRegex(@"\sin\s(?<file>.+?):line\s(?<line>\d+)\s*$")]
    private static partial Regex Location();
}
