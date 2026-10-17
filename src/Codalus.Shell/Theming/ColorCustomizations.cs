using System.Globalization;
using System.Text.Json;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Theming;

/// <summary>The user's colors over a theme: semantic token types, editor elements, and UI tokens (<c>Shell.*</c>), as ARGB.</summary>
public sealed record ColorOverrides(IReadOnlyDictionary<string, uint> Tokens, IReadOnlyDictionary<string, uint> Elements, IReadOnlyDictionary<string, uint> Ui)
{
    public static ColorOverrides None { get; } = new(new Dictionary<string, uint>(), new Dictionary<string, uint>(), new Dictionary<string, uint>());

    public bool IsEmpty => Tokens.Count == 0 && Elements.Count == 0 && Ui.Count == 0;
}

/// <summary>
/// Fonts and Colors (M23a): <c>editor.tokenColorCustomizations</c> and <c>workbench.colorCustomizations</c>, in VS Code's
/// shape so its settings import as they are.
/// <list type="bullet">
/// <item>Entries apply to every theme; an object under <c>"[Dark]"</c> (a theme's title or id) applies to that theme only,
/// over them. A theme family's name (<c>"[Harbor]"</c>, M45) applies to its three variants, under their own.</item>
/// <item>Token colors: <c>semanticTokenColors</c> by token type (<c>"class"</c>, <c>"comment"</c>; a value is a color or
/// <c>{ "foreground": … }</c>), and VS Code's shorthands <c>comments</c>, <c>strings</c>, <c>keywords</c>,
/// <c>numbers</c>, <c>types</c>, <c>functions</c> and <c>variables</c> for groups of them.</item>
/// <item>Workbench colors: the editor's elements by VS Code's ids (<see cref="EditorTheme.Elements"/>), and UI tokens by
/// their resource keys (<c>"Shell.Background"</c>).</item>
/// <item>Colors are <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (VS Code's alpha last); anything else is ignored.</item>
/// </list>
/// </summary>
public static class ColorCustomizations
{
    public const string WorkbenchKey = "workbench.colorCustomizations";
    public const string TokensKey = "editor.tokenColorCustomizations";

    /// <summary>VS Code's token color shorthands, as our semantic token types.</summary>
    public static IReadOnlyDictionary<string, string[]> Shorthands { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["comments"] = ["comment", "xmlDocComment", "razorComment", "razorCommentTransition", "razorCommentStar", "markupComment", "markupCommentPunctuation"],
        ["strings"] = ["string", "stringVerbatim"],
        ["keywords"] = ["keyword", "modifier", "controlKeyword"],
        ["numbers"] = ["number"],
        ["types"] = ["class", "record", "type", "delegate", "struct", "recordStruct", "enum", "interface", "typeParameter"],
        ["functions"] = ["method", "extensionMethod", "function"],
        ["variables"] = ["parameter", "local", "variable"],
    };

    /// <summary>
    /// The overrides for <paramref name="themeId"/> (with its <paramref name="themeTitle"/>, and the theme family it is a
    /// variant of) from the two settings' values.
    /// </summary>
    public static ColorOverrides For(string themeId, string themeTitle, JsonElement workbench, JsonElement tokens, string? family = null)
    {
        var tokenColors = new Dictionary<string, uint>(StringComparer.Ordinal);
        var elements = new Dictionary<string, uint>(StringComparer.Ordinal);
        var ui = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var scope in Scopes(tokens, themeId, themeTitle, family))
        {
            ReadTokens(scope, tokenColors);
        }

        foreach (var scope in Scopes(workbench, themeId, themeTitle, family))
        {
            foreach (var property in scope.EnumerateObject())
            {
                if (Color(property.Value) is not { } argb)
                {
                    continue;
                }

                if (EditorTheme.Elements.Contains(property.Name))
                {
                    elements[property.Name] = argb;
                }
                else if (property.Name.StartsWith("Shell.", StringComparison.Ordinal))
                {
                    ui[property.Name] = argb;
                }
            }
        }

        return tokenColors.Count == 0 && elements.Count == 0 && ui.Count == 0 ? ColorOverrides.None : new ColorOverrides(tokenColors, elements, ui);
    }

    /// <summary>The setting's object, then its object for the family, then for the theme (so the theme's entries win).</summary>
    private static IEnumerable<JsonElement> Scopes(JsonElement value, string themeId, string themeTitle, string? family)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        yield return value;
        if (family is not null)
        {
            foreach (var scope in Named(value, name => string.Equals(name, family, StringComparison.OrdinalIgnoreCase)))
            {
                yield return scope;
            }
        }

        foreach (var scope in Named(value, name => string.Equals(name, themeId, StringComparison.OrdinalIgnoreCase) || string.Equals(name, themeTitle, StringComparison.OrdinalIgnoreCase)))
        {
            yield return scope;
        }

        static IEnumerable<JsonElement> Named(JsonElement value, Func<string, bool> matches)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object && property.Name.Length > 2 && property.Name[0] == '[' && property.Name[^1] == ']'
                    && matches(property.Name[1..^1]))
                {
                    yield return property.Value;
                }
            }
        }
    }

    private static void ReadTokens(JsonElement scope, Dictionary<string, uint> colors)
    {
        foreach (var property in scope.EnumerateObject())
        {
            if (Shorthands.TryGetValue(property.Name, out var types) && Color(property.Value) is { } shorthand)
            {
                foreach (var type in types)
                {
                    colors[type] = shorthand;
                }
            }
        }

        if (scope.TryGetProperty("semanticTokenColors", out var semantic) && semantic.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in semantic.EnumerateObject())
            {
                // "class.declaration" and "*.static" style selectors: the type before the first modifier.
                var type = property.Name.Split('.')[0];
                var value = property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("foreground", out var foreground) ? foreground : property.Value;
                if (TokenTypes.All.Contains(type) && Color(value) is { } argb)
                {
                    colors[type] = argb;
                }
            }
        }
    }

    /// <summary><c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> as ARGB; null for anything else.</summary>
    public static uint? Color(JsonElement value) => value.ValueKind == JsonValueKind.String ? Parse(value.GetString()!) : null;

    public static uint? Parse(string text)
    {
        if (text.Length is not (4 or 7 or 9) || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return text.Length switch
        {
            4 => 0xFF000000 | Expand((value >> 8) & 0xF) << 16 | Expand((value >> 4) & 0xF) << 8 | Expand(value & 0xF),
            7 => 0xFF000000 | value,
            _ => (value & 0xFF) << 24 | value >> 8,
        };

        static uint Expand(uint nibble) => nibble << 4 | nibble;
    }

    /// <summary>ARGB as VS Code writes it: <c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when it isn't opaque.</summary>
    public static string Format(uint argb) =>
        (argb >> 24) == 0xFF
            ? "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture)
            : "#" + ((argb & 0xFFFFFF) << 8 | argb >> 24).ToString("X8", CultureInfo.InvariantCulture);
}
