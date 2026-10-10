using System.Text.RegularExpressions;
using System.Xml;
using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.Markup.Host.Xml;

/// <summary>A property's evaluated value and where it is set, from the build host (via the shell).</summary>
internal sealed record EvaluatedProperty(string? Value, string? Origin);

internal enum XmlContextKind
{
    TagName,
    AttributeName,
    AttributeValue,
    Text,
    None,
}

/// <summary>Where a position is in XML: in a tag name, an attribute or element text, with the enclosing elements.</summary>
internal sealed record XmlContext(XmlContextKind Kind, IReadOnlyList<string> Elements, string? Element, string? Attribute, int ReplaceStart, int ReplaceEnd);

/// <summary>
/// XML (well-formedness) and MSBuild (ADR 0016): completion of elements, properties, items, metadata, attributes and
/// values, hover with documentation and the evaluated value from the build host, and checks of well-known values.
/// </summary>
internal sealed partial class XmlLanguage(Func<string, string, CancellationToken, Task<EvaluatedProperty?>>? evaluate)
{
    public static bool Handles(string languageId) => languageId is "xml" or "msbuild";

    private static bool IsMsBuild(TextDocument document) => document.LanguageId == "msbuild";

    public List<Diagnostic> Diagnostics(TextDocument document)
    {
        var diagnostics = new List<Diagnostic>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(document.Text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            while (reader.Read())
            {
            }
        }
        catch (XmlException e)
        {
            var position = new Position(Math.Max(0, e.LineNumber - 1), Math.Max(0, e.LinePosition - 1));
            var start = document.OffsetAt(position);
            diagnostics.Add(new Diagnostic(document.RangeOf(start, Math.Min(document.Text.Length, start + 1)), DiagnosticSeverity.Error, "xml.syntax", "xml", e.Message.Split(" Line ")[0]));
            return diagnostics;
        }

        if (IsMsBuild(document))
        {
            // Values of well-known properties that are choices.
            foreach (Match match in PropertyElement().Matches(document.Text))
            {
                var name = match.Groups[1].Value;
                var value = match.Groups[2].Value.Trim();
                var allowed = name is "TargetFramework" or "RuntimeIdentifier" ? [] : MsBuildKnowledge.ValuesFor(name);
                if (allowed.Length > 0 && value.Length > 0 && !value.Contains("$(", StringComparison.Ordinal)
                    && !allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    var start = match.Groups[2].Index;
                    diagnostics.Add(new Diagnostic(document.RangeOf(start, start + match.Groups[2].Length), DiagnosticSeverity.Warning, "msbuild.value", "msbuild",
                        $"'{value}' isn't a known value of {name}. Expected: {string.Join(", ", allowed)}."));
                }

                if (name == "TargetFramework" && value.Length > 0 && !value.Contains("$(", StringComparison.Ordinal) && !Tfm().IsMatch(value))
                {
                    var start = match.Groups[2].Index;
                    diagnostics.Add(new Diagnostic(document.RangeOf(start, start + match.Groups[2].Length), DiagnosticSeverity.Warning, "msbuild.tfm", "msbuild",
                        $"'{value}' doesn't look like a target framework (e.g. net10.0, netstandard2.0, net48)."));
                }
            }
        }

        return diagnostics;
    }

    public CompletionList Completion(TextDocument document, int offset)
    {
        if (!IsMsBuild(document))
        {
            return new CompletionList(false, []);
        }

        var context = Locate(document.Text, offset);
        var range = document.RangeOf(context.ReplaceStart, context.ReplaceEnd);
        var parent = context.Elements.Count > 0 ? context.Elements[^1] : null;
        var grandparent = context.Elements.Count > 1 ? context.Elements[^2] : null;
        var items = new List<CompletionItem>();
        switch (context.Kind)
        {
            case XmlContextKind.TagName:
                if (parent is null or "Project")
                {
                    items.AddRange(MsBuildKnowledge.ProjectChildren.Select(e => Item(e, CompletionItemKind.Class, null, range)));
                }
                else if (parent == "PropertyGroup")
                {
                    items.AddRange(MsBuildKnowledge.Properties.Select(p => Item(p.Name, CompletionItemKind.Property, p.Description, range)));
                }
                else if (parent is "ItemGroup" or "ItemDefinitionGroup")
                {
                    items.AddRange(MsBuildKnowledge.Items.Select(i => Item(i.Name, CompletionItemKind.Class, i.Description, range)));
                }
                else if (parent == "Target")
                {
                    items.AddRange(MsBuildKnowledge.Tasks.Select(t => Item(t, CompletionItemKind.Function, null, range)));
                }
                else if (grandparent is "ItemGroup" or "ItemDefinitionGroup" && MsBuildKnowledge.Item(parent) is { } item)
                {
                    items.AddRange(item.Metadata.Select(m => Item(m, CompletionItemKind.Field, null, range)));
                }

                break;
            case XmlContextKind.AttributeName when context.Element is { } element:
                var attributes = MsBuildKnowledge.Attributes.GetValueOrDefault(element)
                    ?? (parent is "ItemGroup" or "ItemDefinitionGroup" ? [.. MsBuildKnowledge.ItemAttributes, .. MsBuildKnowledge.Item(element)?.Metadata ?? []] : ["Condition"]);
                items.AddRange(attributes.Select(a => new CompletionItem(a) { Kind = CompletionItemKind.Field, TextEdit = new TextEdit(range, a + "=\"\"") }));
                break;
            case XmlContextKind.AttributeValue when context.Attribute is { } attribute:
                items.AddRange((MsBuildKnowledge.MetadataValues.GetValueOrDefault(attribute) ?? []).Select(v => Item(v, CompletionItemKind.EnumMember, null, range)));
                break;
            case XmlContextKind.Text when parent is not null:
                var values = grandparent == "PropertyGroup" || context.Elements.Contains("PropertyGroup")
                    ? MsBuildKnowledge.ValuesFor(parent)
                    : MsBuildKnowledge.MetadataValues.GetValueOrDefault(parent) ?? [];
                items.AddRange(values.Select(v => Item(v, CompletionItemKind.EnumMember, null, range)));
                break;
        }

        return new CompletionList(false, [.. items]);
    }

    public async Task<Hover?> HoverAsync(TextDocument document, int offset, CancellationToken cancellationToken)
    {
        if (!IsMsBuild(document))
        {
            return null;
        }

        // The element name under the position.
        var text = document.Text;
        var start = offset;
        while (start > 0 && IsNameChar(text[start - 1]))
        {
            start--;
        }

        var end = offset;
        while (end < text.Length && IsNameChar(text[end]))
        {
            end++;
        }

        if (start == end || start == 0 || !(text[start - 1] == '<' || (start > 1 && text[start - 2] == '<' && text[start - 1] == '/')))
        {
            return null;
        }

        var name = text[start..end];
        var context = Locate(text, start);
        var parent = context.Elements.Count > 0 ? context.Elements[^1] : null;
        var lines = new List<string>();
        if (parent == "PropertyGroup" || MsBuildKnowledge.Property(name) is not null && parent is not ("ItemGroup" or "Project"))
        {
            lines.Add($"**{name}** (property)");
            if (MsBuildKnowledge.Property(name) is { } property)
            {
                lines.Add(property.Description);
                if (property.Values is { Length: > 0 } values)
                {
                    lines.Add("Values: " + string.Join(", ", values.Select(v => "`" + v + "`")));
                }
            }

            // The evaluated value, for a project file the build host knows.
            if (evaluate is not null && document.Path is { } path && IsProjectFile(path)
                && await evaluate(path, name, cancellationToken).ConfigureAwait(false) is { } evaluated)
            {
                lines.Add($"Evaluated: `{(string.IsNullOrEmpty(evaluated.Value) ? "(empty)" : evaluated.Value)}`" + (evaluated.Origin is { Length: > 0 } origin ? $" — {origin}" : string.Empty));
            }
        }
        else if (MsBuildKnowledge.Item(name) is { } item)
        {
            lines.Add($"**{name}** (item)");
            lines.Add(item.Description);
            if (item.Metadata.Length > 0)
            {
                lines.Add("Metadata: " + string.Join(", ", item.Metadata.Select(m => "`" + m + "`")));
            }
        }
        else
        {
            return null;
        }

        return new Hover(MarkupContent.Markdown(string.Join("\n\n", lines)), document.RangeOf(start, end));
    }

    private static bool IsProjectFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".proj" or ".esproj" or ".sqlproj";

    private static CompletionItem Item(string label, int kind, string? description, LanguageServices.Protocol.Range range) => new(label)
    {
        Kind = kind,
        Documentation = description is null ? null : MarkupContent.Markdown(description),
        TextEdit = new TextEdit(range, label),
    };

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    /// <summary>Scans tolerantly from the start to <paramref name="offset"/>: the open elements and what the position is in.</summary>
    internal static XmlContext Locate(string text, int offset)
    {
        var elements = new List<string>();
        var i = 0;
        while (i < offset)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }

            if (Starts(text, i, "<!--"))
            {
                var close = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (close < 0 || close + 3 > offset)
                {
                    return new XmlContext(XmlContextKind.None, elements, null, null, offset, offset);
                }

                i = close + 3;
                continue;
            }

            if (Starts(text, i, "<![CDATA["))
            {
                var close = text.IndexOf("]]>", i, StringComparison.Ordinal);
                if (close < 0 || close + 3 > offset)
                {
                    return new XmlContext(XmlContextKind.None, elements, null, null, offset, offset);
                }

                i = close + 3;
                continue;
            }

            if (Starts(text, i, "<?") || Starts(text, i, "<!"))
            {
                var close = text.IndexOf('>', i);
                i = close < 0 ? text.Length : close + 1;
                continue;
            }

            var closing = i + 1 < text.Length && text[i + 1] == '/';
            var nameStart = i + (closing ? 2 : 1);
            var nameEnd = nameStart;
            while (nameEnd < text.Length && IsNameChar(text[nameEnd]))
            {
                nameEnd++;
            }

            if (offset <= nameEnd)
            {
                // Typing a tag name.
                return new XmlContext(XmlContextKind.TagName, elements, null, null, nameStart, nameEnd);
            }

            var name = text[nameStart..nameEnd];

            // Through the tag's attributes to '>' (or to the position).
            var j = nameEnd;
            string? attribute = null;
            while (j < text.Length && text[j] != '>')
            {
                if (text[j] is '"' or '\'')
                {
                    var quote = text[j];
                    var close = text.IndexOf(quote, j + 1);
                    if (offset > j && (close < 0 || offset <= close))
                    {
                        return new XmlContext(XmlContextKind.AttributeValue, elements, name, attribute, j + 1, close < 0 ? offset : close);
                    }

                    j = close < 0 ? text.Length : close + 1;
                    continue;
                }

                if (IsNameChar(text[j]))
                {
                    var attributeStart = j;
                    while (j < text.Length && IsNameChar(text[j]))
                    {
                        j++;
                    }

                    if (offset >= attributeStart && offset <= j)
                    {
                        return new XmlContext(XmlContextKind.AttributeName, elements, name, null, attributeStart, j);
                    }

                    attribute = text[attributeStart..j];
                    continue;
                }

                if (j >= offset)
                {
                    return new XmlContext(XmlContextKind.AttributeName, elements, name, null, offset, offset);
                }

                j++;
            }

            if (j >= offset && j < text.Length)
            {
                return new XmlContext(XmlContextKind.AttributeName, elements, name, null, offset, offset);
            }

            var selfClosing = j > 0 && j - 1 < text.Length && text[j - 1] == '/';
            if (closing)
            {
                var last = elements.FindLastIndex(e => e == name);
                if (last >= 0)
                {
                    elements.RemoveRange(last, elements.Count - last);
                }
            }
            else if (!selfClosing)
            {
                elements.Add(name);
            }

            i = j + 1;
        }

        // In element text: the run of non-markup characters around the position.
        var textStart = offset;
        while (textStart > 0 && text[textStart - 1] is not ('>' or '<' or '\n'))
        {
            textStart--;
        }

        var textEnd = offset;
        while (textEnd < text.Length && text[textEnd] is not ('<' or '\n'))
        {
            textEnd++;
        }

        while (textStart < offset && char.IsWhiteSpace(text[textStart]))
        {
            textStart++;
        }

        return new XmlContext(XmlContextKind.Text, elements, null, null, textStart, textEnd);
    }

    private static bool Starts(string text, int index, string value) => string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    [GeneratedRegex(@"<([A-Za-z_][\w.]*)(?:\s[^>]*)?>([^<]*)</\1>")]
    private static partial Regex PropertyElement();

    [GeneratedRegex(@"^(net\d+\.\d+(-[a-z]+(\d+(\.\d+)*)?)?|netstandard\d\.\d|netcoreapp\d\.\d|net\d{2,3})$")]
    private static partial Regex Tfm();
}
