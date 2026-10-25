using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Paste JSON as Classes (M12b): C# classes for a JSON document, as the classic IDE generates them. Objects become
/// classes with a property per member (names in PascalCase, with <c>[JsonPropertyName]</c> where they differ), arrays
/// become <c>List&lt;T&gt;</c> of the merged element type, numbers <c>int</c>/<c>long</c>/<c>double</c>, and nulls
/// make the type nullable.
/// </summary>
public static class JsonClassGenerator
{
    public static string Generate(string json, string rootName)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var classes = new List<(string Name, List<(string Json, string Type)> Properties)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            TypeOf(Merge(root.EnumerateArray().ToList()), rootName, classes, names);
        }
        else
        {
            TypeOf(root, rootName, classes, names);
        }

        var builder = new StringBuilder();
        foreach (var (name, properties) in classes)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine(CultureInfo.InvariantCulture, $"public class {name}");
            builder.AppendLine("{");
            var first = true;
            foreach (var (jsonName, type) in properties)
            {
                if (!first)
                {
                    builder.AppendLine();
                }

                first = false;
                var property = Pascal(jsonName);
                if (property != jsonName)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"    [System.Text.Json.Serialization.JsonPropertyName(\"{jsonName.Replace("\"", "\\\"", StringComparison.Ordinal)}\")]");
                }

                builder.AppendLine(CultureInfo.InvariantCulture, $"    public {type} {property} {{ get; set; }}");
            }

            builder.AppendLine("}");
        }

        return builder.ToString();
    }

    /// <summary>The C# type of a value; objects add (and reuse by name) a class.</summary>
    private static string TypeOf(JsonElement? value, string nameHint, List<(string Name, List<(string Json, string Type)> Properties)> classes, HashSet<string> names)
    {
        if (value is not { } element)
        {
            return "object?";
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.TryGetDateTimeOffset(out _) && element.GetString()!.Length >= 10 ? "DateTimeOffset" : element.TryGetGuid(out _) ? "Guid" : "string";
            case JsonValueKind.Number:
                return element.TryGetInt32(out _) ? "int" : element.TryGetInt64(out _) ? "long" : "double";
            case JsonValueKind.True or JsonValueKind.False:
                return "bool";
            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                var itemType = items.Count == 0 ? "object" : TypeOf(Merge(items), Singular(nameHint), classes, names);
                return $"List<{itemType}>";
            case JsonValueKind.Object:
                var name = Pascal(nameHint);
                var unique = name;
                for (var n = 2; !names.Add(unique); n++)
                {
                    unique = name + n.ToString(CultureInfo.InvariantCulture);
                }

                var properties = new List<(string Json, string Type)>();
                classes.Add((unique, properties));
                foreach (var member in element.EnumerateObject())
                {
                    properties.Add((member.Name, member.Value.ValueKind == JsonValueKind.Null ? "object?" : TypeOf(member.Value, member.Name, classes, names)));
                }

                return unique;
            default:
                return "object?";
        }
    }

    /// <summary>The elements of an array as one representative: objects merged member by member (a member missing or null in one makes it nullable).</summary>
    private static JsonElement? Merge(List<JsonElement> items)
    {
        var nonNull = items.Where(i => i.ValueKind != JsonValueKind.Null).ToList();
        if (nonNull.Count == 0)
        {
            return null;
        }

        if (nonNull.Any(i => i.ValueKind != JsonValueKind.Object))
        {
            // Mixed numbers: the widest wins.
            if (nonNull.All(i => i.ValueKind == JsonValueKind.Number))
            {
                return nonNull.FirstOrDefault(i => !i.TryGetInt64(out _)) is { ValueKind: JsonValueKind.Number } real ? real
                    : nonNull.FirstOrDefault(i => !i.TryGetInt32(out _)) is { ValueKind: JsonValueKind.Number } wide ? wide : nonNull[0];
            }

            return nonNull[0];
        }

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in nonNull)
        {
            foreach (var member in item.EnumerateObject())
            {
                if (!merged.TryGetValue(member.Name, out var existing) || existing.ValueKind == JsonValueKind.Null)
                {
                    merged[member.Name] = member.Value;
                }
                else if (existing.ValueKind == JsonValueKind.Number && member.Value.ValueKind == JsonValueKind.Number && Width(member.Value) > Width(existing))
                {
                    merged[member.Name] = member.Value; // 2 and 2.5 make a double
                }
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in merged)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static int Width(JsonElement number) => number.TryGetInt32(out _) ? 0 : number.TryGetInt64(out _) ? 1 : 2;

    private static string Pascal(string name)
    {
        var builder = new StringBuilder();
        var upper = true;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        var result = builder.ToString();
        return result.Length == 0 ? "Property" : char.IsDigit(result[0]) ? "_" + result : result;
    }

    private static string Singular(string name) =>
        name.EndsWith("ies", StringComparison.OrdinalIgnoreCase) ? name[..^3] + "y"
        : name.EndsWith("ses", StringComparison.OrdinalIgnoreCase) ? name[..^2]
        : name.EndsWith('s') && name.Length > 1 ? name[..^1]
        : name + "Item";
}
