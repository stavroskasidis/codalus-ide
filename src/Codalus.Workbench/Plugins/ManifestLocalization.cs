using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Plugins;

/// <summary>
/// Plugin manifest localization (M23b), VS Code's convention: a manifest string that is a whole <c>%key%</c> is
/// replaced by that key's text from <c>codalus-plugin.nls.&lt;locale&gt;.json</c> (or its language's), else
/// <c>codalus-plugin.nls.json</c> (English) next to the manifest. Titles, descriptions, setting texts and anything else
/// a manifest shows can be translated this way; a key with no text stays as written.
/// </summary>
public static class ManifestLocalization
{
    public const string DefaultFile = "codalus-plugin.nls.json";

    private static readonly JsonSerializerOptions s_relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The manifest text with its placeholders replaced, for <paramref name="catalog"/>'s language.</summary>
    public static string Apply(string manifest, string directory, LocalizationCatalog catalog)
    {
        if (!manifest.Contains('%', StringComparison.Ordinal))
        {
            return manifest;
        }

        var strings = Read(Path.Combine(directory, DefaultFile));
        if (catalog.Locale != "en" && !catalog.IsPseudo)
        {
            var language = catalog.Locale.Split('-')[0];
            foreach (var locale in language == catalog.Locale ? [language] : new[] { language, catalog.Locale })
            {
                foreach (var (key, text) in Read(Path.Combine(directory, $"codalus-plugin.nls.{locale}.json")))
                {
                    strings[key] = text;
                }
            }
        }

        if (strings.Count == 0 || JsonNode.Parse(manifest, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not { } root)
        {
            return manifest;
        }

        Replace(root, strings, catalog.IsPseudo);
        return root.ToJsonString(s_relaxed);
    }

    private static void Replace(JsonNode node, Dictionary<string, string> strings, bool pseudo)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (Text(child, strings, pseudo) is { } text)
                    {
                        obj[name] = text;
                    }
                    else if (child is not null)
                    {
                        Replace(child, strings, pseudo);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (Text(array[i], strings, pseudo) is { } text)
                    {
                        array[i] = text;
                    }
                    else if (array[i] is { } child)
                    {
                        Replace(child, strings, pseudo);
                    }
                }

                break;
        }
    }

    private static string? Text(JsonNode? node, Dictionary<string, string> strings, bool pseudo) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 2 && s[0] == '%' && s[^1] == '%'
            && strings.TryGetValue(s[1..^1], out var text)
            ? pseudo ? Localizer.Pseudo(text) : text
            : null;

    private static Dictionary<string, string> Read(string path)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var property in document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                {
                    strings[property.Name] = property.Value.GetString()!;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken translation leaves the placeholders (or the other files' texts).
        }

        return strings;
    }
}
