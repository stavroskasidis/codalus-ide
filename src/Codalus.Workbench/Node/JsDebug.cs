using System.Formats.Tar;
using System.IO.Compression;

namespace Codalus.Workbench.Node;

/// <summary>
/// vscode-js-debug's DAP server (ADR 0026): a pinned release (<c>js-debug-dap</c>, MIT), downloaded from GitHub on the
/// first browser debug session, checked against its SHA-256, and unpacked into the Node cache. It runs on
/// <see cref="NodeRuntime"/>.
/// </summary>
public sealed class JsDebug(NodeRuntime node, string cacheFolder, HttpClient? http = null) : IDisposable
{
    /// <summary>
    /// The pinned release. On every bump, check that js-debug's browser launcher still skips its navigation when the
    /// launch has <c>skipNavigateForTest</c> (<c>finishLaunch</c> in <c>browserLauncher.ts</c>, an undocumented test hook):
    /// <c>JsDebugAdapter</c> sets it behind the Blazor WebAssembly debugging proxy and opens the page itself, after the
    /// proxy's handshake. Without the hook js-debug opens the page at once again, and the race that avoids is back
    /// (<c>BrowserDebugTests</c>' stand-in proxy fails then).
    /// </summary>
    public const string Version = "1.140.0";

    /// <summary>The SHA-256 of <c>js-debug-dap-v1.140.0.tar.gz</c>, pinned when the version was chosen.</summary>
    internal const string Sha256 = "27dab92937ec1ab35821ae955aac867544fe06a1b6307229049f2d789af10968";

    private readonly SemaphoreSlim _install = new(1, 1);

    public string Folder => Path.Combine(cacheFolder, "js-debug", Version);

    /// <summary><c>dapDebugServer.js</c>: <c>node dapDebugServer.js &lt;port&gt; &lt;host&gt;</c> serves DAP over TCP.</summary>
    public string ServerScript => Path.Combine(Folder, "src", "dapDebugServer.js");

    public string NodePath => node.NodePath;

    public bool IsInstalled => node.IsInstalled && File.Exists(ServerScript);

    /// <summary>Installs Node.js (when missing) and js-debug.</summary>
    public async Task EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await node.EnsureAsync(progress, cancellationToken).ConfigureAwait(false);
        if (IsInstalled)
        {
            return;
        }

        await _install.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another process (a second window, a parallel test assembly) may be installing into the same cache.
            using var cacheLock = await CacheLock.AcquireAsync(Folder + ".lock", cancellationToken).ConfigureAwait(false);
            if (IsInstalled)
            {
                return;
            }

            var download = Folder + ".tar.gz.partial";
            var url = new Uri($"https://github.com/microsoft/vscode-js-debug/releases/download/v{Version}/js-debug-dap-v{Version}.tar.gz");
            await VerifiedDownload.FetchAsync(http, url, download, Sha256, $"vscode-js-debug {Version}", progress, cancellationToken).ConfigureAwait(false);

            progress?.Report("Unpacking vscode-js-debug…");
            var staging = Folder + ".staging";
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            await using (var file = File.OpenRead(download))
            await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
            }

            // The archive has one top folder, js-debug; it becomes the version folder.
            var top = Directory.GetDirectories(staging).Single();
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }

            Directory.Move(top, Folder);
            Directory.Delete(staging, recursive: true);
            File.Delete(download);
            progress?.Report($"vscode-js-debug {Version} is ready.");
        }
        finally
        {
            _install.Release();
        }
    }

    public void Dispose() => _install.Dispose();
}
