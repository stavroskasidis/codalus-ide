using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DaedalusStudio.Workbench.Node;

/// <summary>
/// The Node.js runtime for the features that need it (D3, ADR 0024): a pinned release downloaded from nodejs.org on
/// first use, verified against its published SHA-256, and kept in the cache folder. Never bundled, never the user's own.
/// </summary>
public sealed class NodeRuntime(string cacheFolder, HttpClient? http = null) : IDisposable
{
    public const string Version = "24.21.0";

    /// <summary>The SHA-256 of each archive, from https://nodejs.org/dist/v24.21.0/SHASUMS256.txt.</summary>
    private static readonly Dictionary<string, string> s_checksums = new(StringComparer.Ordinal)
    {
        ["win-x64.zip"] = "158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541",
        ["win-arm64.zip"] = "8779b1bde1d39f8d420e3b57aa657b39891af434d3de44a919044cec06785921",
        ["linux-x64.tar.gz"] = "6e1db87ef58b8819e5d5402eff1536491b18edd8eb7bee5ef7897876e88dc5ff",
        ["linux-arm64.tar.gz"] = "724282c3b43aec998aa9527380465b45d229e021b58035f5f4f63095eabfe5d5",
        ["darwin-x64.tar.gz"] = "1462cb3b3046b815cf8ea436d3da450ec1a9f11dac7e5a46b0ada5305d7e8097",
        ["darwin-arm64.tar.gz"] = "bed7eea5325e1108f32ce5228ddd6a5f0f08a499ee42aa7442aea583702f6057",
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
            if (IsInstalled)
            {
                return;
            }

            var platform = Platform ?? throw new PlatformNotSupportedException("Node.js isn't available for this platform.");
            var name = $"node-v{Version}-{platform}";
            var url = new Uri($"https://nodejs.org/dist/v{Version}/{name}");
            var download = Path.Combine(cacheFolder, "node", name + ".partial");
            Directory.CreateDirectory(Path.GetDirectoryName(download)!);
            progress?.Report($"Downloading Node.js {Version} from {url.Host}…");
            var client = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            try
            {
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var file = File.Create(download);
                    await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
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
            await using (var stream = File.OpenRead(download))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }

            if (!string.Equals(actual, s_checksums[platform], StringComparison.Ordinal))
            {
                File.Delete(download);
                throw new InvalidDataException($"The Node.js download doesn't match its published checksum (got {actual}).");
            }

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
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }

    public void Dispose() => _install.Dispose();
}
