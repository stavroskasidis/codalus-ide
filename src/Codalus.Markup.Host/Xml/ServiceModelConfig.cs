using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Markup.Host.Xml;

/// <summary>
/// The <c>system.serviceModel</c> section of <c>app.config</c> and <c>web.config</c> (M42, ADR 0069), from the bundled
/// <c>Schemas/system.serviceModel.xsd</c>: completion of its elements, attributes and values (with the document's own
/// binding and behavior names for <c>bindingConfiguration</c> and <c>behaviorConfiguration</c>), hover with the
/// schema's documentation, and validation of the section (warnings: the rest of the file isn't checked).
/// </summary>
internal sealed partial class ServiceModelConfig
{
    public const string Section = "system.serviceModel";

    private static readonly Lazy<(XmlSchemaSet? Set, string? Error)> s_schema = new(Load);

    /// <summary>An application or web configuration file: <c>app.config</c>, <c>web.config</c>, their transforms, <c>*.exe.config</c>.</summary>
    public static bool IsConfigFile(string? path)
    {
        if (path is null)
        {
            return false;
        }

        var name = System.IO.Path.GetFileName(path).ToLowerInvariant();
        return name is "app.config" or "web.config"
               || (name.EndsWith(".config", StringComparison.Ordinal) && (name.StartsWith("app.", StringComparison.Ordinal) || name.StartsWith("web.", StringComparison.Ordinal)
                   || name.EndsWith(".exe.config", StringComparison.Ordinal) || name.EndsWith(".dll.config", StringComparison.Ordinal)));
    }

    private static (XmlSchemaSet? Set, string? Error) Load()
    {
        try
        {
            using var stream = typeof(ServiceModelConfig).Assembly.GetManifestResourceStream("Schemas.system.serviceModel.xsd")
                               ?? throw new InvalidOperationException("The system.serviceModel schema isn't bundled.");
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var set = new XmlSchemaSet { XmlResolver = null };
            set.Add(null, reader);
            set.Compile();
            return (set, null);
        }
        catch (Exception ex) when (ex is XmlException or XmlSchemaException or InvalidOperationException)
        {
            return (null, ex.Message);
        }
    }

    private static XmlSchemaElement? Root =>
        s_schema.Value.Set?.GlobalElements[new XmlQualifiedName(Section)] as XmlSchemaElement;

    // ------------------------------------------------------------------ validation

    /// <summary>Each <c>system.serviceModel</c> element (without a namespace) validated against the schema.</summary>
    public IEnumerable<Diagnostic> Validate(TextDocument document)
    {
        if (!document.Text.Contains(Section, StringComparison.Ordinal))
        {
            return [];
        }

        if (s_schema.Value.Set is not { } set)
        {
            return [new Diagnostic(document.RangeOf(0, Math.Min(1, document.Text.Length)), DiagnosticSeverity.Warning, "xml.serviceModelSchema", "xml", $"The system.serviceModel schema couldn't be read: {s_schema.Value.Error}")];
        }

        var diagnostics = new List<Diagnostic>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(document.Text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var info = (IXmlLineInfo)reader;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != Section || reader.NamespaceURI.Length > 0)
                {
                    continue;
                }

                var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = set, XmlResolver = null };
                settings.ValidationEventHandler += (_, e) =>
                {
                    var line = e.Exception?.LineNumber is > 0 and var l ? l : info.LineNumber;
                    var column = e.Exception?.LinePosition is > 0 and var c ? c : info.LinePosition;
                    var start = document.OffsetAt(new Position(Math.Max(0, line - 1), Math.Max(0, column - 1)));
                    var end = start;
                    while (end < document.Text.Length && (char.IsLetterOrDigit(document.Text[end]) || document.Text[end] is ':' or '-' or '_' or '.'))
                    {
                        end++;
                    }

                    diagnostics.Add(new Diagnostic(document.RangeOf(start, Math.Max(end, Math.Min(document.Text.Length, start + 1))), DiagnosticSeverity.Warning, "xml.serviceModel", "xml", e.Message));
                };
                using var subtree = reader.ReadSubtree();
                using var validating = XmlReader.Create(subtree, settings);
                while (validating.Read())
                {
                }
            }
        }
        catch (XmlException)
        {
            // Not well-formed: the syntax check reports it.
        }

        return diagnostics;
    }

    // ------------------------------------------------------------------ completion

    public CompletionList? Completion(TextDocument document, int offset)
    {
        if (Root is not { } root)
        {
            return null;
        }

        var context = XmlLanguage.Locate(document.Text, offset);
        var range = document.RangeOf(context.ReplaceStart, context.ReplaceEnd);
        var items = new List<CompletionItem>();
        var at = context.Elements.ToList().LastIndexOf(Section);
        switch (context.Kind)
        {
            case XmlContextKind.TagName when at < 0:
                // The section itself, under <configuration>.
                if (context.Elements.Count == 1 && context.Elements[0] == "configuration")
                {
                    items.Add(Item(Section, CompletionItemKind.Module, Documentation(root), range));
                }

                break;
            case XmlContextKind.TagName:
                if (Find(root, context.Elements.Skip(at + 1)) is { } parent)
                {
                    items.AddRange(Children(parent).Select(c => Item(c.Name!, CompletionItemKind.Class, Documentation(c), range)));
                }

                break;
            case XmlContextKind.AttributeName when context.Element is { } name && (at >= 0 || name == Section):
                if (Find(root, Path(context, at)) is { } element)
                {
                    items.AddRange(Attributes(element).Select(a => new CompletionItem(a.Name!)
                    {
                        Kind = CompletionItemKind.Field,
                        Documentation = Documentation(a) is { } doc ? MarkupContent.Markdown(doc) : null,
                        TextEdit = new TextEdit(range, a.Name + "=\"\""),
                    }));
                }

                break;
            case XmlContextKind.AttributeValue when context.Element is not null && context.Attribute is { } attribute && at >= 0:
                if (Find(root, Path(context, at)) is { } owner && Attributes(owner).FirstOrDefault(a => a.Name == attribute) is { } declared)
                {
                    var values = attribute switch
                    {
                        "bindingConfiguration" => DeclaredNames(document.Text, BindingName()),
                        "behaviorConfiguration" => DeclaredNames(document.Text, BehaviorName()),
                        _ => Values(declared.AttributeSchemaType),
                    };
                    items.AddRange(values.Select(v => Item(v, CompletionItemKind.EnumMember, null, range)));
                }

                break;
        }

        return new CompletionList(false, [.. items]);
    }

    public Hover? Hover(TextDocument document, int offset)
    {
        if (Root is not { } root)
        {
            return null;
        }

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

        if (start == end)
        {
            return null;
        }

        var name = text[start..end];
        var context = XmlLanguage.Locate(text, start);
        var at = context.Elements.ToList().LastIndexOf(Section);
        string? documentation = null;
        string? title = null;
        if (start > 0 && (text[start - 1] == '<' || (start > 1 && text[start - 2] == '<' && text[start - 1] == '/')))
        {
            var path = at >= 0 ? context.Elements.Skip(at + 1).Append(name).ToList() : name == Section ? [] : null;
            if (path is not null && Find(root, path) is { } element)
            {
                (title, documentation) = ($"**{name}** (element)", Documentation(element));
            }
        }
        else if (context.Kind == XmlContextKind.AttributeName && context.Element is not null && at >= 0 && Find(root, Path(context, at)) is { } owner
                 && Attributes(owner).FirstOrDefault(a => a.Name == name) is { } attribute)
        {
            var values = Values(attribute.AttributeSchemaType);
            documentation = string.Join("\n\n", new[] { Documentation(attribute), values.Count > 0 ? "Values: " + string.Join(", ", values.Select(v => "`" + v + "`")) : null }.OfType<string>());
            title = $"**{name}** (attribute)";
        }

        return title is null ? null : new Hover(MarkupContent.Markdown(documentation is { Length: > 0 } ? title + "\n\n" + documentation : title), document.RangeOf(start, end));
    }

    /// <summary>The path below <c>system.serviceModel</c> to the element whose tag the position is in.</summary>
    private static IEnumerable<string> Path(XmlContext context, int at) =>
        context.Element == Section && at < 0 ? [] : context.Elements.Skip(at + 1).Append(context.Element!);

    /// <summary>The schema element at <paramref name="path"/> under the section's root (empty: the root).</summary>
    private static XmlSchemaElement? Find(XmlSchemaElement root, IEnumerable<string> path)
    {
        var current = root;
        foreach (var name in path)
        {
            current = Children(current).FirstOrDefault(c => c.Name == name);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static IReadOnlyList<XmlSchemaElement> Children(XmlSchemaElement element)
    {
        var children = new List<XmlSchemaElement>();
        if (element.ElementSchemaType is XmlSchemaComplexType { ContentTypeParticle: var particle })
        {
            Collect(particle, children);
        }

        return [.. children.DistinctBy(c => c.Name).OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    private static void Collect(XmlSchemaParticle? particle, List<XmlSchemaElement> children)
    {
        switch (particle)
        {
            case XmlSchemaElement element when element.Name is not null:
                children.Add(element);
                break;
            case XmlSchemaGroupBase group:
                foreach (var item in group.Items.OfType<XmlSchemaParticle>())
                {
                    Collect(item, children);
                }

                break;
        }
    }

    private static IReadOnlyList<XmlSchemaAttribute> Attributes(XmlSchemaElement element) =>
        element.ElementSchemaType is XmlSchemaComplexType complex
            ? [.. complex.AttributeUses.Values.OfType<XmlSchemaAttribute>().OrderBy(a => a.Name, StringComparer.Ordinal)]
            : [];

    /// <summary>A simple type's enumerated values (a union's members' too); booleans' true and false.</summary>
    internal static IReadOnlyList<string> Values(XmlSchemaSimpleType? type)
    {
        var values = new List<string>();
        Gather(type, values, 0);
        return values;

        static void Gather(XmlSchemaSimpleType? type, List<string> values, int depth)
        {
            if (type is null || depth > 8)
            {
                return;
            }

            if (type.TypeCode == XmlTypeCode.Boolean)
            {
                values.AddRange(["true", "false"]);
                return;
            }

            switch (type.Content)
            {
                case XmlSchemaSimpleTypeRestriction restriction:
                    values.AddRange(restriction.Facets.OfType<XmlSchemaEnumerationFacet>().Select(f => f.Value).OfType<string>());
                    break;
                case XmlSchemaSimpleTypeUnion union:
                    foreach (var member in union.BaseMemberTypes ?? [])
                    {
                        Gather(member, values, depth + 1);
                    }

                    break;
            }
        }
    }

    private static string? Documentation(XmlSchemaAnnotated annotated)
    {
        var annotation = annotated.Annotation
                         ?? (annotated as XmlSchemaElement)?.ElementSchemaType?.Annotation
                         ?? (annotated as XmlSchemaAttribute)?.AttributeSchemaType?.Annotation;
        var text = annotation?.Items.OfType<XmlSchemaDocumentation>().SelectMany(d => d.Markup ?? []).Select(n => n?.Value?.Trim()).FirstOrDefault(t => t is { Length: > 0 });
        return text;
    }

    private static IReadOnlyList<string> DeclaredNames(string text, Regex pattern) =>
        [.. pattern.Matches(text).Select(m => m.Groups[1].Value).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static CompletionItem Item(string label, int kind, string? documentation, LanguageServices.Protocol.Range range) => new(label)
    {
        Kind = kind,
        Documentation = documentation is null ? null : MarkupContent.Markdown(documentation),
        TextEdit = new TextEdit(range, label),
    };

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    [GeneratedRegex(@"<binding\s[^>]*?\bname\s*=\s*""([^""]*)""")]
    private static partial Regex BindingName();

    [GeneratedRegex(@"<behavior\s[^>]*?\bname\s*=\s*""([^""]*)""")]
    private static partial Regex BehaviorName();
}
