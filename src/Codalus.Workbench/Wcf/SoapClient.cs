using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Codalus.Workbench.Wcf;

/// <summary>A node of a response's tree: an element (with its text when it has no children) or an attribute (<c>@name</c>).</summary>
public sealed record XmlTreeNode(string Name, string? Value, IReadOnlyList<XmlTreeNode> Children)
{
    public string Label => Value is null ? Name : $"{Name} = {Value}";
}

/// <summary>
/// A response: the HTTP status, the body as received and formatted, a fault's code and reason, and the tree of the
/// body's content (the operation's result) for the tree view.
/// </summary>
public sealed record SoapResponse(
    int StatusCode,
    string? ContentType,
    string Body,
    string Formatted,
    bool IsFault,
    string? FaultCode,
    string? FaultReason,
    XmlTreeNode? Tree,
    TimeSpan Elapsed)
{
    /// <summary>One line: "200 OK, 12 ms" or "Fault s:Client: Division by zero".</summary>
    public string Summary => IsFault
        ? $"Fault {FaultCode}: {FaultReason} ({StatusCode}, {(int)Elapsed.TotalMilliseconds} ms)"
        : $"{StatusCode}, {(int)Elapsed.TotalMilliseconds} ms";

    /// <summary>Reads a response body: a SOAP envelope (1.1 or 1.2), or anything else as text.</summary>
    public static SoapResponse Parse(int statusCode, string? contentType, string body, TimeSpan elapsed)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body, LoadOptions.None);
        }
        catch (XmlException)
        {
            return new SoapResponse(statusCode, contentType, body, body, false, null, null, null, elapsed);
        }

        var formatted = Format(document);
        var envelope = document.Root;
        var soapNs = envelope?.Name.NamespaceName;
        if (envelope?.Name.LocalName != "Envelope" || soapNs is not (SoapRequest.Soap11Envelope or SoapRequest.Soap12Envelope))
        {
            return new SoapResponse(statusCode, contentType, body, formatted, false, null, null, TreeOf(document.Root!), elapsed);
        }

        XNamespace s = soapNs;
        var content = envelope.Element(s + "Body")?.Elements().FirstOrDefault();
        if (content?.Name == s + "Fault")
        {
            string? code;
            string? reason;
            if (soapNs == SoapRequest.Soap11Envelope)
            {
                code = content.Element("faultcode")?.Value.Trim();
                reason = content.Element("faultstring")?.Value.Trim();
            }
            else
            {
                var value = content.Element(s + "Code")?.Element(s + "Value")?.Value.Trim();
                var subcode = content.Element(s + "Code")?.Element(s + "Subcode")?.Element(s + "Value")?.Value.Trim();
                code = subcode is { Length: > 0 } ? $"{value} ({subcode})" : value;
                reason = content.Element(s + "Reason")?.Elements(s + "Text").FirstOrDefault()?.Value.Trim();
            }

            return new SoapResponse(statusCode, contentType, body, formatted, true, code, reason, TreeOf(content), elapsed);
        }

        return new SoapResponse(statusCode, contentType, body, formatted, false, null, null, content is null ? null : TreeOf(content), elapsed);
    }

    /// <summary>The document indented by two spaces, without the XML declaration.</summary>
    public static string Format(XDocument document)
    {
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, NewLineChars = "\n" }))
        {
            document.Root!.WriteTo(writer);
        }

        return text.ToString();
    }

    /// <summary>
    /// An element as a tree: attributes first (not namespace declarations), then child elements; an element with only
    /// text is a leaf with its value; <c>xsi:nil</c> shows as <c>(nil)</c>.
    /// </summary>
    public static XmlTreeNode TreeOf(XElement element)
    {
        var children = new List<XmlTreeNode>();
        var nil = element.Attribute(XName.Get("nil", SoapRequest.XsiNamespace))?.Value == "true";
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.NamespaceName != SoapRequest.XsiNamespace))
        {
            children.Add(new XmlTreeNode("@" + attribute.Name.LocalName, attribute.Value, []));
        }

        foreach (var child in element.Elements())
        {
            children.Add(TreeOf(child));
        }

        var leaf = !element.HasElements;
        return new XmlTreeNode(element.Name.LocalName, nil ? "(nil)" : leaf ? element.Value : null, children);
    }
}

/// <summary>
/// Sends SOAP requests over HTTP (M42, ADR 0069): plain <see cref="HttpClient"/>, no <c>System.ServiceModel</c>, so the
/// Native AOT shell can run it. SOAP 1.1 posts <c>text/xml</c> with a <c>SOAPAction</c> header (BasicHttpBinding);
/// SOAP 1.2 posts <c>application/soap+xml</c> with the action as a parameter (WSHttpBinding without security, whose
/// WS-Addressing headers the envelope carries). Responses are read up to <see cref="MaxResponseBytes"/>.
/// </summary>
public sealed class SoapClient(HttpClient http)
{
    public const long MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>A client with the test client's defaults: two minutes, no cookies, the system proxy.</summary>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public async Task<SoapResponse> SendAsync(string address, SoapVersion version, string action, string envelope, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, address);
        var content = new StringContent(envelope, new UTF8Encoding(false));
        if (version == SoapVersion.Soap11)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
            request.Headers.TryAddWithoutValidation("SOAPAction", "\"" + action + "\"");
        }
        else
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
            content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("action", "\"" + action + "\""));
        }

        request.Content = content;
        var watch = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new InvalidOperationException($"The response is larger than {MaxResponseBytes / 1024 / 1024} MB.");
        }

        var bytes = await ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try
        {
            encoding = charset is { Length: > 0 } ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return SoapResponse.Parse((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), encoding.GetString(bytes), watch.Elapsed);
    }

    private static async Task<byte[]> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidOperationException($"The response is larger than {MaxResponseBytes / 1024 / 1024} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a metadata document for <see cref="WsdlReader.LoadAsync"/>: an <c>http(s)</c> URL through this client (16 MB
    /// at most), or a file.
    /// </summary>
    public async Task<string> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.IsFile)
        {
            var info = new FileInfo(uri.LocalPath);
            if (info.Exists && info.Length > MaxResponseBytes)
            {
                throw new InvalidOperationException($"{uri.LocalPath} is larger than {MaxResponseBytes / 1024 / 1024} MB.");
            }

            return await File.ReadAllTextAsync(uri.LocalPath, cancellationToken).ConfigureAwait(false);
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{uri} isn't an http(s) address or a file.");
        }

        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{uri} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return Encoding.UTF8.GetString(await ReadAsync(response.Content, cancellationToken).ConfigureAwait(false));
    }
}
