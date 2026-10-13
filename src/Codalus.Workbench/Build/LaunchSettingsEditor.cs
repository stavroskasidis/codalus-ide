using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Build;

/// <summary>A launch profile being edited: its name and its JSON object (unknown properties are kept as they are).</summary>
public sealed class EditableLaunchProfile(string name, JsonObject properties)
{
    public string Name { get; set; } = name;

    public JsonObject Properties { get; } = properties;

    public string? Get(string key) => Properties[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public bool GetBool(string key) => Properties[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>Sets a string property; null or empty removes it.</summary>
    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Properties.Remove(key);
        }
        else
        {
            Properties[key] = JsonValue.Create(value);
        }
    }

    /// <summary>Sets a boolean; false removes it (the default), unless <paramref name="keepFalse"/>.</summary>
    public void SetBool(string key, bool value, bool keepFalse = false)
    {
        if (value || keepFalse)
        {
            Properties[key] = JsonValue.Create(value);
        }
        else
        {
            Properties.Remove(key);
        }
    }

    /// <summary>Environment variables as <c>NAME=value</c> lines.</summary>
    public string EnvironmentText
    {
        get => Properties["environmentVariables"] is JsonObject variables
            ? string.Join("\n", variables.Select(v => $"{v.Key}={(v.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : v.Value?.ToJsonString())}"))
            : string.Empty;
        set
        {
            var variables = new JsonObject();
            foreach (var line in (value ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = line.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    variables[line[..equals].Trim()] = JsonValue.Create(line[(equals + 1)..]);
                }
            }

            if (variables.Count == 0)
            {
                Properties.Remove("environmentVariables");
            }
            else
            {
                Properties["environmentVariables"] = variables;
            }
        }
    }
}

/// <summary>
/// Reads and writes <c>Properties/launchSettings.json</c> for the launch profile editor (ADR 0013). Only the
/// <c>profiles</c> member is rewritten; other members (<c>$schema</c>, <c>iisSettings</c>) and their comments stay.
/// </summary>
public static class LaunchSettingsEditor
{
    private static readonly JsonDocumentOptions s_read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static List<EditableLaunchProfile> Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var root = JsonNode.Parse(text, documentOptions: s_read) as JsonObject;
        if (root?["profiles"] is not JsonObject profiles)
        {
            return [];
        }

        return [.. profiles.Where(p => p.Value is JsonObject).Select(p => new EditableLaunchProfile(p.Key, (JsonObject)p.Value!.DeepClone()))];
    }

    /// <summary>The file's new text with <paramref name="profiles"/> (in order) as its <c>profiles</c>.</summary>
    public static string Write(string? existing, IReadOnlyList<EditableLaunchProfile> profiles)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name) || !names.Add(profile.Name))
            {
                throw new ArgumentException(string.IsNullOrWhiteSpace(profile.Name) ? "Every profile needs a name." : $"There are two profiles named '{profile.Name}'.");
            }
        }

        var node = new JsonObject();
        foreach (var profile in profiles)
        {
            node[profile.Name] = profile.Properties.DeepClone();
        }

        // Nested one level below the top-level object, so lines after the first get its indentation.
        var json = Serialize(node).Replace("\n", "\n  ", StringComparison.Ordinal);
        var text = string.IsNullOrWhiteSpace(existing) ? "{\n  \"profiles\": {}\n}\n" : existing;
        return JsoncEditor.SetProperty(text, "profiles", json);
    }

    private static string Serialize(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, IndentSize = 2, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
