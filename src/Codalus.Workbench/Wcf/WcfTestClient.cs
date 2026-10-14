using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Codalus.Workbench.Wcf;

/// <summary>
/// A response: the HTTP status, the time it took, the body's content (the SOAP body's child, indented) or the whole
/// text when it isn't SOAP, and the fault's message when the service answered with one.
/// </summary>
public sealed record WcfResponse(int Status, TimeSpan Elapsed, string Body, string? Fault)
{
    public bool IsFault => Fault is not null;
}

/// <summary>
/// The WCF Test Client's engine (M42): loads a service's metadata (a WSDL file, a <c>?wsdl</c> address, or the
/// service's address, to which <c>?singleWsdl</c> and then <c>?wsdl</c> are added), builds a sample request for an
/// operation from its contract, and sends it as SOAP over HTTP, with WS-Addressing headers where the binding uses them.
/// Message security, duplex contracts and transports other than HTTP are gaps. UI-agnostic.
/// </summary>
public sealed class WcfTestClient(HttpClient http)
{
    private const string Soap11Envelope = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Soap12Envelope = "http://www.w3.org/2003/05/soap-envelope";
    private const string Addressing10 = "http://www.w3.org/2005/08/addressing";
    private const long MaxBytes = 16 * 1024 * 1024;

    /// <summary>Where the metadata was read from (an address, or a file's full path).</summary>
    public string? Source { get; private set; }

    public WsdlDocument? Metadata { get; private set; }

    public async Task<WsdlDocument> LoadAsync(string addressOrFile, CancellationToken cancellationToken)
    {
        var input = addressOrFile.Trim();
        if (input.Length == 0)
        {
            throw new ArgumentException("Give the service's address or a WSDL file.");
        }

        WsdlDocument document;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            (document, Source) = await LoadAddressAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var path = Path.GetFullPath(input);
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var folder = Path.GetDirectoryName(path)!;
            document = WsdlDocument.Parse(text, location => ResolveFile(folder, location));
            Source = path;
        }

        Metadata = document;
        return document;
    }

    private async Task<(WsdlDocument Document, string Source)> LoadAddressAsync(Uri uri, CancellationToken cancellationToken)
    {
        var query = uri.Query.TrimStart('?');
        IEnumerable<Uri> candidates = query.Equals("wsdl", StringComparison.OrdinalIgnoreCase) || query.Equals("singleWsdl", StringComparison.OrdinalIgnoreCase) || query.Length > 0
            ? [uri]
            : [new Uri(uri + "?singleWsdl"), new Uri(uri + "?wsdl"), uri];
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var text = await GetTextAsync(candidate, cancellationToken).ConfigureAwait(false);
                var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
                // Imports are fetched up front (Parse is synchronous): the documents a ?wsdl page links to.
                await PrefetchImportsAsync(candidate, text, resolved, 0, cancellationToken).ConfigureAwait(false);
                return (WsdlDocument.Parse(text, location => resolved.GetValueOrDefault(location)), candidate.ToString());
            }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or XmlException or InvalidOperationException)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException($"No service metadata at {uri}: {last?.Message}", last);
    }

    private async Task PrefetchImportsAsync(Uri baseUri, string text, Dictionary<string, string?> resolved, int depth, CancellationToken cancellationToken)
    {
        if (depth > 8)
        {
            return;
        }

        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            root = XElement.Load(reader);
        }
        catch (XmlException)
        {
            return;
        }

        var locations = root.Descendants()
            .Where(e => e.Name.LocalName is "import" or "include")
            .Select(e => (string?)e.Attribute("location") ?? (string?)e.Attribute("schemaLocation"))
            .OfType<string>()
            .Where(l => l.Length > 0 && !resolved.ContainsKey(l))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var location in locations)
        {
            if (!Uri.TryCreate(baseUri, location, out var target) || target.Scheme != baseUri.Scheme || target.Authority != baseUri.Authority)
            {
                resolved[location] = null; // only documents of the same service
                continue;
            }

            try
            {
                var imported = await GetTextAsync(target, cancellationToken).ConfigureAwait(false);
                resolved[location] = imported;
                await PrefetchImportsAsync(target, imported, resolved, depth + 1, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                resolved[location] = null;
            }
        }
    }

    private static string? ResolveFile(string folder, string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return null; // a file's imports are other files next to it
        }

        var path = Path.GetFullPath(Path.Combine(folder, location));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private async Task<string> GetTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{uri} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new HttpRequestException($"The response is larger than {MaxBytes / 1024 / 1024} MB.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new HttpRequestException($"The response is larger than {MaxBytes / 1024 / 1024} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = charset is { Length: > 0 } ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8;
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The operation's request body with sample values of each field's type, for the user to edit.</summary>
    public static string SampleRequest(WsdlOperation operation)
    {
        if (operation.RequestElement is null)
        {
            return string.Empty;
        }

        XNamespace ns = operation.RequestNamespace;
        var root = new XElement(ns + operation.RequestElement);
        if (operation.RequestNamespace.Length > 0)
        {
            root.Add(new XAttribute("xmlns", operation.RequestNamespace));
        }

        foreach (var field in operation.Parameters)
        {
            root.Add(Sample(field, operation.RequestNamespace, 0));
        }

        return root.ToString();
    }

    private static XElement Sample(WsdlField field, string parentNamespace, int depth)
    {
        XNamespace ns = field.Namespace;
        var element = new XElement(ns + field.Name);
        if (field.Namespace != parentNamespace)
        {
            element.Add(new XAttribute("xmlns", field.Namespace));
        }

        if (field.IsComplex)
        {
            if (depth < 6)
            {
                foreach (var child in field.Children)
                {
                    element.Add(Sample(child, field.Namespace, depth + 1));
                }
            }
        }
        else
        {
            element.Value = SampleValue(field);
        }

        return element;
    }

    private static string SampleValue(WsdlField field) => field.Values.Count > 0 ? field.Values[0] : field.Type switch
    {
        "int" or "long" or "short" or "byte" or "unsignedInt" or "unsignedLong" or "unsignedShort" or "unsignedByte" or "integer"
            or "decimal" or "double" or "float" or "char" or "nonNegativeInteger" or "positiveInteger" => "0",
        "boolean" => "false",
        "dateTime" => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        "date" or "dateOnly" => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "time" or "timeOnly" => "00:00:00",
        "duration" => "PT0S",
        "guid" => Guid.Empty.ToString(),
        "base64Binary" => string.Empty,
        _ => string.Empty,
    };

    /// <summary>
    /// Sends <paramref name="request"/> (the body's content, as <see cref="SampleRequest"/> makes it) to the endpoint's
    /// address (or <paramref name="address"/>), and reads the answer.
    /// </summary>
    public async Task<WcfResponse> InvokeAsync(WsdlEndpoint endpoint, WsdlOperation operation, string request, CancellationToken cancellationToken, string? address = null)
    {
        if (endpoint.Unsupported is { } endpointReason)
        {
            throw new InvalidOperationException($"{endpoint.Label} can't be tested: {endpointReason}.");
        }

        if (operation.Unsupported is { } operationReason)
        {
            throw new InvalidOperationException($"{operation.Name} can't be tested: {operationReason}.");
        }

        XElement? body = null;
        if (request.Trim().Length > 0)
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(request), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                body = XElement.Load(reader);
            }
            catch (XmlException e)
            {
                throw new FormatException($"The request isn't well-formed XML: {e.Message}", e);
            }
        }

        var target = address ?? endpoint.Address;
        var soap12 = endpoint.SoapVersion == "1.2";
        XNamespace s = soap12 ? Soap12Envelope : Soap11Envelope;
        XNamespace a = Addressing10;
        var envelope = new XElement(s + "Envelope", new XAttribute(XNamespace.Xmlns + "s", s.NamespaceName));
        if (endpoint.Addressing)
        {
            envelope.Add(new XAttribute(XNamespace.Xmlns + "a", a.NamespaceName));
            envelope.Add(new XElement(s + "Header",
                new XElement(a + "Action", new XAttribute(s + "mustUnderstand", "1"), operation.Action),
                new XElement(a + "MessageID", "urn:uuid:" + Guid.NewGuid()),
                new XElement(a + "ReplyTo", new XElement(a + "Address", "http://www.w3.org/2005/08/addressing/anonymous")),
                new XElement(a + "To", new XAttribute(s + "mustUnderstand", "1"), target)));
        }

        envelope.Add(new XElement(s + "Body", body));
        using var message = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8),
        };
        if (soap12)
        {
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/soap+xml; charset=utf-8; action=\"{operation.Action}\"");
        }
        else
        {
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            message.Headers.TryAddWithoutValidation("SOAPAction", "\"" + operation.Action + "\"");
        }

        var watch = Stopwatch.StartNew();
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var text = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        return Read((int)response.StatusCode, watch.Elapsed, text);
    }

    /// <summary>The body's content of a SOAP answer (indented), and its fault's reason.</summary>
    internal static WcfResponse Read(int status, TimeSpan elapsed, string text)
    {
        XElement envelope;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            envelope = XElement.Load(reader);
        }
        catch (XmlException)
        {
            return new WcfResponse(status, elapsed, text, status >= 400 ? $"HTTP {status}" : null);
        }

        var body = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
        var content = body?.Elements().FirstOrDefault();
        if (content is null)
        {
            return new WcfResponse(status, elapsed, envelope.ToString(), status >= 400 ? $"HTTP {status}" : null);
        }

        string? fault = null;
        if (content.Name.LocalName == "Fault")
        {
            // SOAP 1.1: faultstring; SOAP 1.2: Reason/Text.
            fault = content.Elements().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value
                    ?? content.Elements().FirstOrDefault(e => e.Name.LocalName == "Reason")?.Elements().FirstOrDefault()?.Value
                    ?? "The service answered with a fault.";
        }

        return new WcfResponse(status, elapsed, content.ToString(), fault);
    }
}
