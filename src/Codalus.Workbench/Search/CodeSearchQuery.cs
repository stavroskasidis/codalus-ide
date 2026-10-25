using System.Globalization;

namespace Codalus.Workbench.Search;

/// <summary>Code Search's chips (M47): all kinds, or one of them. A prefix typed in the box (<c>f:</c>, <c>t:</c>, <c>m:</c>, <c>x:</c>) picks one too.</summary>
public enum CodeSearchFilter
{
    All,
    Files,
    Types,
    Members,
    Text,
}

/// <summary>Where Code Search looks: the whole solution, the active document's project, or the active document.</summary>
public enum CodeSearchScope
{
    EntireSolution,
    CurrentProject,
    CurrentDocument,
}

/// <summary>What a result is.</summary>
public enum CodeSearchKind
{
    File,
    Type,
    Member,
    Text,

    /// <summary>A line of the active document (<c>:42</c>).</summary>
    Line,
}

/// <summary>
/// A parsed Code Search query: the filter (from a prefix, else the chip), the text to match, and for <c>:</c> a line
/// (and column) of the active document. Prefixes are the classic IDE's: <c>f:</c> files, <c>t:</c> types, <c>m:</c>
/// members, <c>x:</c> text, <c>:</c> a line (<c>:42</c>, <c>:42:7</c> or <c>:42,7</c>).
/// </summary>
public sealed record CodeSearchQuery(CodeSearchFilter Filter, string Text)
{
    /// <summary>The prefix that set the filter, when one did (the chip then shows it).</summary>
    public bool FromPrefix { get; init; }

    /// <summary>A one-based line for <c>:</c>.</summary>
    public int? Line { get; init; }

    /// <summary>A one-based column for <c>:line:column</c>.</summary>
    public int? Column { get; init; }

    public bool IsLine => Line is not null;

    public bool IsEmpty => Text.Length == 0 && Line is null;

    /// <summary>Whether results of <paramref name="kind"/> are wanted.</summary>
    public bool Wants(CodeSearchKind kind) => Filter switch
    {
        CodeSearchFilter.Files => kind == CodeSearchKind.File,
        CodeSearchFilter.Types => kind == CodeSearchKind.Type,
        CodeSearchFilter.Members => kind == CodeSearchKind.Member,
        CodeSearchFilter.Text => kind == CodeSearchKind.Text,
        _ => kind != CodeSearchKind.Line,
    };

    /// <summary>The prefix for a filter (<c>f:</c>…), or empty for <see cref="CodeSearchFilter.All"/>.</summary>
    public static string PrefixOf(CodeSearchFilter filter) => filter switch
    {
        CodeSearchFilter.Files => "f:",
        CodeSearchFilter.Types => "t:",
        CodeSearchFilter.Members => "m:",
        CodeSearchFilter.Text => "x:",
        _ => string.Empty,
    };

    /// <summary>Parses what was typed; <paramref name="chip"/> is the selected chip, which a prefix overrides.</summary>
    public static CodeSearchQuery Parse(string? input, CodeSearchFilter chip = CodeSearchFilter.All)
    {
        var text = (input ?? string.Empty).TrimStart();
        if (text.StartsWith(':'))
        {
            var parts = text[1..].Split([':', ','], 2, StringSplitOptions.TrimEntries);
            var line = int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var l) && l > 0 ? l : (int?)null;
            var column = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : (int?)null;
            return new CodeSearchQuery(chip, string.Empty) { Line = line ?? 0, Column = column, FromPrefix = true };
        }

        if (text.Length >= 2 && text[1] == ':' && FilterOf(text[0]) is { } filter)
        {
            return new CodeSearchQuery(filter, text[2..].Trim()) { FromPrefix = true };
        }

        return new CodeSearchQuery(chip, text.Trim());
    }

    private static CodeSearchFilter? FilterOf(char c) => char.ToLowerInvariant(c) switch
    {
        'f' => CodeSearchFilter.Files,
        't' => CodeSearchFilter.Types,
        'm' => CodeSearchFilter.Members,
        'x' => CodeSearchFilter.Text,
        _ => null,
    };
}
