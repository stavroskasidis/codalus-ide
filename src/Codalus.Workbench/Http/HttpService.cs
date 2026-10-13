using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Codalus.Workbench.Http;

/// <summary>A request as sent: variables replaced.</summary>
public sealed record HttpSentRequest(string Method, string Url, IReadOnlyList<KeyValuePair<string, string>> Headers, string? Body);

/// <summary>A response: status, headers (response and content headers), the body as text, its size and content type.</summary>
public sealed record HttpResponseInfo(int StatusCode, string? ReasonPhrase, string Version, IReadOnlyList<KeyValuePair<string, string>> Headers, string Body, long Size, string? ContentType)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>One request and its response (or the error that prevented one), kept in the history.</summary>
public sealed record HttpExchange(string? Name, string File, int Line, HttpSentRequest Request, HttpResponseInfo? Response, string? Error, TimeSpan Elapsed, DateTimeOffset When);

/// <summary>
/// Sends the requests of <c>.http</c> files (M15a) with <see cref="HttpClient"/> and keeps a history. Redirects are
/// followed. Development certificates: a localhost server's certificate is accepted even when it isn't trusted yet
/// (<see cref="AcceptUntrustedLocalhost"/>), as a browser would after the user's confirmation; other hosts are
/// verified. Events may be raised on any thread.
/// </summary>
public sealed class HttpService : IDisposable
{
    private const int HistoryLimit = 50;
    private const int BodyLimit = 4 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly List<HttpExchange> _history = [];
    private readonly Dictionary<string, string> _environments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _client;

    public HttpService()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None
                    || AcceptUntrustedLocalhost && sender is HttpRequestMessage { RequestUri: { IsLoopback: true } }
                    || AcceptUntrustedLocalhost && sender is string host && host is "localhost" or "127.0.0.1" or "::1",
            },
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
    }

    /// <summary>Accept untrusted certificates from localhost (the ASP.NET Core development certificate before it is trusted).</summary>
    public bool AcceptUntrustedLocalhost { get; set; } = true;

    /// <summary>A request was sent or answered; the history changed.</summary>
    public event EventHandler<HttpExchange>? Completed;

    /// <summary>The selected environment changed for an environment file (the path, or null).</summary>
    public event EventHandler<string?>? EnvironmentChanged;

    /// <summary>The exchanges, newest first.</summary>
    public IReadOnlyList<HttpExchange> History
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }

    /// <summary>The environment chosen for the files that use <paramref name="environmentFile"/> (null: none chosen).</summary>
    public string? EnvironmentFor(string? environmentFile)
    {
        lock (_gate)
        {
            return _environments.GetValueOrDefault(environmentFile ?? string.Empty);
        }
    }

    public void SelectEnvironment(string? environmentFile, string? environment)
    {
        lock (_gate)
        {
            if (environment is null)
            {
                _environments.Remove(environmentFile ?? string.Empty);
            }
            else
            {
                _environments[environmentFile ?? string.Empty] = environment;
            }
        }

        EnvironmentChanged?.Invoke(this, environmentFile);
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
        }

        Completed?.Invoke(this, null!);
    }

    /// <summary>The last response of the request named <paramref name="name"/> (request variables).</summary>
    public HttpExchange? LastNamed(string name)
    {
        lock (_gate)
        {
            return _history.FirstOrDefault(h => h.Name == name && h.Response is not null);
        }
    }

    /// <summary>
    /// Sends the request of <paramref name="file"/> (text <paramref name="text"/>) at <paramref name="line"/> (1-based).
    /// Returns null when no request is there. Unresolved variables are reported as an error without sending.
    /// </summary>
    public async Task<HttpExchange?> SendAsync(string file, string text, int line, CancellationToken cancellationToken)
    {
        var document = HttpFileParser.Parse(text);
        if (document.At(line) is not { } block)
        {
            return null;
        }

        var environments = HttpEnvironments.Load(file);
        var names = environments.Names;
        var variables = environments.For(EnvironmentFor(environments.File) ?? (names.Count > 0 ? names[0] : null));
        var unresolved = new List<string>();
        string Resolve(string value) => HttpVariables.Resolve(value, block.Variables, variables, LastNamed, unresolved);

        var url = Resolve(block.Url);
        var headers = block.Headers.Select(h => new KeyValuePair<string, string>(h.Key, Resolve(h.Value))).ToList();
        var body = block.Body is { } raw ? Resolve(BodyText(raw, file)) : null;
        var sent = new HttpSentRequest(block.Method, url, headers, body);
        if (unresolved.Count > 0)
        {
            return Record(new HttpExchange(block.Name, file, block.RequestLine, sent, null,
                $"Unknown variable(s): {string.Join(", ", unresolved.Distinct())}. Define them with @name = value, or in http-client.env.json.", TimeSpan.Zero, DateTimeOffset.Now));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return Record(new HttpExchange(block.Name, file, block.RequestLine, sent, null, $"'{url}' is not an http or https URL.", TimeSpan.Zero, DateTimeOffset.Now));
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(block.Method), uri);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = null;
            }

            foreach (var (name, value) in headers)
            {
                if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is not null)
                {
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            if (request.Content is { Headers.ContentType: null } content)
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var bytes = await ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var responseHeaders = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
                .ToList();
            var info = new HttpResponseInfo((int)response.StatusCode, response.ReasonPhrase, "HTTP/" + response.Version.ToString(2), responseHeaders,
                Decode(bytes, response.Content.Headers.ContentType?.CharSet, contentType), bytes.Length, contentType);
            return Record(new HttpExchange(block.Name, file, block.RequestLine, sent, info, null, stopwatch.Elapsed, DateTimeOffset.Now));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or IOException && !cancellationToken.IsCancellationRequested)
        {
            return Record(new HttpExchange(block.Name, file, block.RequestLine, sent, null, ex is TaskCanceledException ? "The request timed out." : ex.Message, stopwatch.Elapsed, DateTimeOffset.Now));
        }
    }

    /// <summary>A body of <c>&lt; path</c> is the content of that file (relative to the <c>.http</c> file).</summary>
    private static string BodyText(string body, string file)
    {
        var trimmed = body.Trim();
        if (trimmed.StartsWith('<') && !trimmed.Contains('\n', StringComparison.Ordinal) && !trimmed.StartsWith("<?", StringComparison.Ordinal) && !trimmed.StartsWith("</", StringComparison.Ordinal))
        {
            var path = trimmed[1..].Trim();
            var full = Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(file)!, path);
            if (File.Exists(full))
            {
                return File.ReadAllText(full);
            }
        }

        return body;
    }

    private static async Task<byte[]> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > BodyLimit)
            {
                break;
            }
        }

        return buffer.ToArray();
    }

    private static string Decode(byte[] bytes, string? charset, string? contentType)
    {
        var textual = contentType is null || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                      || contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                      || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                      || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
        if (!textual)
        {
            return $"({bytes.Length:N0} bytes of {contentType})";
        }

        Encoding encoding;
        try
        {
            encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        var text = encoding.GetString(bytes);
        return bytes.Length > BodyLimit ? text + "\n… (truncated)" : text;
    }

    private HttpExchange Record(HttpExchange exchange)
    {
        lock (_gate)
        {
            _history.Insert(0, exchange);
            if (_history.Count > HistoryLimit)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }

        Completed?.Invoke(this, exchange);
        return exchange;
    }

    public void Dispose() => _client.Dispose();
}
