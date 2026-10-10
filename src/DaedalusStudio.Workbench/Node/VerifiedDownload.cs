using System.Security.Cryptography;

namespace DaedalusStudio.Workbench.Node;

/// <summary>Downloads a file and checks it against a pinned SHA-256 (ADR 0024, 0026). A mismatch deletes it and throws.</summary>
internal static class VerifiedDownload
{
    public static async Task FetchAsync(HttpClient? http, Uri url, string path, string sha256, string what, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        progress?.Report($"Downloading {what} from {url.Host}…");
        var client = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (http is null)
            {
                client.Dispose();
            }
        }

        progress?.Report("Verifying the download…");
        string actual;
        await using (var stream = File.OpenRead(path))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(actual, sha256, StringComparison.Ordinal))
        {
            File.Delete(path);
            throw new InvalidDataException($"The {what} download doesn't match its published checksum (got {actual}).");
        }
    }
}
