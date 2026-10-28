using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Codalus.Platform;

namespace Codalus.Workbench.Node;

/// <summary>
/// The Node.js runtime for the features that need it (D3, ADR 0024): a pinned release downloaded from nodejs.org on
/// first use, verified against its published SHA-256, and kept in the cache folder. Never bundled, never the user's own.
/// </summary>
public sealed class NodeRuntime(string cacheFolder, HttpClient? http = null) : IDisposable
{
    public const string Version = "24.22.1";

    /// <summary>The SHA-256 of each archive, from https://nodejs.org/dist/v24.22.1/SHASUMS256.txt.</summary>
    private static readonly Dictionary<string, string> s_checksums = new(StringComparer.Ordinal)
    {
        ["win-x64.zip"] = "d2046d28c2c3dcd211ac5145c0bf30e2047ef9dfb306693c8c6e2eccd8b95fa5",
        ["win-arm64.zip"] = "a0c807319fede24c4a0d8f84564c82a619bd313b16b10c007c383c2609e99171",
        ["linux-x64.tar.gz"] = "eb6728e77c576a35162c122c45ccadf224d89ff00abc8dae46cc2897c6002d7c",
        ["linux-arm64.tar.gz"] = "33c37be234ba8bee041961047b19160f4d7d0f164e8e246f78bdd10feff148b3",
        ["darwin-x64.tar.gz"] = "d132557e7ee2b85af08b0c8c34338e65c6430f2238d01f727728ef42dc83b035",
        ["darwin-arm64.tar.gz"] = "89c682593d76cd44233fa53b1563264e7de9cbb189cad169135c5b4fadfbc0a7",
    };

    private readonly SemaphoreSlim _install = new(1, 1);

    /// <summary>The archive name for this machine (<c>win-x64.zip</c>…), or null on an unsupported platform.</summary>
    public static string? Platform
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : null;
            return arch is null || os is null ? null : $"{os}-{arch}{(os == "win" ? ".zip" : ".tar.gz")}";
        }
    }

    /// <summary>The folder of this version (it holds <c>node</c> and <c>npm</c> once installed).</summary>
    public string Folder => Path.Combine(cacheFolder, "node", Version);

    /// <summary>The <c>node</c> executable, whether or not it is installed.</summary>
    public string NodePath => OperatingSystem.IsWindows() ? Path.Combine(Folder, "node.exe") : Path.Combine(Folder, "bin", "node");

    /// <summary>npm's command-line script (run with <see cref="NodePath"/>).</summary>
    public string NpmCli => OperatingSystem.IsWindows()
        ? Path.Combine(Folder, "node_modules", "npm", "bin", "npm-cli.js")
        : Path.Combine(Folder, "lib", "node_modules", "npm", "bin", "npm-cli.js");

    public bool IsInstalled => File.Exists(NodePath) && File.Exists(NpmCli);

    /// <summary>Downloads, verifies and unpacks the runtime if it isn't there yet. <paramref name="progress"/> gets messages.</summary>
    public async Task EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
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

            var platform = Platform ?? throw new PlatformNotSupportedException("Node.js isn't available for this platform.");
            var name = $"node-v{Version}-{platform}";
            var url = new Uri($"https://nodejs.org/dist/v{Version}/{name}");
            var download = Path.Combine(cacheFolder, "node", name + ".partial");
            await VerifiedDownload.FetchAsync(http, url, download, s_checksums[platform], $"Node.js {Version}", progress, cancellationToken).ConfigureAwait(false);

            progress?.Report("Unpacking Node.js…");
            var staging = Folder + ".staging";
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            if (platform.EndsWith(".zip", StringComparison.Ordinal))
            {
                await ZipFile.ExtractToDirectoryAsync(download, staging, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var file = File.OpenRead(download);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
            }

            // The archive has one top folder, node-v<version>-<platform>; it becomes the version folder.
            var top = Directory.GetDirectories(staging).Single();
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }

            Directory.Move(top, Folder);
            Directory.Delete(staging, recursive: true);
            File.Delete(download);
            progress?.Report($"Node.js {Version} is ready.");
        }
        finally
        {
            _install.Release();
        }
    }

    /// <summary>Runs npm with <paramref name="arguments"/> in <paramref name="directory"/>. Returns the exit code and the output.</summary>
    public async Task<(int ExitCode, string Output)> NpmAsync(IReadOnlyList<string> arguments, string directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(NodePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = directory,
        };
        start.ArgumentList.Add(NpmCli);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // npm finds node next to itself; keep the user's npm configuration out of the install.
        var bin = Path.GetDirectoryName(NodePath)!;
        start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["npm_config_update_notifier"] = "false";
        start.Environment["npm_config_fund"] = "false";
        start.Environment["npm_config_audit"] = "false";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("npm did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessTree.Kill(process);
            throw;
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }

    public void Dispose() => _install.Dispose();
}
