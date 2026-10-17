using System.Xml;
using System.Xml.Linq;

namespace Codalus.Workbench.Wcf;

public enum SoapVersion
{
    /// <summary>SOAP 1.1 (<c>text/xml</c> and a <c>SOAPAction</c> header): BasicHttpBinding.</summary>
    Soap11,

    /// <summary>SOAP 1.2 (<c>application/soap+xml</c> with the action): WSHttpBinding, NetHttpBinding, custom bindings.</summary>
    Soap12,
}

/// <summary>A part of a WSDL message: an element (document style) or a type (RPC style).</summary>
public sealed record WsdlPart(string Name, XName? Element, XName? Type);

public sealed record WsdlMessage(string Name, IReadOnlyList<WsdlPart> Parts);

/// <summary>
/// An operation of an endpoint's contract. <see cref="Action"/> is the SOAP action the request carries (the binding's
/// <c>soapAction</c>, else the port type's WS-Addressing action); <see cref="Output"/> is null for a one-way operation.
/// <see cref="RpcNamespace"/>: the wrapper's namespace of an RPC-style operation (null for document style).
/// </summary>
public sealed record WsdlOperation(
    string Name,
    string Action,
    string? ReplyAction,
    WsdlMessage Input,
    WsdlMessage? Output,
    IReadOnlyList<string> Faults,
    bool Rpc,
    string? RpcNamespace,
    string? Documentation)
{
    public bool IsOneWay => Output is null;
}

/// <summary>
/// A port of a service: its address, SOAP version, whether it wants WS-Addressing headers (WSHttpBinding), and its
/// contract's operations. <see cref="Problem"/> says why the test client can't call it (message security, a non-HTTP
/// address); the endpoint is listed anyway.
/// </summary>
public sealed record WsdlEndpoint(
    string Service,
    string Name,
    string Address,
    string Binding,
    SoapVersion Soap,
    bool Addressing,
    string Contract,
    IReadOnlyList<WsdlOperation> Operations,
    string? Problem = null)
{
    public string BindingLabel => Soap == SoapVersion.Soap11 ? "SOAP 1.1" : Addressing ? "SOAP 1.2, WS-Addressing" : "SOAP 1.2";
}

/// <summary>A service description: its endpoints, the schemas of its messages, and what was left out.</summary>
public sealed record WsdlDocument(string Location, IReadOnlyList<WsdlEndpoint> Endpoints, XsdSchemaSet Schemas, IReadOnlyList<string> Warnings, int DocumentCount);

/// <summary>
/// Reads WSDL 1.1 with its XSD schemas (M42, ADR 0069), with <see cref="System.Xml.Linq"/> only, so it runs in the
/// Native AOT shell: <c>wsdl:import</c>, <c>xsd:import</c> and <c>xsd:include</c> are followed (WCF's <c>?wsdl</c> splits a
/// service into <c>?wsdl=wsdl0</c> and <c>?xsd=xsd0</c>…), SOAP 1.1 and 1.2 bindings, WS-Addressing and security policies,
/// document and RPC styles. Duplex contracts (operations without input) and message security aren't supported: they're
/// reported in <see cref="WsdlDocument.Warnings"/> and on the endpoint.
/// </summary>
public static class WsdlReader
{
    public const string WsdlNamespace = "http://schemas.xmlsoap.org/wsdl/";
    public const string Soap11BindingNamespace = "http://schemas.xmlsoap.org/wsdl/soap/";
    public const string Soap12BindingNamespace = "http://schemas.xmlsoap.org/wsdl/soap12/";
    public const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";
    public const string PolicyNamespace = "http://schemas.xmlsoap.org/ws/2004/09/policy";
    public const string Policy15Namespace = "http://www.w3.org/ns/ws-policy";
    public const string WsawNamespace = "http://www.w3.org/2006/05/addressing/wsdl";
    public const string WsamNamespace = "http://www.w3.org/2007/05/addressing/metadata";
    public const string WsuNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    /// <summary>The most documents one description may pull in.</summary>
    public const int MaxDocuments = 64;

    private static readonly XNamespace s_wsdl = WsdlNamespace;
    private static readonly XNamespace s_soap = Soap11BindingNamespace;
    private static readonly XNamespace s_soap12 = Soap12BindingNamespace;
    private static readonly XNamespace s_xsd = XsdNamespace;

    /// <summary>
    /// The address to read metadata from: a file as it is; a service address without a query gets <c>?wsdl</c> (WCF's
    /// and CoreWCF's metadata page).
    /// </summary>
    public static Uri MetadataUri(string location)
    {
        var trimmed = location.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return uri.Query.Length == 0 && !uri.AbsolutePath.EndsWith(".wsdl", StringComparison.OrdinalIgnoreCase) && !uri.AbsolutePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? new Uri(uri.GetLeftPart(UriPartial.Path) + "?wsdl")
                : uri;
        }

        return uri is { IsFile: true } ? uri : new Uri(Path.GetFullPath(trimmed));
    }

    /// <summary>
    /// Reads the description at <paramref name="location"/> and every document it imports, through
    /// <paramref name="fetch"/> (the caller decides how: HTTP with limits, files, recorded documents in tests).
    /// </summary>
    public static async Task<WsdlDocument> LoadAsync(string location, Func<Uri, CancellationToken, Task<string>> fetch, CancellationToken cancellationToken)
    {
        var start = MetadataUri(location);
        var documents = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        var queue = new Queue<Uri>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            var key = next.AbsoluteUri;
            if (documents.ContainsKey(key))
            {
                continue;
            }

            if (documents.Count >= MaxDocuments)
            {
                throw new InvalidOperationException($"{location} imports more than {MaxDocuments} documents.");
            }

            var text = await fetch(next, cancellationToken).ConfigureAwait(false);
            XDocument document;
            try
            {
                document = XDocument.Parse(text, LoadOptions.None);
            }
            catch (XmlException ex)
            {
                throw new InvalidOperationException(documents.Count == 0
                    ? $"{next} isn't a WSDL document: {ex.Message}"
                    : $"{next}, imported by the service's metadata, isn't XML: {ex.Message}", ex);
            }

            if (documents.Count == 0 && document.Root?.Name != s_wsdl + "definitions")
            {
                throw new InvalidOperationException($"{next} isn't a WSDL document (its root is <{document.Root?.Name.LocalName}>). Use the service's metadata address, such as …/Service.svc?wsdl.");
            }

            documents[key] = document;
            foreach (var import in Imports(document))
            {
                if (Uri.TryCreate(next, import, out var resolved))
                {
                    queue.Enqueue(resolved);
                }
            }
        }

        return Read(start.ToString(), [.. documents.Values]);
    }

    /// <summary>The locations a document imports: <c>wsdl:import</c>, and <c>xsd:import</c>/<c>xsd:include</c> with a location.</summary>
    private static IEnumerable<string> Imports(XDocument document)
    {
        foreach (var import in document.Descendants(s_wsdl + "import"))
        {
            if ((string?)import.Attribute("location") is { Length: > 0 } location)
            {
                yield return location;
            }
        }

        foreach (var import in document.Descendants().Where(e => e.Name == s_xsd + "import" || e.Name == s_xsd + "include"))
        {
            if ((string?)import.Attribute("schemaLocation") is { Length: > 0 } location)
            {
                yield return location;
            }
        }
    }

    /// <summary>Builds the description from documents already read (the first is the one asked for).</summary>
    public static WsdlDocument Read(string location, IReadOnlyList<XDocument> documents)
    {
        var warnings = new List<string>();
        var schemas = new XsdSchemaSet();
        var messages = new Dictionary<XName, XElement>();
        var portTypes = new Dictionary<XName, XElement>();
        var bindings = new Dictionary<XName, XElement>();
        var services = new List<XElement>();
        var policies = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var root = document.Root!;
            if (root.Name == s_xsd + "schema")
            {
                schemas.Add(root);
                continue;
            }

            if (root.Name != s_wsdl + "definitions")
            {
                continue;
            }

            var tns = (string?)root.Attribute("targetNamespace") ?? string.Empty;
            foreach (var schema in root.Elements(s_wsdl + "types").Elements(s_xsd + "schema"))
            {
                schemas.Add(schema);
            }

            foreach (var message in root.Elements(s_wsdl + "message"))
            {
                messages[XName.Get((string)message.Attribute("name")!, tns)] = message;
            }

            foreach (var portType in root.Elements(s_wsdl + "portType"))
            {
                portTypes[XName.Get((string)portType.Attribute("name")!, tns)] = portType;
            }

            foreach (var binding in root.Elements(s_wsdl + "binding"))
            {
                bindings[XName.Get((string)binding.Attribute("name")!, tns)] = binding;
            }

            services.AddRange(root.Elements(s_wsdl + "service"));
            foreach (var policy in root.Descendants().Where(e => e.Name.LocalName == "Policy" && e.Name.NamespaceName is PolicyNamespace or Policy15Namespace))
            {
                if (((string?)policy.Attribute(XName.Get("Id", WsuNamespace)) ?? (string?)policy.Attribute("Name")) is { } id)
                {
                    policies[id] = policy;
                }
            }
        }

        var endpoints = new List<WsdlEndpoint>();
        foreach (var service in services)
        {
            var serviceName = (string?)service.Attribute("name") ?? "Service";
            foreach (var port in service.Elements(s_wsdl + "port"))
            {
                var portName = (string?)port.Attribute("name") ?? "Endpoint";
                if (Resolve(port, (string?)port.Attribute("binding")) is not { } bindingName || !bindings.TryGetValue(bindingName, out var binding))
                {
                    warnings.Add($"{serviceName}/{portName}: its binding isn't in the metadata.");
                    continue;
                }

                var soapBinding = binding.Element(s_soap + "binding");
                var soap12Binding = binding.Element(s_soap12 + "binding");
                if (soapBinding is null && soap12Binding is null)
                {
                    warnings.Add($"{serviceName}/{portName}: {bindingName.LocalName} isn't a SOAP binding.");
                    continue;
                }

                var version = soapBinding is not null ? SoapVersion.Soap11 : SoapVersion.Soap12;
                var bindingNs = version == SoapVersion.Soap11 ? s_soap : s_soap12;
                var address = (string?)port.Element(bindingNs + "address")?.Attribute("location") ?? string.Empty;
                if (Resolve(binding, (string?)binding.Attribute("type")) is not { } portTypeName || !portTypes.TryGetValue(portTypeName, out var portType))
                {
                    warnings.Add($"{serviceName}/{portName}: its contract isn't in the metadata.");
                    continue;
                }

                var assertions = PolicyAssertions(binding, policies).ToList();
                var addressing = assertions.Any(a => a.Name.LocalName is "UsingAddressing" or "Addressing" && a.Name.NamespaceName is WsawNamespace or WsamNamespace or "http://schemas.xmlsoap.org/ws/2004/08/addressing/policy");
                string? problem = null;
                if (assertions.Any(a => a.Name.LocalName is "SymmetricBinding" or "AsymmetricBinding" or "TransportBinding" or "SignedSupportingTokens" or "EndorsingSupportingTokens" or "SignedEncryptedSupportingTokens"))
                {
                    problem = "The binding uses WS-Security (message or transport-with-credential security), which the test client doesn't support.";
                }
                else if (assertions.Any(a => a.Name.LocalName is "RMAssertion"))
                {
                    problem = "The binding uses reliable sessions, which the test client doesn't support.";
                }
                else if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    problem = $"Only HTTP endpoints can be called ({address}).";
                }

                var style = (string?)(version == SoapVersion.Soap11 ? soapBinding : soap12Binding)!.Attribute("style") ?? "document";
                var operations = new List<WsdlOperation>();
                foreach (var operation in portType.Elements(s_wsdl + "operation"))
                {
                    var name = (string)operation.Attribute("name")!;
                    var input = operation.Element(s_wsdl + "input");
                    var output = operation.Element(s_wsdl + "output");
                    if (input is null)
                    {
                        warnings.Add($"{portTypeName.LocalName}.{name} is a callback of a duplex contract, which the test client doesn't support.");
                        continue;
                    }

                    var bound = binding.Elements(s_wsdl + "operation").FirstOrDefault(o => (string?)o.Attribute("name") == name);
                    var soapOperation = bound?.Element(bindingNs + "operation");
                    var operationStyle = (string?)soapOperation?.Attribute("style") ?? style;
                    var inputAction = (string?)input.Attribute(XName.Get("Action", WsawNamespace)) ?? (string?)input.Attribute(XName.Get("Action", WsamNamespace));
                    var soapAction = (string?)soapOperation?.Attribute("soapAction");
                    var action = soapAction is { Length: > 0 } ? soapAction
                        : inputAction is { Length: > 0 } ? inputAction
                        : $"{portTypeName.NamespaceName.TrimEnd('/')}/{portTypeName.LocalName}/{name}";
                    var replyAction = output is null ? null : (string?)output.Attribute(XName.Get("Action", WsawNamespace)) ?? (string?)output.Attribute(XName.Get("Action", WsamNamespace));
                    var rpcNamespace = operationStyle == "rpc" ? (string?)bound?.Element(s_wsdl + "input")?.Element(bindingNs + "body")?.Attribute("namespace") ?? portTypeName.NamespaceName : null;
                    operations.Add(new WsdlOperation(
                        name,
                        action,
                        replyAction,
                        Message(operation, input, messages) ?? new WsdlMessage(name, []),
                        output is null ? null : Message(operation, output, messages) ?? new WsdlMessage(name + "Response", []),
                        [.. operation.Elements(s_wsdl + "fault").Select(f => (string?)f.Attribute("name") ?? "Fault")],
                        operationStyle == "rpc",
                        rpcNamespace,
                        operation.Element(s_wsdl + "documentation")?.Value.Trim() is { Length: > 0 } doc ? doc : null));
                }

                endpoints.Add(new WsdlEndpoint(serviceName, portName, address, bindingName.LocalName, version, addressing, portTypeName.LocalName, operations, problem));
            }
        }

        if (endpoints.Count == 0)
        {
            warnings.Add("The metadata has no SOAP endpoint.");
        }

        return new WsdlDocument(location, endpoints, schemas, warnings.Distinct(StringComparer.Ordinal).ToList(), documents.Count);
    }

    private static WsdlMessage? Message(XElement operation, XElement io, Dictionary<XName, XElement> messages)
    {
        if (Resolve(io, (string?)io.Attribute("message")) is not { } name || !messages.TryGetValue(name, out var message))
        {
            return null;
        }

        return new WsdlMessage(
            name.LocalName,
            [.. message.Elements(s_wsdl + "part").Select(p => new WsdlPart(
                (string?)p.Attribute("name") ?? "parameters",
                Resolve(p, (string?)p.Attribute("element")),
                Resolve(p, (string?)p.Attribute("type"))))]);
    }

    /// <summary>The policy assertions that apply to a binding: inline ones and those its policy references name.</summary>
    private static IEnumerable<XElement> PolicyAssertions(XElement binding, Dictionary<string, XElement> policies)
    {
        var applied = new List<XElement>();
        foreach (var element in binding.Elements())
        {
            if (element.Name.LocalName == "PolicyReference" && (string?)element.Attribute("URI") is { } reference && policies.TryGetValue(reference.TrimStart('#'), out var policy))
            {
                applied.Add(policy);
            }
            else if (element.Name.LocalName == "Policy" && element.Name.NamespaceName is PolicyNamespace or Policy15Namespace)
            {
                applied.Add(element);
            }
        }

        return applied.SelectMany(p => p.Descendants());
    }

    /// <summary>A <c>prefix:name</c> QName in scope at <paramref name="context"/>.</summary>
    internal static XName? Resolve(XElement context, string? qname)
    {
        if (string.IsNullOrWhiteSpace(qname))
        {
            return null;
        }

        var colon = qname.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon < 0 ? string.Empty : qname[..colon];
        var local = colon < 0 ? qname : qname[(colon + 1)..];
        var ns = prefix.Length == 0 ? context.GetDefaultNamespace() : context.GetNamespaceOfPrefix(prefix);
        return XName.Get(local, ns?.NamespaceName ?? string.Empty);
    }
}
