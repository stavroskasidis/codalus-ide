using System.Text;
using Codalus.EditorConfig;

namespace Codalus.Workbench.Settings;

/// <summary>An option the EditorConfig settings page shows: its key, what it does, its group and its values (null: free text).</summary>
public sealed record EditorConfigOption(string Key, string Description, string Group, IReadOnlyList<string>? Values);

/// <summary>
/// The EditorConfig settings page's model (deferred from M12b): an <c>.editorconfig</c> file's sections and their keys,
/// with edits applied to the text itself, so comments, blank lines, order and unknown keys stay as they were. A key set
/// in a section replaces its line; a new key goes after the section's last key; a new section goes at the end.
/// </summary>
public sealed class EditorConfigFile
{
    private readonly List<string> _lines;
    private readonly string _newline;

    private EditorConfigFile(List<string> lines, string newline)
    {
        _lines = lines;
        _newline = newline;
    }

    /// <summary>The options the page offers, from the catalog the markup host completes, in groups.</summary>
    public static IReadOnlyList<EditorConfigOption> Options { get; } =
    [
        .. EditorConfigCatalog.Keys.Select(k => new EditorConfigOption(k.Name, k.Description, GroupOf(k.Name), k.Values)),
    ];

    public static EditorConfigFile Parse(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1); // the final newline
        }

        return new EditorConfigFile(lines, newline);
    }

    /// <summary>The section headers (<c>*</c>, <c>*.cs</c>, <c>*.{cs,vb}</c>), in file order.</summary>
    public IReadOnlyList<string> Sections => [.. _lines.Select(Header).OfType<string>()];

    /// <summary>The value a section sets for a key (keys compare case-insensitively), or null.</summary>
    public string? Get(string section, string key)
    {
        foreach (var (_, k, value) in Assignments(section))
        {
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Every key the section sets, with its value.</summary>
    public IReadOnlyDictionary<string, string> Values(string section) =>
        Assignments(section).GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets a key in a section (adding the section when there is none); null removes the key.</summary>
    public void Set(string section, string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var existing = Assignments(section).Where(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(a => a.Line).ToList();
        if (value is null)
        {
            foreach (var line in existing.OrderDescending())
            {
                _lines.RemoveAt(line);
            }

            return;
        }

        var text = $"{key} = {value}";
        if (existing.Count > 0)
        {
            _lines[existing[^1]] = text;
            foreach (var line in existing.SkipLast(1).OrderDescending())
            {
                _lines.RemoveAt(line);
            }

            return;
        }

        if (SectionRange(section) is not { } range)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0)
            {
                _lines.Add(string.Empty);
            }

            _lines.Add($"[{section}]");
            _lines.Add(text);
            return;
        }

        // After the section's last key (or its header), before the blank lines and comments that end it.
        var at = range.Start;
        for (var i = range.Start + 1; i < range.End; i++)
        {
            if (IsAssignment(_lines[i]))
            {
                at = i;
            }
        }

        _lines.Insert(at + 1, text);
    }

    public string Save() => string.Join(_newline, _lines) + _newline;

    /// <summary>The section of a key's settings group: EditorConfig's own, .NET and C# code style, formatting, analyzers.</summary>
    internal static string GroupOf(string key) => key switch
    {
        _ when key.StartsWith("dotnet_diagnostic", StringComparison.Ordinal) || key.StartsWith("dotnet_analyzer", StringComparison.Ordinal)
            || key.StartsWith("dotnet_code_quality", StringComparison.Ordinal) => "Analyzers",
        _ when key.StartsWith("dotnet_style", StringComparison.Ordinal) || key.StartsWith("dotnet_sort", StringComparison.Ordinal)
            || key.StartsWith("dotnet_separate", StringComparison.Ordinal) => ".NET code style",
        _ when key.StartsWith("csharp_new_line", StringComparison.Ordinal) || key.StartsWith("csharp_indent", StringComparison.Ordinal)
            || key.StartsWith("csharp_space", StringComparison.Ordinal) || key.StartsWith("csharp_preserve", StringComparison.Ordinal) => "C# formatting",
        _ when key.StartsWith("csharp_", StringComparison.Ordinal) => "C# code style",
        _ when key.StartsWith("dotnet_", StringComparison.Ordinal) => ".NET code style",
        _ => "Whitespace and files",
    };

    private static string? Header(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1] : null;
    }

    private static bool IsAssignment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0 && trimmed[0] is not ('#' or ';' or '[') && trimmed.Contains('=', StringComparison.Ordinal);
    }

    /// <summary>The lines of a section: its header, then up to the next header.</summary>
    private (int Start, int End)? SectionRange(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (Header(_lines[i]) == section)
            {
                var end = i + 1;
                while (end < _lines.Count && Header(_lines[end]) is null)
                {
                    end++;
                }

                return (i, end);
            }
        }

        return null;
    }

    private IEnumerable<(int Line, string Key, string Value)> Assignments(string section)
    {
        if (SectionRange(section) is not { } range)
        {
            yield break;
        }

        for (var i = range.Start + 1; i < range.End; i++)
        {
            if (IsAssignment(_lines[i]))
            {
                var line = _lines[i];
                var equals = line.IndexOf('=', StringComparison.Ordinal);
                yield return (i, line[..equals].Trim(), StripComment(line[(equals + 1)..]).Trim());
            }
        }
    }

    /// <summary>A value without an inline <c> #</c> or <c> ;</c> comment.</summary>
    private static string StripComment(string value)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is '#' or ';' && i > 0 && char.IsWhiteSpace(value[i - 1]))
            {
                break;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }
}
