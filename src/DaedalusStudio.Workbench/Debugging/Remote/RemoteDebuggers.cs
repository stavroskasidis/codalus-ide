using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DaedalusStudio.Debugging;

namespace DaedalusStudio.Workbench.Debugging.Remote;

/// <summary>
/// netcoredbg on a <see cref="RemoteTarget"/> (M14c, ADR 0039): found in <see cref="RemoteTarget.DebuggerHome"/>, or
/// copied there with the user's consent, streamed as a <c>.tar.gz</c> into <c>tar -xzf -</c> on the target (so the
/// target needs <c>tar</c> and <c>gzip</c>, not network access). The archive is the pinned release of the target's
/// platform (the version and checksums of <c>eng/get-netcoredbg.sh</c>), from, in order:
/// <c>DAEDALUS_NETCOREDBG_LINUX_X64</c> (and the other platforms; a folder or an archive), this machine's netcoredbg
/// when the platforms match, <c>.work/netcoredbg-&lt;platform&gt;</c> in a source checkout, or a download into the
/// cache, verified by SHA-256.
/// </summary>
public sealed class RemoteDebuggers(string cacheFolder, HttpClient? http = null)
{
    public const string Version = "3.2.0-1092";

    /// <summary>The release assets by platform, with their SHA-256 (as in <c>eng/get-netcoredbg.sh</c>).</summary>
    public static IReadOnlyDictionary<string, (string Asset, string Sha256)> Assets { get; } = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["linux-x64"] = ("netcoredbg-linux-amd64.tar.gz", "080eb3b2d2152465f599d3b33d1ee6e747794e11cc0a3773ec689f5e5f2c5afa"),
        ["linux-arm64"] = ("netcoredbg-linux-arm64.tar.gz", "065ff49badec8a695dbea2de6ab6a330c774a191e426a217ab8cc05250627ccb"),
        ["osx-arm64"] = ("netcoredbg-osx-arm64.zip", "f4fa33b3ff874910cc184b4bb3b9c56d0abdf5c6521cee0b144d7c6e4a6e59ea"),
        ["win-x64"] = ("netcoredbg-win64.zip", "3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a"),
    };

    /// <summary>The platform of <c>uname -s</c> and <c>uname -m</c>; null when netcoredbg has no build for it.</summary>
    public static string? Platform(string os, string architecture) => (os, architecture) switch
    {
        ("Linux", "x86_64" or "amd64") => "linux-x64",
        ("Linux", "aarch64" or "arm64") => "linux-arm64",
        ("Darwin", "arm64") => "osx-arm64",
        _ => null,
    };

    /// <summary>This machine's platform, in the same terms.</summary>
    public static string? LocalPlatform =>
        OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64 ? "win-x64"
        : OperatingSystem.IsLinux() ? Platform("Linux", RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64")
        : OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64"
        : null;

    /// <summary>
    /// The path of netcoredbg on <paramref name="target"/>, copying it there first when it's missing and
    /// <paramref name="consent"/> agrees (it gets the question). Throws when it can't be had.
    /// </summary>
    public async Task<string> EnsureAsync(RemoteTarget target, Func<string, Task<bool>> consent, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var folder = $"{target.DebuggerHome}/{Version}";
        var probe = await RemoteShell.RunAsync(target, $"d=\"{folder}\"; if [ -x \"$d/netcoredbg\" ]; then echo \"ok $d/netcoredbg\"; else echo \"missing $(uname -s) $(uname -m)\"; fi", cancellationToken).ConfigureAwait(false);
        var answer = probe.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
        if (answer.StartsWith("ok ", StringComparison.Ordinal))
        {
            return answer[3..];
        }

        if (!answer.StartsWith("missing ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Couldn't reach {target.Display}: {RemoteShell.Trim(probe.Error + probe.Output)}");
        }

        var system = answer["missing ".Length..].Split(' ');
        var platform = system.Length == 2 ? Platform(system[0], system[1]) : null;
        if (platform is null)
        {
            throw new InvalidOperationException($"netcoredbg has no build for {target.Display} ({answer["missing ".Length..]}).");
        }

        if (!await consent($"The debugger (netcoredbg {Version}, MIT) isn't on {target.Display}. Copy the {platform} build to {folder} there?").ConfigureAwait(false))
        {
            throw new OperationCanceledException("The debugger wasn't copied.");
        }

        var archive = await ArchiveAsync(platform, progress, cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report($"Copying netcoredbg to {target.Display}…");
            await using var stream = File.OpenRead(archive.Path);
            var install = await RemoteShell.RunAsync(
                target,
                $"d=\"{folder}\"; rm -rf \"$d.tmp\" && mkdir -p \"$d.tmp\" && tar -xzf - -C \"$d.tmp\" && rm -rf \"$d\" && mv \"$d.tmp/netcoredbg\" \"$d\" && rm -rf \"$d.tmp\" && chmod +x \"$d/netcoredbg\" && echo \"ok $d/netcoredbg\"",
                cancellationToken,
                stream).ConfigureAwait(false);
            var installed = install.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
            return installed.StartsWith("ok ", StringComparison.Ordinal)
                ? installed[3..]
                : throw new InvalidOperationException($"Couldn't copy netcoredbg to {target.Display} (it needs tar and gzip): {RemoteShell.Trim(install.Error + install.Output)}");
        }
        finally
        {
            if (archive.Temporary)
            {
                File.Delete(archive.Path);
            }
        }
    }

    /// <summary>The <c>.tar.gz</c> of <paramref name="platform"/>'s netcoredbg (a <c>netcoredbg/</c> folder inside).</summary>
    public async Task<(string Path, bool Temporary)> ArchiveAsync(string platform, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!Assets.TryGetValue(platform, out var asset))
        {
            throw new InvalidOperationException($"netcoredbg has no build for {platform}.");
        }

        var variable = "DAEDALUS_NETCOREDBG_" + platform.Replace('-', '_').ToUpperInvariant();
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        if (LocalPlatform == platform && DebugAdapterLocator.Resolve() is { } local)
        {
            candidates.Add(Path.GetDirectoryName(local)!);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DaedalusStudio.slnx")))
            {
                candidates.Add(Path.Combine(directory.FullName, ".work", "netcoredbg-" + platform));
                break;
            }
        }

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "netcoredbg")))
            {
                return (await PackAsync(candidate, cancellationToken).ConfigureAwait(false), true);
            }

            if (File.Exists(candidate))
            {
                return candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? (await RepackAsync(candidate, cancellationToken).ConfigureAwait(false), true)
                    : (candidate, false);
            }
        }

        var cached = Path.Combine(cacheFolder, "netcoredbg", Version, asset.Asset);
        if (!File.Exists(cached))
        {
            await DownloadAsync(asset.Asset, asset.Sha256, cached, progress, cancellationToken).ConfigureAwait(false);
        }

        return cached.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? (await RepackAsync(cached, cancellationToken).ConfigureAwait(false), true)
            : (cached, false);
    }

    private async Task DownloadAsync(string asset, string sha256, string target, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var url = $"https://github.com/Samsung/netcoredbg/releases/download/{Version}/{asset}";
        progress?.Report($"Downloading {url}…");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".download";
        var client = http ?? new HttpClient();
        try
        {
            await using (var response = await client.GetStreamAsync(new Uri(url), cancellationToken).ConfigureAwait(false))
            await using (var file = File.Create(temporary))
            {
                await response.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            string actual;
            await using (var file = File.OpenRead(temporary))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            }

            if (actual != sha256)
            {
                throw new InvalidDataException($"{asset}'s SHA-256 is {actual}, not {sha256}.");
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
            if (http is null)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>A folder with netcoredbg as a temporary <c>.tar.gz</c> (executables marked so).</summary>
    public static async Task<string> PackAsync(string folder, CancellationToken cancellationToken)
    {
        var archive = Path.Combine(Path.GetTempPath(), "daedalus-netcoredbg-" + Guid.NewGuid().ToString("N")[..8] + ".tar.gz");
        await using var file = File.Create(archive);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var name = "netcoredbg/" + Path.GetRelativePath(folder, path).Replace('\\', '/');
            await using var data = File.OpenRead(path);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data, Mode = Executable };
            await tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return archive;
    }

    /// <summary>A release <c>.zip</c> (macOS) as a temporary <c>.tar.gz</c>.</summary>
    private static async Task<string> RepackAsync(string zip, CancellationToken cancellationToken)
    {
        var archive = Path.Combine(Path.GetTempPath(), "daedalus-netcoredbg-" + Guid.NewGuid().ToString("N")[..8] + ".tar.gz");
        using var source = ZipFile.OpenRead(zip);
        await using var file = File.Create(archive);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var item in source.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            // The entry's length must be known: a zip entry's stream doesn't seek.
            using var data = new MemoryStream();
            await using (var compressed = item.Open())
            {
                await compressed.CopyToAsync(data, cancellationToken).ConfigureAwait(false);
            }

            data.Position = 0;
            var entry = new PaxTarEntry(TarEntryType.RegularFile, item.FullName.Replace('\\', '/')) { DataStream = data, Mode = Executable };
            await tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return archive;
    }

    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
}
