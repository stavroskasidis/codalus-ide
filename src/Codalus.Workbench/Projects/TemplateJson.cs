using System.Globalization;
using System.Text.Json;

namespace Codalus.Workbench.Projects;

/// <summary>
/// Reading the template engine's JSON files (the template cache, <c>template.json</c>, <c>ide.host.json</c>, the
/// localized strings) as the engine does: comments and trailing commas allowed, a byte order mark skipped, property
/// names in any case (<c>PersistenceScope</c> and <c>persistenceScope</c> are the same key), and booleans written as
/// JSON booleans or as the strings <c>"true"</c> and <c>"false"</c>.
/// </summary>
internal static class TemplateJson
{
    public static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Parses a stream (a byte order mark is skipped); null when it isn't JSON.</summary>
    public static JsonDocument? Parse(Stream stream)
    {
        try
        {
            return JsonDocument.Parse(stream, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses text (a leading byte order mark is skipped); null when it isn't JSON.</summary>
    public static JsonDocument? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text.TrimStart('﻿'), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The property <paramref name="name"/> in any case.</summary>
    public static bool TryGet(this JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>A string property (numbers and booleans as their text), or null.</summary>
    public static string? Text(this JsonElement element, string name) =>
        element.TryGet(name, out var value) ? AsText(value) : null;

    /// <summary>A value as text: a string, a number's or boolean's text; null for anything else.</summary>
    public static string? AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    /// <summary>A boolean property, written as a JSON boolean or as the string <c>"true"</c>/<c>"false"</c> (any case); else null.</summary>
    public static bool? Bool(this JsonElement element, string name) =>
        element.TryGet(name, out var value) ? AsBool(value) : null;

    public static bool? AsBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString()?.Trim(), out var parsed) => parsed,
        _ => null,
    };

    /// <summary>An integer property (a number or numeric text), or null.</summary>
    public static int? Int(this JsonElement element, string name) =>
        element.TryGet(name, out var value) && (value.ValueKind == JsonValueKind.Number ? value.TryGetInt32(out var number) : int.TryParse(AsText(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            ? number
            : null;

    /// <summary>The elements of an array property (none when it isn't an array).</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.TryGet(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    /// <summary>The strings of an array property, or of a single string property.</summary>
    public static IReadOnlyList<string> Strings(this JsonElement element, string name) =>
        element.TryGet(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Array => [.. value.EnumerateArray().Select(AsText).OfType<string>()],
                JsonValueKind.String => [value.GetString()!],
                _ => [],
            }
            : [];

    /// <summary>An object property, or null.</summary>
    public static JsonElement? Object(this JsonElement element, string name) =>
        element.TryGet(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
}
