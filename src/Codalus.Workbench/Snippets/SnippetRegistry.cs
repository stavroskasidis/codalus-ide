using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Codalus.Workbench.Snippets;

/// <summary>
/// A code snippet in VS Code's format: a name, the prefixes that offer it in completion, a body in TextMate snippet
/// syntax (<c>$1</c>, <c>${1:default}</c>, <c>${1|a,b|}</c>, <c>$0</c>, <c>$TM_SELECTED_TEXT</c>…) and a description.
/// <see cref="IsSurround"/>: the body uses the selection, so Surround With offers it.
/// </summary>
public sealed record Snippet(string Name, IReadOnlyList<string> Prefixes, string Body, string? Description, string Language, string Source)
{
    public bool IsSurround => Body.Contains("$TM_SELECTED_TEXT", StringComparison.Ordinal) || Body.Contains("${TM_SELECTED_TEXT", StringComparison.Ordinal)
                              || Body.Contains("$SELECTION", StringComparison.Ordinal) || Body.Contains("${SELECTION", StringComparison.Ordinal);
}

/// <summary>
/// Snippets by language (M12b): built in, contributed by plugins (<c>contributes.snippets</c>, VS Code's
/// <c>{ language, path }</c>), and the user's (<c>&lt;user data&gt;/config/snippets/&lt;language&gt;.json</c>). Later
/// sources with the same name replace earlier ones: the user's win. Thread-safe.
/// </summary>
public sealed class SnippetRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Snippet>> _bySource = new(StringComparer.Ordinal);

    /// <summary>Raised when snippets were added or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>Replaces the snippets of a source (e.g. <c>builtin</c>, a plugin id, <c>user</c>).</summary>
    public void Set(string source, IEnumerable<Snippet> snippets)
    {
        lock (_gate)
        {
            _bySource[source] = [.. snippets];
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string source)
    {
        bool removed;
        lock (_gate)
        {
            removed = _bySource.Remove(source);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A language's snippets (and those for every language, <c>*</c>), the user's replacing others with the same name.</summary>
    public IReadOnlyList<Snippet> For(string? languageId)
    {
        lock (_gate)
        {
            var result = new Dictionary<string, Snippet>(StringComparer.Ordinal);
            foreach (var (_, snippets) in _bySource.OrderBy(s => s.Key == "user" ? 2 : s.Key == "builtin" ? 0 : 1))
            {
                foreach (var snippet in snippets.Where(s => s.Language == languageId || s.Language == "*"))
                {
                    result[snippet.Name] = snippet;
                }
            }

            return [.. result.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// Parses a VS Code snippet file: <c>{ "Name": { "prefix": "p" | ["a", "b"], "body": "…" | ["line", …], "description": "…", "scope": "csharp,…" } }</c>.
    /// Without <c>scope</c>, snippets are for <paramref name="language"/>.
    /// </summary>
    public static IReadOnlyList<Snippet> ParseJson(string json, string language, string source)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var snippets = new List<Snippet>();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var value = entry.Value;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("body", out var body))
            {
                continue;
            }

            var text = body.ValueKind == JsonValueKind.Array
                ? string.Join("\n", body.EnumerateArray().Select(l => l.GetString() ?? string.Empty))
                : body.GetString() ?? string.Empty;
            IReadOnlyList<string> prefixes = value.TryGetProperty("prefix", out var prefix)
                ? prefix.ValueKind == JsonValueKind.Array ? [.. prefix.EnumerateArray().Select(p => p.GetString() ?? string.Empty).Where(p => p.Length > 0)] : [prefix.GetString() ?? string.Empty]
                : [];
            var description = value.TryGetProperty("description", out var d) ? (d.ValueKind == JsonValueKind.Array ? string.Join("\n", d.EnumerateArray().Select(l => l.GetString())) : d.GetString()) : null;
            var scopes = value.TryGetProperty("scope", out var scope) && scope.GetString() is { Length: > 0 } list
                ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [language];
            foreach (var scopeLanguage in scopes)
            {
                snippets.Add(new Snippet(entry.Name, [.. prefixes.Where(p => p.Length > 0)], text, description, scopeLanguage, source));
            }
        }

        return snippets;
    }

    /// <summary>
    /// Converts a Visual Studio <c>.snippet</c> file (the CodeSnippets XML schema) to snippets: declared literals become
    /// placeholders (<c>$name$</c> → <c>${1:default}</c>, repeated ones as mirrors), <c>$end$</c> becomes <c>$0</c> and
    /// <c>$selected$</c> the selection.
    /// </summary>
    public static IReadOnlyList<Snippet> ParseVisualStudio(string xml, string source)
    {
        var document = XDocument.Parse(xml);
        var result = new List<Snippet>();
        foreach (var code in document.Descendants().Where(e => e.Name.LocalName == "CodeSnippet"))
        {
            var header = code.Elements().FirstOrDefault(e => e.Name.LocalName == "Header");
            var snippet = code.Elements().FirstOrDefault(e => e.Name.LocalName == "Snippet");
            var codeElement = snippet?.Elements().FirstOrDefault(e => e.Name.LocalName == "Code");
            if (header is null || codeElement is null)
            {
                continue;
            }

            string? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            var title = Child(header, "Title") ?? "Snippet";
            var shortcut = Child(header, "Shortcut");
            var description = Child(header, "Description");
            var language = (codeElement.Attribute("Language")?.Value ?? "csharp").ToLowerInvariant() switch
            {
                "csharp" or "c#" => "csharp",
                "vb" or "visualbasic" => "vb",
                "xml" => "xml",
                "javascript" or "js" => "javascript",
                "typescript" => "typescript",
                "html" => "html",
                "css" => "css",
                "sql" => "sql",
                var other => other,
            };
            var delimiter = codeElement.Attribute("Delimiter")?.Value is { Length: 1 } d ? d[0] : '$';
            var defaults = (snippet!.Elements().FirstOrDefault(e => e.Name.LocalName == "Declarations")?.Elements() ?? [])
                .Where(e => e.Name.LocalName is "Literal" or "Object")
                .Select(e => (Id: Child(e, "ID") ?? string.Empty, Default: Child(e, "Default") ?? Child(e, "ID") ?? string.Empty))
                .Where(e => e.Id.Length > 0)
                .ToDictionary(e => e.Id, e => e.Default, StringComparer.Ordinal);
            var body = new StringBuilder();
            var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
            var text = codeElement.Value.Replace("\r\n", "\n", StringComparison.Ordinal);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == delimiter)
                {
                    var end = text.IndexOf(delimiter, i + 1);
                    if (end > i)
                    {
                        var id = text[(i + 1)..end];
                        if (id.Length == 0)
                        {
                            body.Append(delimiter == '$' ? "\\$" : delimiter.ToString()); // $$ is a literal delimiter
                        }
                        else if (id == "end")
                        {
                            body.Append("$0");
                        }
                        else if (id == "selected")
                        {
                            body.Append("$TM_SELECTED_TEXT");
                        }
                        else if (defaults.TryGetValue(id, out var value))
                        {
                            if (numbers.TryGetValue(id, out var number))
                            {
                                body.Append('$').Append(number);
                            }
                            else
                            {
                                number = numbers[id] = numbers.Count + 1;
                                body.Append("${").Append(number).Append(':').Append(Escape(value)).Append('}');
                            }
                        }
                        else
                        {
                            body.Append(Escape(text[i..(end + 1)]));
                        }

                        i = end;
                        continue;
                    }
                }

                body.Append(c is '$' or '\\' ? "\\" + c : c.ToString());
            }

            result.Add(new Snippet(title, shortcut is { Length: > 0 } s ? [s] : [], body.ToString(), description, language, source));
        }

        return result;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal).Replace("}", "\\}", StringComparison.Ordinal);

    /// <summary>Serializes snippets to VS Code's format (the user's snippet file after an import).</summary>
    public static string ToJson(IEnumerable<Snippet> snippets, IReadOnlyDictionary<string, Snippet>? existing = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var snippet in (existing?.Values ?? []).Concat(snippets).GroupBy(s => s.Name).Select(g => g.Last()))
            {
                writer.WriteStartObject(snippet.Name);
                writer.WriteStartArray("prefix");
                foreach (var prefix in snippet.Prefixes)
                {
                    writer.WriteStringValue(prefix);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("body");
                foreach (var line in snippet.Body.Split('\n'))
                {
                    writer.WriteStringValue(line);
                }

                writer.WriteEndArray();
                if (snippet.Description is { } description)
                {
                    writer.WriteString("description", description);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>A parsed snippet body: text runs, placeholders (tab stops with defaults or choices), variables and the final caret.</summary>
public abstract record SnippetPart;

public sealed record SnippetText(string Text) : SnippetPart;

/// <summary>A tab stop. <see cref="Index"/> 0 is the final caret position.</summary>
public sealed record SnippetPlaceholder(int Index, string Default, IReadOnlyList<string>? Choices) : SnippetPart;

/// <summary>A variable such as <c>TM_SELECTED_TEXT</c>, <c>TM_FILENAME_BASE</c>, <c>CLIPBOARD</c>, with its default.</summary>
public sealed record SnippetVariable(string Name, string Default) : SnippetPart;

/// <summary>Parses TextMate/VS Code snippet syntax. Nested placeholders keep their text (the outer one is the tab stop).</summary>
public static class SnippetParser
{
    public static IReadOnlyList<SnippetPart> Parse(string body)
    {
        var parts = new List<SnippetPart>();
        var text = new StringBuilder();
        var i = 0;
        void Flush()
        {
            if (text.Length > 0)
            {
                parts.Add(new SnippetText(text.ToString()));
                text.Clear();
            }
        }

        while (i < body.Length)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length && body[i + 1] is '$' or '}' or '\\' or ',' or '|')
            {
                text.Append(body[i + 1]);
                i += 2;
                continue;
            }

            if (c != '$' || i + 1 >= body.Length)
            {
                text.Append(c);
                i++;
                continue;
            }

            var next = body[i + 1];
            if (char.IsDigit(next))
            {
                var start = i + 1;
                var end = start;
                while (end < body.Length && char.IsDigit(body[end]))
                {
                    end++;
                }

                Flush();
                parts.Add(new SnippetPlaceholder(int.Parse(body[start..end], System.Globalization.CultureInfo.InvariantCulture), string.Empty, null));
                i = end;
            }
            else if (next == '{')
            {
                var close = Matching(body, i + 1);
                if (close < 0)
                {
                    text.Append(c);
                    i++;
                    continue;
                }

                var inner = body[(i + 2)..close];
                Flush();
                parts.Add(Braced(inner));
                i = close + 1;
            }
            else if (char.IsLetter(next) || next == '_')
            {
                var end = i + 1;
                while (end < body.Length && (char.IsLetterOrDigit(body[end]) || body[end] == '_'))
                {
                    end++;
                }

                Flush();
                parts.Add(new SnippetVariable(body[(i + 1)..end], string.Empty));
                i = end;
            }
            else
            {
                text.Append(c);
                i++;
            }
        }

        Flush();
        return parts;
    }

    /// <summary>The text of a body with placeholders at their defaults and variables resolved (previews, plain insertion).</summary>
    public static string ToPlainText(string body, Func<string, string?>? variable = null)
    {
        var parts = Parse(body);

        // A repeated tab stop ($1 after ${1:i}) mirrors the first one's text.
        var defaults = new Dictionary<int, string>();
        foreach (var placeholder in parts.OfType<SnippetPlaceholder>())
        {
            var text = placeholder.Choices is [var first, ..] ? first : placeholder.Default;
            if (text.Length > 0)
            {
                defaults.TryAdd(placeholder.Index, text);
            }
        }

        return string.Concat(parts.Select(p => p switch
        {
            SnippetText t => t.Text,
            SnippetPlaceholder ph => defaults.GetValueOrDefault(ph.Index, string.Empty),
            SnippetVariable v => variable?.Invoke(v.Name) ?? v.Default,
            _ => string.Empty,
        }));
    }

    private static SnippetPart Braced(string inner)
    {
        var digits = 0;
        while (digits < inner.Length && char.IsDigit(inner[digits]))
        {
            digits++;
        }

        if (digits > 0)
        {
            var index = int.Parse(inner[..digits], System.Globalization.CultureInfo.InvariantCulture);
            var rest = inner[digits..];
            if (rest.StartsWith(':'))
            {
                return new SnippetPlaceholder(index, ToPlainText(rest[1..]), null);
            }

            if (rest.StartsWith('|') && rest.EndsWith('|') && rest.Length >= 2)
            {
                var choices = Split(rest[1..^1]);
                return new SnippetPlaceholder(index, choices.FirstOrDefault() ?? string.Empty, choices);
            }

            return new SnippetPlaceholder(index, string.Empty, null);
        }

        var colon = inner.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? new SnippetVariable(inner, string.Empty) : new SnippetVariable(inner[..colon], ToPlainText(inner[(colon + 1)..]));
    }

    private static List<string> Split(string choices)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < choices.Length; i++)
        {
            if (choices[i] == '\\' && i + 1 < choices.Length)
            {
                current.Append(choices[++i]);
            }
            else if (choices[i] == ',')
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(choices[i]);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    /// <summary>The index of the <c>}</c> closing the <c>{</c> at <paramref name="open"/>, honoring escapes and nesting.</summary>
    private static int Matching(string body, int open)
    {
        var depth = 0;
        for (var i = open; i < body.Length; i++)
        {
            if (body[i] == '\\')
            {
                i++;
            }
            else if (body[i] == '{')
            {
                depth++;
            }
            else if (body[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
