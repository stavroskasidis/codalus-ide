using System.Text.Json;

namespace Codalus.Workbench.Sdks;

/// <summary>Release metadata, and where it came from: fetched now, or the cache (when, and why a fetch failed).</summary>
public sealed record DotnetMetadata<T>(T Value, DateTimeOffset FetchedAt, bool FromCache)
{
    /// <summary>Why the fetch failed, when the cache answered in its place; null when it was fresh enough or fetched.</summary>
    public string? FetchError { get; init; }
}

/// <summary>
/// Microsoft's .NET release metadata (ADR 0111): <c>releases-index.json</c> and each channel's <c>releases.json</c>,
/// fetched when asked (the SDK manager's window opening, Refresh) and kept in <paramref name="cacheFolder"/> for a day.
/// A failed fetch falls back on the cache whatever its age; with no cache it throws. Nothing here runs on its own.
/// </summary>
/// <param name="location">
/// Where the files are: <see cref="DefaultLocation"/>, another URL, or a folder with the same files
/// (<c>CODALUS_DOTNET_RELEASES</c>: the tests and the smoke test).
/// </param>
/// <param name="fetch">Reads a URL's text (tests answer for the network).</param>
public sealed class DotnetReleaseSource(string cacheFolder, string? location = null, Func<Uri, CancellationToken, Task<string>>? fetch = null, Func<DateTimeOffset>? clock = null)
{
    public const string DefaultLocation = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/";

    /// <summary>The variable that points the manager at another location (<c>docs/environment-variables.md</c>).</summary>
    public const string LocationVariable = "CODALUS_DOTNET_RELEASES";

    /// <summary>How long a fetched file is used without asking again.</summary>
    public static TimeSpan CacheLifetime { get; } = TimeSpan.FromDays(1);

    private static readonly Lazy<HttpClient> s_client = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Codalus-IDE");
        return client;
    });

    private readonly string _location = location is { Length: > 0 } given ? given : DefaultLocation;
    private readonly Func<Uri, CancellationToken, Task<string>> _fetch = fetch ?? ((uri, ct) => s_client.Value.GetStringAsync(uri, ct));
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The location in use, as configured.</summary>
    public string Location => _location;

    /// <summary>Every channel, newest first. <paramref name="refresh"/> asks again even when the cache is fresh.</summary>
    public async Task<DotnetMetadata<IReadOnlyList<DotnetChannel>>> GetIndexAsync(bool refresh, CancellationToken cancellationToken)
    {
        var (text, at, cached, error) = await ReadAsync("releases-index.json", refresh, cancellationToken).ConfigureAwait(false);
        return new(DotnetReleaseParser.ParseIndex(text), at, cached) { FetchError = error };
    }

    /// <summary>A channel's releases (<c>10.0</c>), newest first.</summary>
    public async Task<DotnetMetadata<DotnetChannelReleases>> GetChannelAsync(string channel, bool refresh, CancellationToken cancellationToken)
    {
        if (channel.Length == 0 || channel.Any(c => !char.IsAsciiDigit(c) && c != '.'))
        {
            throw new ArgumentException($"'{channel}' isn't a .NET channel.", nameof(channel));
        }

        var (text, at, cached, error) = await ReadAsync(channel + "/releases.json", refresh, cancellationToken).ConfigureAwait(false);
        return new(DotnetReleaseParser.ParseChannel(text), at, cached) { FetchError = error };
    }

    private async Task<(string Text, DateTimeOffset At, bool FromCache, string? Error)> ReadAsync(string relative, bool refresh, CancellationToken cancellationToken)
    {
        var cacheFile = Path.Combine(cacheFolder, relative.Replace('/', Path.DirectorySeparatorChar));
        var cachedAt = File.Exists(cacheFile) ? new DateTimeOffset(File.GetLastWriteTimeUtc(cacheFile), TimeSpan.Zero) : (DateTimeOffset?)null;
        if (!refresh && cachedAt is { } at && _clock() - at < CacheLifetime && TryRead(cacheFile) is { } fresh)
        {
            return (fresh, at, true, null);
        }

        string text;
        try
        {
            text = await FetchAsync(relative, cancellationToken).ConfigureAwait(false);
            using (JsonDocument.Parse(text))
            {
                // Only what parses replaces the cache.
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UnauthorizedAccessException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (cachedAt is { } stale && TryRead(cacheFile) is { } old)
            {
                return (old, stale, true, ex.Message);
            }

            throw new IOException($"The .NET release metadata ({relative}) couldn't be read from {_location}: {ex.Message}", ex);
        }

        var now = _clock();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var temporary = cacheFile + ".tmp";
            await File.WriteAllTextAsync(temporary, text, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, cacheFile, overwrite: true);
            File.SetLastWriteTimeUtc(cacheFile, now.UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that can't be written costs a fetch next time, nothing more.
        }

        return (text, now, false, null);
    }

    private Task<string> FetchAsync(string relative, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(_location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var baseUri = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
            return _fetch(new Uri(baseUri, relative), cancellationToken);
        }

        // A folder (the tests, the smoke test): the same files, read from disk.
        var folder = uri is { IsFile: true } ? uri.LocalPath : _location;
        return File.ReadAllTextAsync(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)), cancellationToken);
    }

    private static string? TryRead(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
