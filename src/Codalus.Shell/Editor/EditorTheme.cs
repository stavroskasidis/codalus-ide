using Avalonia.Media;
using Avalonia.Media.Immutable;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Shell.Editor;

/// <summary>
/// Editor colors for the active theme: semantic token colors (indexed by the token legend) and the editor's own
/// highlights (selection, current line, matches, squiggles). Light and Dark follow common IDE palettes; High Contrast
/// uses the Windows high-contrast black conventions.
/// </summary>
public sealed class EditorTheme
{
    /// <summary>
    /// The editor's own colors by name (VS Code's color ids, so <c>workbench.colorCustomizations</c> and its importer
    /// share them), in the order of the constructor's parameters.
    /// </summary>
    public static IReadOnlyList<string> Elements { get; } =
    [
        "editor.selectionBackground", "editor.lineHighlightBackground", "editor.wordHighlightBackground",
        "editor.wordHighlightStrongBackground", "editorBracketMatch.background", "editorError.foreground",
        "editorWarning.foreground", "editorInfo.foreground", "editorHint.foreground", "editor.findMatchHighlightBackground",
    ];

    private readonly IBrush?[] _tokens;
    private readonly Dictionary<string, uint> _tokenColors;
    private readonly Dictionary<string, uint> _elementColors;

    private EditorTheme(string id, Dictionary<string, uint> tokens, uint selection, uint currentLine, uint highlightRead, uint highlightWrite, uint bracket, uint error, uint warning, uint info, uint hint, uint findMatch)
        : this(id, tokens, Elements.Zip([selection, currentLine, highlightRead, highlightWrite, bracket, error, warning, info, hint, findMatch]).ToDictionary(e => e.First, e => e.Second, StringComparer.Ordinal))
    {
    }

    private EditorTheme(string id, Dictionary<string, uint> tokens, Dictionary<string, uint> elements)
    {
        Id = id;
        _tokenColors = tokens;
        _elementColors = elements;
        _tokens = [.. TokenTypes.All.Select(name => tokens.TryGetValue(name, out var argb) ? new ImmutableSolidColorBrush(argb) : (IBrush?)null)];
        IBrush Brush(int index) => new ImmutableSolidColorBrush(elements[Elements[index]]);
        Selection = Brush(0);
        CurrentLine = Brush(1);
        HighlightRead = Brush(2);
        HighlightWrite = Brush(3);
        Bracket = Brush(4);
        Error = Brush(5);
        Warning = Brush(6);
        Info = Brush(7);
        Hint = Brush(8);
        FindMatch = Brush(9);
    }

    /// <summary>A semantic token type's color (ARGB), or null when the theme leaves it to the text's.</summary>
    public uint? TokenColor(string type) => _tokenColors.TryGetValue(type, out var argb) ? argb : null;

    /// <summary>An editor element's color (ARGB) by its name in <see cref="Elements"/>.</summary>
    public uint ElementColor(string element) => _elementColors[element];

    /// <summary>
    /// This theme with the user's colors over it (Fonts and Colors, M23a): token types and editor elements by name;
    /// unknown names are ignored.
    /// </summary>
    public EditorTheme WithOverrides(IReadOnlyDictionary<string, uint> tokens, IReadOnlyDictionary<string, uint> elements)
    {
        if (tokens.Count == 0 && elements.Count == 0)
        {
            return this;
        }

        var tokenColors = new Dictionary<string, uint>(_tokenColors, StringComparer.Ordinal);
        foreach (var (name, argb) in tokens.Where(t => TokenTypes.All.Contains(t.Key)))
        {
            tokenColors[name] = argb;
        }

        var elementColors = new Dictionary<string, uint>(_elementColors, StringComparer.Ordinal);
        foreach (var (name, argb) in elements.Where(e => elementColors.ContainsKey(e.Key)))
        {
            elementColors[name] = argb;
        }

        return new EditorTheme(Id, tokenColors, elementColors);
    }

    public string Id { get; }

    public IBrush Selection { get; }

    public IBrush CurrentLine { get; }

    public IBrush HighlightRead { get; }

    public IBrush HighlightWrite { get; }

    public IBrush Bracket { get; }

    public IBrush Error { get; }

    public IBrush Warning { get; }

    public IBrush Info { get; }

    public IBrush Hint { get; }

    public IBrush FindMatch { get; }

    public IBrush? Token(int type) => (uint)type < (uint)_tokens.Length ? _tokens[type] : null;

    public static EditorTheme For(string themeId) => themeId switch
    {
        "light" or "blue" => Light,
        "highContrast" => HighContrast,
        _ => Dark,
    };

    public static EditorTheme Dark { get; } = new(
        "dark",
        new()
        {
            ["namespace"] = 0xFFC8C8C8,
            ["class"] = 0xFF4EC9B0, ["record"] = 0xFF4EC9B0, ["type"] = 0xFF4EC9B0, ["delegate"] = 0xFF4EC9B0,
            ["struct"] = 0xFF86C691, ["recordStruct"] = 0xFF86C691, ["enum"] = 0xFFB8D7A3, ["interface"] = 0xFFB8D7A3,
            ["typeParameter"] = 0xFFB8D7A3,
            ["parameter"] = 0xFF9CDCFE, ["local"] = 0xFF9CDCFE, ["variable"] = 0xFF9CDCFE,
            ["field"] = 0xFFDCDCDC, ["property"] = 0xFFDCDCDC, ["event"] = 0xFFDCDCDC, ["constant"] = 0xFF4FC1FF,
            ["enumMember"] = 0xFF4FC1FF,
            ["method"] = 0xFFDCDCAA, ["extensionMethod"] = 0xFFDCDCAA, ["function"] = 0xFFDCDCAA,
            ["keyword"] = 0xFF569CD6, ["modifier"] = 0xFF569CD6, ["controlKeyword"] = 0xFFC586C0,
            ["comment"] = 0xFF6A9955, ["xmlDocComment"] = 0xFF608B4E,
            ["string"] = 0xFFCE9178, ["stringVerbatim"] = 0xFFCE9178, ["stringEscape"] = 0xFFFFD68F,
            ["number"] = 0xFFB5CEA8, ["regexp"] = 0xFFD16969,
            ["operator"] = 0xFFD4D4D4, ["punctuation"] = 0xFFD4D4D4,
            ["preprocessor"] = 0xFF9B9B9B, ["excludedCode"] = 0xFF767676, ["label"] = 0xFFC8C8C8,

            // Razor and its markup, as Visual Studio's Dark theme colors them.
            ["razorTransition"] = 0xFFA699E6, ["razorDirective"] = 0xFFA699E6, ["razorDirectiveAttribute"] = 0xFFA699E6, ["razorDirectiveColon"] = 0xFFA699E6,
            ["razorComponentElement"] = 0xFF4EC9B0, ["razorComponentAttribute"] = 0xFF9CDCFE,
            ["razorTagHelperElement"] = 0xFF4EC9B0, ["razorTagHelperAttribute"] = 0xFF9CDCFE,
            ["razorComment"] = 0xFF6A9955, ["razorCommentTransition"] = 0xFF6A9955, ["razorCommentStar"] = 0xFF6A9955,
            ["markupTagDelimiter"] = 0xFF808080, ["markupElement"] = 0xFF569CD6, ["markupAttribute"] = 0xFF9CDCFE,
            ["markupAttributeQuote"] = 0xFFCE9178, ["markupAttributeValue"] = 0xFFCE9178, ["markupOperator"] = 0xFFD4D4D4,
            ["markupComment"] = 0xFF6A9955, ["markupCommentPunctuation"] = 0xFF6A9955,
        },
        selection: 0xFF264F78, currentLine: 0x12FFFFFF, highlightRead: 0x48575757, highlightWrite: 0x48004972,
        bracket: 0x500064C8, error: 0xFFF14C4C, warning: 0xFFCCA700, info: 0xFF3794FF, hint: 0xA0A0A0A0, findMatch: 0x80623315);

    public static EditorTheme Light { get; } = new(
        "light",
        new()
        {
            ["namespace"] = 0xFF1E1E1E,
            ["class"] = 0xFF267F99, ["record"] = 0xFF267F99, ["type"] = 0xFF267F99, ["delegate"] = 0xFF267F99,
            ["struct"] = 0xFF2B91AF, ["recordStruct"] = 0xFF2B91AF, ["enum"] = 0xFF2B91AF, ["interface"] = 0xFF2B91AF,
            ["typeParameter"] = 0xFF2B91AF,
            ["parameter"] = 0xFF001080, ["local"] = 0xFF001080, ["variable"] = 0xFF001080,
            ["field"] = 0xFF1E1E1E, ["property"] = 0xFF1E1E1E, ["event"] = 0xFF1E1E1E, ["constant"] = 0xFF0070C1,
            ["enumMember"] = 0xFF0070C1,
            ["method"] = 0xFF795E26, ["extensionMethod"] = 0xFF795E26, ["function"] = 0xFF795E26,
            ["keyword"] = 0xFF0000FF, ["modifier"] = 0xFF0000FF, ["controlKeyword"] = 0xFFAF00DB,
            ["comment"] = 0xFF008000, ["xmlDocComment"] = 0xFF808080,
            ["string"] = 0xFFA31515, ["stringVerbatim"] = 0xFFA31515, ["stringEscape"] = 0xFFEE0000,
            ["number"] = 0xFF098658, ["regexp"] = 0xFF811F3F,
            ["operator"] = 0xFF1E1E1E, ["punctuation"] = 0xFF1E1E1E,
            ["preprocessor"] = 0xFF808080, ["excludedCode"] = 0xFF9E9E9E, ["label"] = 0xFF1E1E1E,

            // Razor and its markup, as Visual Studio's Light (and Blue) theme colors them.
            ["razorTransition"] = 0xFF826EE6, ["razorDirective"] = 0xFF826EE6, ["razorDirectiveAttribute"] = 0xFF826EE6, ["razorDirectiveColon"] = 0xFF826EE6,
            ["razorComponentElement"] = 0xFF0B7F7F, ["razorComponentAttribute"] = 0xFFFF0000,
            ["razorTagHelperElement"] = 0xFF800080, ["razorTagHelperAttribute"] = 0xFF800080,
            ["razorComment"] = 0xFF008000, ["razorCommentTransition"] = 0xFF008000, ["razorCommentStar"] = 0xFF008000,
            ["markupTagDelimiter"] = 0xFF0000FF, ["markupElement"] = 0xFF800000, ["markupAttribute"] = 0xFFFF0000,
            ["markupAttributeQuote"] = 0xFF0000FF, ["markupAttributeValue"] = 0xFF0000FF, ["markupOperator"] = 0xFF0000FF,
            ["markupComment"] = 0xFF008000, ["markupCommentPunctuation"] = 0xFF008000,
        },
        selection: 0xFFADD6FF, currentLine: 0x0F000000, highlightRead: 0x40B4B4B4, highlightWrite: 0x40A8CEFF,
        bracket: 0x400064C8, error: 0xFFE51400, warning: 0xFFBF8803, info: 0xFF1A85FF, hint: 0xA0808080, findMatch: 0x80F8C9AB);

    public static EditorTheme HighContrast { get; } = new(
        "highContrast",
        new()
        {
            ["namespace"] = 0xFFFFFFFF,
            ["class"] = 0xFF4EC9B0, ["record"] = 0xFF4EC9B0, ["type"] = 0xFF4EC9B0, ["delegate"] = 0xFF4EC9B0,
            ["struct"] = 0xFF4EC9B0, ["recordStruct"] = 0xFF4EC9B0, ["enum"] = 0xFFB8D7A3, ["interface"] = 0xFFB8D7A3,
            ["typeParameter"] = 0xFFB8D7A3,
            ["parameter"] = 0xFF9CDCFE, ["local"] = 0xFF9CDCFE, ["variable"] = 0xFF9CDCFE,
            ["field"] = 0xFFFFFFFF, ["property"] = 0xFFFFFFFF, ["event"] = 0xFFFFFFFF, ["constant"] = 0xFF9CDCFE,
            ["enumMember"] = 0xFF9CDCFE,
            ["method"] = 0xFFDCDCAA, ["extensionMethod"] = 0xFFDCDCAA, ["function"] = 0xFFDCDCAA,
            ["keyword"] = 0xFF569CD6, ["modifier"] = 0xFF569CD6, ["controlKeyword"] = 0xFFC586C0,
            ["comment"] = 0xFF7CA668, ["xmlDocComment"] = 0xFF7CA668,
            ["string"] = 0xFFCE9178, ["stringVerbatim"] = 0xFFCE9178, ["stringEscape"] = 0xFFFFD68F,
            ["number"] = 0xFFB5CEA8, ["regexp"] = 0xFFD16969,
            ["operator"] = 0xFFFFFFFF, ["punctuation"] = 0xFFFFFFFF,
            ["preprocessor"] = 0xFFFFFFFF, ["excludedCode"] = 0xFFA0A0A0, ["label"] = 0xFFFFFFFF,

            // Razor and its markup.
            ["razorTransition"] = 0xFFFFFF00, ["razorDirective"] = 0xFFFFFF00, ["razorDirectiveAttribute"] = 0xFFFFFF00, ["razorDirectiveColon"] = 0xFFFFFF00,
            ["razorComponentElement"] = 0xFF4EC9B0, ["razorComponentAttribute"] = 0xFF9CDCFE,
            ["razorTagHelperElement"] = 0xFF4EC9B0, ["razorTagHelperAttribute"] = 0xFF9CDCFE,
            ["razorComment"] = 0xFF7CA668, ["razorCommentTransition"] = 0xFF7CA668, ["razorCommentStar"] = 0xFF7CA668,
            ["markupTagDelimiter"] = 0xFFFFFFFF, ["markupElement"] = 0xFF569CD6, ["markupAttribute"] = 0xFF9CDCFE,
            ["markupAttributeQuote"] = 0xFFCE9178, ["markupAttributeValue"] = 0xFFCE9178, ["markupOperator"] = 0xFFFFFFFF,
            ["markupComment"] = 0xFF7CA668, ["markupCommentPunctuation"] = 0xFF7CA668,
        },
        selection: 0xFF0F4A85, currentLine: 0x00000000, highlightRead: 0x60F38518, highlightWrite: 0x80F38518,
        bracket: 0x6000FFFF, error: 0xFFF48771, warning: 0xFFFFD700, info: 0xFF00FFFF, hint: 0xFFFFFFFF, findMatch: 0x80F38518);
}
