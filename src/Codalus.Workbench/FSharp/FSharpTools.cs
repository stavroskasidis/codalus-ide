using Codalus.Rpc;
using Codalus.Workbench.Node;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.FSharp;

/// <summary>
/// The F# tools (M36, ADR 0067): FsAutoComplete, the F# language server (D10), and Fantomas, the formatter it runs.
/// Both are .NET tools on NuGet, installed on first use with the user's own <c>dotnet tool install --tool-path</c> into
/// the cache folder at pinned versions: the user's NuGet sources and proxy apply, nuget.org's package signatures are
/// checked by the SDK, and the tool picks the newest of its frameworks the installed runtimes can run.
/// FsAutoComplete finds Fantomas through a local tool manifest, a global tool, or <c>PATH</c>, where we put ours last.
/// </summary>
public sealed class FSharpTools(string cacheFolder, string dotnet, ProcessRunner? run = null) : IDisposable
{
    public const string FsAutoCompleteVersion = "0.85.3";

    public const string FantomasVersion = "7.0.3";

    // dotnet tool install downloads the package: minutes on a slow connection, not the 30 seconds of a version check.
    private readonly ProcessRunner _run = run ?? ((program, arguments, directory, ct) => EnvironmentCheck.RunProcessAsync(program, arguments, directory, TimeSpan.FromMinutes(10), ct));
    private readonly SemaphoreSlim _install = new(1, 1);

    /// <summary>The folder both tools install into (one subfolder per tool and version).</summary>
    public string Folder => Path.Combine(cacheFolder, "fsharp");

    public string FsAutoCompleteFolder => Path.Combine(Folder, "fsautocomplete", FsAutoCompleteVersion);

    /// <summary>The tool path with the <c>fantomas</c> command (it goes on FsAutoComplete's <c>PATH</c>).</summary>
    public string FantomasFolder => Path.Combine(Folder, "fantomas", FantomasVersion);

    public bool IsInstalled => FsAutoCompleteDll is not null && File.Exists(Path.Combine(FantomasFolder, Marker));

    /// <summary><c>fsautocomplete.dll</c> of the installed tool (of the framework <c>dotnet tool install</c> chose), or null.</summary>
    public string? FsAutoCompleteDll =>
        File.Exists(Path.Combine(FsAutoCompleteFolder, Marker)) && Directory.Exists(Path.Combine(FsAutoCompleteFolder, ".store"))
            ? Directory.EnumerateFiles(Path.Combine(FsAutoCompleteFolder, ".store"), "fsautocomplete.dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;

    private const string Marker = ".installed";

    public void Dispose() => _install.Dispose();

    /// <summary>Installs whichever tool is missing; another process installing into the same cache is waited for.</summary>
    public async Task EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (IsInstalled)
        {
            return;
        }

        await _install.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cacheLock = await CacheLock.AcquireAsync(Path.Combine(Folder, "install.lock"), cancellationToken).ConfigureAwait(false);
            await InstallAsync("fsautocomplete", FsAutoCompleteVersion, FsAutoCompleteFolder, progress, cancellationToken).ConfigureAwait(false);
            await InstallAsync("fantomas", FantomasVersion, FantomasFolder, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report("The F# language service is ready.");
        }
        finally
        {
            _install.Release();
        }
    }

    private async Task InstallAsync(string tool, string version, string folder, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(folder, Marker)))
        {
            return;
        }

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true); // an install that didn't finish
        }

        progress?.Report($"Installing {tool} {version}…");
        // From the cache folder, so a solution's global.json or tool manifest doesn't apply.
        Directory.CreateDirectory(Folder);
        var (exitCode, output) = await _run(dotnet, ["tool", "install", tool, "--version", version, "--tool-path", folder], Folder, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"dotnet tool install {tool} {version} failed (exit code {exitCode}): {output.Trim()}");
        }

        await File.WriteAllTextAsync(Path.Combine(folder, Marker), version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// How FsAutoComplete starts, or null while it isn't installed: with <c>dotnet</c>, Fantomas's tool path at the end
    /// of <c>PATH</c> (a local or global Fantomas wins), and <c>DOTNET_ROOT</c> so the Fantomas command finds the runtime.
    /// </summary>
    public ProcessLaunchInfo? Launch()
    {
        if (!IsInstalled || FsAutoCompleteDll is not { } dll)
        {
            return null;
        }

        var environment = new Dictionary<string, string>
        {
            ["PATH"] = (Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } path ? path + Path.PathSeparator : string.Empty) + FantomasFolder,
        };
        if (Path.IsPathRooted(dotnet) && Path.GetDirectoryName(dotnet) is { } root)
        {
            environment["DOTNET_ROOT"] = root;
        }

        return new ProcessLaunchInfo(dotnet, [dll]) { Environment = environment };
    }
}
