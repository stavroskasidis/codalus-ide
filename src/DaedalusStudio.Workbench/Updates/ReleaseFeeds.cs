using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DaedalusStudio.Workbench.Updates;

/// <summary>A channel's update feed (Velopack's <c>releases.&lt;channel&gt;.json</c>), verified, and where its files are.</summary>
public sealed record ReleaseFeed(string Tag, string Json, Uri? ReleasePage);

/// <summary>A feed or package that must not be trusted: no signature, a signature that doesn't verify, or no hashes.</summary>
public sealed class FeedVerificationException(string message) : Exception(message);

/// <summary>Where releases live (M32, ADR 0029): GitHub releases, or a local folder (tests and the update test script).</summary>
public interface IReleaseStore
{
    /// <summary>The newest release's feed for <paramref name="channel"/> (<c>&lt;rid&gt;-&lt;kind&gt;</c>), verified; null when there is none.</summary>
    Task<ReleaseFeed?> GetFeedAsync(string channel, CancellationToken cancellationToken);

    /// <summary>Downloads a file a feed names (a full or delta package) to <paramref name="destination"/>.</summary>
    Task DownloadAsync(string fileName, string destination, Action<int>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// The feed signature (D19): each <c>releases.&lt;channel&gt;.json</c> has a <c>.sig</c> next to it, the base64 of an
/// ECDSA P-256 / SHA-256 signature (DER) over the file's bytes, made with the project's key. The public key is built in.
/// A feed that doesn't verify is refused; a feed whose packages lack SHA-256 hashes is refused too, since the hashes in
/// the signed feed are what Velopack checks each download against.
/// </summary>
public static class FeedSignature
{
    /// <summary>The project's feed key (SubjectPublicKeyInfo, base64), also in eng/packaging/update-feed-key.pub.pem.</summary>
    public const string ProjectKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEl8piDBAqEMnvJ+kX8ug8Tu88jP5tkFWP9zAAwmWfztlwylb44JfsI3Vu7T0BZ/s6wKWFRkzVJQ/N+x/rxP5PrQ==";

    public static void Verify(byte[] feed, string? signature, string publicKey)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new FeedVerificationException("The update feed has no signature.");
        }

        byte[] der;
        try
        {
            der = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            throw new FeedVerificationException("The update feed's signature isn't valid base64.");
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!key.VerifyData(feed, der, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new FeedVerificationException("The update feed's signature doesn't verify: it wasn't made with the project's key.");
        }

        using var document = JsonDocument.Parse(feed);
        if (!document.RootElement.TryGetProperty("Assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new FeedVerificationException("The update feed lists no packages.");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("SHA256", out var sha) || sha.GetString() is not { Length: 64 })
            {
                throw new FeedVerificationException($"The update feed has no SHA-256 for {(asset.TryGetProperty("FileName", out var name) ? name.GetString() : "a package")}.");
            }
        }
    }

    /// <summary>Signs a feed (tests; the release workflow signs with openssl, see eng/package.sh).</summary>
    public static string Sign(byte[] feed, ECDsa key) =>
        Convert.ToBase64String(key.SignData(feed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    public static string FeedName(string channel) => $"releases.{channel}.json";
}

/// <summary>A folder holding feeds and packages (the update test script; integration tests).</summary>
public sealed class FolderReleaseStore(string folder, string publicKey) : IReleaseStore
{
    public async Task<ReleaseFeed?> GetFeedAsync(string channel, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, FeedSignature.FeedName(channel));
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var signature = File.Exists(path + ".sig") ? await File.ReadAllTextAsync(path + ".sig", cancellationToken).ConfigureAwait(false) : null;
        FeedSignature.Verify(bytes, signature, publicKey);
        return new ReleaseFeed(Path.GetFileName(folder), Encoding.UTF8.GetString(bytes), null);
    }

    public async Task DownloadAsync(string fileName, string destination, Action<int>? progress, CancellationToken cancellationToken)
    {
        var source = Path.Combine(folder, Path.GetFileName(fileName));
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        progress?.Invoke(100);
    }
}

/// <summary>
/// The repository's GitHub releases. A channel's feed is in the newest release of its kind (stable: <c>vX.Y.Z</c>;
/// nightly: <c>vX.Y.Z-nightly.…</c>) that carries it; a release named "… (pulled)" is skipped, which is how a bad release
/// is withdrawn. Packages named by a feed may sit in older releases (delta chains): they are found by name. A private
/// repository needs a token (D20).
/// </summary>
public sealed partial class GitHubReleaseStore(HttpClient http, string repository, string publicKey, Func<CancellationToken, Task<string?>> token) : IReleaseStore
{
    private const string Api = "https://api.github.com";
    private List<Release>? _releases;

    private sealed record Asset(string Name, Uri ApiUrl, Uri DownloadUrl);

    private sealed record Release(string Tag, string Name, bool Draft, Uri? Page, IReadOnlyList<Asset> Assets);

    public async Task<ReleaseFeed?> GetFeedAsync(string channel, CancellationToken cancellationToken)
    {
        var kind = channel[(channel.LastIndexOf('-') + 1)..];
        var feedName = FeedSignature.FeedName(channel);
        var releases = await ReleasesAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        var release = releases.FirstOrDefault(r => !r.Draft && OfKind(r.Tag, kind) && !r.Name.EndsWith("(pulled)", StringComparison.OrdinalIgnoreCase)
            && r.Assets.Any(a => a.Name == feedName));
        if (release is null)
        {
            return null;
        }

        var bytes = await GetBytesAsync(release.Assets.First(a => a.Name == feedName), cancellationToken).ConfigureAwait(false);
        var signature = release.Assets.FirstOrDefault(a => a.Name == feedName + ".sig") is { } sig
            ? Encoding.UTF8.GetString(await GetBytesAsync(sig, cancellationToken).ConfigureAwait(false))
            : null;
        FeedSignature.Verify(bytes, signature, publicKey);
        return new ReleaseFeed(release.Tag, Encoding.UTF8.GetString(bytes), release.Page);
    }

    public async Task DownloadAsync(string fileName, string destination, Action<int>? progress, CancellationToken cancellationToken)
    {
        var releases = await ReleasesAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        var asset = releases.SelectMany(r => r.Assets).FirstOrDefault(a => a.Name == fileName)
            ?? throw new FileNotFoundException($"No release has {fileName}.");
        using var request = await RequestAsync(asset, cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(destination);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (total > 0)
            {
                progress?.Invoke((int)(copied * 100 / total));
            }
        }
    }

    private static bool OfKind(string tag, string kind) => kind switch
    {
        InstallInfo.Stable => StableTag().IsMatch(tag),
        _ => tag.Contains($"-{kind}.", StringComparison.Ordinal),
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"^v\d+\.\d+\.\d+$")]
    private static partial System.Text.RegularExpressions.Regex StableTag();

    private async Task<List<Release>> ReleasesAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (_releases is { } cached && !refresh)
        {
            return cached;
        }

        // Newest first; three pages reach back far enough for a delta chain (nightlies are pruned to 14).
        var releases = new List<Release>();
        for (var page = 1; page <= 3; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/repos/{repository}/releases?per_page=100&page={page}");
            await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new HttpRequestException($"No releases found for {repository} (a private repository needs a token: DAEDALUS_UPDATE_TOKEN or `gh auth login`).");
            }

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var count = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                count++;
                var assets = item.GetProperty("assets").EnumerateArray()
                    .Select(a => new Asset(a.GetProperty("name").GetString()!, new Uri(a.GetProperty("url").GetString()!), new Uri(a.GetProperty("browser_download_url").GetString()!)))
                    .ToList();
                releases.Add(new Release(
                    item.GetProperty("tag_name").GetString()!,
                    item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("draft", out var draft) && draft.GetBoolean(),
                    item.TryGetProperty("html_url", out var page2) && page2.GetString() is { } url ? new Uri(url) : null,
                    assets));
            }

            if (count < 100)
            {
                break;
            }
        }

        return _releases = releases;
    }

    private async Task<byte[]> GetBytesAsync(Asset asset, CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(asset, cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>With a token, through the API (a private repository's assets need it); without one, the public download URL.</summary>
    private async Task<HttpRequestMessage> RequestAsync(Asset asset, CancellationToken cancellationToken)
    {
        var bearer = await token(cancellationToken).ConfigureAwait(false);
        var request = new HttpRequestMessage(HttpMethod.Get, bearer is null ? asset.DownloadUrl : asset.ApiUrl);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }

        return request;
    }

    /// <summary>GitHub's API refuses requests without one.</summary>
    private const string UserAgent = "DaedalusStudio-Updates";

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (await token(cancellationToken).ConfigureAwait(false) is { } bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
    }
}
