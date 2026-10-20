using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Codalus.Tests;

/// <summary>A request the mock server received.</summary>
internal sealed record MockHttpRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, byte[] RawBody)
{
    public string Body => Encoding.UTF8.GetString(RawBody);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>A query-string value (decoded), or null.</summary>
    public string? QueryValue(string name) => Decode(Query).TryGetValue(name, out var value) ? value : null;

    /// <summary>The body as <c>application/x-www-form-urlencoded</c> fields.</summary>
    public IReadOnlyDictionary<string, string> Form => Decode(Body);

    /// <summary>The bearer token of the Authorization header, or null.</summary>
    public string? BearerToken => Header("Authorization") is { } value && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim()
        : Header("Authorization") is { } token && token.StartsWith("token ", StringComparison.OrdinalIgnoreCase) ? token[6..].Trim()
        : null;

    private static Dictionary<string, string> Decode(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = WebUtility.UrlDecode(equals < 0 ? pair : pair[..equals]);
            values[key] = equals < 0 ? string.Empty : WebUtility.UrlDecode(pair[(equals + 1)..]);
        }

        return values;
    }
}

/// <summary>What the mock server answers.</summary>
internal sealed record MockHttpResponse(int Status, byte[] Body, string ContentType)
{
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public static MockHttpResponse Json(string json, int status = 200) => new(status, Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8");

    public static MockHttpResponse Text(string text, int status = 200, string contentType = "text/plain; charset=utf-8") => new(status, Encoding.UTF8.GetBytes(text), contentType);

    public static MockHttpResponse NotFound() => Json("""{ "message": "Not Found" }""", 404);

    /// <summary>A file's bytes (git's dumb HTTP protocol), or 404 when it doesn't exist.</summary>
    public static MockHttpResponse File(string path) =>
        System.IO.File.Exists(path) ? new MockHttpResponse(200, System.IO.File.ReadAllBytes(path), "application/octet-stream") : NotFound();
}

/// <summary>
/// A loopback HTTP/1.1 server for tests and the smoke test (ADR 0031: mock servers stand in for every hosting API, and
/// for git over HTTP). Built on <see cref="TcpListener"/> (no http.sys reservation on Windows, Native AOT safe). One
/// request per connection (<c>Connection: close</c>), bodies by <c>Content-Length</c>.
/// </summary>
internal sealed class MockHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<MockHttpRequest, Task<MockHttpResponse>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public MockHttpServer(Func<MockHttpRequest, MockHttpResponse> handler)
        : this(request => Task.FromResult(handler(request)))
    {
    }

    public MockHttpServer(Func<MockHttpRequest, Task<MockHttpResponse>> handler)
    {
        _handler = handler;
        _listener.Start();
        BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        _accepting = AcceptAsync();
    }

    /// <summary><c>http://127.0.0.1:&lt;port&gt;</c>, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Every request, in order.</summary>
    public ConcurrentQueue<MockHttpRequest> Requests { get; } = new();

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // InvalidOperationException ("Not listening"): disposed before the loop got here (macOS CI, run 39510353199).
                return;
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                if (await ReadRequestAsync(stream).ConfigureAwait(false) is not { } request)
                {
                    return;
                }

                Requests.Enqueue(request);
                MockHttpResponse response;
                try
                {
                    response = await _handler(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response = MockHttpResponse.Text("The mock failed: " + ex, 500);
                }

                var head = new StringBuilder();
                head.Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(response.Status)).Append("\r\n");
                head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
                head.Append("Content-Length: ").Append(response.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                head.Append("Connection: close\r\n");
                foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>())
                {
                    head.Append(name).Append(": ").Append(value).Append("\r\n");
                }

                head.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _stop.Token).ConfigureAwait(false);
                if (request.Method != "HEAD")
                {
                    await stream.WriteAsync(response.Body, _stop.Token).ConfigureAwait(false);
                }

                await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task<MockHttpRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var chunk = new byte[8192];
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
        {
            var read = await stream.ReadAsync(chunk, _stop.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        var head = Encoding.ASCII.GetString([.. buffer.Take(headerEnd)]).Split("\r\n");
        var requestLine = head[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var body = buffer.Skip(headerEnd + 4).ToList();
        var length = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : 0;
        while (body.Count < length)
        {
            var read = await stream.ReadAsync(chunk, _stop.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            body.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        var target = requestLine.Length > 1 ? requestLine[1] : "/";
        var question = target.IndexOf('?', StringComparison.Ordinal);
        var path = Uri.UnescapeDataString(question < 0 ? target : target[..question]);
        return new MockHttpRequest(requestLine[0], path, question < 0 ? string.Empty : target[(question + 1)..], headers, [.. body]);
    }

    private static int IndexOfHeaderEnd(List<byte> buffer)
    {
        for (var i = 3; i < buffer.Count; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i - 3;
            }
        }

        return -1;
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        422 => "Unprocessable Entity",
        _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
        }

        _stop.Dispose();
    }
}
