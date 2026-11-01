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
    public const string Version = "26.13.0";

    /// <summary>The SHA-256 of each archive, from https://nodejs.org/dist/v26.13.0/SHASUMS256.txt.</summary>
    private static readonly Dictionary<string, string> s_checksums = new(StringComparer.Ordinal)
    {
        ["win-x64.zip"] = "dcc60d59a0746999f99064a01c020445d54d84228c691cdbf25b49ac3b59150a",
        ["win-arm64.zip"] = "a9427139595156ef484e1fb08957676e09d4232368dd1743a2a9f93f3d241b14",
        ["linux-x64.tar.gz"] = "5848553868b402d3fabb5b09270649977780a390f832155721025a08b5104455",
        ["linux-arm64.tar.gz"] = "439e9c438acdf6a6ffe47418da709153d66c4bed812e30a473faacb107dd2791",
        ["darwin-x64.tar.gz"] = "525c9b29bb0263d687de60a4cec0e2a6db876a8eb032369e5795eca71e7c358e",
        ["darwin-arm64.tar.gz"] = "d38bd5e5a2ddce6bbb5ce45e46e208337da4d51fffe611e567fd32073d6439f6",
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
