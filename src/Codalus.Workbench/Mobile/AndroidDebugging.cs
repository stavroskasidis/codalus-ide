using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using Codalus.Debugging;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Debugging.Remote;

namespace Codalus.Workbench.Mobile;

/// <summary>Runs adb with <c>arguments</c> to its end within a time limit: its exit code, and its output and errors together.</summary>
public delegate Task<(int ExitCode, string Output)> AdbRunner(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken cancellationToken);

/// <summary>
/// The Codalus debugger on an Android device or emulator (ADR 0116), for apps that run on CoreCLR. An app there is a
/// process of the device's own Linux system, so the debugger runs there too: its bundle for the device's ABI
/// (<c>android-arm64</c>, <c>android-x64</c>) is pushed to <c>/data/local/tmp</c> once per build of it, copied into the
/// app's own storage (<c>code_cache</c>, which Android empties when the app is installed again), and started there as
/// the app's user with <c>run-as</c>, which a Debug build (a debuggable app) allows. Its DAP is adb's standard streams.
/// The app starts held by the debugger's startup hook, which the build puts into it (<c>dotnet run -e</c>), and reports in
/// a folder of its cache that the hook makes.
/// </summary>
public sealed class AndroidDebugging(AdbRunner adb, string cacheFolder)
{
    /// <summary>A short command on the device.</summary>
    private static readonly TimeSpan s_short = TimeSpan.FromSeconds(30);

    /// <summary>Pushing the bundle (about 45 MB) over USB.</summary>
    private static readonly TimeSpan s_push = TimeSpan.FromMinutes(10);

    /// <summary>The bundle's platform for a device's primary ABI (<c>ro.product.cpu.abi</c>); null when there is none for it.</summary>
    public static string? Platform(string abi) => abi.Trim() switch
    {
        "arm64-v8a" => "android-arm64",
        "x86_64" => "android-x64",
        _ => null,
    };

    /// <summary>The app's own folder, where <c>run-as</c> starts.</summary>
    public static string AppFolder(string package) => "/data/data/" + package;

    /// <summary>The app's temporary folder: its runtime makes the debugger's pipes there.</summary>
    public static string TempFolder(string package) => AppFolder(package) + "/cache";

    /// <summary>A new folder the app's startup hook reports in, one per run (a run that ended leaves nothing for the next to read).</summary>
    public static string WaitFolder(string package, string run) => $"{TempFolder(package)}/codalus-debug-{run}";

    /// <summary>The debugger in the app's storage, one folder per build of it.</summary>
    public static string DebuggerFolder(string package, string bundleId) => $"{AppFolder(package)}/code_cache/codalus-debugger-{bundleId}";

    /// <summary>The debugger on the device, from where it is copied into each app.</summary>
    public static string StagingFolder(string bundleId) => "/data/local/tmp/codalus-debugger-" + bundleId;

    /// <summary>
    /// The arguments of <c>dotnet run</c> that start the app held by the debugger's startup hook (<paramref name="hook"/>,
    /// a file here: the Android SDK puts it into the app and names it there), reporting in <paramref name="waitFolder"/>,
    /// which the hook makes, for up to 15 minutes (a session's start: copying the debugger in).
    /// </summary>
    public static IReadOnlyList<string> HeldStartArguments(string hook, string waitFolder) =>
    [
        "-e", HeldLaunch.StartupHooksVariable + "=" + hook,
        "-e", HeldLaunch.WaitVariable + "=" + waitFolder,
        "-e", HeldLaunch.CreateVariable + "=1",
        "-e", HeldLaunch.TimeoutVariable + "=900",
    ];

    /// <summary>
    /// The debugger's start through adb: <c>codalus-debughost --clr-dap</c> as the app's user, with the app's temporary
    /// folder as its own (where the app's runtime made the debugger's pipes). <c>-T</c>: no terminal, so DAP's bytes
    /// pass as they are. With <paramref name="log"/> (<c>CODALUS_CLR_DEBUGGER_LOG</c> set here) it writes its log into
    /// that folder too (<c>adb shell run-as &lt;package&gt; cat cache/clr-dap-…</c>).
    /// </summary>
    public static DebugAdapterStartInfo Adapter(string adbPath, string serial, string package, string bundleId, bool log = false) =>
        new(adbPath, [
            "-s", serial, "shell", "-T", "run-as", package, "env", "TMPDIR=" + TempFolder(package),
            .. log ? [CoreClrAdapter.LogVariable + "=" + TempFolder(package)] : Array.Empty<string>(),
            DebuggerFolder(package, bundleId) + "/" + RemoteDebuggers.CodalusExecutable, CoreClrAdapter.AdapterArgument])
        {
            IsCodalus = true,
        };

    /// <summary>The device's bundle platform (<see cref="Platform"/>), or null when it has none.</summary>
    public async Task<string?> PlatformAsync(string serial, CancellationToken cancellationToken)
    {
        var (code, output) = await adb(["-s", serial, "shell", "getprop", "ro.product.cpu.abi"], s_short, cancellationToken).ConfigureAwait(false);
        return code == 0 ? Platform(output) : null;
    }

    /// <summary>
    /// The process id the app reported in <paramref name="waitFolder"/>, or null while it hasn't (its build and install
    /// are still going; until it is installed <c>run-as</c> doesn't know it).
    /// </summary>
    public async Task<int?> ReportedProcessAsync(string serial, string package, string waitFolder, CancellationToken cancellationToken)
    {
        var (code, output) = await adb(["-s", serial, "shell", "run-as", package, "cat", waitFolder + "/pid"], s_short, cancellationToken).ConfigureAwait(false);
        return code == 0 && int.TryParse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    /// <summary>The app's process on the device, or null while it isn't running.</summary>
    public async Task<int?> RunningProcessAsync(string serial, string package, CancellationToken cancellationToken)
    {
        var (code, output) = await adb(["-s", serial, "shell", "pidof", package], s_short, cancellationToken).ConfigureAwait(false);
        return code == 0 && int.TryParse(output.Trim().Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    /// <summary>Removes a run's wait folder (a session that didn't start leaves it; the hook removes it when it goes on).</summary>
    public Task RemoveWaitFolderAsync(string serial, string package, string waitFolder, CancellationToken cancellationToken) =>
        adb(["-s", serial, "shell", "run-as", package, "rm", "-rf", waitFolder], s_short, cancellationToken);

    /// <summary>
    /// Puts the debugger <paramref name="bundle"/> into the app's storage, unless that build of it is there: on the
    /// device first (once per build; <paramref name="copying"/> is called then, it takes a while), then into the app.
    /// Throws with adb's words when a step fails.
    /// </summary>
    public async Task EnsureDebuggerAsync(string serial, string package, CodalusDebuggerBundle bundle, Action? copying, CancellationToken cancellationToken)
    {
        var target = DebuggerFolder(package, bundle.Id);
        if (await adb(["-s", serial, "shell", "run-as", package, "ls", target + "/" + RemoteDebuggers.CodalusBundleId], s_short, cancellationToken).ConfigureAwait(false) is (0, _))
        {
            return;
        }

        var staging = StagingFolder(bundle.Id);
        if (await adb(["-s", serial, "shell", "ls", staging + "/" + RemoteDebuggers.CodalusBundleId], s_short, cancellationToken).ConfigureAwait(false) is not (0, _))
        {
            copying?.Invoke();
            var folder = bundle.IsArchive ? Unpack(bundle) : bundle.Path;
            await RemoveOtherBuildsAsync(serial, bundle.Id, cancellationToken).ConfigureAwait(false);
            Check(await adb(["-s", serial, "push", folder, staging], s_push, cancellationToken).ConfigureAwait(false), "copy the debugger to the device");
        }

        Check(await adb(["-s", serial, "shell", "run-as", package, "mkdir", "-p", target], s_short, cancellationToken).ConfigureAwait(false), "make the debugger's folder in the app");
        Check(await adb(["-s", serial, "shell", "run-as", package, "cp", "-r", staging + "/.", target + "/"], s_push, cancellationToken).ConfigureAwait(false), "copy the debugger into the app");
        Check(await adb(["-s", serial, "shell", "run-as", package, "chmod", "700", target + "/" + RemoteDebuggers.CodalusExecutable], s_short, cancellationToken).ConfigureAwait(false), "make the debugger executable");
    }

    /// <summary>The copies of other builds of the debugger in <c>/data/local/tmp</c>: each is about 45 MB.</summary>
    private async Task RemoveOtherBuildsAsync(string serial, string bundleId, CancellationToken cancellationToken)
    {
        var (code, output) = await adb(["-s", serial, "shell", "ls", "/data/local/tmp"], s_short, cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            return;
        }

        foreach (var name in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name.StartsWith("codalus-debugger-", StringComparison.Ordinal) && name != "codalus-debugger-" + bundleId && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
            {
                await adb(["-s", serial, "shell", "rm", "-rf", "/data/local/tmp/" + name], s_short, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A shipped bundle's archive, unpacked once per build into the cache (adb pushes folders).</summary>
    internal string Unpack(CodalusDebuggerBundle bundle)
    {
        var folder = Path.Combine(cacheFolder, "android-debugger", bundle.Id);
        var unpacked = Path.Combine(folder, "codalus-debugger");
        if (File.Exists(Path.Combine(unpacked, RemoteDebuggers.CodalusExecutable)))
        {
            return unpacked;
        }

        var partial = folder + ".tmp";
        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        Directory.CreateDirectory(partial);
        using (var archive = File.OpenRead(bundle.Path))
        using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
        {
            TarFile.ExtractToDirectory(gzip, partial, overwriteFiles: true);
        }

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.Move(partial, folder);
        return unpacked;
    }

    private static void Check((int ExitCode, string Output) result, string step)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"adb couldn't {step}: {result.Output.Trim()}");
        }
    }
}
