namespace Codalus.Workbench.Ai;

/// <summary>
/// A change an agent reports, as lines to show (M48): the lines it keeps around the change for context, the old lines it
/// removes and the new lines it adds. Not a minimal diff: the common start and end are trimmed, and what's between them
/// is shown removed, then added, which is what a reader needs to see one tool call's edit.
/// </summary>
public static class AiDiffText
{
    /// <summary>The lines: <c>' '</c> context, <c>'-'</c> removed, <c>'+'</c> added; at most <paramref name="limit"/>.</summary>
    public static IReadOnlyList<(char Kind, string Text)> Lines(string? oldText, string newText, int context = 2, int limit = 400)
    {
        var before = Split(oldText ?? "");
        var after = Split(newText);
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start])
        {
            start++;
        }

        var end = 0;
        while (end < before.Length - start && end < after.Length - start && before[^(end + 1)] == after[^(end + 1)])
        {
            end++;
        }

        var lines = new List<(char, string)>();
        for (var i = Math.Max(0, start - context); i < start; i++)
        {
            lines.Add((' ', before[i]));
        }

        for (var i = start; i < before.Length - end; i++)
        {
            lines.Add(('-', before[i]));
        }

        for (var i = start; i < after.Length - end; i++)
        {
            lines.Add(('+', after[i]));
        }

        for (var i = after.Length - end; i < Math.Min(after.Length, after.Length - end + context); i++)
        {
            lines.Add((' ', after[i]));
        }

        if (lines.Count > limit)
        {
            lines = lines.Take(limit).ToList();
            lines.Add((' ', "…"));
        }

        return lines;
    }

    private static string[] Split(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }
}
