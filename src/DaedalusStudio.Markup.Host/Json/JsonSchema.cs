using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DaedalusStudio.Markup.Host.Json;

/// <summary>A schema problem found in a document: the offending value's span and a message.</summary>
internal sealed record SchemaProblem(int Start, int End, string Message, bool IsWarning);

/// <summary>
/// The subset of JSON Schema (drafts 4 to 2020-12) that editing needs (ADR 0016): <c>type</c>, <c>properties</c>,
/// <c>patternProperties</c>, <c>additionalProperties</c>, <c>required</c>, <c>enum</c>, <c>const</c>, <c>items</c>,
/// <c>minimum</c>/<c>maximum</c>, <c>minLength</c>/<c>maxLength</c>, <c>pattern</c>, <c>allOf</c>/<c>anyOf</c>/<c>oneOf</c>,
/// local <c>$ref</c>s and <c>description</c>/<c>markdownDescription</c>. Other keywords are ignored, which errs on the
/// side of fewer diagnostics.
/// </summary>
internal sealed class JsonSchema(JsonElement root)
{
    public JsonElement Root { get; } = root;

    public static JsonSchema Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return new JsonSchema(document.RootElement.Clone());
    }

    /// <summary>Follows a local <c>$ref</c> (<c>#/definitions/x</c>, <c>#/$defs/x</c>); remote references resolve to an empty schema.</summary>
    public JsonElement Resolve(JsonElement schema, int depth = 0)
    {
        while (depth++ < 32 && schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
        {
            var target = reference.GetString()!;
            if (!target.StartsWith('#'))
            {
                return default;
            }

            var current = Root;
            foreach (var segment in target.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = Uri.UnescapeDataString(segment).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    return default;
                }
            }

            schema = current;
        }

        return schema;
    }

    /// <summary>The schema and its <c>allOf</c>/<c>anyOf</c>/<c>oneOf</c> branches, flattened (for completion and hover).</summary>
    public IEnumerable<JsonElement> Expand(JsonElement schema, int depth = 0)
    {
        schema = Resolve(schema);
        if (schema.ValueKind != JsonValueKind.Object || depth > 8)
        {
            yield break;
        }

        yield return schema;
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (schema.TryGetProperty(keyword, out var branches) && branches.ValueKind == JsonValueKind.Array)
            {
                foreach (var branch in branches.EnumerateArray())
                {
                    foreach (var expanded in Expand(branch, depth + 1))
                    {
                        yield return expanded;
                    }
                }
            }
        }

        if (schema.TryGetProperty("then", out var then))
        {
            foreach (var expanded in Expand(then, depth + 1))
            {
                yield return expanded;
            }
        }
    }

    /// <summary>The schemas for a value at <paramref name="path"/> (property names and array indexes).</summary>
    public List<JsonElement> At(IReadOnlyList<string> path)
    {
        List<JsonElement> current = [.. Expand(Root)];
        foreach (var segment in path)
        {
            var next = new List<JsonElement>();
            foreach (var schema in current)
            {
                if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(segment, out var property))
                {
                    next.AddRange(Expand(property));
                    continue;
                }

                var matched = false;
                if (schema.TryGetProperty("patternProperties", out var patterns) && patterns.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pattern in patterns.EnumerateObject())
                    {
                        if (Matches(pattern.Name, segment))
                        {
                            next.AddRange(Expand(pattern.Value));
                            matched = true;
                        }
                    }
                }

                if (!matched && schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
                {
                    next.AddRange(Expand(additional));
                }

                if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) && schema.TryGetProperty("items", out var items))
                {
                    next.AddRange(items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().SelectMany(i => Expand(i)) : Expand(items));
                }
            }

            current = next;
        }

        return current;
    }

    public static string? Description(JsonElement schema) =>
        schema.ValueKind != JsonValueKind.Object ? null
        : schema.TryGetProperty("markdownDescription", out var markdown) && markdown.ValueKind == JsonValueKind.String ? markdown.GetString()
        : schema.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString()
        : null;

    public static IEnumerable<string> Types(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                yield return type.GetString()!;
            }
            else if (type.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String))
                {
                    yield return t.GetString()!;
                }
            }
        }
    }

    // ------------------------------------------------------------------ validation

    public List<SchemaProblem> Validate(JsonNodeInfo value)
    {
        var problems = new List<SchemaProblem>();
        Validate(Root, value, problems, 0);
        return problems;
    }

    private void Validate(JsonElement schema, JsonNodeInfo value, List<SchemaProblem> problems, int depth)
    {
        schema = Resolve(schema);
        if (schema.ValueKind is JsonValueKind.False)
        {
            problems.Add(new SchemaProblem(value.Start, value.End, "This value isn't allowed here.", false));
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object || depth > 64)
        {
            return;
        }

        foreach (var all in Branches(schema, "allOf"))
        {
            Validate(all, value, problems, depth + 1);
        }

        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            var branches = Branches(schema, keyword);
            if (branches.Count > 0 && !branches.Any(b => Valid(b, value, depth)))
            {
                // Report the closest branch's problems: the one with the fewest.
                var best = branches.Select(b => { var p = new List<SchemaProblem>(); Validate(b, value, p, depth + 1); return p; }).OrderBy(p => p.Count).First();
                problems.AddRange(best.Count > 0 ? best : [new SchemaProblem(value.Start, value.End, "The value doesn't match any of the allowed forms.", false)]);
            }
        }

        var types = Types(schema).ToList();
        if (types.Count > 0 && !types.Any(t => t == value.TypeName || (t == "number" && value.TypeName == "integer")))
        {
            problems.Add(new SchemaProblem(value.Start, value.End, $"Expected {string.Join(" or ", types)}, not {value.TypeName}.", false));
            return;
        }

        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array && !values.EnumerateArray().Any(v => Equal(v, value)))
        {
            var allowed = string.Join(", ", values.EnumerateArray().Take(12).Select(v => v.GetRawText()));
            problems.Add(new SchemaProblem(value.Start, value.End, $"Value not allowed. Allowed: {allowed}{(values.GetArrayLength() > 12 ? ", …" : string.Empty)}.", false));
        }

        if (schema.TryGetProperty("const", out var constant) && !Equal(constant, value))
        {
            problems.Add(new SchemaProblem(value.Start, value.End, $"Expected {constant.GetRawText()}.", false));
        }

        switch (value.Kind)
        {
            case JsonKind.Object:
                ValidateObject(schema, value, problems, depth);
                break;
            case JsonKind.Array:
                if (schema.TryGetProperty("items", out var items))
                {
                    for (var i = 0; i < value.Items.Count; i++)
                    {
                        var itemSchema = items.ValueKind == JsonValueKind.Array ? (i < items.GetArrayLength() ? items[i] : default) : items;
                        if (itemSchema.ValueKind != JsonValueKind.Undefined)
                        {
                            Validate(itemSchema, value.Items[i], problems, depth + 1);
                        }
                    }
                }

                break;
            case JsonKind.String when value.Text is { } text:
                if (schema.TryGetProperty("minLength", out var minLength) && minLength.TryGetInt32(out var min) && text.Length < min)
                {
                    problems.Add(new SchemaProblem(value.Start, value.End, $"At least {min} characters.", false));
                }

                if (schema.TryGetProperty("maxLength", out var maxLength) && maxLength.TryGetInt32(out var max) && text.Length > max)
                {
                    problems.Add(new SchemaProblem(value.Start, value.End, $"At most {max} characters.", false));
                }

                if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String && !Matches(pattern.GetString()!, text))
                {
                    problems.Add(new SchemaProblem(value.Start, value.End, schema.TryGetProperty("patternErrorMessage", out var custom) && custom.ValueKind == JsonValueKind.String
                        ? custom.GetString()! : $"Doesn't match the pattern {pattern.GetString()}.", false));
                }

                break;
            case JsonKind.Number when value.Number is { } number:
                if (schema.TryGetProperty("minimum", out var minimum) && minimum.TryGetDouble(out var lower) && number < lower)
                {
                    problems.Add(new SchemaProblem(value.Start, value.End, $"The minimum is {minimum.GetRawText()}.", false));
                }

                if (schema.TryGetProperty("maximum", out var maximum) && maximum.TryGetDouble(out var upper) && number > upper)
                {
                    problems.Add(new SchemaProblem(value.Start, value.End, $"The maximum is {maximum.GetRawText()}.", false));
                }

                break;
        }
    }

    private void ValidateObject(JsonElement schema, JsonNodeInfo value, List<SchemaProblem> problems, int depth)
    {
        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var patterns = schema.TryGetProperty("patternProperties", out var pp) && pp.ValueKind == JsonValueKind.Object ? pp : default;
        var additional = schema.TryGetProperty("additionalProperties", out var ap) ? ap : default;
        foreach (var member in value.Members)
        {
            var matched = false;
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(member.Key, out var propertySchema))
            {
                Validate(propertySchema, member.Value, problems, depth + 1);
                matched = true;
            }

            if (patterns.ValueKind == JsonValueKind.Object)
            {
                foreach (var pattern in patterns.EnumerateObject().Where(pattern => Matches(pattern.Name, member.Key)))
                {
                    Validate(pattern.Value, member.Value, problems, depth + 1);
                    matched = true;
                }
            }

            if (!matched)
            {
                if (additional.ValueKind == JsonValueKind.False)
                {
                    problems.Add(new SchemaProblem(member.KeyStart, member.KeyEnd, $"Property '{member.Key}' isn't allowed.", true));
                }
                else if (additional.ValueKind == JsonValueKind.Object)
                {
                    Validate(additional, member.Value, problems, depth + 1);
                }
            }
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            var missing = required.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String && value.Member(r.GetString()!) is null).Select(r => r.GetString()!).ToList();
            if (missing.Count > 0)
            {
                problems.Add(new SchemaProblem(value.Start, Math.Min(value.End, value.Start + 1), $"Missing property {string.Join(", ", missing.Select(m => "'" + m + "'"))}.", true));
            }
        }
    }

    private bool Valid(JsonElement schema, JsonNodeInfo value, int depth)
    {
        var problems = new List<SchemaProblem>();
        Validate(schema, value, problems, depth + 1);
        return problems.Count == 0;
    }

    private static List<JsonElement> Branches(JsonElement schema, string keyword) =>
        schema.TryGetProperty(keyword, out var branches) && branches.ValueKind == JsonValueKind.Array ? [.. branches.EnumerateArray()] : [];

    private static bool Equal(JsonElement expected, JsonNodeInfo value) => expected.ValueKind switch
    {
        JsonValueKind.String => value.Kind == JsonKind.String && value.Text == expected.GetString(),
        JsonValueKind.Number => value.Kind == JsonKind.Number && expected.TryGetDouble(out var n) && value.Number == n,
        JsonValueKind.True => value.Kind == JsonKind.True,
        JsonValueKind.False => value.Kind == JsonKind.False,
        JsonValueKind.Null => value.Kind == JsonKind.Null,
        _ => true, // objects and arrays in enum: not compared
    };

    private static bool Matches(string pattern, string text)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(50));
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return true;
        }
    }
}
