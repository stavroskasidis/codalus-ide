using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Codalus.Workbench.Node;

/// <summary>Downloads a file and checks it against a pinned SHA-256 (ADR 0024, 0026). A mismatch deletes it and throws.</summary>
internal static class VerifiedDownload
{
    /// <summary>
    /// How long the download may go without a byte before it counts as stalled. The client's timeout covers only the
    /// response's headers, so a body that stopped coming waited for ever: a stalled download of Node.js held its install
    /// lock, and every feature that needs Node.js waited behind it (macOS CI, run 40632085595: 12 minutes, then 10 more).
    /// </summary>
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How many times a download that stalled or dropped is asked for again, from where it stopped.</summary>
    internal const int Attempts = 4;

    /// <summary>
    /// A folder of verified downloads, by file name, that a download is taken from when it is there with the right
    /// checksum, and kept in when it isn't: CI keeps it between runs, so its tests and smoke tests don't wait on
    /// nodejs.org and github.com (which stalled the macOS smoke test, run 40632085595).
    /// </summary>
    internal const string CacheVariable = "CODALUS_DOWNLOAD_CACHE";

    /// <param name="downloadCache">
    /// The folder of verified downloads; by default <see cref="CacheVariable"/>'s, unless <paramref name="http"/> is given
    /// (a test's own server).
    /// </param>
    public static async Task FetchAsync(HttpClient? http, Uri url, string path, string sha256, string what, IProgress<string>? progress, CancellationToken cancellationToken, TimeSpan? stallTimeout = null, string? downloadCache = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        downloadCache ??= http is null ? Environment.GetEnvironmentVariable(CacheVariable) is { Length: > 0 } folder ? folder : null : null;
        var cached = downloadCache is null ? null : Path.Combine(downloadCache, Path.GetFileName(url.AbsolutePath));
        if (cached is not null && File.Exists(cached) && string.Equals(await Sha256Async(cached, cancellationToken).ConfigureAwait(false), sha256, StringComparison.Ordinal))
        {
            progress?.Report($"{what} from {downloadCache}.");
            File.Copy(cached, path, overwrite: true);
            return;
        }

        progress?.Report($"Downloading {what} from {url.Host}…");
        var client = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            await using var file = File.Create(path);
            await DownloadAsync(client, url, file, what, progress, stallTimeout ?? StallTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (http is null)
            {
                client.Dispose();
            }
        }

        progress?.Report("Verifying the download…");
        var actual = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, sha256, StringComparison.Ordinal))
        {
            File.Delete(path);
            throw new InvalidDataException($"The {what} download doesn't match its published checksum (got {actual}).");
        }

        if (cached is not null)
        {
            try
            {
                // Through a file of its own, so a reader never sees half of it.
                Directory.CreateDirectory(downloadCache!);
                var copy = cached + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                File.Copy(path, copy);
                File.Move(copy, cached, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                progress?.Report($"{what} wasn't kept in {downloadCache}: {ex.Message}");
            }
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Writes the body to <paramref name="file"/>. A body that stalls for <paramref name="stallTimeout"/> or drops is asked
    /// for again from the byte it stopped at (a range request; a server that sends the whole file again starts it over),
    /// up to <see cref="Attempts"/> times in all.
    /// </summary>
    private static async Task DownloadAsync(HttpClient client, Uri url, FileStream file, string what, IProgress<string>? progress, TimeSpan stallTimeout, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        for (var attempt = 1; ; attempt++)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (file.Length > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(file.Length, null);
                }

                stall.CancelAfter(stallTimeout);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (file.Length > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    file.SetLength(0);
                }

                await using var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                while (true)
                {
                    stall.CancelAfter(stallTimeout);
                    var read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && Retriable(ex))
            {
                var reason = ex is OperationCanceledException ? $"no data for {stallTimeout.TotalSeconds:0.#} s" : ex.Message;
                if (attempt == Attempts)
                {
                    throw new IOException($"The {what} download failed after {Attempts} attempts, at {Megabytes(file.Length)} MB: {reason}.", ex);
                }

                progress?.Report($"The {what} download stopped at {Megabytes(file.Length)} MB ({reason}); asking again ({attempt + 1} of {Attempts})…");
            }
        }
    }

    /// <summary>A stall, a dropped connection or a server error, which another request may get past (not a 404).</summary>
    private static bool Retriable(Exception ex) => ex switch
    {
        OperationCanceledException or IOException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500,
        _ => false,
    };

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
