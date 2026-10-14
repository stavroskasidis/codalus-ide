using System.Text.RegularExpressions;
using System.Xml;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Markup.Host.Xml;

/// <summary>WCF's <c>&lt;system.serviceModel&gt;</c> in <c>.config</c> files (M42): completion, hover and checks from <see cref="ServiceModelKnowledge"/>.</summary>
internal sealed partial class XmlLanguage
{
    private static bool IsServiceModelConfig(TextDocument document) =>
        !IsMsBuild(document) && ServiceModelKnowledge.IsConfigFile(document.Path) && document.Text.Contains(ServiceModelKnowledge.Section, StringComparison.Ordinal);

    private static CompletionList ServiceModelCompletion(TextDocument document, int offset)
    {
        var context = Locate(document.Text, offset);
        var range = document.RangeOf(context.ReplaceStart, context.ReplaceEnd);
        var items = new List<CompletionItem>();
        switch (context.Kind)
        {
            case XmlContextKind.TagName when ServiceModelKnowledge.Find(context.Elements) is { } parent:
                items.AddRange(parent.Children.Values.Select(c => Item(c.Name, CompletionItemKind.Class, c.Description, range)));
                break;
            case XmlContextKind.TagName when context.Elements.Count > 0 && context.Elements[^1] == "configuration":
                items.Add(Item(ServiceModelKnowledge.Section, CompletionItemKind.Module, ServiceModelKnowledge.Root.Description, range));
                break;
            case XmlContextKind.AttributeName when context.Element is { } name && ServiceModelKnowledge.Find([.. context.Elements, name]) is { } element:
                items.AddRange(element.Attributes.Keys.Select(a => new CompletionItem(a) { Kind = CompletionItemKind.Field, TextEdit = new TextEdit(range, a + "=\"\"") }));
                break;
            case XmlContextKind.AttributeValue when context.Element is { } name && context.Attribute is { } attribute && ServiceModelKnowledge.Find([.. context.Elements, name]) is { } element:
                var values = attribute switch
                {
                    // The configurations the file declares.
                    "bindingConfiguration" => DeclaredNames(document.Text, "binding"),
                    "behaviorConfiguration" => DeclaredNames(document.Text, "behavior"),
                    _ => element.Attributes.GetValueOrDefault(attribute) ?? [],
                };
                items.AddRange(values.Select(v => Item(v, CompletionItemKind.EnumMember, null, range)));
                break;
        }

        return new CompletionList(false, [.. items]);
    }

    /// <summary>The <c>name</c>s of the file's <c>&lt;binding&gt;</c> or <c>&lt;behavior&gt;</c> elements.</summary>
    private static string[] DeclaredNames(string text, string element) =>
        [.. Regex.Matches(text, "<" + element + @"\b[^>]*\bname\s*=\s*""([^""]+)""", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    private static Hover? ServiceModelHover(TextDocument document, int offset)
    {
        var context = Locate(document.Text, offset);
        if (context.Kind != XmlContextKind.TagName)
        {
            return null;
        }

        var start = context.ReplaceStart;
        var end = context.ReplaceEnd;
        var name = document.Text[start..end];
        var element = name == ServiceModelKnowledge.Section ? ServiceModelKnowledge.Root : ServiceModelKnowledge.Find([.. context.Elements, name]);
        return element?.Description is { } description
            ? new Hover(MarkupContent.Markdown($"**{name}**\n\n{description}"), document.RangeOf(start, end))
            : null;
    }

    /// <summary>Unknown elements, and values outside an attribute's choices, inside <c>&lt;system.serviceModel&gt;</c>.</summary>
    private static void ServiceModelDiagnostics(TextDocument document, List<Diagnostic> diagnostics)
    {
        using var reader = XmlReader.Create(new StringReader(document.Text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var info = (IXmlLineInfo)reader;
        var path = new List<string>();
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    path.RemoveAt(path.Count - 1);
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var name = reader.Name;
                var line = info.LineNumber - 1;
                var column = info.LinePosition - 1;
                if (ServiceModelKnowledge.Find(path) is { } parent)
                {
                    if (!parent.Children.TryGetValue(name, out var element))
                    {
                        // Inside <extensions> and <standardEndpoints> anything goes; elsewhere it's likely a typo.
                        if (parent.Name is not ("extensions" or "standardEndpoints" or "headers" or "clientCredentials" or "metadata"))
                        {
                            var start = document.OffsetAt(new Position(line, column));
                            diagnostics.Add(new Diagnostic(document.RangeOf(start, start + name.Length), DiagnosticSeverity.Warning, "config.element", "config",
                                $"<{name}> isn't an element of <{parent.Name}>. Expected: {string.Join(", ", parent.Children.Keys)}."));
                        }
                    }
                    else if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            if (element.Attributes.TryGetValue(reader.Name, out var allowed) && allowed.Length > 0 && reader.Value.Length > 0
                                && !allowed.Contains(reader.Value, StringComparer.Ordinal))
                            {
                                var start = document.OffsetAt(new Position(info.LineNumber - 1, info.LinePosition - 1));
                                diagnostics.Add(new Diagnostic(document.RangeOf(start, start + reader.Name.Length), DiagnosticSeverity.Warning, "config.value", "config",
                                    $"'{reader.Value}' isn't a value of {reader.Name}. Expected: {string.Join(", ", allowed)}."));
                            }
                        }
                        while (reader.MoveToNextAttribute());

                        reader.MoveToElement();
                    }
                }

                if (!reader.IsEmptyElement)
                {
                    path.Add(name);
                }
            }
        }
        catch (XmlException)
        {
            // Reported as a syntax error already.
        }
    }
}
