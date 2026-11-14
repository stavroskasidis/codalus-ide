using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codalus.Workbench.Debugging.Remote;
using Codalus.Workbench.Node;

namespace Codalus.Workbench.Debugging;

/// <summary>A bundle of the Codalus debugger that this install can download: its platform, the release asset and its SHA-256.</summary>
public sealed record DebuggerComponent(string Platform, string Asset, string Sha256, long Size, string Url);

/// <summary>
/// The Codalus debugger's bundles for programs that this machine's own debugger can't debug (M58b, ADR 0094), which a
/// package offers as on-demand components instead of carrying them:
/// <list type="bullet">
/// <item><c>osx-x64</c>: x64 programs on Apple silicon (.NET 5 and earlier under Rosetta 2), which an x64 debugger debugs;</item>
/// <item><c>win-x86</c>: 32-bit .NET Framework programs, which a 32-bit debugger debugs.</item>
/// </list>
/// A bundle is <c>codalus-debughost</c> with its own .NET runtime, an asset of the release this install is of. The
/// package's <c>debughost/debugger-components.json</c> names each asset with its SHA-256 (written by
/// <c>eng/package.sh</c>, which built it), so a download is checked against what this install was packaged with. A
/// bundle in <c>CODALUS_DEBUGGER_OSX_X64</c> (or <c>_WIN_X86</c>; a folder) or a source checkout's
/// <c>.work/codalus-debugger-&lt;platform&gt;</c> (<c>eng/get-codalus-debugger.sh</c>) is used as it is.
/// </summary>
public sealed class DebuggerComponents(string cacheFolder, HttpClient? http = null, string? indexPath = null) : IDisposable
{
    public const string IndexFile = "debugger-components.json";

    public const string X64OnAppleSilicon = "osx-x64";

    public const string X86OnWindows = "win-x86";

    private readonly SemaphoreSlim _install = new(1, 1);

    private string IndexPath => indexPath ?? Path.Combine(AppContext.BaseDirectory, "debughost", IndexFile);

    /// <summary>The bundles this install can download; none in a source checkout or a package that offers none.</summary>
    public IReadOnlyList<DebuggerComponent> Available
    {
        get
        {
            try
            {
                return File.Exists(IndexPath) ? JsonSerializer.Deserialize(File.ReadAllText(IndexPath), DebuggerComponentsJsonContext.Default.DebuggerComponentArray) ?? [] : [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    /// <summary>A platform's bundle folder under the cache, relative to it (the Components page's folder).</summary>
    public string RelativeFolder(string platform) =>
        Path.Combine("codalus-debugger", Available.FirstOrDefault(c => c.Platform == platform) is { } component ? Stem(component.Asset) : platform);

    private static string Stem(string asset) => asset.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? asset[..^7] : Path.GetFileNameWithoutExtension(asset);

    private static string Executable(string platform) => platform.StartsWith("win-", StringComparison.Ordinal) ? "codalus-debughost.exe" : RemoteDebuggers.CodalusExecutable;

    /// <summary>
    /// The bundle's debugger on this machine when it is here already: the variable's or the checkout's, else the
    /// downloaded one. Null when it has to be downloaded first (<see cref="EnsureAsync"/>), or can't be.
    /// </summary>
    public string? Installed(string platform)
    {
        if (Local(platform) is { } local)
        {
            return local;
        }

        var path = Path.Combine(cacheFolder, RelativeFolder(platform), Executable(platform));
        return File.Exists(path) ? path : null;
    }

    /// <summary>The variable's or the checkout's bundle (it isn't a download).</summary>
    private static string? Local(string platform)
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("CODALUS_DEBUGGER_" + platform.Replace('-', '_').ToUpperInvariant()) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Codalus.slnx")))
            {
                candidates.Add(Path.Combine(directory.FullName, ".work", "codalus-debugger-" + platform));
                break;
            }
        }

        return candidates.Select(c => Path.Combine(c, Executable(platform))).FirstOrDefault(File.Exists);
    }

    /// <summary>Whether <paramref name="platform"/>'s bundle is here or can be downloaded.</summary>
    public bool Offers(string platform) => Installed(platform) is not null || Available.Any(c => c.Platform == platform);

    /// <summary>
    /// The bundle's debugger for <paramref name="platform"/>, downloaded and unpacked first when it isn't here: the
    /// release's asset, refused unless its SHA-256 is the one this install was packaged with. Throws
    /// <see cref="InvalidOperationException"/> when this install offers none for the platform.
    /// </summary>
    public async Task<string> EnsureAsync(string platform, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (Installed(platform) is { } installed)
        {
            return installed;
        }

        var component = Available.FirstOrDefault(c => c.Platform == platform)
            ?? throw new InvalidOperationException($"This installation has no Codalus debugger bundle for {platform} to download ({IndexFile} doesn't list one).");
        await _install.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = Path.Combine(cacheFolder, RelativeFolder(platform));
            using var cacheLock = await CacheLock.AcquireAsync(folder + ".lock", cancellationToken).ConfigureAwait(false);
            if (Installed(platform) is { } meanwhile)
            {
                return meanwhile;
            }

            var download = folder + ".tar.gz.partial";
            await VerifiedDownload.FetchAsync(http, new Uri(component.Url), download, component.Sha256, $"the Codalus debugger for {platform}", progress, cancellationToken).ConfigureAwait(false);
            progress?.Report($"Unpacking the Codalus debugger for {platform}…");
            var staging = folder + ".staging";
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

            // The archive has one top folder; it becomes the bundle's folder.
            var top = Directory.GetDirectories(staging).Single();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.Move(top, folder);
            Directory.Delete(staging, recursive: true);
            File.Delete(download);
            var path = Path.Combine(folder, Executable(platform));
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"{component.Asset} has no {Executable(platform)}.");
            }

            progress?.Report($"The Codalus debugger for {platform} is ready.");
            return path;
        }
        finally
        {
            _install.Release();
        }
    }

    public void Dispose() => _install.Dispose();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DebuggerComponent[]))]
internal sealed partial class DebuggerComponentsJsonContext : JsonSerializerContext;
