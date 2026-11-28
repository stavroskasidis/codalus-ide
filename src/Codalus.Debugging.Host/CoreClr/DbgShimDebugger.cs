using System.Globalization;
using System.Runtime.InteropServices;
using ClrDebug;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// Starts and attaches to CoreCLR programs (.NET Core, .NET 5 and later) through dbgshim (M54, ADR 0086), for ADR
/// 0076's session over <see cref="CorDebugTarget"/>. A launch goes through the launcher (<see cref="Launcher"/>): it
/// reports the program's id while the program is suspended, this registers for its runtime's startup and lets it go,
/// and the startup callback hands over the program's own runtime's ICorDebug, which attaches. An attach finds the
/// loaded runtime (<c>EnumerateCLRs</c>) and its ICorDebug by its version; breakpoints bind on the attach's module
/// loads, which arrive after <c>DebugActiveProcess</c> returns.
/// <para>
/// Where dbgshim's own search for the runtime can't be used (<see cref="RuntimeLocator.StartsHeld"/>: as x64 under
/// Rosetta 2, where it crashed), a launch is a held start instead (<see cref="LaunchHeld"/>): the program waits in the
/// startup hook, as a program in the terminal does for netcoredbg, and the debugger attaches to it by its runtime's
/// path.
/// </para>
/// </summary>
internal sealed class DbgShimDebugger : IClrDebugger
{
    /// <summary>How long a launched program's runtime may take to start.</summary>
    public static readonly TimeSpan RuntimeStartTimeout = TimeSpan.FromSeconds(30);

    public string Runtime => ".NET";

    /// <summary>
    /// The client's <c>runInTerminal</c> (when it has one): starts a command in its integrated terminal or a console
    /// window and returns the process id it started, for a launch whose <see cref="NetFxDebugArguments.Console"/> asks.
    /// </summary>
    public Func<RunInTerminalRequestArguments, int?>? RunInTerminal { get; set; }

    public IClrProcess Launch(NetFxDebugArguments arguments, Action<string, string> output)
    {
        if (RuntimeLocator.StartsHeld)
        {
            return LaunchHeld(arguments, output);
        }

        var shim = DbgShimLibrary.Shim;
        var program = arguments.Program!;
        var launched = LaunchedProgram.Start(arguments, output, RunInTerminal);
        var pid = launched.ProcessId;
        var attached = new TaskCompletionSource<CorDebugTarget>(TaskCreationOptions.RunContinuationsAsynchronously);

        // dbgshim calls back on a thread of its own once the runtime has started, with that runtime's ICorDebug.
        var startup = new PSTARTUP_CALLBACK((corDebugUnknown, _, result) =>
        {
            try
            {
                if (result != HRESULT.S_OK)
                {
                    attached.TrySetException(new ClrException($"{Path.GetFileName(program)}'s .NET runtime didn't start under the debugger ({result})."));
                    return;
                }

                var corDebug = new CorDebug(ClrDebug.Extensions.GetObjectForIUnknown<ICorDebug>(corDebugUnknown));
                corDebug.Initialize();
                var target = new CorDebugTarget(corDebug, RuntimeVersion(shim, pid), arguments.JustMyCode) { Launched = launched };
                target.Attach(System.Diagnostics.Process.GetProcessById(pid));
                attached.TrySetResult(target);
            }
            catch (Exception ex)
            {
                attached.TrySetException(ex);
            }
        });

        var registration = IntPtr.Zero;
        try
        {
            registration = shim.RegisterForRuntimeStartup(pid, startup, IntPtr.Zero);
            launched.Resume();

            // A program that ends without starting a .NET runtime (a script that failed, a native program) says so at
            // once, instead of after the whole wait. One that ends right after its runtime started was attached to first.
            if (Task.WaitAny([attached.Task, launched.Exited], RuntimeStartTimeout) == 1 && !attached.Task.Wait(TimeSpan.FromMilliseconds(500)))
            {
                var code = launched.Exited.Result is { } exit ? $" (exit code {exit.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
                throw new ClrException($"{Path.GetFileName(program)} ended{code} before a .NET runtime started in it: is it a .NET program?");
            }

            if (!attached.Task.Wait(attached.Task.IsCompleted ? TimeSpan.Zero : RuntimeStartTimeout))
            {
                throw new ClrStartTimeoutException($"{Path.GetFileName(program)}'s .NET runtime didn't start in {RuntimeStartTimeout.TotalSeconds:0} seconds: is it a .NET program? {StartState(pid, id => LoadedRuntime(shim, id))}");
            }

            return attached.Task.Result;
        }
        catch (Exception ex)
        {
            // The launcher ends the program when its pipe closes.
            launched.Dispose();
            throw ex is AggregateException { InnerException: { } inner } ? inner : ex is DebugException debug ? new ClrException($"{Path.GetFileName(program)} couldn't be debugged: {debug.Message}", debug) : ex;
        }
        finally
        {
            if (registration != IntPtr.Zero)
            {
                shim.TryUnregisterForRuntimeStartup(registration);
            }

            GC.KeepAlive(startup);
        }
    }

    /// <summary>
    /// How long a held start waits for the startup hook before it attaches to a program whose runtime is loaded. That
    /// isn't only a runtime that runs no hooks: one that is slow to get to them looks the same (see <see cref="LaunchHeld"/>).
    /// </summary>
    internal static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a held start waits for the startup hook in a runtime that runs startup hooks (.NET Core 3.0 and later)
    /// before it attaches to the program as it runs: only a program that turned startup hooks off gets there. Below the
    /// runtime's own start limit (<see cref="RuntimeStartTimeout"/>), so it isn't reported as not starting.
    /// </summary>
    internal static readonly TimeSpan HookTimeoutWithHooks = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How long to wait for the startup hook once the runtime at <paramref name="runtimePath"/> has loaded. A runtime
    /// from .NET Core 3.0 runs the hook, however long it takes to: an x64 .NET Core 3.1 program under Rosetta 2 on a
    /// busy runner ran its hook after the 5 s that were enough for .NET Core 2, was attached to as it ran, and passed
    /// its breakpoint first (macOS CI, run 43344309044, DebuggerBundleTests). An unknown version (a self-contained
    /// program's runtime, without the framework's folder) or .NET Core 2 waits <see cref="HookTimeout"/>.
    /// </summary>
    internal static TimeSpan HookWait(string? runtimePath) =>
        runtimePath is not null && Version.TryParse(VersionOf(runtimePath).Split('-')[0], out var version) && version.Major >= 3
            ? HookTimeoutWithHooks
            : HookTimeout;

    /// <summary>
    /// Milliseconds a held start waits between the program's start and the attach: for the tests, which end the
    /// program in between (a program that isn't held can end there by itself).
    /// </summary>
    public const string AttachDelayVariable = "CODALUS_DEBUG_HELD_ATTACH_DELAY_MS";

    /// <summary>How long a failed attach waits for the word that the program has ended (the launcher's, right after its exit).</summary>
    internal static readonly TimeSpan EndedWait = TimeSpan.FromSeconds(2);

    /// <summary>What a held start says of a program that ended before it was attached to.</summary>
    internal static string EndedBeforeAttach(string program, int? exitCode)
    {
        var code = exitCode is { } exit ? $" (exit code {exit.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        return $"{Path.GetFileName(program)} ended{code} before the debugger could attach to it: the startup hook that holds a program at its start didn't run in it. .NET Core 2.0 and 2.1 run no startup hooks; a program that isn't .NET's doesn't either.";
    }

    /// <summary>The startup hook's file, next to this host (and in the debugger's bundle).</summary>
    private const string HookFile = "Codalus.Debugging.StartupHook.dll";

    /// <summary>
    /// A launch without dbgshim's wait for the runtime (see the class): the program starts with the startup hook, which
    /// reports its process id before <c>Main</c> and waits; the debugger attaches by the runtime's path, and lets the
    /// hook go once the program is stopped for it. The breakpoints set meanwhile bind as the program's modules load. A
    /// program whose runtime is loaded and hasn't reached the hook after <see cref="HookWait"/> is attached to as it
    /// runs, and is told so: a runtime without startup hooks (.NET Core 2), one whose program turned them off, or one
    /// that is slower to get there than the wait. The last still comes to the hook, so the hook is let go in every
    /// case: an x64 program under Rosetta 2 on a busy machine took longer than the wait (5 s then, for every runtime),
    /// and then sat in the hook for the two minutes it gives a debugger, with the debugger attached all along (macOS
    /// CI, run 43309447208).
    /// </summary>
    private CorDebugTarget LaunchHeld(NetFxDebugArguments arguments, Action<string, string> output)
    {
        var shim = DbgShimLibrary.Shim;
        var program = arguments.Program!;
        var hook = Path.Combine(AppContext.BaseDirectory, HookFile);
        if (!File.Exists(hook))
        {
            throw new ClrException($"The debugger's startup hook ({HookFile}) isn't next to it, so it can't start {Path.GetFileName(program)}.");
        }

        var wait = Directory.CreateTempSubdirectory("codalus-debug-wait-").FullName;
        var environment = new Dictionary<string, string>(arguments.Env ?? [], StringComparer.Ordinal) { ["CODALUS_DEBUG_WAIT"] = wait };
        var hooks = (environment.GetValueOrDefault("DOTNET_STARTUP_HOOKS") ?? Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (!hooks.Any(h => Path.GetFileName(h) == HookFile))
        {
            environment["DOTNET_STARTUP_HOOKS"] = string.Join(Path.PathSeparator, [hook, .. hooks]);
        }

        // An attached program takes edits (Hot Reload, Edit and Continue) only if its runtime started knowing it would:
        // a launch says so to each module as it loads, which an attach isn't allowed to (CORDBG_E_ENC_MODULE_NOT_ENC_ENABLED).
        environment.TryAdd("DOTNET_MODIFIABLE_ASSEMBLIES", "debug");

        var launched = LaunchedProgram.Start(arguments with { Env = environment }, output, RunInTerminal);
        var pid = launched.ProcessId;
        CorDebugTarget? target = null;
        try
        {
            launched.Resume();

            // The hook's file, the program's end, or a runtime that runs without having run the hook.
            var started = System.Diagnostics.Stopwatch.StartNew();
            var held = false;
            while (true)
            {
                if (File.Exists(Path.Combine(wait, "pid")))
                {
                    held = true;
                    break;
                }

                if (launched.Exited.IsCompleted)
                {
                    // Not "is it a .NET program?": a program of .NET Core 2.0 or 2.1 ends here too, having run without
                    // the hook (the old-.NET matrix through this start, 2026-11-12).
                    throw new ClrException(EndedBeforeAttach(program, launched.Exited.Result));
                }

                if (started.Elapsed >= HookTimeout && RuntimeLocator.Find(pid, program) is [var loaded, ..] && started.Elapsed >= HookWait(loaded))
                {
                    output("console", $"The program hasn't reached the debugger's startup hook after {started.Elapsed.TotalSeconds:0} seconds (a slow start, a program that turned startup hooks off, or a .NET runtime that runs none: .NET Core 2), so the debugger attaches to it as it runs: breakpoints in its first lines may have been passed.\n");
                    break;
                }

                if (started.Elapsed >= RuntimeStartTimeout)
                {
                    throw new ClrStartTimeoutException($"{Path.GetFileName(program)}'s .NET runtime didn't start in {RuntimeStartTimeout.TotalSeconds:0} seconds: is it a .NET program? {StartState(pid, id => RuntimeLocator.Find(id, program) is [var loaded, ..] ? loaded : null)}");
                }

                Thread.Sleep(20);
            }

            if (int.TryParse(Environment.GetEnvironmentVariable(AttachDelayVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var delay) && delay > 0)
            {
                Thread.Sleep(delay); // the tests' way to end the program between its start and the attach
            }

            var (corDebug, runtime) = CorDebugByPath(shim, pid, Path.GetFileName(program), program);
            target = new CorDebugTarget(corDebug, VersionOf(runtime), arguments.JustMyCode) { Launched = launched };
            target.Attach(System.Diagnostics.Process.GetProcessById(pid));
            // Once the program is stopped for the debugger (its first event is here, and the session answers events
            // only after configurationDone), the hook may go: it runs on when the session continues the program. A
            // program that hadn't reached the hook gets the same word, for when it does: it finds it there and goes
            // on at once, before Main, with the debugger attached.
            target.Events.WaitToReadAsync().AsTask().Wait(RuntimeStartTimeout);
            File.WriteAllText(Path.Combine(wait, "go"), string.Empty);
            if (!held)
            {
                // The hook removes the folder when it takes the word. A program that never runs it leaves that to us.
                _ = launched.Exited.ContinueWith(_ => RemoveWaitFolder(wait), TaskScheduler.Default);
            }

            return target;
        }
        catch (Exception ex)
        {
            target?.Dispose();

            // A program that wasn't held can end while the debugger attaches: its runtime was there a moment ago, and
            // dbgshim then answers for a process that is gone (ERROR_INVALID_PARAMETER, reported as "couldn't load the
            // debugging API"; an x64 .NET Core 2.1 program under Rosetta 2 in the Mac's full suite, 2026-11-24,
            // docs/ci-flakes.md). That is the program's end before the attach, and is said so.
            if (ex is not OperationCanceledException && launched.Exited.Wait(EndedWait))
            {
                var ended = launched.Exited.Result;
                launched.Dispose();
                throw new ClrException(EndedBeforeAttach(program, ended), ex);
            }

            if (target is null)
            {
                // The launcher ends the program when its pipe closes.
                launched.Dispose();
            }

            throw ex is AggregateException { InnerException: { } inner } ? inner : ex is DebugException debug ? new ClrException($"{Path.GetFileName(program)} couldn't be debugged: {debug.Message}", debug) : ex;
        }
        finally
        {
            if (target is null)
            {
                RemoveWaitFolder(wait);
            }
        }
    }

    private static void RemoveWaitFolder(string wait)
    {
        try
        {
            Directory.Delete(wait, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public IClrProcess Attach(int processId)
    {
        if (OperatingSystem.IsAndroid())
        {
            return AndroidRuntime.Attach(processId, justMyCode: true);
        }

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            throw new ClrException($"There's no process {processId.ToString(CultureInfo.InvariantCulture)}.");
        }

        var (corDebug, runtime) = CorDebugOf(process);
        var target = new CorDebugTarget(corDebug, VersionOf(runtime), justMyCode: true);
        try
        {
            target.Attach(process);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>The ICorDebug of the runtime a running process loaded, and that runtime's path.</summary>
    private static (CorDebug CorDebug, string Runtime) CorDebugOf(System.Diagnostics.Process process)
    {
        var processId = process.Id;
        var shim = DbgShimLibrary.Shim;
        if (RuntimeLocator.StartsHeld)
        {
            return CorDebugByPath(shim, processId, process.ProcessName, ProgramOf(process));
        }

        var runtime = LoadedRuntime(shim, processId) ?? throw new ClrException($"Process {processId.ToString(CultureInfo.InvariantCulture)} ({process.ProcessName}) isn't running .NET.");
        try
        {
            return (CreateCorDebug(shim, processId, runtime), runtime);
        }
        catch (DebugException ex)
        {
            throw new ClrException($"The debugger couldn't load the debugging API of {process.ProcessName}'s runtime: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The ICorDebug of the runtime a process loaded, found by that runtime's path (<see cref="RuntimeLocator"/>), and
    /// the path: dbgshim reads only that library. Of several candidates, the one the process maps answers.
    /// </summary>
    private static (CorDebug CorDebug, string Runtime) CorDebugByPath(DbgShim shim, int processId, string name, string? program)
    {
        var candidates = RuntimeLocator.Find(processId, program);
        if (candidates.Count == 0)
        {
            throw new ClrException($"Process {processId.ToString(CultureInfo.InvariantCulture)} ({name}) isn't running .NET.");
        }

        DebugException? last = null;
        foreach (var runtime in candidates)
        {
            try
            {
                return (CreateCorDebug(shim, processId, runtime), runtime);
            }
            catch (DebugException ex)
            {
                last = ex; // not the one it maps, or its debugging API didn't load
            }
        }

        throw new ClrException($"The debugger couldn't load the debugging API of {name}'s runtime ({string.Join(", ", candidates)}): {last!.Message}", last);
    }

    /// <summary>
    /// The ICorDebug of the runtime at <paramref name="runtime"/> in a running process. An app in an iOS simulator or a
    /// Mac Catalyst app (ADR 0115) gets its debugging libraries from copies macOS loads; and a process whose temporary
    /// folder isn't this one's (such an app's container, a sandbox's) is looked for there, where its runtime made the
    /// debugger's pipes.
    /// </summary>
    private static CorDebug CreateCorDebug(DbgShim shim, int processId, string runtime)
    {
        var version = shim.CreateVersionStringFromModule(processId, runtime);
        var libraries = AppleRuntime.IsForeign(runtime) ? AppleRuntime.DebugLibraries(runtime) : null;
        if (AppleRuntime.TempFolder(processId) is { Length: > 0 } temp && !SameFolder(temp, Path.GetTempPath()))
        {
            AppleRuntime.UseTempFolder(temp);
        }

        var corDebug = libraries is null
            ? shim.CreateDebuggingInterfaceFromVersion(version)
            : shim.CreateDebuggingInterfaceFromVersion3(CorDebugInterfaceVersion.CorDebugVersion_4_0, version, null!, new DebugLibraryProvider(libraries));
        corDebug.Initialize();
        return corDebug;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>How long a program someone else starts may take to report (<see cref="AttachWaiting"/>): a build and a deploy.</summary>
    internal static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A launch of a program someone else starts (ADR 0115: a mobile app that the platform's <c>Run</c> target builds,
    /// installs and starts in a simulator or on this Mac), with the startup hook and
    /// <see cref="NetFxDebugArguments.WaitFolder"/> as its <c>CODALUS_DEBUG_WAIT</c>: the hook reports the process id
    /// there before <c>Main</c> and waits; the debugger attaches by the runtime's path and lets the hook go once the
    /// program is stopped for it, as <see cref="LaunchHeld"/> does. The breakpoints set meanwhile bind as the program's
    /// modules load. A folder that is removed meanwhile is the start given up.
    /// </summary>
    public IClrProcess AttachWaiting(NetFxDebugArguments arguments, Action<string, string> output)
    {
        var wait = arguments.WaitFolder!;
        var timeout = arguments.WaitSeconds is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : WaitTimeout;
        var name = arguments.Program is { Length: > 0 } program ? Path.GetFileName(program) : "The app";
        output("console", "Waiting for the app to start…\n");
        var started = System.Diagnostics.Stopwatch.StartNew();
        int pid;
        while (!TryReadProcessId(Path.Combine(wait, "pid"), out pid))
        {
            if (!Directory.Exists(wait))
            {
                throw new ClrException($"{name} wasn't started, so there is nothing to debug.");
            }

            if (started.Elapsed >= timeout)
            {
                throw new ClrException($"{name} didn't reach the debugger's startup hook in {timeout.TotalSeconds:0} seconds: it didn't start, or it runs no startup hooks (a trimmed or Release build turns them off).");
            }

            Thread.Sleep(50);
        }

        if (OperatingSystem.IsAndroid())
        {
            // On the device (ADR 0116): the app's own debugging library, and no OS process object to hold.
            var attached = AndroidRuntime.Attach(pid, arguments.JustMyCode, name);
            try
            {
                attached.Events.WaitToReadAsync().AsTask().Wait(RuntimeStartTimeout);
                File.WriteAllText(Path.Combine(wait, "go"), string.Empty);
                return attached;
            }
            catch
            {
                attached.Dispose();
                throw;
            }
        }

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            throw new ClrException($"{name} ended before the debugger could attach to it.");
        }

        CorDebugTarget? target = null;
        try
        {
            var (corDebug, runtime) = CorDebugOf(process);
            target = new CorDebugTarget(corDebug, VersionOf(runtime), arguments.JustMyCode);
            target.Attach(process);
            // As in LaunchHeld: the hook may go once the program is stopped for the debugger.
            target.Events.WaitToReadAsync().AsTask().Wait(RuntimeStartTimeout);
            File.WriteAllText(Path.Combine(wait, "go"), string.Empty);
            return target;
        }
        catch (Exception ex)
        {
            target?.Dispose();
            if (Ended(process))
            {
                // dbgshim answers for a process that is gone with an error about its debugging API (see LaunchHeld).
                throw new ClrException($"{name} ended before the debugger could attach to it.", ex);
            }

            throw ex is AggregateException { InnerException: { } inner } ? inner : ex is DebugException debug ? new ClrException($"{name} couldn't be debugged: {debug.Message}", debug) : ex;
        }
    }

    private static bool Ended(System.Diagnostics.Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The process id the startup hook wrote (whole: it writes the file aside and renames it).</summary>
    private static bool TryReadProcessId(string file, out int processId)
    {
        processId = 0;
        try
        {
            return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out processId) && processId > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A process's executable, when it can be read.</summary>
    private static string? ProgramOf(System.Diagnostics.Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The path of the CoreCLR library a process loaded; null when it hasn't loaded one.</summary>
    private static string? LoadedRuntime(DbgShim shim, int processId)
    {
        if (shim.TryEnumerateCLRs(processId, out var clrs) != HRESULT.S_OK)
        {
            return null;
        }

        try
        {
            return clrs.Items is [var first, ..] ? first.Path : null;
        }
        finally
        {
            shim.TryCloseCLREnumeration(clrs);
        }
    }

    /// <summary>
    /// What a launched program was doing when its runtime's startup wasn't reported in time: ended, or still running (how
    /// long, on how much processor time) with or without a .NET runtime loaded. On Windows CI the first two launches of a
    /// shard waited out the 30 seconds while every later one started in about a second (run 41442485548,
    /// docs/ci-flakes.md), and the message didn't say whether the program was stuck before its runtime or the startup
    /// callback never came.
    /// </summary>
    /// <param name="loadedRuntime">The path of the CoreCLR library a process loaded, or null.</param>
    internal static string StartState(int processId, Func<int, string?> loadedRuntime)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return $"The program ended (exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}).";
            }

            var age = (DateTime.Now - process.StartTime).TotalSeconds;
            var processor = process.TotalProcessorTime.TotalSeconds;
            var threads = process.Threads.Count;
            var runtime = loadedRuntime(processId) is { } path ? $"has loaded {path}" : "hasn't loaded a .NET runtime";
            return string.Create(CultureInfo.InvariantCulture, $"The program still runs ({age:0.0} s old, {processor:0.0} s of processor time, {threads} threads) and {runtime}.");
        }
        catch (ArgumentException)
        {
            return "The program ended.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or DebugException)
        {
            return $"The program's state couldn't be read ({ex.Message}).";
        }
    }

    /// <summary>The runtime's version, from its folder (<c>shared/Microsoft.NETCore.App/10.0.13/</c>).</summary>
    private static string RuntimeVersion(DbgShim shim, int processId) =>
        LoadedRuntime(shim, processId) is { } path ? VersionOf(path) : "CoreCLR";

    private static string VersionOf(string runtimePath) =>
        Path.GetFileName(Path.GetDirectoryName(runtimePath)) is { Length: > 0 } folder && char.IsAsciiDigit(folder[0]) ? folder : "CoreCLR";
}
