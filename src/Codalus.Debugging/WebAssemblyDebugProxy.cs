using System.Net;
using System.Text.RegularExpressions;

namespace Codalus.Debugging;

/// <summary>
/// The Blazor WebAssembly debugging proxy an app starts in development (ASP.NET Core's <c>UseWebAssemblyDebugging</c>),
/// which js-debug opens through the launch profile's <c>inspectUri</c>. When js-debug can't open it, the app's answer to
/// the same request says which part failed: the app starting the proxy (ASP.NET Core gives it 10 seconds and keeps the
/// failure until the app restarts), or the proxy, which the app redirects to once it runs (Windows CI, run 41555852375).
/// </summary>
public static partial class WebAssemblyDebugProxy
{
    /// <summary>The proxy's address in js-debug's <c>Could not open ws://…/_framework/debug/ws-proxy?browser=…</c>, or null.</summary>
    public static string? UnopenedAddress(string line) => UnopenedRegex().Match(line) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>
    /// What the app answers at <paramref name="address"/>: the request js-debug made, over HTTP and without the upgrade
    /// or following the redirect. Never throws; a request that fails is described too.
    /// </summary>
    public static async Task<string> DescribeAsync(string address, CancellationToken cancellationToken)
    {
        Uri url;
        try
        {
            var builder = new UriBuilder(address);
            builder.Scheme = builder.Scheme == "wss" ? "https" : "http";
            url = builder.Uri;
        }
        catch (UriFormatException ex)
        {
            return $"The app's WebAssembly debugging proxy ({address}) couldn't be asked: {ex.Message}";
        }

        var where = url.GetLeftPart(UriPartial.Path);
        try
        {
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("text/plain");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            if (response.Headers.Location is { } location)
            {
                return $"The app's WebAssembly debugging proxy ({where}) answers {status}, to {location.GetLeftPart(UriPartial.Path)}: the app started the proxy.";
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var first = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return $"The app's WebAssembly debugging proxy ({where}) answers {status}" + (first is null ? "." : ": " + (first.Length > 300 ? first[..300] + "…" : first));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return $"The app's WebAssembly debugging proxy ({where}) didn't answer: {ex.Message}";
        }
    }

    [GeneratedRegex(@"Could not open (wss?://[^""\s]+/_framework/debug/ws-proxy[^""\s]*)")]
    private static partial Regex UnopenedRegex();
}
