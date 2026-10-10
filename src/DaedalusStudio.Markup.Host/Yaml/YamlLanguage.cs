using System.Globalization;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Markup.Host.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DaedalusStudio.Markup.Host.Yaml;

/// <summary>
/// YAML (ADR 0016): syntax errors, and schema validation and hover through the JSON schema machinery: the YAML is
/// converted to positioned JSON values, so a SchemaStore schema (GitHub Actions, Azure Pipelines, …) applies as is.
/// </summary>
internal sealed class YamlLanguage(SchemaStore schemas)
{
    public static bool Handles(string languageId) => languageId == "yaml";

    public async Task<List<Diagnostic>> DiagnosticsAsync(TextDocument document, CancellationToken cancellationToken)
    {
        var (root, error) = Parse(document.Text);
        if (error is { } e)
        {
            var start = document.OffsetAt(new Position((int)Math.Max(0, e.Start.Line - 1), (int)Math.Max(0, e.Start.Column - 1)));
            var message = e.InnerException?.Message ?? e.Message;
            return [new Diagnostic(document.RangeOf(start, Math.Min(document.Text.Length, start + 1)), DiagnosticSeverity.Error, "yaml.syntax", "yaml", Clean(message))];
        }

        if (root is null || await schemas.ForAsync(document.Path, root, cancellationToken).ConfigureAwait(false) is not { } schema)
        {
            return [];
        }

        return [.. schema.Validate(root).Take(200).Select(p => new Diagnostic(
            document.RangeOf(p.Start, Math.Max(p.Start + 1, p.End)),
            p.IsWarning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
            "yaml.schema",
            "yaml",
            p.Message))];
    }

    public async Task<Hover?> HoverAsync(TextDocument document, int offset, CancellationToken cancellationToken)
    {
        var (root, _) = Parse(document.Text);
        if (root is null || await schemas.ForAsync(document.Path, root, cancellationToken).ConfigureAwait(false) is not { } schema)
        {
            return null;
        }

        var path = new List<string>();
        var node = root;
        while (node is not null)
        {
            JsonNodeInfo? next = null;
            foreach (var member in node.Members)
            {
                if (offset >= member.KeyStart && offset <= member.KeyEnd)
                {
                    path.Add(member.Key);
                    var schemasAt = schema.At(path);
                    var description = schemasAt.Select(JsonSchema.Description).FirstOrDefault(d => d is not null);
                    return description is null ? null : new Hover(MarkupContent.Markdown($"**{member.Key}**\n\n{description}"), document.RangeOf(member.KeyStart, member.KeyEnd));
                }

                if (offset >= member.Value.Start && offset <= member.Value.End)
                {
                    path.Add(member.Key);
                    next = member.Value;
                    break;
                }
            }

            for (var i = 0; next is null && i < node.Items.Count; i++)
            {
                if (offset >= node.Items[i].Start && offset <= node.Items[i].End)
                {
                    path.Add(i.ToString(CultureInfo.InvariantCulture));
                    next = node.Items[i];
                }
            }

            node = next;
        }

        return null;
    }

    /// <summary>The first document as positioned JSON values, or the syntax error.</summary>
    internal static (JsonNodeInfo? Root, YamlException? Error) Parse(string text)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            return (stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode), null);
        }
        catch (YamlException e)
        {
            return (null, e);
        }
    }

    private static JsonNodeInfo Convert(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                var result = new JsonNodeInfo(JsonKind.Object, (int)mapping.Start.Index) { End = (int)mapping.End.Index };
                foreach (var (key, value) in mapping.Children)
                {
                    var name = key is YamlScalarNode scalar ? scalar.Value ?? string.Empty : key.ToString();
                    result.Members.Add(new JsonMember(name, (int)key.Start.Index, (int)key.End.Index, Convert(value)));
                }

                return result;
            case YamlSequenceNode sequence:
                var array = new JsonNodeInfo(JsonKind.Array, (int)sequence.Start.Index) { End = (int)sequence.End.Index };
                array.Items.AddRange(sequence.Children.Select(Convert));
                return array;
            case YamlScalarNode scalar:
                var start = (int)scalar.Start.Index;
                var end = (int)scalar.End.Index;
                var text = scalar.Value ?? string.Empty;

                // Plain scalars follow YAML's core schema; quoted ones are strings.
                if (scalar.Style == ScalarStyle.Plain)
                {
                    if (text is "true" or "True" or "TRUE")
                    {
                        return new JsonNodeInfo(JsonKind.True, start) { End = end };
                    }

                    if (text is "false" or "False" or "FALSE")
                    {
                        return new JsonNodeInfo(JsonKind.False, start) { End = end };
                    }

                    if (text is "" or "~" or "null" or "Null" or "NULL")
                    {
                        return new JsonNodeInfo(JsonKind.Null, start) { End = end };
                    }

                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        return new JsonNodeInfo(JsonKind.Number, start) { End = end, Number = number, Text = text };
                    }
                }

                return new JsonNodeInfo(JsonKind.String, start) { End = end, Text = text };
            default:
                return new JsonNodeInfo(JsonKind.Null, (int)node.Start.Index) { End = (int)node.End.Index };
        }
    }

    private static string Clean(string message)
    {
        // YamlDotNet prefixes positions, e.g. "(Line: 3, Col: 5, Idx: 20) - (...): message".
        var at = message.LastIndexOf("): ", StringComparison.Ordinal);
        return at >= 0 ? message[(at + 3)..] : message;
    }
}
