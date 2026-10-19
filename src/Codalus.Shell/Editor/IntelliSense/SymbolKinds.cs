using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor.IntelliSense;

/// <summary>
/// A filter of the completion list (a classic IDE's filter bar): the symbol kinds it shows, its icon, and the command
/// that toggles it, whose key binding (Alt and a letter while the list is open) its tooltip names.
/// </summary>
public sealed record CompletionFilter(string Id, string Glyph, string Command)
{
    /// <summary>The filter's name in the current language: "Classes", "Extension methods".</summary>
    public string Title => Id switch
    {
        "namespaces" => Localizer.Get("editor.completion.filter.namespaces", "Namespaces"),
        "classes" => Localizer.Get("editor.completion.filter.classes", "Classes"),
        "structures" => Localizer.Get("editor.completion.filter.structures", "Structures"),
        "interfaces" => Localizer.Get("editor.completion.filter.interfaces", "Interfaces"),
        "enums" => Localizer.Get("editor.completion.filter.enums", "Enums"),
        "delegates" => Localizer.Get("editor.completion.filter.delegates", "Delegates"),
        "constants" => Localizer.Get("editor.completion.filter.constants", "Constants"),
        "fields" => Localizer.Get("editor.completion.filter.fields", "Fields"),
        "events" => Localizer.Get("editor.completion.filter.events", "Events"),
        "properties" => Localizer.Get("editor.completion.filter.properties", "Properties"),
        "methods" => Localizer.Get("editor.completion.filter.methods", "Methods"),
        "extensionMethods" => Localizer.Get("editor.completion.filter.extensionMethods", "Extension methods"),
        "locals" => Localizer.Get("editor.completion.filter.locals", "Locals and parameters"),
        "keywords" => Localizer.Get("editor.completion.filter.keywords", "Keywords"),
        _ => Localizer.Get("editor.completion.filter.snippets", "Snippets"),
    };
}

/// <summary>
/// The code symbol kinds of IntelliSense: a completion item's glyph (the C# service names it; other servers' LSP kinds
/// map onto one), its icon (Icons/Symbols, colored by kind) and the filter it belongs to.
/// </summary>
public static class SymbolKinds
{
    public const string FilterCommandPrefix = "editor.action.completionFilter.";

    /// <summary>The filters in the order the filter bar shows them, with the letters a classic IDE binds to Alt.</summary>
    public static IReadOnlyList<CompletionFilter> Filters { get; } =
    [
        new("namespaces", SymbolGlyphs.Namespace, FilterCommandPrefix + "namespaces"),
        new("classes", SymbolGlyphs.Class, FilterCommandPrefix + "classes"),
        new("structures", SymbolGlyphs.Struct, FilterCommandPrefix + "structures"),
        new("interfaces", SymbolGlyphs.Interface, FilterCommandPrefix + "interfaces"),
        new("enums", SymbolGlyphs.Enum, FilterCommandPrefix + "enums"),
        new("delegates", SymbolGlyphs.Delegate, FilterCommandPrefix + "delegates"),
        new("constants", SymbolGlyphs.Constant, FilterCommandPrefix + "constants"),
        new("fields", SymbolGlyphs.Field, FilterCommandPrefix + "fields"),
        new("events", SymbolGlyphs.Event, FilterCommandPrefix + "events"),
        new("properties", SymbolGlyphs.Property, FilterCommandPrefix + "properties"),
        new("methods", SymbolGlyphs.Method, FilterCommandPrefix + "methods"),
        new("extensionMethods", SymbolGlyphs.ExtensionMethod, FilterCommandPrefix + "extensionMethods"),
        new("locals", SymbolGlyphs.Local, FilterCommandPrefix + "locals"),
        new("keywords", SymbolGlyphs.Keyword, FilterCommandPrefix + "keywords"),
        new("snippets", SymbolGlyphs.Snippet, FilterCommandPrefix + "snippets"),
    ];

    /// <summary>The filter key bindings (Alt and the filter's letter, while the completion list is open).</summary>
    public static IReadOnlyDictionary<string, char> FilterKeys { get; } = new Dictionary<string, char>(StringComparer.Ordinal)
    {
        ["namespaces"] = 'n',
        ["classes"] = 'c',
        ["structures"] = 's',
        ["interfaces"] = 'i',
        ["enums"] = 'e',
        ["delegates"] = 'd',
        ["constants"] = 'o',
        ["fields"] = 'f',
        ["events"] = 'v',
        ["properties"] = 'p',
        ["methods"] = 'm',
        ["extensionMethods"] = 'x',
        ["locals"] = 'l',
        ["keywords"] = 'k',
        ["snippets"] = 't',
    };

    /// <summary>A completion item's glyph: the server's, or its LSP kind's.</summary>
    public static string GlyphOf(CompletionItem item) => item.Glyph ?? FromKind(item.Kind);

    /// <summary>The glyph of an LSP completion item kind (servers that don't name one).</summary>
    public static string FromKind(int? kind) => kind switch
    {
        CompletionItemKind.Method or CompletionItemKind.Constructor => SymbolGlyphs.Method,
        CompletionItemKind.Function => SymbolGlyphs.Method,
        CompletionItemKind.Field => SymbolGlyphs.Field,
        CompletionItemKind.Variable => SymbolGlyphs.Local,
        CompletionItemKind.Class => SymbolGlyphs.Class,
        CompletionItemKind.Interface => SymbolGlyphs.Interface,
        CompletionItemKind.Module => SymbolGlyphs.Namespace,
        CompletionItemKind.Property or CompletionItemKind.Unit or CompletionItemKind.Color => SymbolGlyphs.Property,
        CompletionItemKind.Value or CompletionItemKind.Constant => SymbolGlyphs.Constant,
        CompletionItemKind.Enum => SymbolGlyphs.Enum,
        CompletionItemKind.EnumMember => SymbolGlyphs.EnumMember,
        CompletionItemKind.Keyword => SymbolGlyphs.Keyword,
        CompletionItemKind.Snippet => SymbolGlyphs.Snippet,
        CompletionItemKind.Struct => SymbolGlyphs.Struct,
        CompletionItemKind.Event => SymbolGlyphs.Event,
        CompletionItemKind.Operator => SymbolGlyphs.Operator,
        CompletionItemKind.TypeParameter => SymbolGlyphs.TypeParameter,
        CompletionItemKind.Reference => SymbolGlyphs.Label,
        _ => SymbolGlyphs.Text,
    };

    /// <summary>The icon of a glyph (<c>symbol-class</c>…): one of IconSet's code symbol icons.</summary>
    public static string IconOf(string? glyph) => glyph switch
    {
        SymbolGlyphs.Namespace => "symbol-namespace",
        SymbolGlyphs.Class => "symbol-class",
        SymbolGlyphs.Struct => "symbol-struct",
        SymbolGlyphs.Interface => "symbol-interface",
        SymbolGlyphs.Enum => "symbol-enum",
        SymbolGlyphs.EnumMember => "symbol-enum-member",
        SymbolGlyphs.Delegate => "symbol-delegate",
        SymbolGlyphs.Module => "symbol-module",
        SymbolGlyphs.TypeParameter => "symbol-type-parameter",
        SymbolGlyphs.Method => "symbol-method",
        SymbolGlyphs.ExtensionMethod => "symbol-extension-method",
        SymbolGlyphs.Property => "symbol-property",
        SymbolGlyphs.Field => "symbol-field",
        SymbolGlyphs.Event => "symbol-event",
        SymbolGlyphs.Constant => "symbol-constant",
        SymbolGlyphs.Local => "symbol-local",
        SymbolGlyphs.Parameter => "symbol-parameter",
        SymbolGlyphs.Keyword => "symbol-keyword",
        SymbolGlyphs.Snippet => "symbol-snippet",
        SymbolGlyphs.Operator => "symbol-operator",
        SymbolGlyphs.Label => "symbol-label",
        _ => "symbol-text",
    };

    /// <summary>The filter a glyph belongs to, or null (words, labels, type parameters: shown only while no filter is on).</summary>
    public static string? FilterOf(string glyph) => glyph switch
    {
        SymbolGlyphs.Namespace or SymbolGlyphs.Module => "namespaces",
        SymbolGlyphs.Class => "classes",
        SymbolGlyphs.Struct => "structures",
        SymbolGlyphs.Interface => "interfaces",
        SymbolGlyphs.Enum => "enums",
        SymbolGlyphs.Delegate => "delegates",
        SymbolGlyphs.Constant or SymbolGlyphs.EnumMember => "constants",
        SymbolGlyphs.Field => "fields",
        SymbolGlyphs.Event => "events",
        SymbolGlyphs.Property => "properties",
        SymbolGlyphs.Method or SymbolGlyphs.Operator => "methods",
        SymbolGlyphs.ExtensionMethod => "extensionMethods",
        SymbolGlyphs.Local or SymbolGlyphs.Parameter => "locals",
        SymbolGlyphs.Keyword => "keywords",
        SymbolGlyphs.Snippet => "snippets",
        _ => null,
    };
}
