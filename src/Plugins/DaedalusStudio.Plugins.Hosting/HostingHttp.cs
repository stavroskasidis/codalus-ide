using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DaedalusStudio.Plugins.Proposed;

namespace DaedalusStudio.Plugins.Hosting;

/// <summary>A host answered with an error: <see cref="Status"/> and the host's message.</summary>
public sealed class HostingHttpException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// JSON over HTTPS to a host's API. <see cref="Authorize"/> adds the session's credentials to API calls; token endpoints
/// are called without them. A 401 is <see cref="HostingAuthenticationException"/> (sign in again); other errors are
/// <see cref="HostingHttpException"/> with the host's message.
/// </summary>
public sealed class HostingHttp : IDisposable
{
    private readonly HttpClient _client;

    public HostingHttp(string userAgent, TimeSpan? timeout = null)
    {
        _client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>Adds the session's Authorization header to a request (throws <see cref="HostingAuthenticationException"/> without one).</summary>
    public Func<HttpRequestMessage, CancellationToken, Task>? Authorize { get; set; }

    /// <summary>Headers every API call sends (an Accept media type, an API version).</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Task<JsonElement> GetAsync(string url, CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, url, null, cancellationToken);

    public Task<JsonElement> PostAsync(string url, string json, CancellationToken cancellationToken) => SendAsync(HttpMethod.Post, url, json, cancellationToken);

    /// <summary>An authorized API call with an optional JSON body; the JSON answer (null for an empty one).</summary>
    public async Task<JsonElement> SendAsync(HttpMethod method, string url, string? json, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in Headers.Concat(headers ?? new Dictionary<string, string>()))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (Authorize is { } authorize)
        {
            await authorize(request, cancellationToken).ConfigureAwait(false);
        }

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HostingAuthenticationException($"The host refused the session ({MessageOf(text) ?? "401 Unauthorized"}). Sign in again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HostingHttpException((int)response.StatusCode, $"{method} {new Uri(url).AbsolutePath}: {(int)response.StatusCode} {MessageOf(text) ?? response.ReasonPhrase}");
        }

        return text.Trim().Length == 0 ? default : Json.Parse(text);
    }

    /// <summary>
    /// A form post without the session (OAuth endpoints), answered with JSON whatever the status: device-flow errors
    /// (<c>authorization_pending</c>…) come as 400 from some hosts and as 200 from GitHub.
    /// </summary>
    public async Task<(int Status, JsonElement Body)> PostFormAsync(string url, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonElement body;
        try
        {
            body = Json.Parse(text);
        }
        catch (JsonException)
        {
            throw new HostingHttpException((int)response.StatusCode, $"POST {new Uri(url).AbsolutePath}: {(int)response.StatusCode} {(text.Length > 200 ? text[..200] : text)}");
        }

        return ((int)response.StatusCode, body);
    }

    /// <summary>The message in a host's error body: <c>message</c>, <c>error_description</c>, <c>error</c>, GraphQL's <c>errors</c>.</summary>
    public static string? MessageOf(string text)
    {
        try
        {
            var body = Json.Parse(text);
            return body.Str("message") ?? body.Str("error_description") ?? body.Str("error")
                ?? body.Items("errors").Select(e => e.Str("message")).FirstOrDefault(m => m is not null);
        }
        catch (JsonException)
        {
            return text.Length == 0 ? null : text.Length > 200 ? text[..200] : text;
        }
    }

    /// <summary>A query-string value, escaped.</summary>
    public static string Escape(string value) => Uri.EscapeDataString(value);

    /// <summary>An integer for a URL.</summary>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    public void Dispose() => _client.Dispose();
}
