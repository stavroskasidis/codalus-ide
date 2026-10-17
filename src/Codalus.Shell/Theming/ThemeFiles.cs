using System.Text.Json;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Shell.Theming;

/// <summary>
/// Color themes plugins contribute (plugin API 1.0, M22a): VS Code's theme files. A theme is a base (Light, Dark or High
/// Contrast, from its <c>uiTheme</c>) and these colors over it; the user's color customizations apply over the theme.
/// <list type="bullet">
/// <item><c>colors</c>: VS Code's workbench keys, mapped onto our UI tokens (<see cref="UiKeys"/>) and editor elements.</item>
/// <item><c>tokenColors</c>: TextMate scopes, mapped onto our semantic token types by scope (<see cref="Scopes"/>).</item>
/// <item><c>semanticTokenColors</c>: as in <c>editor.tokenColorCustomizations</c>.</item>
/// </list>
/// </summary>
public static class ThemeFiles
{
    /// <summary>VS Code's workbench color keys and the UI tokens they set (the first key a theme has wins).</summary>
    public static IReadOnlyList<(string Key, string Token)> UiKeys { get; } =
    [
        ("editor.background", "Shell.Editor.Background"),
        ("editor.foreground", "Shell.Editor.Foreground"),
        ("editorLineNumber.foreground", "Shell.Editor.LineNumbers"),
        ("editorIndentGuide.background", "Shell.Editor.Guide"),
        ("editorIndentGuide.background1", "Shell.Editor.Guide"),
        ("editorInlayHint.background", "Shell.Editor.InlayHint.Background"),
        ("editorInlayHint.foreground", "Shell.Editor.InlayHint.Foreground"),
        ("foreground", "Shell.Foreground"),
        ("descriptionForeground", "Shell.MutedForeground"),
        ("editorGroupHeader.tabsBackground", "Shell.Background"),
        ("sideBar.background", "Shell.Panel"),
        ("panel.background", "Shell.Panel"),
        ("panel.border", "Shell.Border"),
        ("sideBar.border", "Shell.Border"),
        ("contrastBorder", "Shell.Border"),
        ("focusBorder", "Shell.Focus"),
        ("textLink.foreground", "Shell.Link"),
        ("list.activeSelectionBackground", "Shell.Selection"),
        ("list.hoverBackground", "Shell.Hover"),
        ("button.background", "Shell.Accent"),
        ("button.foreground", "Shell.AccentForeground"),
        ("titleBar.activeBackground", "Shell.Caption.Active"),
        ("titleBar.activeForeground", "Shell.Caption.Active.Foreground"),
        ("titleBar.inactiveBackground", "Shell.Caption.Inactive"),
        ("titleBar.inactiveForeground", "Shell.Caption.Inactive.Foreground"),
        ("tab.activeBackground", "Shell.Tab.Active"),
        ("tab.activeForeground", "Shell.Tab.Active.Foreground"),
        ("statusBar.background", "Shell.StatusBar"),
        ("statusBar.foreground", "Shell.StatusBar.Foreground"),
        ("statusBar.border", "Shell.StatusBar.Border"),
        ("statusBar.noFolderBackground", "Shell.StatusBar.Empty"),
        ("statusBar.debuggingBackground", "Shell.StatusBar.Debugging"),
        ("statusBarItem.hoverBackground", "Shell.StatusBar.Hover"),
        ("editorWidget.background", "Shell.Overlay"),
        ("quickInput.background", "Shell.Overlay"),
        ("editorWidget.border", "Shell.Overlay.Border"),
        ("widget.border", "Shell.Overlay.Border"),
        ("input.background", "Shell.Search"),
        ("input.border", "Shell.Search.Border"),
        ("errorForeground", "Shell.Error"),
        ("editorInfo.foreground", "Shell.Info"),
        ("editorGutter.modifiedBackground", "Shell.Modified"),
        ("diffEditor.insertedTextBackground", "Shell.Diff.Added"),
        ("diffEditor.removedTextBackground", "Shell.Diff.Removed"),
        ("editor.findMatchHighlightBackground", "Shell.Match"),
    ];

    /// <summary>TextMate scope prefixes and the semantic token types they color (the most specific prefix wins).</summary>
    public static IReadOnlyList<(string Scope, string[] Types)> Scopes { get; } =
    [
        ("comment.block.documentation", ["xmlDocComment"]),
        ("comment", ["comment", "razorComment", "razorCommentTransition", "razorCommentStar", "markupComment", "markupCommentPunctuation"]),
        ("string.regexp", ["regexp"]),
        ("constant.character.escape", ["stringEscape"]),
        ("string", ["string", "stringVerbatim", "markupAttributeValue", "markupAttributeQuote"]),
        ("constant.numeric", ["number"]),
        ("constant.language", ["keyword"]),
        ("constant", ["constant"]),
        ("keyword.control.razor", ["razorTransition", "razorDirective", "razorDirectiveAttribute", "razorDirectiveColon"]),
        ("keyword.control.cshtml", ["razorTransition", "razorDirective"]),
        ("keyword.control", ["controlKeyword"]),
        ("keyword.operator", ["operator"]),
        ("keyword", ["keyword"]),
        ("storage.modifier", ["modifier"]),
        ("storage.type", ["keyword"]),
        ("storage", ["keyword"]),
        ("entity.name.type.interface", ["interface"]),
        ("entity.name.type.enum", ["enum"]),
        ("entity.name.type.struct", ["struct", "recordStruct"]),
        ("entity.name.type.namespace", ["namespace"]),
        ("entity.name.namespace", ["namespace"]),
        ("entity.name.type.parameter", ["typeParameter"]),
        ("entity.name.type", ["type", "class", "record", "delegate", "razorComponentElement", "razorTagHelperElement"]),
        ("entity.name.class", ["class", "record", "razorComponentElement", "razorTagHelperElement"]),
        ("entity.name.tag", ["markupElement"]),
        ("entity.other.attribute-name", ["markupAttribute", "razorComponentAttribute", "razorTagHelperAttribute"]),
        ("support.type", ["type"]),
        ("support.class", ["class"]),
        ("entity.name.function", ["method", "function", "extensionMethod"]),
        ("support.function", ["function"]),
        ("meta.preprocessor", ["preprocessor"]),
        ("variable.parameter", ["parameter"]),
        ("variable.other.property", ["property"]),
        ("variable.other.enummember", ["enumMember"]),
        ("variable.other.constant", ["constant"]),
        ("variable", ["variable", "local"]),
        ("entity.name.label", ["label"]),
        ("punctuation.definition.tag", ["markupTagDelimiter"]),
        ("punctuation.separator.key-value.html", ["markupOperator"]),
        ("punctuation", ["punctuation"]),
    ];

    /// <summary>
    /// A theme file's overrides. <paramref name="root"/> is the file's JSON (with comments allowed by the caller's
    /// reader); <c>include</c> is not followed.
    /// </summary>
    public static ColorOverrides Read(JsonElement root)
    {
        var tokens = new Dictionary<string, uint>(StringComparer.Ordinal);
        var elements = new Dictionary<string, uint>(StringComparer.Ordinal);
        var ui = new Dictionary<string, uint>(StringComparer.Ordinal);
        if (root.TryGetProperty("colors", out var colors) && colors.ValueKind == JsonValueKind.Object)
        {
            var values = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var property in colors.EnumerateObject())
            {
                if (ColorCustomizations.Color(property.Value) is { } argb)
                {
                    values[property.Name] = argb;
                }
            }

            foreach (var element in Editor.EditorTheme.Elements)
            {
                if (values.TryGetValue(element, out var argb))
                {
                    elements[element] = argb;
                }
            }

            foreach (var (key, token) in UiKeys)
            {
                if (!ui.ContainsKey(token) && values.TryGetValue(key, out var argb))
                {
                    ui[token] = argb;
                }
            }
        }

        if (root.TryGetProperty("tokenColors", out var rules) && rules.ValueKind == JsonValueKind.Array)
        {
            // Rules apply in order, later ones over earlier ones; a type takes the most specific matching scope.
            var specificity = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var rule in rules.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object))
            {
                if (!rule.TryGetProperty("settings", out var settings) || !settings.TryGetProperty("foreground", out var foreground)
                    || ColorCustomizations.Color(foreground) is not { } argb)
                {
                    continue;
                }

                foreach (var scope in RuleScopes(rule))
                {
                    foreach (var (prefix, types) in Scopes)
                    {
                        if (scope == prefix || scope.StartsWith(prefix + ".", StringComparison.Ordinal))
                        {
                            foreach (var type in types)
                            {
                                if (prefix.Length >= specificity.GetValueOrDefault(type))
                                {
                                    specificity[type] = prefix.Length;
                                    tokens[type] = argb;
                                }
                            }

                            break;
                        }
                    }
                }
            }
        }

        if (root.TryGetProperty("semanticTokenColors", out var semantic) && semantic.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in semantic.EnumerateObject())
            {
                var type = property.Name.Split('.')[0];
                var value = property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("foreground", out var fg) ? fg : property.Value;
                if (TokenTypes.All.Contains(type) && ColorCustomizations.Color(value) is { } argb)
                {
                    tokens[type] = argb;
                }
            }
        }

        return tokens.Count == 0 && elements.Count == 0 && ui.Count == 0 ? ColorOverrides.None : new ColorOverrides(tokens, elements, ui);
    }

    /// <summary>A rule's scopes: a string (comma-separated) or an array; a scope selector's last part counts.</summary>
    private static IEnumerable<string> RuleScopes(JsonElement rule)
    {
        if (!rule.TryGetProperty("scope", out var scope))
        {
            yield break;
        }

        IEnumerable<string> raw = scope.ValueKind switch
        {
            JsonValueKind.String => scope.GetString()!.Split(','),
            JsonValueKind.Array => scope.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!),
            _ => [],
        };
        foreach (var selector in raw)
        {
            var parts = selector.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                yield return parts[^1];
            }
        }
    }

    /// <summary><paramref name="over"/>'s colors over <paramref name="under"/>'s.</summary>
    public static ColorOverrides Merge(ColorOverrides under, ColorOverrides over)
    {
        if (over.IsEmpty)
        {
            return under;
        }

        if (under.IsEmpty)
        {
            return over;
        }

        static Dictionary<string, uint> Combine(IReadOnlyDictionary<string, uint> a, IReadOnlyDictionary<string, uint> b)
        {
            var result = new Dictionary<string, uint>(a, StringComparer.Ordinal);
            foreach (var (key, value) in b)
            {
                result[key] = value;
            }

            return result;
        }

        return new ColorOverrides(Combine(under.Tokens, over.Tokens), Combine(under.Elements, over.Elements), Combine(under.Ui, over.Ui));
    }
}
