using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Codalus.Workbench.Localization;

/// <summary>
/// The UI's strings by key (M23b) in one display language: JSON catalogues of <c>"key": "text"</c>, English
/// (<c>en</c>) being the source that translations (on hosted Weblate, D11) follow. Code passes its English as the
/// fallback, so a string missing from a translation shows in English. Two pseudo-locales make untranslated and clipped
/// text visible: <see cref="Localizer.PseudoLocale"/> accents and pads every string, and
/// <see cref="Localizer.PseudoMirroredLocale"/> also lays the UI out right to left.
/// </summary>
public sealed class LocalizationCatalog
{
    private static readonly string[] s_rightToLeft = ["ar", "fa", "he", "ur", "ps", "yi", "dv", "ug"];

    private readonly Dictionary<string, string> _english;
    private readonly Dictionary<string, string> _strings;

    private LocalizationCatalog(string locale, Dictionary<string, string> english, Dictionary<string, string> strings)
    {
        Locale = locale;
        _english = english;
        _strings = strings;
    }

    public static LocalizationCatalog English { get; } = new("en", new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    /// <summary>The display language, as a BCP 47 name.</summary>
    public string Locale { get; }

    public bool IsPseudo => Locale is Localizer.PseudoLocale or Localizer.PseudoMirroredLocale;

    /// <summary>Whether the UI is laid out right to left.</summary>
    public bool IsRightToLeft =>
        Locale == Localizer.PseudoMirroredLocale || s_rightToLeft.Contains(Locale.Split('-')[0], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="locale"/>'s catalogue over the English one; <paramref name="open"/> opens a catalogue by locale
    /// (null when there is none). A locale without one falls back to its language (<c>pt-BR</c> → <c>pt</c>), then to English.
    /// </summary>
    public static LocalizationCatalog Load(string? locale, Func<string, Stream?> open)
    {
        var english = Read(open("en"));
        locale = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
        if (locale is "en" or Localizer.PseudoLocale or Localizer.PseudoMirroredLocale)
        {
            return new(locale, english, english);
        }

        var strings = Read(open(locale));
        if (strings.Count == 0 && locale.Contains('-', StringComparison.Ordinal))
        {
            strings = Read(open(locale.Split('-')[0]));
        }

        return new(locale, english, strings);
    }

    /// <summary>
    /// The text for <paramref name="key"/>: its translation, else <paramref name="english"/> (the code's own text wins over
    /// the English catalogue, which is generated from it), else the English catalogue's (strings that live only there,
    /// such as the views').
    /// </summary>
    public string Get(string key, string? english = null)
    {
        var translated = Locale != "en" && !IsPseudo ? _strings.GetValueOrDefault(key) : null;
        var text = translated ?? english ?? _english.GetValueOrDefault(key) ?? key;
        return IsPseudo ? Localizer.Pseudo(text) : text;
    }

    private static Dictionary<string, string> Read(Stream? stream)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (stream is null)
        {
            return strings;
        }

        using (stream)
        {
            try
            {
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var property in document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                {
                    strings[property.Name] = property.Value.GetString()!;
                }
            }
            catch (JsonException)
            {
                // A broken translation falls back to English.
            }
        }

        return strings;
    }
}

/// <summary>
/// The process's display language (M23b): <see cref="Load"/> sets it once at startup, before anything registers its
/// titles; a change applies at the next start.
/// </summary>
public static class Localizer
{
    public const string PseudoLocale = "qps-ploc";
    public const string PseudoMirroredLocale = "qps-plocm";

    public static LocalizationCatalog Current { get; private set; } = LocalizationCatalog.English;

    public static string Locale => Current.Locale;

    public static bool IsPseudo => Current.IsPseudo;

    public static bool IsRightToLeft => Current.IsRightToLeft;

    public static void Load(string? locale, Func<string, Stream?> open) => Current = LocalizationCatalog.Load(locale, open);

    /// <inheritdoc cref="LocalizationCatalog.Get"/>
    public static string Get(string key, string? english = null) => Current.Get(key, english);

    /// <summary>Formats a translated pattern (<c>{0}</c> placeholders) with the current culture.</summary>
    public static string Format(string key, string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key, english), args);

    /// <summary>
    /// Pseudo-localized text: letters with accents, about a third longer, between brackets, so text that bypasses the
    /// catalogue, clips or is joined from pieces shows. Placeholders and <c>$(icon)</c> names stay.
    /// </summary>
    public static string Pseudo(string text)
    {
        if (text.Length == 0 || (text.StartsWith('[') && text.EndsWith("·]", StringComparison.Ordinal)))
        {
            return text; // already pseudo (a title translated where it's declared, then again as it registers)
        }

        var builder = new StringBuilder(text.Length * 2);
        builder.Append('[');
        var depth = 0; // inside {0} or $(name)
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{' || (c == '$' && i + 1 < text.Length && text[i + 1] == '('))
            {
                depth++;
            }
            else if ((c == '}' || c == ')') && depth > 0)
            {
                depth--;
                builder.Append(c);
                continue;
            }

            builder.Append(depth > 0 ? c : Accented(c));
        }

        builder.Append(' ').Append('·', Math.Max(1, text.Length / 3)).Append(']');
        return builder.ToString();
    }

    private static char Accented(char c) => c switch
    {
        'a' => 'å', 'b' => 'ƀ', 'c' => 'ç', 'd' => 'ð', 'e' => 'é', 'f' => 'ƒ', 'g' => 'ĝ', 'h' => 'ĥ', 'i' => 'î', 'j' => 'ĵ',
        'k' => 'ķ', 'l' => 'ļ', 'm' => 'ɱ', 'n' => 'ñ', 'o' => 'ö', 'p' => 'þ', 'r' => 'ŕ', 's' => 'š', 't' => 'ţ', 'u' => 'û',
        'w' => 'ŵ', 'y' => 'ý', 'z' => 'ž', 'A' => 'Å', 'C' => 'Ç', 'D' => 'Ð', 'E' => 'É', 'I' => 'Î', 'N' => 'Ñ', 'O' => 'Ö',
        'S' => 'Š', 'U' => 'Û', 'Y' => 'Ý', 'Z' => 'Ž', 'F' => 'Ƒ', 'W' => 'Ŵ', 'G' => 'Ĝ', 'H' => 'Ĥ', 'L' => 'Ļ', 'R' => 'Ŕ', 'T' => 'Ţ',
        _ => c,
    };
}
