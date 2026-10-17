using System.Xml.Linq;

namespace Codalus.Workbench.Wcf;

/// <summary>
/// An element of a schema. <see cref="Name"/> is qualified as it appears in XML (the schema's namespace for a global
/// element or a qualified local one, none for an unqualified local one). The type is named (<see cref="TypeName"/>) or
/// anonymous (<see cref="Complex"/>, <see cref="Simple"/>); <see cref="MaxOccurs"/> is -1 for unbounded.
/// </summary>
public sealed record XsdElement(XName Name, XName? TypeName, XsdComplexType? Complex, XsdSimpleType? Simple, int MinOccurs, int MaxOccurs, bool Nillable)
{
    public bool IsRepeated => MaxOccurs is -1 or > 1;

    /// <summary>The global element a <c>ref</c> names (see <see cref="XsdSchemaSet.Dereference"/>).</summary>
    public XName? Reference { get; init; }
}

public sealed record XsdAttributeUse(XName Name, XName? TypeName, bool Required);

/// <summary>
/// A complex type's content, with the elements of the type it extends first. <see cref="SimpleContent"/>: the base of
/// simple content (text with attributes). <see cref="IsChoice"/>: its elements are alternatives.
/// </summary>
public sealed class XsdComplexType
{
    public XName? Name { get; init; }

    public XName? Base { get; set; }

    public List<XsdElement> Elements { get; } = [];

    public List<XsdAttributeUse> Attributes { get; } = [];

    public XName? SimpleContent { get; set; }

    public bool IsChoice { get; set; }

    public bool HasAny { get; set; }
}

/// <summary>A simple type: its built-in base, and its values when it's an enumeration.</summary>
public sealed record XsdSimpleType(XName? Name, XName? Base, IReadOnlyList<string> Enumeration);

/// <summary>
/// The schemas of a service description, read with <see cref="System.Xml.Linq"/> (no <c>XmlSchemaSet</c>): global
/// elements, complex and simple types by name. What a test client needs to build a request form: sequences, choices,
/// <c>all</c>, extensions, enumerations, <c>minOccurs</c>/<c>maxOccurs</c>, <c>nillable</c>, element and attribute references.
/// </summary>
public sealed class XsdSchemaSet
{
    private static readonly XNamespace s_xs = WsdlReader.XsdNamespace;

    public Dictionary<XName, XsdElement> Elements { get; } = [];

    public Dictionary<XName, XsdComplexType> ComplexTypes { get; } = [];

    public Dictionary<XName, XsdSimpleType> SimpleTypes { get; } = [];

    public int SchemaCount { get; private set; }

    public static bool IsBuiltIn(XName? type) => type is not null && type.NamespaceName == WsdlReader.XsdNamespace;

    /// <summary>Adds an <c>xs:schema</c> element (inline in a WSDL, or a document of its own).</summary>
    public void Add(XElement schema)
    {
        SchemaCount++;
        var tns = (string?)schema.Attribute("targetNamespace") ?? string.Empty;
        var qualified = (string?)schema.Attribute("elementFormDefault") == "qualified";
        var context = new Context(tns, qualified);
        foreach (var child in schema.Elements())
        {
            var name = (string?)child.Attribute("name");
            if (name is null)
            {
                continue;
            }

            if (child.Name == s_xs + "element")
            {
                Elements.TryAdd(XName.Get(name, tns), ReadElement(child, context, global: true));
            }
            else if (child.Name == s_xs + "complexType")
            {
                ComplexTypes.TryAdd(XName.Get(name, tns), ReadComplex(child, context, XName.Get(name, tns)));
            }
            else if (child.Name == s_xs + "simpleType")
            {
                SimpleTypes.TryAdd(XName.Get(name, tns), ReadSimple(child, XName.Get(name, tns)));
            }
        }
    }

    /// <summary>A <c>ref</c> particle as the global element it names, with the particle's occurrences.</summary>
    public XsdElement Dereference(XsdElement element) =>
        element.Reference is { } reference && Elements.TryGetValue(reference, out var global)
            ? global with { MinOccurs = element.MinOccurs, MaxOccurs = element.MaxOccurs, Nillable = element.Nillable || global.Nillable }
            : element;

    /// <summary>The complex type of an element, with base types' elements first; null for a simple one.</summary>
    public XsdComplexType? ComplexOf(XsdElement element) =>
        element.Complex ?? (element.TypeName is { } name && ComplexTypes.TryGetValue(name, out var complex) ? complex : null);

    /// <summary>A complex type's elements, its bases' first (an extension's content follows its base's).</summary>
    public IReadOnlyList<XsdElement> AllElements(XsdComplexType type)
    {
        var chain = new List<XsdComplexType>();
        for (var current = type; current is not null && chain.Count < 16; current = current.Base is { } b && ComplexTypes.TryGetValue(b, out var next) ? next : null)
        {
            chain.Insert(0, current);
        }

        return [.. chain.SelectMany(t => t.Elements)];
    }

    /// <summary>The simple type of an element or attribute (a user-defined restriction or list); null for built-ins.</summary>
    public XsdSimpleType? SimpleOf(XsdElement element) =>
        element.Simple ?? (element.TypeName is { } name && SimpleTypes.TryGetValue(name, out var simple) ? simple : null);

    /// <summary>The built-in type a simple type derives from (<c>xs:string</c> for an enumeration of strings).</summary>
    public XName? BuiltInOf(XName? type)
    {
        for (var i = 0; type is not null && i < 16; i++)
        {
            if (IsBuiltIn(type))
            {
                return type;
            }

            type = SimpleTypes.TryGetValue(type, out var simple) ? simple.Base : null;
        }

        return type;
    }

    private sealed record Context(string TargetNamespace, bool Qualified);

    private XsdElement ReadElement(XElement element, Context context, bool global)
    {
        var min = int.TryParse((string?)element.Attribute("minOccurs"), out var m) ? m : 1;
        var maxText = (string?)element.Attribute("maxOccurs");
        var max = maxText == "unbounded" ? -1 : int.TryParse(maxText, out var x) ? x : 1;
        var nillable = (string?)element.Attribute("nillable") == "true";
        if (WsdlReader.Resolve(element, (string?)element.Attribute("ref")) is { } reference)
        {
            // A reference to a global element: its name and type, with this particle's occurrences.
            return new XsdElement(reference, null, null, null, min, max, nillable) { Reference = reference };
        }

        var name = (string)element.Attribute("name")!;
        var form = (string?)element.Attribute("form");
        var qualified = global || (form is null ? context.Qualified : form == "qualified");
        var qname = XName.Get(name, qualified ? context.TargetNamespace : string.Empty);
        var type = WsdlReader.Resolve(element, (string?)element.Attribute("type"));
        var complex = element.Element(s_xs + "complexType") is { } inlineComplex ? ReadComplex(inlineComplex, context, null) : null;
        var simple = element.Element(s_xs + "simpleType") is { } inlineSimple ? ReadSimple(inlineSimple, null) : null;
        if (type is null && complex is null && simple is null)
        {
            type = XName.Get("anyType", WsdlReader.XsdNamespace);
        }

        return new XsdElement(qname, type, complex, simple, min, max, nillable);
    }

    private XsdComplexType ReadComplex(XElement complexType, Context context, XName? name)
    {
        var type = new XsdComplexType { Name = name };
        var content = complexType;
        if (complexType.Element(s_xs + "complexContent") is { } complexContent
            && (complexContent.Element(s_xs + "extension") ?? complexContent.Element(s_xs + "restriction")) is { } derivation)
        {
            if (derivation.Name.LocalName == "extension")
            {
                type.Base = WsdlReader.Resolve(derivation, (string?)derivation.Attribute("base"));
            }

            content = derivation;
        }
        else if (complexType.Element(s_xs + "simpleContent") is { } simpleContent
                 && (simpleContent.Element(s_xs + "extension") ?? simpleContent.Element(s_xs + "restriction")) is { } simpleDerivation)
        {
            type.SimpleContent = WsdlReader.Resolve(simpleDerivation, (string?)simpleDerivation.Attribute("base"));
            content = simpleDerivation;
        }

        foreach (var group in content.Elements().Where(e => e.Name == s_xs + "sequence" || e.Name == s_xs + "all" || e.Name == s_xs + "choice"))
        {
            ReadParticles(group, context, type);
        }

        foreach (var attribute in content.Elements(s_xs + "attribute"))
        {
            var attributeName = (string?)attribute.Attribute("name") is { } local ? XName.Get(local) : WsdlReader.Resolve(attribute, (string?)attribute.Attribute("ref"));
            if (attributeName is not null)
            {
                type.Attributes.Add(new XsdAttributeUse(attributeName, WsdlReader.Resolve(attribute, (string?)attribute.Attribute("type")), (string?)attribute.Attribute("use") == "required"));
            }
        }

        return type;
    }

    private void ReadParticles(XElement group, Context context, XsdComplexType type)
    {
        if (group.Name == s_xs + "choice")
        {
            type.IsChoice = true;
        }

        foreach (var particle in group.Elements())
        {
            if (particle.Name == s_xs + "element")
            {
                type.Elements.Add(ReadElement(particle, context, global: false));
            }
            else if (particle.Name == s_xs + "sequence" || particle.Name == s_xs + "choice" || particle.Name == s_xs + "all")
            {
                ReadParticles(particle, context, type);
            }
            else if (particle.Name == s_xs + "any")
            {
                type.HasAny = true;
            }
        }
    }

    private static XsdSimpleType ReadSimple(XElement simpleType, XName? name)
    {
        if (simpleType.Element(s_xs + "restriction") is { } restriction)
        {
            return new XsdSimpleType(
                name,
                WsdlReader.Resolve(restriction, (string?)restriction.Attribute("base")) ?? XName.Get("string", WsdlReader.XsdNamespace),
                [.. restriction.Elements(s_xs + "enumeration").Select(e => (string?)e.Attribute("value")).OfType<string>()]);
        }

        // A list or a union: text, as a string.
        return new XsdSimpleType(name, XName.Get("string", WsdlReader.XsdNamespace), []);
    }
}
