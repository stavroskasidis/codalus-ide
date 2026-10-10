using System.Text.Json;
using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.Markup.Host.Json;

/// <summary>JSON and JSON with comments (ADR 0016): syntax errors, schema validation, completion and hover from the schema.</summary>
internal sealed class JsonLanguage(SchemaStore schemas)
{
    public static bool Handles(string languageId) => languageId is "json" or "jsonc";

    private static bool AllowsComments(TextDocument document) => document.LanguageId == "jsonc";

    public async Task<List<Diagnostic>> DiagnosticsAsync(TextDocument document, CancellationToken cancellationToken)
    {
        var (root, error) = JsonTree.Parse(document.Text, AllowsComments(document));
        if (error is not null)
        {
            var end = Math.Min(document.Text.Length, error.Offset + 1);
            return [new Diagnostic(document.RangeOf(error.Offset, end), DiagnosticSeverity.Error, "json.syntax", "json", error.Message)];
        }

        if (root is null || await schemas.ForAsync(document.Path, root, cancellationToken).ConfigureAwait(false) is not { } schema)
        {
            return [];
        }

        return [.. schema.Validate(root).Take(200).Select(p => new Diagnostic(
            document.RangeOf(p.Start, Math.Max(p.Start + 1, p.End)),
            p.IsWarning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
            "json.schema",
            "json",
            p.Message))];
    }

    public async Task<CompletionList> CompletionAsync(TextDocument document, int offset, CancellationToken cancellationToken)
    {
        var cursor = JsonCursor.Locate(document.Text, offset);
        if (cursor.Kind == JsonCursorKind.None)
        {
            return new CompletionList(false, []);
        }

        var (root, _) = JsonTree.Parse(document.Text, true);
        if (await schemas.ForAsync(document.Path, root, cancellationToken).ConfigureAwait(false) is not { } schema)
        {
            return new CompletionList(false, []);
        }

        var range = document.RangeOf(cursor.ReplaceStart, cursor.ReplaceEnd);
        var items = new List<CompletionItem>();
        var candidates = schema.At(cursor.Path);
        if (cursor.Kind == JsonCursorKind.PropertyName)
        {
            foreach (var property in candidates
                .Where(s => s.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                .SelectMany(s => s.GetProperty("properties").EnumerateObject())
                .Where(p => !cursor.ExistingKeys.Contains(p.Name))
                .DistinctBy(p => p.Name))
            {
                var propertySchema = schema.Expand(property.Value).ToList();
                var types = propertySchema.SelectMany(JsonSchema.Types).Distinct().ToList();
                var text = cursor.InString ? property.Name : $"\"{property.Name}\": {Placeholder(types)}";
                items.Add(new CompletionItem(property.Name)
                {
                    Kind = CompletionItemKind.Property,
                    Detail = types.Count > 0 ? string.Join(" | ", types) : null,
                    Documentation = propertySchema.Select(JsonSchema.Description).FirstOrDefault(d => d is not null) is { } description ? MarkupContent.Markdown(description) : null,
                    TextEdit = new TextEdit(range, text),
                    FilterText = cursor.InString ? property.Name : "\"" + property.Name,
                });
            }
        }
        else
        {
            var values = new List<(string Label, string Json, string? Description)>();
            foreach (var candidate in candidates)
            {
                if (candidate.TryGetProperty("enum", out var enumeration) && enumeration.ValueKind == JsonValueKind.Array)
                {
                    values.AddRange(enumeration.EnumerateArray().Select(v => (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText(), v.GetRawText(), (string?)null)));
                }

                if (candidate.TryGetProperty("const", out var constant))
                {
                    values.Add((constant.GetRawText(), constant.GetRawText(), null));
                }

                if (JsonSchema.Types(candidate).Contains("boolean"))
                {
                    values.Add(("true", "true", null));
                    values.Add(("false", "false", null));
                }

                if (candidate.TryGetProperty("examples", out var examples) && examples.ValueKind == JsonValueKind.Array)
                {
                    values.AddRange(examples.EnumerateArray().Select(v => (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText(), v.GetRawText(), (string?)"example")));
                }
            }

            foreach (var (label, json, description) in values.DistinctBy(v => v.Json))
            {
                var isString = json.StartsWith('"');
                var text = cursor.InString ? (isString ? label : json) : json;
                items.Add(new CompletionItem(label)
                {
                    Kind = CompletionItemKind.EnumMember,
                    Detail = description,
                    TextEdit = new TextEdit(cursor.InString && !isString ? document.RangeOf(cursor.ReplaceStart - 1, cursor.ReplaceEnd + 1) : range, text),
                    FilterText = cursor.InString ? label : json,
                });
            }
        }

        return new CompletionList(false, [.. items]);
    }

    public async Task<Hover?> HoverAsync(TextDocument document, int offset, CancellationToken cancellationToken)
    {
        var (root, _) = JsonTree.Parse(document.Text, true);
        if (root is null || await schemas.ForAsync(document.Path, root, cancellationToken).ConfigureAwait(false) is not { } schema)
        {
            return null;
        }

        // The member whose key is under the position, with its path.
        var path = new List<string>();
        JsonMember? hovered = null;
        var node = root;
        while (node is not null && hovered is null)
        {
            JsonNodeInfo? next = null;
            if (node.Kind == JsonKind.Object)
            {
                foreach (var member in node.Members)
                {
                    if (offset >= member.KeyStart && offset <= member.KeyEnd)
                    {
                        hovered = member;
                        path.Add(member.Key);
                        break;
                    }

                    if (offset >= member.Value.Start && offset < member.Value.End)
                    {
                        path.Add(member.Key);
                        next = member.Value;
                        break;
                    }
                }
            }
            else if (node.Kind == JsonKind.Array)
            {
                for (var i = 0; i < node.Items.Count; i++)
                {
                    if (offset >= node.Items[i].Start && offset < node.Items[i].End)
                    {
                        path.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        next = node.Items[i];
                        break;
                    }
                }
            }

            node = next;
        }

        if (hovered is null)
        {
            return null;
        }

        var schemasAt = schema.At(path);
        var description = schemasAt.Select(JsonSchema.Description).FirstOrDefault(d => d is not null);
        var types = schemasAt.SelectMany(JsonSchema.Types).Distinct().ToList();
        if (description is null && types.Count == 0)
        {
            return null;
        }

        var text = $"**{hovered.Key}**" + (types.Count > 0 ? $": `{string.Join(" | ", types)}`" : string.Empty) + (description is null ? string.Empty : "\n\n" + description);
        return new Hover(MarkupContent.Markdown(text), document.RangeOf(hovered.KeyStart, hovered.KeyEnd));
    }

    private static string Placeholder(List<string> types) => types switch
    {
        ["object"] => "{}",
        ["array"] => "[]",
        ["boolean"] => "false",
        ["integer"] or ["number"] => "0",
        _ => "\"\"",
    };
}
