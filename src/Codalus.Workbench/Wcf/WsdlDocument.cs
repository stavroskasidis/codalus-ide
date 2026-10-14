using System.Xml;
using System.Xml.Linq;

namespace Codalus.Workbench.Wcf;

/// <summary>
/// A field of a request or a data contract, from the service's XML schema: <see cref="Namespace"/> is the element's
/// namespace (empty when unqualified), <see cref="Type"/> an XML Schema type's local name (<c>int</c>, <c>string</c>)
/// for a value or the complex type's name, <see cref="Values"/> an enumeration's members.
/// </summary>
public sealed record WsdlField(
    string Name,
    string Namespace,
    string Type,
    bool Nillable,
    bool Repeated,
    IReadOnlyList<WsdlField> Children,
    IReadOnlyList<string> Values)
{
    public bool IsComplex => Children.Count > 0;
}

/// <summary>
/// An operation of an endpoint: its SOAP action, and its request's wrapper element (document/literal wrapped, as WCF
/// writes it) with the parameters. <see cref="Unsupported"/>: why the test client can't call it (a one-way callback of
/// a duplex contract, an rpc style), else null.
/// </summary>
public sealed record WsdlOperation(
    string Name,
    string Action,
    string? RequestElement,
    string RequestNamespace,
    IReadOnlyList<WsdlField> Parameters,
    bool OneWay,
    string? Unsupported);

/// <summary>
/// An endpoint (a WSDL port): its address and binding, SOAP 1.1 or 1.2, whether it uses WS-Addressing, and why the
/// test client can't call it (message security, a transport other than HTTP), else null.
/// </summary>
public sealed record WsdlEndpoint(
    string Service,
    string Name,
    string Binding,
    string Address,
    string SoapVersion,
    bool Addressing,
    IReadOnlyList<WsdlOperation> Operations,
    string? Unsupported)
{
    public string Label => $"{Service} / {Name}";
}

/// <summary>
/// A WCF service's metadata, read for the WCF Test Client (M42): WSDL 1.1 with its XML Schema types (inline, or
/// imported through <see cref="Parse"/>'s resolver), as WCF and CoreWCF publish it at <c>?wsdl</c> and
/// <c>?singleWsdl</c>. Not a general WSDL processor: what a request needs, and a reason where it can't be built.
/// </summary>
public sealed class WsdlDocument
{
    public const string WsdlNs = "http://schemas.xmlsoap.org/wsdl/";
    public const string Soap11BindingNs = "http://schemas.xmlsoap.org/wsdl/soap/";
    public const string Soap12BindingNs = "http://schemas.xmlsoap.org/wsdl/soap12/";
    public const string XsdNs = "http://www.w3.org/2001/XMLSchema";
    public const string HttpTransport = "http://schemas.xmlsoap.org/soap/http";
    private const int MaxDepth = 6;

    private readonly Dictionary<XName, XElement> _elements = [];
    private readonly Dictionary<XName, XElement> _complexTypes = [];
    private readonly Dictionary<XName, XElement> _simpleTypes = [];
    private readonly Dictionary<string, XElement> _policies = new(StringComparer.Ordinal);
    private readonly List<XElement> _definitions = [];

    private WsdlDocument()
    {
    }

    public string? TargetNamespace { get; private set; }

    public IReadOnlyList<WsdlEndpoint> Endpoints { get; private set; } = [];

    /// <summary>
    /// Reads a WSDL document; <paramref name="resolve"/> returns the text of an imported WSDL or schema
    /// (<c>location</c>/<c>schemaLocation</c>, relative to the importing document) or null when it can't.
    /// </summary>
    public static WsdlDocument Parse(string xml, Func<string, string?>? resolve = null)
    {
        var document = new WsdlDocument();
        var root = Load(xml);
        if (root.Name != XName.Get("definitions", WsdlNs))
        {
            throw new FormatException($"This isn't a WSDL document: its root is <{root.Name.LocalName}>.");
        }

        document.TargetNamespace = (string?)root.Attribute("targetNamespace");
        document.Collect(root, resolve, new HashSet<string>(StringComparer.Ordinal), 0);
        document.Endpoints = document.ReadEndpoints();
        return document;
    }

    private static XElement Load(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XElement.Load(reader, LoadOptions.None);
    }

    /// <summary>Indexes a document's definitions, schemas and policies, following its imports.</summary>
    private void Collect(XElement definitions, Func<string, string?>? resolve, HashSet<string> seen, int depth)
    {
        _definitions.Add(definitions);
        foreach (var policy in definitions.Elements().Where(e => e.Name.LocalName == "Policy"))
        {
            if (PolicyId(policy) is { } id)
            {
                _policies[id] = policy;
            }
        }

        foreach (var schema in definitions.Elements(XName.Get("types", WsdlNs)).Elements(XName.Get("schema", XsdNs)))
        {
            CollectSchema(schema, resolve, seen, depth);
        }

        if (depth > 8 || resolve is null)
        {
            return;
        }

        foreach (var import in definitions.Elements(XName.Get("import", WsdlNs)))
        {
            if ((string?)import.Attribute("location") is { Length: > 0 } location && seen.Add(location) && resolve(location) is { } text)
            {
                var imported = Load(text);
                if (imported.Name == XName.Get("definitions", WsdlNs))
                {
                    Collect(imported, resolve, seen, depth + 1);
                }
            }
        }
    }

    private void CollectSchema(XElement schema, Func<string, string?>? resolve, HashSet<string> seen, int depth)
    {
        var ns = (string?)schema.Attribute("targetNamespace") ?? string.Empty;
        var qualified = (string?)schema.Attribute("elementFormDefault") == "qualified";
        foreach (var child in schema.Elements())
        {
            // Each declaration remembers its schema's namespace and form for the elements it declares.
            child.RemoveAnnotations<SchemaInfo>();
            child.AddAnnotation(new SchemaInfo(ns, qualified));
            switch (child.Name.LocalName)
            {
                case "element" when (string?)child.Attribute("name") is { } name:
                    _elements[XName.Get(name, ns)] = child;
                    break;
                case "complexType" when (string?)child.Attribute("name") is { } name:
                    _complexTypes[XName.Get(name, ns)] = child;
                    break;
                case "simpleType" when (string?)child.Attribute("name") is { } name:
                    _simpleTypes[XName.Get(name, ns)] = child;
                    break;
                case "import" or "include" when resolve is not null && depth <= 8
                                                && (string?)child.Attribute("schemaLocation") is { Length: > 0 } location && seen.Add(location)
                                                && resolve(location) is { } text:
                    var imported = Load(text);
                    if (imported.Name == XName.Get("schema", XsdNs))
                    {
                        CollectSchema(imported, resolve, seen, depth + 1);
                    }

                    break;
            }
        }
    }

    private sealed record SchemaInfo(string Namespace, bool Qualified);

    private static string? PolicyId(XElement policy) =>
        policy.Attributes().FirstOrDefault(a => a.Name.LocalName == "Id")?.Value ?? (string?)policy.Attribute("Name");

    private IEnumerable<XElement> All(string localName) => _definitions.SelectMany(d => d.Elements(XName.Get(localName, WsdlNs)));

    private List<WsdlEndpoint> ReadEndpoints()
    {
        var endpoints = new List<WsdlEndpoint>();
        foreach (var service in All("service"))
        {
            var serviceName = (string?)service.Attribute("name") ?? "Service";
            foreach (var port in service.Elements(XName.Get("port", WsdlNs)))
            {
                var bindingName = Resolve(port, (string?)port.Attribute("binding"));
                var binding = All("binding").FirstOrDefault(b => bindingName is not null && Matches(b, bindingName));
                var soap11 = port.Element(XName.Get("address", Soap11BindingNs));
                var soap12 = port.Element(XName.Get("address", Soap12BindingNs));
                var address = (string?)(soap11 ?? soap12)?.Attribute("location");
                if (binding is null || address is null)
                {
                    continue; // not a SOAP port (HTTP GET/POST bindings)
                }

                var version = soap12 is not null ? "1.2" : "1.1";
                var soapNs = version == "1.2" ? Soap12BindingNs : Soap11BindingNs;
                var transport = (string?)binding.Element(XName.Get("binding", soapNs))?.Attribute("transport");
                var policy = PolicyOf(binding);
                var addressing = binding.Descendants().Any(e => e.Name.LocalName == "UsingAddressing")
                                 || policy?.Descendants().Any(e => e.Name.LocalName is "UsingAddressing" or "Addressing") == true;
                string? unsupported = transport is not null && transport != HttpTransport
                    ? $"its transport ({transport}) isn't HTTP; the test client sends SOAP over HTTP only"
                    : policy?.Descendants().Any(e => e.Name.LocalName is "SymmetricBinding" or "AsymmetricBinding") == true
                        ? "it uses message security, which the test client doesn't do"
                        : policy?.Descendants().Any(e => e.Name.LocalName is "OneWay" or "CompositeDuplex") == true
                            ? "it is a duplex endpoint"
                            : null;
                var operations = ReadOperations(binding, soapNs);
                endpoints.Add(new WsdlEndpoint(serviceName, (string?)port.Attribute("name") ?? bindingName!.LocalName, bindingName!.LocalName, address, version, addressing, operations, unsupported));
            }
        }

        return endpoints;
    }

    private XElement? PolicyOf(XElement binding)
    {
        var reference = binding.Elements().FirstOrDefault(e => e.Name.LocalName == "PolicyReference");
        if ((string?)reference?.Attribute("URI") is { } uri && _policies.TryGetValue(uri.TrimStart('#'), out var policy))
        {
            return policy;
        }

        return binding.Elements().FirstOrDefault(e => e.Name.LocalName == "Policy");
    }

    private List<WsdlOperation> ReadOperations(XElement binding, string soapNs)
    {
        var portTypeName = Resolve(binding, (string?)binding.Attribute("type"));
        var portType = All("portType").FirstOrDefault(p => portTypeName is not null && Matches(p, portTypeName));
        var style = (string?)binding.Element(XName.Get("binding", soapNs))?.Attribute("style") ?? "document";
        var operations = new List<WsdlOperation>();
        foreach (var operation in binding.Elements(XName.Get("operation", WsdlNs)))
        {
            var name = (string?)operation.Attribute("name") ?? "?";
            var abstractOperation = portType?.Elements(XName.Get("operation", WsdlNs)).FirstOrDefault(o => (string?)o.Attribute("name") == name);
            var input = abstractOperation?.Element(XName.Get("input", WsdlNs));
            var output = abstractOperation?.Element(XName.Get("output", WsdlNs));
            var action = (string?)operation.Element(XName.Get("operation", soapNs))?.Attribute("soapAction")
                         ?? input?.Attributes().FirstOrDefault(a => a.Name.LocalName == "Action")?.Value
                         ?? string.Empty;
            var operationStyle = (string?)operation.Element(XName.Get("operation", soapNs))?.Attribute("style") ?? style;
            if (input is null)
            {
                operations.Add(new WsdlOperation(name, action, null, string.Empty, [], OneWay: true, "it is a callback (output only) of a duplex contract"));
                continue;
            }

            if (operationStyle == "rpc")
            {
                operations.Add(new WsdlOperation(name, action, null, string.Empty, [], output is null, "it uses the rpc style; the test client builds document/literal requests"));
                continue;
            }

            var messageName = Resolve(input, (string?)input.Attribute("message"));
            var message = All("message").FirstOrDefault(m => messageName is not null && Matches(m, messageName));
            var part = message?.Elements(XName.Get("part", WsdlNs)).FirstOrDefault();
            var elementName = part is null ? null : Resolve(part, (string?)part.Attribute("element"));
            if (elementName is null || !_elements.TryGetValue(elementName, out var element))
            {
                operations.Add(new WsdlOperation(name, action, null, string.Empty, [], output is null,
                    part is null ? null : "its request isn't an element of the service's schema"));
                continue;
            }

            operations.Add(new WsdlOperation(name, action, elementName.LocalName, elementName.NamespaceName, ChildrenOf(element, 0), output is null, null));
        }

        return operations;
    }

    /// <summary>Whether a WSDL definition (message, binding, port type) is the one a qualified name refers to.</summary>
    private static bool Matches(XElement definition, XName name) =>
        (string?)definition.Attribute("name") == name.LocalName
        && ((string?)definition.Parent?.Attribute("targetNamespace") ?? string.Empty) == name.NamespaceName;

    /// <summary>A <c>prefix:name</c> reference resolved with the namespaces in scope at <paramref name="context"/>.</summary>
    private static XName? Resolve(XElement context, string? qualified)
    {
        if (string.IsNullOrEmpty(qualified))
        {
            return null;
        }

        var colon = qualified.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon < 0 ? string.Empty : qualified[..colon];
        var local = colon < 0 ? qualified : qualified[(colon + 1)..];
        var ns = prefix.Length == 0 ? context.GetDefaultNamespace() : context.GetNamespaceOfPrefix(prefix);
        return ns is null ? null : ns + local;
    }

    /// <summary>The fields of an element's or a type's content (its sequence, all or choice, with its base type's first).</summary>
    private List<WsdlField> ChildrenOf(XElement declaration, int depth)
    {
        if (depth > MaxDepth)
        {
            return [];
        }

        var complex = declaration.Name.LocalName == "complexType"
            ? declaration
            : declaration.Element(XName.Get("complexType", XsdNs))
              ?? (Resolve(declaration, (string?)declaration.Attribute("type")) is { } typeName && _complexTypes.TryGetValue(typeName, out var named) ? named : null);
        if (complex is null)
        {
            return [];
        }

        var fields = new List<WsdlField>();
        var content = complex.Element(XName.Get("complexContent", XsdNs))?.Elements().FirstOrDefault(e => e.Name.LocalName is "extension" or "restriction");
        if (content is not null && Resolve(content, (string?)content.Attribute("base")) is { } baseName && _complexTypes.TryGetValue(baseName, out var baseType))
        {
            fields.AddRange(ChildrenOf(baseType, depth + 1));
        }

        foreach (var element in ParticleElements(content ?? complex))
        {
            if (Field(element, complex, depth) is { } field)
            {
                fields.Add(field);
            }
        }

        return fields;
    }

    /// <summary>The elements a content model declares: in its sequences, choices and groups, not inside those elements' own types.</summary>
    private static IEnumerable<XElement> ParticleElements(XElement model)
    {
        foreach (var child in model.Elements())
        {
            if (child.Name == XName.Get("element", XsdNs))
            {
                yield return child;
            }
            else if (child.Name.LocalName is "sequence" or "all" or "choice")
            {
                foreach (var nested in ParticleElements(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private WsdlField? Field(XElement element, XElement owner, int depth)
    {
        var repeated = (string?)element.Attribute("maxOccurs") is { } max && max != "0" && max != "1";
        var nillable = (string?)element.Attribute("nillable") == "true";
        if (Resolve(element, (string?)element.Attribute("ref")) is { } referenced)
        {
            return _elements.TryGetValue(referenced, out var target)
                ? Describe(referenced.LocalName, referenced.NamespaceName, target, repeated, nillable || (string?)target.Attribute("nillable") == "true", depth)
                : new WsdlField(referenced.LocalName, referenced.NamespaceName, "anyType", nillable, repeated, [], []);
        }

        if ((string?)element.Attribute("name") is not { } name)
        {
            return null;
        }

        var info = (Top(owner) ?? Top(element))?.Annotation<SchemaInfo>();
        var form = (string?)element.Attribute("form");
        var qualified = form is not null ? form == "qualified" : info?.Qualified == true;
        return Describe(name, qualified ? info?.Namespace ?? string.Empty : string.Empty, element, repeated, nillable, depth);
    }

    /// <summary>The schema's top-level declaration that contains <paramref name="element"/> (it carries the schema's namespace).</summary>
    private static XElement? Top(XElement element)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            if (current.Annotation<SchemaInfo>() is not null)
            {
                return current;
            }
        }

        return null;
    }

    private WsdlField Describe(string name, string ns, XElement declaration, bool repeated, bool nillable, int depth)
    {
        var typeName = Resolve(declaration, (string?)declaration.Attribute("type"));
        if (declaration.Element(XName.Get("complexType", XsdNs)) is not null || (typeName is not null && _complexTypes.ContainsKey(typeName)))
        {
            return new WsdlField(name, ns, typeName?.LocalName ?? name, nillable, repeated, ChildrenOf(declaration, depth + 1), []);
        }

        var simple = declaration.Element(XName.Get("simpleType", XsdNs)) ?? (typeName is not null && _simpleTypes.TryGetValue(typeName, out var named) ? named : null);
        if (simple is not null)
        {
            var restriction = simple.Element(XName.Get("restriction", XsdNs));
            var values = restriction?.Elements(XName.Get("enumeration", XsdNs)).Select(e => (string?)e.Attribute("value") ?? string.Empty).ToArray() ?? [];
            var baseType = Resolve(restriction ?? simple, (string?)restriction?.Attribute("base"));
            // A list of enumeration values ([Flags]) or a restriction of a built-in type.
            return new WsdlField(name, ns, values.Length > 0 ? typeName?.LocalName ?? "enum" : baseType?.LocalName ?? typeName?.LocalName ?? "string", nillable, repeated, [], values);
        }

        return new WsdlField(name, ns, typeName?.LocalName ?? "string", nillable, repeated, [], []);
    }
}
