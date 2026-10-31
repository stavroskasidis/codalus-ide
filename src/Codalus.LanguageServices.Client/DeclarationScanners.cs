namespace Codalus.LanguageServices.Client;

/// <summary>
/// What a document's language service will list, estimated from its text before the service answers, by language: the
/// lines of its CodeLens (C#, F#) and whether it declares a type for the navigation bar (C#, Visual Basic, XAML, F#,
/// JavaScript and TypeScript, CSS, SCSS and Less). The editor keeps their room from the first frame, so the code doesn't
/// move down when they come.
/// </summary>
public static class DeclarationScanners
{
    /// <summary>Whether <paramref name="text"/> (of <paramref name="path"/>, in <paramref name="languageId"/>) declares a type its service will list.</summary>
    public static bool DeclaresType(string? languageId, string path, string text) => languageId switch
    {
        "csharp" => CSharpLensScanner.DeclaresType(text),
        "vb" => VisualBasicTypeScanner.DeclaresType(text),
        "xaml" or "axaml" => HasRootElement(text),
        "fsharp" => FSharpDeclarationScanner.DeclaresType(text, IsFSharpScript(path)),
        "javascript" or "javascriptreact" => JavaScriptTypeScanner.DeclaresType(text, javaScript: true),
        "typescript" or "typescriptreact" => JavaScriptTypeScanner.DeclaresType(text),
        "css" => CssRuleScanner.DeclaresType(text, lineComments: false),
        "scss" or "less" => CssRuleScanner.DeclaresType(text, lineComments: true),
        _ => false,
    };

    /// <summary>The offsets where <paramref name="text"/>'s language service will put CodeLens; empty for a language it doesn't estimate.</summary>
    public static IReadOnlyList<int> Lenses(string? languageId, string path, string text) => languageId switch
    {
        "csharp" => CSharpLensScanner.Scan(text),
        "fsharp" => FSharpDeclarationScanner.Lenses(text, IsFSharpScript(path)),
        _ => [],
    };

    /// <summary>An F# script (<c>.fsx</c>, <c>.fsscript</c>): checked without a project's <c>DEBUG</c>.</summary>
    public static bool IsFSharpScript(string path) =>
        path.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fsscript", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a XAML file has a root element, past its declaration, comments and processing instructions: the C# host
    /// lists every element of XAML as a class, from the root.
    /// </summary>
    public static bool HasRootElement(string text)
    {
        var i = 0;
        while ((i = text.IndexOf('<', i)) >= 0 && i + 1 < text.Length)
        {
            var c = text[i + 1];
            if (c == '?' || c == '!')
            {
                var close = text.IndexOf(c == '!' && string.CompareOrdinal(text, i, "<!--", 0, 4) == 0 ? "-->" : c == '?' ? "?>" : ">", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return false;
                }

                i = close + 1;
                continue;
            }

            return char.IsLetter(c) || c == '_';
        }

        return false;
    }
}
