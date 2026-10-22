using System.Globalization;

namespace Codalus.Shell.ViewModels;

/// <summary>A color of the color drop-down: its name as the cell writes it, and its swatch.</summary>
public sealed record PropertyColorChoice(string Name, Avalonia.Media.Color Color);

/// <summary>
/// The color drop-down's three tabs (#68, D41 question 18), as the classic IDE's color editor has them: Custom (the 48
/// basic colors and 16 slots of the session), Web (the named colors, Transparent first, then by hue, saturation and
/// brightness) and System (the system colors, alphabetically).
/// </summary>
public static class PropertyColors
{
    public const string CustomTab = "Custom";
    public const string WebTab = "Web";
    public const string SystemTab = "System";

    /// <summary>The Custom tab's 48 basic colors, row by row.</summary>
    public static IReadOnlyList<Avalonia.Media.Color> Basic { get; } = [.. new[]
    {
        "FFFFFF", "FFC0C0", "FFE0C0", "FFFFC0", "C0FFC0", "C0FFFF", "C0C0FF", "FFC0FF", "E0E0E0", "FF8080", "FFC080", "FFFF80", "80FF80", "80FFFF", "8080FF", "FF80FF",
        "C0C0C0", "FF0000", "FF8000", "FFFF00", "00FF00", "00FFFF", "0000FF", "FF00FF", "808080", "C00000", "C04000", "C0C000", "00C000", "00C0C0", "0000C0", "C000C0",
        "404040", "800000", "804000", "808000", "008000", "008080", "000080", "800080", "000000", "400000", "804040", "404000", "004000", "004040", "000040", "400040",
    }.Select(h => Avalonia.Media.Color.FromRgb(byte.Parse(h[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture), byte.Parse(h[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture), byte.Parse(h[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)))];

    /// <summary>The number of custom slots a grid keeps for its session.</summary>
    public const int CustomSlots = 16;

    private static IEnumerable<System.Drawing.Color> Known(IEnumerable<string>? names) =>
        (names?.ToList() is { Count: > 0 } list ? list : Enum.GetNames<System.Drawing.KnownColor>().ToList())
            .Select(System.Drawing.Color.FromName).Where(c => c.IsKnownColor).DistinctBy(c => c.Name);

    private static PropertyColorChoice Choice(System.Drawing.Color color) => new(color.Name, Avalonia.Media.Color.FromUInt32((uint)color.ToArgb()));

    /// <summary>The Web tab: the named colors that aren't system colors, Transparent first, then by hue, saturation and brightness.</summary>
    public static IReadOnlyList<PropertyColorChoice> WebColors(IEnumerable<string>? names = null) =>
        [.. Known(names).Where(c => !c.IsSystemColor).OrderBy(c => c.A).ThenBy(c => c.GetHue()).ThenBy(c => c.GetSaturation()).ThenBy(c => c.GetBrightness()).Select(Choice)];

    /// <summary>The System tab: the system colors, by name.</summary>
    public static IReadOnlyList<PropertyColorChoice> SystemColors(IEnumerable<string>? names = null) =>
        [.. Known(names).Where(c => c.IsSystemColor).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(Choice)];

    /// <summary>The tab a value opens on: System for a system color's name, Web for another name, Custom for numbers.</summary>
    public static string TabOf(string value) =>
        System.Drawing.Color.FromName(value.Trim()) is { IsKnownColor: true } known ? (known.IsSystemColor ? SystemTab : WebTab) : CustomTab;

    /// <summary>A custom color as the cell writes it: <c>255, 128, 0</c>.</summary>
    public static string Text(Avalonia.Media.Color color) => string.Create(CultureInfo.InvariantCulture, $"{color.R}, {color.G}, {color.B}");
}

/// <summary>The text editors' rules (#68): CRLF line endings, as the classic editors write them, and a collection's lines.</summary>
public static class PropertyText
{
    /// <summary>
    /// Every line ending as CRLF: the classic multi-line string editor writes CRLF, and a Windows Forms multi-line text
    /// box breaks lines only on CRLF (an Avalonia text box inserts "\n" on Linux and macOS).
    /// </summary>
    public static string WithCrLf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>A String Collection Editor's items: a line each, split on any line ending, empty lines kept, one trailing empty line dropped.</summary>
    public static IReadOnlyList<string> Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}

/// <summary>A menu item's shortcut keys as KeysConverter writes them (#68): <c>Ctrl+Shift+S</c>, or <c>None</c>.</summary>
public static class ShortcutKeysText
{
    public static string Compose(bool ctrl, bool shift, bool alt, string? key)
    {
        if (string.IsNullOrEmpty(key) || key == "None")
        {
            return "None";
        }

        var parts = new List<string>();
        if (ctrl)
        {
            parts.Add("Ctrl");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        parts.Add(key);
        return string.Join('+', parts);
    }

    public static (bool Ctrl, bool Shift, bool Alt, string Key) Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool Has(string modifier) => parts.Any(p => string.Equals(p, modifier, StringComparison.OrdinalIgnoreCase) || (modifier == "Ctrl" && string.Equals(p, "Control", StringComparison.OrdinalIgnoreCase)));
        var key = parts.LastOrDefault(p => p is not ("Ctrl" or "Control" or "Shift" or "Alt")) ?? "None";
        return (Has("Ctrl"), Has("Shift"), Has("Alt"), key);
    }
}

/// <summary>A date drop-down's value (#68, D41 question 20): a picked day keeps the value's time, written as DateTimeConverter writes it.</summary>
public static class PropertyDates
{
    public static DateTime? Parse(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value) ? value : null;

    /// <summary>The day picked with the old value's time; invariant, as the host's converter reads it.</summary>
    public static string WithDay(string old, DateTime day)
    {
        var time = Parse(old)?.TimeOfDay ?? TimeSpan.Zero;
        var value = day.Date + time;
        return time == TimeSpan.Zero ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
    }
}
