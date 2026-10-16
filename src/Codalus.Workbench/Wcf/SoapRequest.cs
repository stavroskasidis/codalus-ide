using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Codalus.Workbench.Wcf;

public enum SoapFieldKind
{
    /// <summary>Text: a string, a number, a date… (<see cref="SoapField.Value"/>).</summary>
    Simple,

    /// <summary>One of <see cref="SoapField.Choices"/>: an enumeration or a boolean.</summary>
    Choice,

    /// <summary>An element with child elements (<see cref="SoapField.Children"/>).</summary>
    Complex,

    /// <summary>A repeated element: <see cref="SoapField.Children"/> are its items, added from <see cref="SoapField.NewItem"/>.</summary>
    Repeated,
}

/// <summary>
/// A field of a request form, built from the message's schema (<see cref="SoapRequest.Fields"/>). An optional field
/// with no value is left out of the request; <see cref="IsNil"/> sends <c>xsi:nil="true"</c>. UI-agnostic: the view
/// models bind to it.
/// </summary>
public sealed class SoapField
{
    private readonly Func<SoapField>? _newItem;

    internal SoapField(XName name, SoapFieldKind kind, string typeLabel, int depth, Func<SoapField>? newItem = null)
    {
        Name = name;
        Kind = kind;
        TypeLabel = typeLabel;
        Depth = depth;
        _newItem = newItem;
    }

    public XName Name { get; }

    public SoapFieldKind Kind { get; }

    /// <summary>The schema type: <c>int</c>, <c>string</c>, <c>Person</c>, <c>ArrayOfstring</c>.</summary>
    public string TypeLabel { get; }

    /// <summary>How deep in the message the field is (the message's parameters are 0).</summary>
    public int Depth { get; }

    /// <summary>The built-in type of a simple field (<c>int</c>, <c>dateTime</c>…), for checking its value.</summary>
    public string? BuiltIn { get; init; }

    public bool Optional { get; init; }

    public bool Nillable { get; init; }

    /// <summary>The values of a <see cref="SoapFieldKind.Choice"/> field.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    public string? Value { get; set; }

    public bool IsNil { get; set; }

    public List<SoapField> Children { get; } = [];

    /// <summary>The schema went deeper than the form follows (a type that contains itself).</summary>
    public bool Truncated { get; init; }

    public bool CanAddItem => Kind == SoapFieldKind.Repeated && _newItem is not null;

    /// <summary>A new item of a repeated field, added to <see cref="Children"/>.</summary>
    public SoapField NewItem()
    {
        var item = _newItem?.Invoke() ?? throw new InvalidOperationException($"{Name.LocalName} isn't repeated.");
        Children.Add(item);
        return item;
    }

    /// <summary>The field and all the fields under it, depth first (the form's rows).</summary>
    public IEnumerable<SoapField> Flatten()
    {
        yield return this;
        foreach (var child in Children.SelectMany(c => c.Flatten()))
        {
            yield return child;
        }
    }

    /// <summary>Whether the field takes part in the request: it has a value, is nil, or holds a field that does.</summary>
    public bool HasContent => IsNil || Kind switch
    {
        SoapFieldKind.Simple or SoapFieldKind.Choice => !string.IsNullOrEmpty(Value),
        _ => Children.Any(c => c.HasContent) || (!Optional && Kind == SoapFieldKind.Complex),
    };
}

/// <summary>
/// A request to an operation (M42, ADR 0069): the form's fields from the input message's schema, and the SOAP envelope
/// built from them, SOAP 1.1 or 1.2, with WS-Addressing headers when the endpoint wants them (WSHttpBinding).
/// </summary>
public sealed class SoapRequest
{
    public const string Soap11Envelope = "http://schemas.xmlsoap.org/soap/envelope/";
    public const string Soap12Envelope = "http://www.w3.org/2003/05/soap-envelope";
    public const string Addressing10 = "http://www.w3.org/2005/08/addressing";
    public const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>How deep the form follows nested types (a type can contain itself).</summary>
    public const int MaxDepth = 8;

    private readonly XsdSchemaSet _schemas;

    public SoapRequest(WsdlDocument document, WsdlEndpoint endpoint, WsdlOperation operation)
    {
        Endpoint = endpoint;
        Operation = operation;
        _schemas = document.Schemas;
        foreach (var part in operation.Input.Parts)
        {
            if (part.Element is { } element)
            {
                var declared = _schemas.Elements.TryGetValue(element, out var global) ? global : new XsdElement(element, XName.Get("anyType", WsdlReader.XsdNamespace), null, null, 1, 1, false);
                Fields.Add(Build(declared with { MinOccurs = 1, MaxOccurs = 1 }, 0, []));
            }
            else
            {
                // RPC style: the part is an unqualified child of the operation's wrapper.
                Fields.Add(Build(new XsdElement(XName.Get(part.Name), part.Type ?? XName.Get("string", WsdlReader.XsdNamespace), null, null, 1, 1, false), 0, []));
            }
        }
    }

    public WsdlEndpoint Endpoint { get; }

    public WsdlOperation Operation { get; }

    /// <summary>
    /// The message's fields. In document/literal wrapped style (WCF's), there is one, the operation's wrapper element,
    /// whose children are the parameters.
    /// </summary>
    public List<SoapField> Fields { get; } = [];

    /// <summary>The fields a form shows: the parameters, under a wrapper element when there is one.</summary>
    public IReadOnlyList<SoapField> Parameters =>
        Fields is [{ Kind: SoapFieldKind.Complex } wrapper] && !Operation.Rpc ? wrapper.Children : Fields;

    private SoapField Build(XsdElement declared, int depth, HashSet<XName> path)
    {
        var element = _schemas.Dereference(declared);
        if (element.IsRepeated)
        {
            var single = element with { MinOccurs = 1, MaxOccurs = 1 };
            var copy = new HashSet<XName>(path);
            var repeated = new SoapField(element.Name, SoapFieldKind.Repeated, TypeLabel(element) + "[]", depth, () => Build(single, depth + 1, copy))
            {
                Optional = element.MinOccurs == 0,
            };
            for (var i = 0; i < element.MinOccurs && i < 10; i++)
            {
                repeated.NewItem();
            }

            return repeated;
        }

        var optional = element.MinOccurs == 0;
        if (_schemas.ComplexOf(element) is { } complex && complex.SimpleContent is null)
        {
            var typeKey = complex.Name ?? element.Name;
            var truncated = depth >= MaxDepth || path.Contains(typeKey);
            var field = new SoapField(element.Name, SoapFieldKind.Complex, TypeLabel(element), depth)
            {
                Optional = optional,
                Nillable = element.Nillable,
                Truncated = truncated,
            };
            if (!truncated)
            {
                var inner = new HashSet<XName>(path) { typeKey };
                foreach (var child in _schemas.AllElements(complex))
                {
                    field.Children.Add(Build(child, depth + 1, inner));
                }
            }

            return field;
        }

        var simple = _schemas.SimpleOf(element);
        var builtIn = _schemas.BuiltInOf(simple?.Base ?? (_schemas.ComplexOf(element)?.SimpleContent) ?? element.TypeName)?.LocalName ?? "string";
        var choices = simple is { Enumeration.Count: > 0 } ? simple.Enumeration : builtIn == "boolean" ? ["true", "false"] : [];
        return new SoapField(element.Name, choices.Count > 0 ? SoapFieldKind.Choice : SoapFieldKind.Simple, TypeLabel(element), depth)
        {
            BuiltIn = builtIn,
            Optional = optional,
            Nillable = element.Nillable,
            Choices = choices,
        };
    }

    private static string TypeLabel(XsdElement element) =>
        element.TypeName?.LocalName ?? (element.Complex is not null ? element.Name.LocalName : element.Simple?.Base?.LocalName ?? "anyType");

    /// <summary>The fields whose values don't fit their types (a number that isn't one…), as "path: problem".</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        foreach (var field in Fields)
        {
            Validate(field, string.Empty, problems);
        }

        return problems;
    }

    private static void Validate(SoapField field, string path, List<string> problems)
    {
        var here = path.Length == 0 ? field.Name.LocalName : path + "." + field.Name.LocalName;
        if (field.IsNil)
        {
            return;
        }

        if (field.Kind is SoapFieldKind.Simple && !string.IsNullOrEmpty(field.Value) && ValueProblem(field.BuiltIn, field.Value) is { } problem)
        {
            problems.Add($"{here}: {problem}");
        }
        else if (field.Kind is SoapFieldKind.Choice && !string.IsNullOrEmpty(field.Value) && !field.Choices.Contains(field.Value, StringComparer.Ordinal))
        {
            problems.Add($"{here}: '{field.Value}' isn't one of {string.Join(", ", field.Choices)}.");
        }

        for (var i = 0; i < field.Children.Count; i++)
        {
            Validate(field.Children[i], field.Kind == SoapFieldKind.Repeated ? $"{here}[{i}]" : here, problems);
        }
    }

    /// <summary>Why a value doesn't fit an XSD built-in type, or null.</summary>
    internal static string? ValueProblem(string? builtIn, string value)
    {
        var invariant = CultureInfo.InvariantCulture;
        var ok = builtIn switch
        {
            "int" or "integer" or "long" or "short" or "byte" or "nonNegativeInteger" or "positiveInteger" or "negativeInteger" or "nonPositiveInteger"
                or "unsignedInt" or "unsignedLong" or "unsignedShort" or "unsignedByte" => long.TryParse(value, NumberStyles.Integer, invariant, out _) || ulong.TryParse(value, NumberStyles.Integer, invariant, out _),
            "double" or "float" => value is "INF" or "-INF" or "NaN" || double.TryParse(value, NumberStyles.Float, invariant, out _),
            "decimal" => decimal.TryParse(value, NumberStyles.Number, invariant, out _),
            "boolean" => value is "true" or "false" or "1" or "0",
            "dateTime" or "date" or "time" => DateTimeOffset.TryParse(value, invariant, DateTimeStyles.AllowWhiteSpaces, out _) || IsXmlDate(value),
            "duration" => IsXmlDuration(value),
            "guid" => Guid.TryParse(value, out _),
            "anyURI" => Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _),
            _ => true,
        };
        return ok ? null : $"'{value}' isn't a valid {builtIn}.";
    }

    private static bool IsXmlDate(string value)
    {
        try
        {
            _ = XmlConvert.ToDateTimeOffset(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsXmlDuration(string value)
    {
        try
        {
            _ = XmlConvert.ToTimeSpan(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The SOAP envelope, indented. <paramref name="messageId"/> is the WS-Addressing message id (a new one when null).
    /// </summary>
    public string BuildEnvelope(string? messageId = null)
    {
        var soap12 = Endpoint.Soap == SoapVersion.Soap12;
        XNamespace s = soap12 ? Soap12Envelope : Soap11Envelope;
        XNamespace a = Addressing10;
        var envelope = new XElement(s + "Envelope", new XAttribute(XNamespace.Xmlns + "s", s.NamespaceName));
        if (soap12 && Endpoint.Addressing)
        {
            envelope.Add(new XAttribute(XNamespace.Xmlns + "a", Addressing10));
            envelope.Add(new XElement(
                s + "Header",
                new XElement(a + "Action", new XAttribute(s + "mustUnderstand", "1"), Operation.Action),
                new XElement(a + "MessageID", messageId ?? "urn:uuid:" + Guid.NewGuid().ToString("D")),
                new XElement(a + "ReplyTo", new XElement(a + "Address", Addressing10 + "/anonymous")),
                new XElement(a + "To", new XAttribute(s + "mustUnderstand", "1"), Endpoint.Address)));
        }

        var body = new XElement(s + "Body");
        envelope.Add(body);
        if (Operation.Rpc)
        {
            XNamespace rpc = Operation.RpcNamespace ?? string.Empty;
            var wrapper = new XElement(rpc + Operation.Name);
            foreach (var field in Fields)
            {
                Write(wrapper, field);
            }

            Declare(wrapper);
            body.Add(wrapper);
        }
        else
        {
            foreach (var field in Fields)
            {
                var before = body.Elements().Count();
                Write(body, field, force: true);
                foreach (var added in body.Elements().Skip(before))
                {
                    Declare(added);
                }
            }
        }

        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, IndentChars = "  ", NewLineChars = "\n" };
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, settings))
        {
            envelope.WriteTo(writer);
        }

        return text.ToString();
    }

    /// <summary>
    /// Declares a body element's namespaces once, on it: its own as the default, the others as <c>q1</c>, <c>q2</c>… and
    /// <c>i</c> for <c>xsi:nil</c>, as WCF writes them.
    /// </summary>
    private static void Declare(XElement root)
    {
        var namespaces = root.DescendantsAndSelf().Select(e => e.Name.Namespace).Where(n => n != XNamespace.None && n != root.Name.Namespace).Distinct().ToList();
        if (root.Name.Namespace != XNamespace.None)
        {
            root.Add(new XAttribute("xmlns", root.Name.NamespaceName));
        }

        for (var i = 0; i < namespaces.Count; i++)
        {
            root.Add(new XAttribute(XNamespace.Xmlns + ("q" + (i + 1).ToString(CultureInfo.InvariantCulture)), namespaces[i].NamespaceName));
        }

        if (root.DescendantsAndSelf().Any(e => e.Attribute(XName.Get("nil", XsiNamespace)) is not null))
        {
            root.Add(new XAttribute(XNamespace.Xmlns + "i", XsiNamespace));
        }
    }

    private static void Write(XElement parent, SoapField field, bool force = false)
    {
        if (!force && !field.HasContent)
        {
            return;
        }

        switch (field.Kind)
        {
            case SoapFieldKind.Repeated:
                foreach (var item in field.Children)
                {
                    Write(parent, item, force: !item.Optional || item.HasContent);
                }

                return;
            case SoapFieldKind.Complex:
            {
                var element = new XElement(field.Name);
                if (field.IsNil)
                {
                    element.Add(new XAttribute(XName.Get("nil", XsiNamespace), "true"));
                }
                else
                {
                    foreach (var child in field.Children)
                    {
                        // A required field is written even when empty, so the message keeps its shape.
                        Write(element, child, force: !child.Optional && child.Kind != SoapFieldKind.Repeated);
                    }
                }

                parent.Add(element);
                return;
            }

            default:
            {
                var element = new XElement(field.Name);
                if (field.IsNil)
                {
                    element.Add(new XAttribute(XName.Get("nil", XsiNamespace), "true"));
                }
                else
                {
                    element.Value = field.Value ?? string.Empty;
                }

                parent.Add(element);
                return;
            }
        }
    }
}
