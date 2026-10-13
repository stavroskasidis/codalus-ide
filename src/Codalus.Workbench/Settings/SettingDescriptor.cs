using System.Globalization;
using System.Text.Json;
using Codalus.Workbench.Commands;

namespace Codalus.Workbench.Settings;

public enum SettingType
{
    String,
    Boolean,
    Integer,
    Number,
    StringArray,

    /// <summary>A JSON object (M23a: the color customizations), edited as JSON.</summary>
    Object,
}

public static class SettingTypes
{
    /// <summary>Maps a manifest type name (JSON Schema subset) to <see cref="SettingType"/>.</summary>
    public static SettingType? Parse(string? name) => name switch
    {
        "string" => SettingType.String,
        "boolean" => SettingType.Boolean,
        "integer" => SettingType.Integer,
        "number" => SettingType.Number,
        "string[]" or "array" => SettingType.StringArray,
        "object" => SettingType.Object,
        _ => null,
    };
}

public enum SettingScope
{
    /// <summary>Only in the user's settings.json.</summary>
    User,

    /// <summary>In user settings, overridable per workspace (.codalus/settings.json).</summary>
    Workspace,
}

/// <summary>
/// The schema of one setting (ADR 0003, a JSON-Schema subset). Settings are flat, dotted keys such as
/// <c>workbench.colorTheme</c>; the part before the last dot is the default category in the Settings UI.
/// </summary>
public sealed record SettingDescriptor(string Key, SettingType Type, JsonElement Default, ContributionSource Source)
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<string>? Enum { get; init; }

    public IReadOnlyList<string>? EnumDescriptions { get; init; }

    /// <summary>
    /// False when the enum's names are found at run time and aren't text to translate (the terminal's shells, M23c):
    /// they stay as given, and the catalogue leaves them out.
    /// </summary>
    public bool TranslateEnumDescriptions { get; init; } = true;

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public SettingScope Scope { get; init; } = SettingScope.Workspace;

    public int Order { get; init; }

    public string DisplayCategory => Category ?? (Key.LastIndexOf('.') is var i and > 0 ? Key[..i] : "General");

    public string DisplayTitle => Title ?? Humanize(Key[(Key.LastIndexOf('.') + 1)..]);

    public static SettingDescriptor Create(string key, string defaultJson, SettingType type, ContributionSource source)
    {
        using var doc = JsonDocument.Parse(defaultJson);
        return new SettingDescriptor(key, type, doc.RootElement.Clone(), source);
    }

    /// <summary>Checks a value against the type, enum and range; returns null when valid.</summary>
    public string? Validate(JsonElement value)
    {
        switch (Type)
        {
            case SettingType.Boolean when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                return "Expected true or false.";
            case SettingType.String when value.ValueKind != JsonValueKind.String:
                return "Expected a string.";
            case SettingType.String when Enum is { Count: > 0 } && !Enum.Contains(value.GetString()!):
                return $"Expected one of: {string.Join(", ", Enum)}.";
            case SettingType.Integer when value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _):
                return "Expected an integer.";
            case SettingType.Number when value.ValueKind != JsonValueKind.Number:
                return "Expected a number.";
            case SettingType.StringArray when value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String):
                return "Expected an array of strings.";
            case SettingType.Object when value.ValueKind != JsonValueKind.Object:
                return "Expected an object.";
        }

        if (Type is SettingType.Integer or SettingType.Number)
        {
            var number = value.GetDouble();
            if (Minimum is { } min && number < min)
            {
                return $"Must be at least {min.ToString(CultureInfo.InvariantCulture)}.";
            }

            if (Maximum is { } max && number > max)
            {
                return $"Must be at most {max.ToString(CultureInfo.InvariantCulture)}.";
            }
        }

        return null;
    }

    private static string Humanize(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(char.ToUpperInvariant(name[0]));
        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                sb.Append(' ');
            }

            sb.Append(name[i]);
        }

        return sb.ToString();
    }
}
