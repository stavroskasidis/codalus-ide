using System.Globalization;

namespace Codalus.Plugins.Hosting;

/// <summary>A git remote URL split into its host and path: HTTPS, <c>ssh://</c> or scp-like <c>[user@]host:path</c>.</summary>
/// <param name="Host">The host, lower case, with the port for HTTP(S) when it isn't the default.</param>
/// <param name="Path">The path without the leading slash or the <c>.git</c> suffix, decoded.</param>
/// <param name="IsSsh">Whether it is an SSH URL.</param>
public sealed record RemoteUrl(string Host, string Path, bool IsSsh)
{
    /// <summary>The path's segments.</summary>
    public string[] Segments => Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    public static RemoteUrl? Parse(string url)
    {
        var text = url.Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                return null;
            }

            var web = uri.Scheme is "http" or "https";
            var host = web && !uri.IsDefaultPort ? uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture) : uri.Host;
            return new RemoteUrl(host.ToLowerInvariant(), Clean(Uri.UnescapeDataString(uri.AbsolutePath)), !web);
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (colon > 1 && (slash < 0 || colon < slash))
        {
            var hostPart = text[..colon];
            var at = hostPart.LastIndexOf('@');
            return new RemoteUrl((at >= 0 ? hostPart[(at + 1)..] : hostPart).ToLowerInvariant(), Clean(Uri.UnescapeDataString(text[(colon + 1)..])), true);
        }

        return null;
    }

    /// <summary>The host of a server URL setting (<c>https://github.com</c> → <c>github.com</c>; a port kept).</summary>
    public static string HostOf(string serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            ? (uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture)).ToLowerInvariant()
            : serverUrl.ToLowerInvariant();

    private static string Clean(string path)
    {
        var trimmed = path.Trim('/');
        return trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
