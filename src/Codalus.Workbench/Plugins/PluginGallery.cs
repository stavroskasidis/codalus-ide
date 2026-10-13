using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Codalus.Workbench.Plugins;

/// <summary>A plugin a gallery offers: its newest version that matches the search, and the others.</summary>
/// <param name="Id">The package id, which is the plugin's id.</param>
/// <param name="Version">The newest version.</param>
/// <param name="Title">Its display name.</param>
/// <param name="Description">What it does.</param>
/// <param name="Authors">Who publishes it, as the package says.</param>
/// <param name="TotalDownloads">How many times it was downloaded, when the source counts.</param>
/// <param name="ProjectUrl">Its home page.</param>
/// <param name="Versions">Every version, newest last.</param>
/// <param name="Source">The source's service index it came from.</param>
public sealed record GalleryPlugin(
    string Id,
    string Version,
    string? Title,
    string? Description,
    string? Authors,
    long TotalDownloads,
    string? ProjectUrl,
    IReadOnlyList<string> Versions,
    string Source);

/// <summary>
/// Plugins on NuGet feeds (M22c, ADR 0060, D5): packages of type <see cref="PackageType"/>, found through a v3 source's
/// <c>SearchQueryService</c> and downloaded from its <c>PackageBaseAddress</c>. A small client of the protocol's
/// documents, so the shell needs neither the build host nor NuGet's libraries to browse and install plugins.
/// </summary>
public sealed class PluginGallery(HttpClient http)
{
    /// <summary>The NuGet package type of plugins.</summary>
    public const string PackageType = "CodalusPlugin";

    /// <summary>nuget.org's service index, the default source.</summary>
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    private readonly ConcurrentDictionary<string, (string? Search, string? Packages)> _resources = new(StringComparer.Ordinal);

    /// <summary>Plugins matching <paramref name="query"/> (empty: the most downloaded first) on <paramref name="source"/>.</summary>
    public async Task<IReadOnlyList<GalleryPlugin>> SearchAsync(string source, string query, bool prerelease, int skip, int take, CancellationToken cancellationToken)
    {
        var (search, _) = await ResourcesAsync(source, cancellationToken).ConfigureAwait(false);
        if (search is null)
        {
            throw new InvalidOperationException($"{source} has no search service.");
        }

        var url = $"{search}?q={Uri.EscapeDataString(query)}&packageType={PackageType}&prerelease={(prerelease ? "true" : "false")}&semVerLevel=2.0.0"
            + $"&skip={skip.ToString(CultureInfo.InvariantCulture)}&take={take.ToString(CultureInfo.InvariantCulture)}";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var plugins = new List<GalleryPlugin>();
        foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
        {
            plugins.Add(new GalleryPlugin(
                item.GetProperty("id").GetString()!,
                item.GetProperty("version").GetString()!,
                String(item, "title"),
                String(item, "description"),
                item.TryGetProperty("authors", out var authors) ? authors.ValueKind == JsonValueKind.Array ? string.Join(", ", authors.EnumerateArray().Select(a => a.GetString())) : authors.GetString() : null,
                item.TryGetProperty("totalDownloads", out var downloads) && downloads.TryGetInt64(out var count) ? count : 0,
                String(item, "projectUrl"),
                item.TryGetProperty("versions", out var versions) ? [.. versions.EnumerateArray().Select(v => v.GetProperty("version").GetString()!)] : [],
                source));
        }

        return plugins;
    }

    /// <summary>Every version of <paramref name="id"/> on <paramref name="source"/>, oldest first; empty when it has none.</summary>
    public async Task<IReadOnlyList<string>> VersionsAsync(string source, string id, CancellationToken cancellationToken)
    {
        var (_, packages) = await ResourcesAsync(source, cancellationToken).ConfigureAwait(false);
        if (packages is null)
        {
            return [];
        }

        try
        {
            using var document = await GetJsonAsync($"{packages.TrimEnd('/')}/{id.ToLowerInvariant()}/index.json", cancellationToken).ConfigureAwait(false);
            return [.. document.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!)];
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    /// <summary>The newest version of <paramref name="id"/> that is newer than <paramref name="installed"/> (stable ones unless <paramref name="prerelease"/>), or null.</summary>
    public async Task<string?> NewerVersionAsync(string source, string id, string installed, bool prerelease, CancellationToken cancellationToken)
    {
        SemanticVersion? best = SemanticVersion.TryParse(installed, out var parsed) ? parsed : null;
        string? newest = null;
        foreach (var version in await VersionsAsync(source, id, cancellationToken).ConfigureAwait(false))
        {
            if (SemanticVersion.TryParse(version, out var candidate) && (prerelease || candidate.Prerelease is null)
                && (best is null || candidate > best))
            {
                best = candidate;
                newest = version;
            }
        }

        return newest;
    }

    /// <summary>Downloads <paramref name="id"/> <paramref name="version"/> into <paramref name="folder"/>; returns the package's path.</summary>
    public async Task<string> DownloadAsync(string source, string id, string version, string folder, CancellationToken cancellationToken)
    {
        var (_, packages) = await ResourcesAsync(source, cancellationToken).ConfigureAwait(false);
        if (packages is null)
        {
            throw new InvalidOperationException($"{source} has no package download service.");
        }

        var lowerId = id.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{lowerId}.{lowerVersion}.nupkg");
        using var response = await http.GetAsync(new Uri($"{packages.TrimEnd('/')}/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using (var file = File.Create(path))
        {
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        return path;
    }

    private async Task<(string? Search, string? Packages)> ResourcesAsync(string source, CancellationToken cancellationToken)
    {
        if (_resources.TryGetValue(source, out var cached))
        {
            return cached;
        }

        using var document = await GetJsonAsync(source, cancellationToken).ConfigureAwait(false);
        string? search = null;
        string? packages = null;
        foreach (var resource in document.RootElement.GetProperty("resources").EnumerateArray())
        {
            var type = resource.GetProperty("@type").GetString() ?? string.Empty;
            var id = resource.GetProperty("@id").GetString();
            if (search is null && type.StartsWith("SearchQueryService", StringComparison.Ordinal))
            {
                search = id;
            }
            else if (packages is null && type.StartsWith("PackageBaseAddress", StringComparison.Ordinal))
            {
                packages = id;
            }
        }

        return _resources[source] = (search, packages);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
