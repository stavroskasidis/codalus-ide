using Codalus.EditorConfig;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Markup.Host.EditorConfig;

/// <summary>
/// <c>.editorconfig</c> (ADR 0016): completion of the common keys (EditorConfig's own and .NET's code style and
/// analyzer options) and their values, hover, and warnings for values a key doesn't take. Unknown keys are allowed:
/// analyzers define their own.
/// </summary>
internal static class EditorConfigLanguage
{
    private static EditorConfigKey[] Keys => EditorConfigCatalog.Keys;

    public static List<Diagnostic> Diagnostics(TextDocument document)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var (line, key, keyStart, value, valueStart) in Assignments(document))
        {
            var allowed = ValuesFor(key);
            if (allowed is not null && value.Length > 0 && !allowed.Contains(value, StringComparer.OrdinalIgnoreCase)
                && !(key.EndsWith(".severity", StringComparison.Ordinal) && EditorConfigCatalog.Severities.Contains(value, StringComparer.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new Diagnostic(new LanguageServices.Protocol.Range(new Position(line, valueStart), new Position(line, valueStart + value.Length)),
                    DiagnosticSeverity.Warning, "editorconfig.value", "editorconfig", $"'{value}' isn't a value of {key}. Expected: {string.Join(", ", allowed.Take(10))}{(allowed.Length > 10 ? ", …" : string.Empty)}."));
            }
        }

        return diagnostics;
    }

    public static CompletionList Completion(TextDocument document, int offset)
    {
        var position = document.PositionAt(offset);
        var lineStart = document.LineStarts[position.Line];
        var lineText = document.Text[lineStart..offset];
        if (lineText.TrimStart().StartsWith('[') || lineText.TrimStart().StartsWith('#') || lineText.TrimStart().StartsWith(';'))
        {
            return new CompletionList(false, []);
        }

        var equals = lineText.IndexOf('=', StringComparison.Ordinal);
        var items = new List<CompletionItem>();
        if (equals < 0)
        {
            var start = lineStart + (lineText.Length - lineText.TrimStart().Length);
            var range = document.RangeOf(start, offset);
            items.AddRange(Keys.Select(k => new CompletionItem(k.Name) { Kind = CompletionItemKind.Property, Documentation = MarkupContent.Markdown(k.Description), TextEdit = new TextEdit(range, k.Name + " = ") }));
            items.Add(new CompletionItem("dotnet_diagnostic.<id>.severity") { Kind = CompletionItemKind.Snippet, TextEdit = new TextEdit(range, "dotnet_diagnostic.CA0000.severity = "), Documentation = MarkupContent.Markdown("The severity of one analyzer rule.") });
        }
        else
        {
            var key = lineText[..equals].Trim();
            var valueStart = lineStart + equals + 1;
            while (valueStart < offset && document.Text[valueStart] == ' ')
            {
                valueStart++;
            }

            var range = document.RangeOf(valueStart, offset);
            items.AddRange((ValuesFor(key) ?? []).Select(v => new CompletionItem(v) { Kind = CompletionItemKind.EnumMember, TextEdit = new TextEdit(range, v) }));
        }

        return new CompletionList(false, [.. items]);
    }

    public static Hover? Hover(TextDocument document, int offset)
    {
        var line = document.PositionAt(offset).Line;
        foreach (var (l, key, keyStart, _, _) in Assignments(document))
        {
            if (l == line && Keys.FirstOrDefault(k => k.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                var values = known.Values is { } v ? "\n\nValues: " + string.Join(", ", v.Select(x => "`" + x + "`")) : string.Empty;
                return new Hover(MarkupContent.Markdown($"**{known.Name}**\n\n{known.Description}{values}"),
                    new LanguageServices.Protocol.Range(new Position(l, keyStart), new Position(l, keyStart + key.Length)));
            }
        }

        return null;
    }

    private static string[]? ValuesFor(string key) =>
        key.StartsWith("dotnet_diagnostic.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".severity", StringComparison.OrdinalIgnoreCase)
            ? EditorConfigCatalog.Severities
            : Keys.FirstOrDefault(k => k.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Values;

    private static IEnumerable<(int Line, string Key, int KeyStart, string Value, int ValueStart)> Assignments(TextDocument document)
    {
        var lines = document.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i].TrimEnd('\r');
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';' or '[')
            {
                continue;
            }

            var equals = text.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = text[..equals].Trim();
            var keyStart = text.IndexOf(key, StringComparison.Ordinal);
            var raw = text[(equals + 1)..];
            var value = raw.Trim();
            var comment = value.IndexOfAny(['#', ';']);
            if (comment > 0)
            {
                value = value[..comment].TrimEnd();
            }

            yield return (i, key, keyStart, value, equals + 1 + (raw.Length - raw.TrimStart().Length));
        }
    }
}
