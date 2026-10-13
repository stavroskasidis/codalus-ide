using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codalus.Markup.Host.Json;

/// <summary>
/// Finds a JSON document's schema (ADR 0016), in this order: its <c>$schema</c> property (a file next to it, or a URL),
/// the schemas bundled with Codalus IDE for the files of .NET repositories (by file name), then the SchemaStore
/// catalog's file patterns. Downloads (the catalog and schemas by URL) are cached for a week in
/// <see cref="CacheDirectory"/> and only happen when <see cref="AllowDownloads"/> is set; offline, the bundled schemas
/// and cached copies still apply.
/// </summary>
internal sealed class SchemaStore(string? cacheDirectory, bool allowDownloads, Action<string>? log = null)
{
    private const string CatalogUrl = "https://www.schemastore.org/api/json/catalog.json";
    private static readonly TimeSpan s_maxAge = TimeSpan.FromDays(7);

    private static readonly (Regex Name, string Resource)[] s_bundled =
    [
        (new Regex(@"^global\.json$", RegexOptions.IgnoreCase), "global.json"),
        (new Regex(@"^launchSettings\.json$", RegexOptions.IgnoreCase), "launchSettings.json"),
        (new Regex(@"^appsettings(\.[^.]+)*\.json$", RegexOptions.IgnoreCase), "appsettings.json"),
        (new Regex(@"^secrets\.json$", RegexOptions.IgnoreCase), "appsettings.json"), // user secrets (M15a)
        (new Regex(@"^dotnet-tools\.json$", RegexOptions.IgnoreCase), "dotnet-tools.json"),
        (new Regex(@"^codalus-plugin\.json$", RegexOptions.IgnoreCase), "codalus-plugin.json"),
        (new Regex(@"^runtimeconfig\.template\.json$", RegexOptions.IgnoreCase), "runtimeconfig.template.json"),
        (new Regex(@"^(docker-)?compose(\.[^.]+)*\.ya?ml$", RegexOptions.IgnoreCase), "compose-spec.json"), // the Compose Specification's schema (M17a)
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ConcurrentDictionary<string, Task<JsonSchema?>> _schemas = new(StringComparer.Ordinal);
    private Task<List<(string[] Patterns, string Url)>>? _catalog;

    public string? CacheDirectory { get; } = cacheDirectory;

    public bool AllowDownloads { get; set; } = allowDownloads;

    /// <summary>The schema for a document, or null.</summary>
    public async Task<JsonSchema?> ForAsync(string? path, JsonNodeInfo? root, CancellationToken cancellationToken)
    {
        if (root?.Member("$schema") is { Kind: JsonKind.String, Text: { Length: > 0 } reference })
        {
            if (Uri.TryCreate(reference, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                return await DownloadAsync(reference, cancellationToken).ConfigureAwait(false);
            }

            if (path is not null)
            {
                var file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, reference));
                return await _schemas.GetOrAdd("file:" + file, _ => Task.Run(() => LoadFile(file), CancellationToken.None)).ConfigureAwait(false);
            }
        }

        if (path is null)
        {
            return null;
        }

        var name = Path.GetFileName(path);
        if (s_bundled.FirstOrDefault(b => b.Name.IsMatch(name)) is { Resource: { } resource })
        {
            return await _schemas.GetOrAdd("bundled:" + resource, _ => Task.FromResult<JsonSchema?>(Bundled(resource))).ConfigureAwait(false);
        }

        if (!AllowDownloads && CacheDirectory is null)
        {
            return null;
        }

        var catalog = await CatalogAsync(cancellationToken).ConfigureAwait(false);
        var normalized = path.Replace('\\', '/');
        foreach (var (patterns, url) in catalog)
        {
            if (patterns.Any(p => Glob(p, normalized, name)))
            {
                return await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>A bundled schema by resource name (tests and hover links).</summary>
    public static JsonSchema? Bundled(string resource)
    {
        using var stream = typeof(SchemaStore).Assembly.GetManifestResourceStream("Schemas." + resource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return JsonSchema.Parse(reader.ReadToEnd());
    }

    private JsonSchema? LoadFile(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSchema.Parse(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.Invoke($"Schema {file}: {e.Message}");
            return null;
        }
    }

    private Task<JsonSchema?> DownloadAsync(string url, CancellationToken cancellationToken) =>
        _schemas.GetOrAdd(url, _ => Task.Run(async () =>
        {
            var text = await FetchAsync(url, CancellationToken.None).ConfigureAwait(false);
            try
            {
                return text is null ? null : JsonSchema.Parse(text);
            }
            catch (JsonException e)
            {
                log?.Invoke($"Schema {url}: {e.Message}");
                return null;
            }
        }, CancellationToken.None)).WaitAsync(cancellationToken);

    private async Task<List<(string[] Patterns, string Url)>> CatalogAsync(CancellationToken cancellationToken)
    {
        _catalog ??= Task.Run(async () =>
        {
            var entries = new List<(string[], string)>();
            var text = await FetchAsync(CatalogUrl, CancellationToken.None).ConfigureAwait(false);
            if (text is null)
            {
                return entries;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                foreach (var schema in document.RootElement.GetProperty("schemas").EnumerateArray())
                {
                    if (schema.TryGetProperty("fileMatch", out var matches) && matches.ValueKind == JsonValueKind.Array
                        && schema.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                    {
                        entries.Add(([.. matches.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!)], url.GetString()!));
                    }
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                log?.Invoke($"SchemaStore catalog: {e.Message}");
            }

            return entries;
        }, CancellationToken.None);
        return await _catalog.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A cached copy (fresh, or stale when downloads are off or fail), else a download.</summary>
    private async Task<string?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var cached = CacheDirectory is null ? null : Path.Combine(CacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".json");
        if (cached is not null && File.Exists(cached) && (!AllowDownloads || DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < s_maxAge))
        {
            return await File.ReadAllTextAsync(cached, cancellationToken).ConfigureAwait(false);
        }

        if (!AllowDownloads)
        {
            return null;
        }

        try
        {
            var text = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                Directory.CreateDirectory(CacheDirectory!);
                await File.WriteAllTextAsync(cached, text, cancellationToken).ConfigureAwait(false);
            }

            return text;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            log?.Invoke($"Download of {url} failed: {e.Message}");
            return cached is not null && File.Exists(cached) ? await File.ReadAllTextAsync(cached, cancellationToken).ConfigureAwait(false) : null;
        }
    }

    /// <summary>SchemaStore's file patterns: a file name, or a glob over the path (<c>**/</c>, <c>*</c>).</summary>
    internal static bool Glob(string pattern, string path, string name)
    {
        if (!pattern.Contains('/', StringComparison.Ordinal))
        {
            return Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", "[^/]*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        }

        var regex = Regex.Escape(pattern.TrimStart('/'))
            .Replace("\\*\\*/", "(.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);
        return Regex.IsMatch(path, "(^|/)" + regex + "$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
    }
}
