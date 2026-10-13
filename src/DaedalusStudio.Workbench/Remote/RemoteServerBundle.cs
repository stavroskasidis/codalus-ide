using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DaedalusStudio.Workbench.Debugging.Remote;

namespace DaedalusStudio.Workbench.Remote;

/// <summary>A folder of the server bundle: its name there and where its files are here.</summary>
public sealed record BundlePart(string Name, string Source);

/// <summary>Where the bundle is on the remote machine, and that machine's platform.</summary>
/// <param name="Folder">The bundle's folder there (absolute).</param>
/// <param name="Platform">A runtime identifier (<c>linux-x64</c>, <c>osx-arm64</c>…), or null for one we don't know.</param>
/// <param name="WslDistribution">The distribution's name when the machine is WSL (<c>WSL_DISTRO_NAME</c>).</param>
public sealed record InstalledServer(string Folder, string? Platform, bool SelfContained, string? WslDistribution, bool Copied);

/// <summary>
/// The version-locked server bundle (M21, ADR 0050): <c>server/</c> (self-contained for the remote platform when this
/// install carries one in <c>remote/&lt;rid&gt;/</c>, else the framework-dependent <c>remote/server/</c>), and the hosts
/// it starts there — <c>buildhost/</c>, <c>lshost/</c>, <c>markuphost/</c> — the same builds the shell runs here. It is
/// copied as a <c>.tar.gz</c> through the user's ssh or wsl.exe (the remote machine needs <c>tar</c>, <c>gzip</c> and
/// <c>sha256sum</c> or <c>shasum</c>, not network access), checked against its SHA-256 there, and kept in
/// <c>~/.daedalus-server/&lt;version&gt;/</c> with that checksum, so it is copied again only when the client changes:
/// updates follow the client. Archives are cached here by the files' sizes and times.
/// </summary>
public sealed class RemoteServerBundle(string cacheFolder, string version, IReadOnlyList<BundlePart> hosts, Func<string?, string?> serverFolder, string serverHome = RemoteServerBundle.DefaultHome)
{
    /// <summary>Where bundles live on the remote machine (a shell expression), one folder per version.</summary>
    public const string DefaultHome = "$HOME/.daedalus-server";

    /// <summary>The folder name of a version (what a shell expression can hold safely).</summary>
    public string VersionFolder { get; } = new([.. version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_' ? c : '_')]);

    /// <summary>The runtime identifier of <c>uname -s</c> and <c>uname -m</c>.</summary>
    public static string? Platform(string os, string architecture) => (os, architecture) switch
    {
        ("Linux", "x86_64" or "amd64") => "linux-x64",
        ("Linux", "aarch64" or "arm64") => "linux-arm64",
        ("Darwin", "arm64") => "osx-arm64",
        ("Darwin", "x86_64") => "osx-x64",
        _ => null,
    };

    /// <summary>
    /// The server folder for a platform in this install: <c>remote/&lt;rid&gt;/</c> (self-contained), else
    /// <c>remote/server/</c> (framework-dependent), else <c>DAEDALUS_SERVER</c>'s folder (development and tests).
    /// </summary>
    public static string? LocateServer(string? platform)
    {
        if (platform is not null && Path.Combine(AppContext.BaseDirectory, "remote", platform) is var native && File.Exists(Path.Combine(native, "daedalus-server")))
        {
            return native;
        }

        if (Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } fromEnv)
        {
            return Directory.Exists(fromEnv) ? fromEnv : Path.GetDirectoryName(fromEnv);
        }

        var portable = Path.Combine(AppContext.BaseDirectory, "remote", "server");
        return File.Exists(Path.Combine(portable, "daedalus-server.dll")) ? portable : null;
    }

    /// <summary>A framework-dependent server build (<c>daedalus-server.dll</c>) or its folder: development and tests.</summary>
    public const string ServerVariable = "DAEDALUS_SERVER";

    /// <summary>
    /// Makes sure the bundle is on <paramref name="target"/>: probes its platform and installed checksum, and copies
    /// the bundle when it differs. Returns where it is.
    /// </summary>
    public async Task<InstalledServer> EnsureAsync(RemoteTarget target, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var home = serverHome.TrimEnd('/') + "/" + VersionFolder;
        var probe = await RemoteShell.RunAsync(target, $"d=\"{home}\"; printf 'folder %s\\nsystem %s %s\\nwsl %s\\nbundle %s\\n' \"$d\" \"$(uname -s)\" \"$(uname -m)\" \"$WSL_DISTRO_NAME\" \"$(cat \"$d/.bundle\" 2>/dev/null)\"", cancellationToken).ConfigureAwait(false);
        var fields = probe.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Select(l => (Key: l.Split(' ', 2)[0], Value: l.Contains(' ', StringComparison.Ordinal) ? l.Split(' ', 2)[1].Trim() : string.Empty))
            .GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        if (!fields.TryGetValue("folder", out var folder) || !folder.StartsWith('/') || !fields.TryGetValue("system", out var system))
        {
            throw new InvalidOperationException($"Couldn't reach {target.Display}: {RemoteShell.Trim(probe.Error + probe.Output)}");
        }

        var uname = system.Split(' ');
        var platform = uname.Length == 2 ? Platform(uname[0], uname[1]) : null;
        var wsl = fields.GetValueOrDefault("wsl") is { Length: > 0 } distribution ? distribution : null;
        var server = serverFolder(platform) ?? throw new InvalidOperationException(
            "The remote server (daedalus-server) isn't part of this installation: remote/ next to the shell is missing.");
        var selfContained = File.Exists(Path.Combine(server, "daedalus-server")) && !File.Exists(Path.Combine(server, "daedalus-server.dll"));
        var archive = await ArchiveAsync([new BundlePart("server", server), .. hosts], progress, cancellationToken).ConfigureAwait(false);
        if (fields.GetValueOrDefault("bundle") == archive.Sha256)
        {
            return new InstalledServer(folder, platform, selfContained, wsl, Copied: false);
        }

        progress?.Report($"Copying the Daedalus server ({archive.Size / (1024 * 1024)} MB) to {target.Display}…");
        await using var stream = File.OpenRead(archive.Path);
        var install = await RemoteShell.RunAsync(target, InstallScript(home, archive.Sha256), cancellationToken, stream).ConfigureAwait(false);
        var answer = install.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
        if (answer.StartsWith("checksum ", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The server bundle arrived damaged on {target.Display}: its SHA-256 is {answer[9..]}, not {archive.Sha256}.");
        }

        return answer.StartsWith("ok ", StringComparison.Ordinal)
            ? new InstalledServer(answer[3..], platform, selfContained, wsl, Copied: true)
            : throw new InvalidOperationException($"Couldn't install the server on {target.Display} (it needs tar, gzip and sha256sum or shasum): {RemoteShell.Trim(install.Error + install.Output)}");
    }

    /// <summary>
    /// Reads the archive from stdin into a temporary folder next to the version's, checks its SHA-256, unpacks it and
    /// moves it into place (a half-copied bundle is never used).
    /// </summary>
    internal static string InstallScript(string home, string sha256) => $$"""
        d="{{home}}"; t="$d.tmp.$$"
        rm -rf "$t" && mkdir -p "$t" && cat > "$t/bundle.tar.gz" || exit 2
        h=$( (sha256sum "$t/bundle.tar.gz" 2>/dev/null || shasum -a 256 "$t/bundle.tar.gz") | cut -d' ' -f1)
        if [ "$h" != "{{sha256}}" ]; then rm -rf "$t"; echo "checksum $h"; exit 3; fi
        tar -xzf "$t/bundle.tar.gz" -C "$t" && rm -f "$t/bundle.tar.gz" || { rm -rf "$t"; exit 4; }
        [ -f "$t/server/daedalus-server" ] && chmod +x "$t/server/daedalus-server"
        echo "{{sha256}}" > "$t/.bundle" && rm -rf "$d" && mv "$t" "$d" && echo "ok $d"
        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The command that runs the installed server on the target: its own executable, or <c>dotnet</c> with its dll.</summary>
    public static string StartScript(string folder) => $$"""
        [ -f "$HOME/.profile" ] && . "$HOME/.profile" >/dev/null 2>&1 </dev/null
        PATH="$PATH:$HOME/.dotnet"; export PATH
        d="{{folder}}"
        if [ -f "$d/server/daedalus-server.dll" ]; then exec dotnet "$d/server/daedalus-server.dll" --stdio; else exec "$d/server/daedalus-server" --stdio; fi
        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The bundle's archive (cached by its files' names, sizes and times) and its SHA-256.</summary>
    public async Task<(string Path, string Sha256, long Size)> ArchiveAsync(IReadOnlyList<BundlePart> parts, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var files = parts
            .SelectMany(part => Directory.EnumerateFiles(part.Source, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                .Select(f => (Name: part.Name + "/" + Path.GetRelativePath(part.Source, f).Replace('\\', '/'), File: new FileInfo(f))))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        var fingerprint = new StringBuilder(VersionFolder);
        foreach (var (name, file) in files)
        {
            fingerprint.Append('\n').Append(name).Append(' ').Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }

        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())))[..16];
        var folder = Path.Combine(cacheFolder, "remote-server");
        var path = Path.Combine(folder, key + ".tar.gz");
        var sumFile = path + ".sha256";
        if (File.Exists(path) && File.Exists(sumFile))
        {
            return (path, (await File.ReadAllTextAsync(sumFile, cancellationToken).ConfigureAwait(false)).Trim(), new FileInfo(path).Length);
        }

        progress?.Report("Packing the Daedalus server…");
        Directory.CreateDirectory(folder);
        var temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        await using (var file = File.Create(temporary))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var (name, info) in files)
            {
                await using var data = info.OpenRead();
                var executable = name.EndsWith("/daedalus-server", StringComparison.Ordinal) || !Path.HasExtension(name);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data, Mode = executable ? Executable : Regular };
                await tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }

        string sha256;
        await using (var file = File.OpenRead(temporary))
        {
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        }

        File.Move(temporary, path, overwrite: true);
        await File.WriteAllTextAsync(sumFile, sha256, cancellationToken).ConfigureAwait(false);

        // Older archives of this cache are of older builds.
        foreach (var old in Directory.EnumerateFiles(folder, "*.tar.gz").Where(f => !string.Equals(f, path, StringComparison.Ordinal)))
        {
            TryDelete(old);
            TryDelete(old + ".sha256");
        }

        return (path, sha256, new FileInfo(path).Length);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const UnixFileMode Regular = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode Executable = Regular | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
}
