using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Codalus.Plugins.Hosting;

/// <summary>Reading the hosts' JSON without models: absent and null values read as null.</summary>
public static class Json
{
    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    /// <summary>A nested value: <c>e.At("head", "repo", "full_name")</c>.</summary>
    public static JsonElement? At(this JsonElement element, params string[] names)
    {
        JsonElement? current = element;
        foreach (var name in names)
        {
            current = current?.Get(name);
        }

        return current;
    }

    public static string? Str(this JsonElement element, params string[] names) =>
        element.At(names) is { } value
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    public static long? Long(this JsonElement element, params string[] names) =>
        element.At(names) is { } value && (value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out var number) : long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            ? number
            : null;

    public static bool Bool(this JsonElement element, params string[] names) => element.At(names) is { ValueKind: JsonValueKind.True };

    public static DateTimeOffset? Date(this JsonElement element, params string[] names) =>
        element.Str(names) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    /// <summary>The items of an array (the element itself, or a property of it); none when it isn't an array.</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement element, params string[] names) =>
        (names.Length == 0 ? element : element.At(names)) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>A JSON object's text from fields (strings, numbers, booleans, null; nulls left out).</summary>
    public static string Object(params (string Name, object? Value)[] fields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields)
            {
                switch (value)
                {
                    case null:
                        break;
                    case string text:
                        writer.WriteString(name, text);
                        break;
                    case bool flag:
                        writer.WriteBoolean(name, flag);
                        break;
                    case int number:
                        writer.WriteNumber(name, number);
                        break;
                    case long number:
                        writer.WriteNumber(name, number);
                        break;
                    case JsonElement element:
                        writer.WritePropertyName(name);
                        element.WriteTo(writer);
                        break;
                    default:
                        throw new ArgumentException($"Unsupported JSON value for '{name}'.", nameof(fields));
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Parses JSON text into an element that outlives its document.</summary>
    public static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text.Length == 0 ? "null" : text);
        return document.RootElement.Clone();
    }
}
