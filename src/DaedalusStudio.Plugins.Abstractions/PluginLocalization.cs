using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DaedalusStudio.Plugins;

/// <summary>
/// A plugin's own text in the display language (since API 0.2.9, M23c). The strings come from the same files as the
/// manifest's (<c>daedalus-plugin.nls.&lt;locale&gt;.json</c>, then the language's, next to the manifest), and a
/// key no file has shows <c>english</c>. The display language is <see cref="LocaleVariable"/>, which Daedalus Studio
/// sets for the processes it starts.
/// </summary>
public static class PluginLocalization
{
    /// <summary>The display language's environment variable (a BCP 47 name, or a pseudo-language).</summary>
    public const string LocaleVariable = "DAEDALUS_DISPLAY_LOCALE";

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> s_strings = new(StringComparer.Ordinal);

    /// <summary>The text for <paramref name="key"/> in the display language, else <paramref name="english"/>.</summary>
    public static string Localize(this IPluginContext context, string key, string english)
    {
        var locale = Environment.GetEnvironmentVariable(LocaleVariable) ?? string.Empty;
        if (locale is "qps-ploc" or "qps-plocm")
        {
            return Pseudo(english);
        }

        if (locale.Length == 0 || locale == "en")
        {
            return english;
        }

        var strings = s_strings.GetOrAdd(context.Plugin.InstallPath + "|" + locale, _ => Read(context.Plugin.InstallPath, locale));
        return strings.TryGetValue(key, out var text) ? text : english;
    }

    /// <summary><see cref="Localize(IPluginContext, string, string)"/> for a pattern with <c>{0}</c> placeholders.</summary>
    public static string Localize(this IPluginContext context, string key, string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, context.Localize(key, english), args);

    private static Dictionary<string, string> Read(string folder, string locale)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var language = locale.Split('-')[0];
        foreach (var name in language == locale ? [language] : new[] { language, locale })
        {
            var path = Path.Combine(folder, $"daedalus-plugin.nls.{name}.json");
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        strings[property.Name] = property.Value.GetString()!;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A broken translation file: English.
            }
        }

        return strings;
    }

    /// <summary>The pseudo-language's form, as the shell makes it (accents, a third longer, brackets).</summary>
    private static string Pseudo(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        const string Plain = "abcdefghijklmnoprstuwyzACDEFGHILNORSTUWYZ";
        const string Accented = "åƀçðéƒĝĥîĵķļɱñöþŕšţûŵýžÅÇÐÉƑĜĤÎĻÑÖŔŠŢÛŴÝŽ";
        var builder = new StringBuilder("[");
        var depth = 0;
        foreach (var c in text)
        {
            depth += c == '{' ? 1 : c == '}' && depth > 0 ? -1 : 0;
            var index = Plain.IndexOf(c, StringComparison.Ordinal);
            builder.Append(depth > 0 || index < 0 ? c : Accented[index]);
        }

        return builder.Append(' ').Append('·', Math.Max(1, text.Length / 3)).Append(']').ToString();
    }
}
