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
                throw new ClrException($"{Path.GetFileName(program)}'s .NET runtime didn't start in {RuntimeStartTimeout.TotalSeconds:0} seconds: is it a .NET program? {StartState(pid, id => LoadedRuntime(shim, id))}");
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

    public IClrProcess Attach(int processId)
    {
        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            throw new ClrException($"There's no process {processId.ToString(CultureInfo.InvariantCulture)}.");
        }

        var shim = DbgShimLibrary.Shim;
        var runtime = LoadedRuntime(shim, processId) ?? throw new ClrException($"Process {processId.ToString(CultureInfo.InvariantCulture)} ({process.ProcessName}) isn't running .NET.");
        CorDebug corDebug;
        try
        {
            corDebug = shim.CreateDebuggingInterfaceFromVersion(shim.CreateVersionStringFromModule(processId, runtime));
            corDebug.Initialize();
        }
        catch (DebugException ex)
        {
            throw new ClrException($"The debugger couldn't load the debugging API of {process.ProcessName}'s runtime: {ex.Message}", ex);
        }

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
