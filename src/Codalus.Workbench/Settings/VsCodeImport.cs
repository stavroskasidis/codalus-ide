using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Workbench.Settings;

/// <summary>What an import takes and what it leaves, with why.</summary>
public sealed record ImportResult<T>(IReadOnlyList<T> Imported, IReadOnlyList<(string Name, string Reason)> Skipped);

/// <summary>
/// Importing VS Code's user settings and key bindings (M23a). Its <c>settings.json</c> keys that are ours (with valid
/// values) come over as they are; so do its color customizations, in the same shape. <c>workbench.colorTheme</c> maps
/// VS Code's built-in themes to ours. Its <c>keybindings.json</c> has our shape: entries whose command exists here come
/// over. Nothing else is touched, and every skipped key says why.
/// </summary>
public static class VsCodeImport
{
    private static readonly JsonDocumentOptions s_jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly string[] s_products = ["Code", "Code - Insiders", "VSCodium"];

    /// <summary>VS Code's built-in themes by name, as ours.</summary>
    public static IReadOnlyDictionary<string, string> Themes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Default Dark Modern"] = "dark", ["Default Dark+"] = "dark", ["Dark+"] = "dark", ["Dark (Visual Studio)"] = "dark", ["Visual Studio Dark"] = "dark",
        ["Default Light Modern"] = "light", ["Default Light+"] = "light", ["Light+"] = "light", ["Light (Visual Studio)"] = "light", ["Visual Studio Light"] = "light",
        ["Default High Contrast"] = "highContrast", ["Default High Contrast Light"] = "highContrast",
    };

    /// <summary>VS Code's user folder on this machine (VS Code, then Insiders, then VSCodium), or null.</summary>
    public static string? UserFolder(PlatformKind platform, Func<string, string?> environment)
    {
        var home = environment("HOME") ?? environment("USERPROFILE") ?? string.Empty;
        var bases = platform switch
        {
            PlatformKind.Windows => [environment("APPDATA") ?? Path.Combine(home, "AppData", "Roaming")],
            PlatformKind.MacOS => [Path.Combine(home, "Library", "Application Support")],
            _ => new[] { environment("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config") },
        };
        return bases.SelectMany(b => s_products.Select(product => Path.Combine(b, product, "User")))
            .FirstOrDefault(folder => File.Exists(Path.Combine(folder, "settings.json")) || File.Exists(Path.Combine(folder, "keybindings.json")));
    }

    /// <summary>The settings of VS Code's <c>settings.json</c> text that are ours, with valid values.</summary>
    public static ImportResult<(string Key, JsonElement Value)> Settings(string json, Func<string, SettingDescriptor?> find)
    {
        var imported = new List<(string, JsonElement)>();
        var skipped = new List<(string, string)>();
        using var document = JsonDocument.Parse(json, s_jsonc);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new([], [("settings.json", "isn't a JSON object")]);
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value.Clone();
            if (property.Name == "workbench.colorTheme")
            {
                if (value.ValueKind == JsonValueKind.String && Themes.TryGetValue(value.GetString()!, out var theme))
                {
                    imported.Add((property.Name, JsonSerializer.SerializeToElement(theme, SettingsJsonContext.Default.String)));
                }
                else
                {
                    skipped.Add((property.Name, $"the theme {value} isn't one of Codalus IDE's (Blue, Light, Dark, High Contrast)"));
                }

                continue;
            }

            if (SettingsService.LanguageIds(property.Name) is not null && value.ValueKind == JsonValueKind.Object)
            {
                // Language-specific settings: the block with the settings that are ours and valid.
                var block = new System.Text.Json.Nodes.JsonObject();
                foreach (var inner in value.EnumerateObject())
                {
                    if (find(inner.Name) is not { } innerDescriptor)
                    {
                        skipped.Add(($"{property.Name} {inner.Name}", "not a Codalus IDE setting"));
                    }
                    else if (innerDescriptor.Validate(inner.Value) is { } innerError)
                    {
                        skipped.Add(($"{property.Name} {inner.Name}", innerError));
                    }
                    else
                    {
                        block[inner.Name] = System.Text.Json.Nodes.JsonNode.Parse(inner.Value.GetRawText());
                    }
                }

                if (block.Count > 0)
                {
                    using var written = JsonDocument.Parse(block.ToJsonString());
                    imported.Add((property.Name, written.RootElement.Clone()));
                }
            }
            else if (property.Name.StartsWith('['))
            {
                skipped.Add((property.Name, "not a Codalus IDE setting"));
            }
            else if (find(property.Name) is not { } descriptor)
            {
                skipped.Add((property.Name, "not a Codalus IDE setting"));
            }
            else if (descriptor.Validate(value) is { } error)
            {
                skipped.Add((property.Name, error));
            }
            else
            {
                imported.Add((property.Name, value));
            }
        }

        return new(imported, skipped);
    }

    /// <summary>The entries of VS Code's <c>keybindings.json</c> text whose command exists here (and their removals, <c>-command</c>).</summary>
    public static ImportResult<JsonElement> Keybindings(string json, Func<string, bool> commandExists)
    {
        var imported = new List<JsonElement>();
        var skipped = new List<(string, string)>();
        using var document = JsonDocument.Parse(json, s_jsonc);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new([], [("keybindings.json", "isn't a JSON array")]);
        }

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
            {
                skipped.Add((entry.GetRawText(), "not a key binding"));
                continue;
            }

            var name = command.GetString()!;
            if (commandExists(name.TrimStart('-')))
            {
                imported.Add(entry.Clone());
            }
            else
            {
                skipped.Add((name, "no such command here"));
            }
        }

        return new(imported, skipped);
    }
}
