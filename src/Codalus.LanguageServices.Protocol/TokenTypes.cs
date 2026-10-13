namespace Codalus.LanguageServices.Protocol;

/// <summary>
/// Semantic token legend. The first block mirrors the LSP standard token types;
/// the rest are Roslyn-specific types that LSP has no name for (documented in docs/protocol.md).
/// Indices are part of the wire contract: append only.
/// </summary>
public static class TokenTypes
{
    public static readonly string[] All =
    [
        // LSP standard
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter",
        "parameter", "variable", "property", "enumMember", "event", "function", "method",
        "macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator",
        // Codalus extensions (Roslyn classifications)
        "delegate", "record", "recordStruct", "field", "constant", "local", "label",
        "excludedCode", "preprocessor", "punctuation", "stringEscape", "xmlDocComment",
        "controlKeyword", "extensionMethod", "stringVerbatim",
    ];

    public static int IndexOf(string name)
    {
        var index = Array.IndexOf(All, name);
        return index >= 0 ? index : throw new ArgumentException($"Unknown token type '{name}'.", nameof(name));
    }
}

public static class TokenModifiers
{
    public const int Static = 1 << 0;

    public static readonly string[] All = ["static"];
}
