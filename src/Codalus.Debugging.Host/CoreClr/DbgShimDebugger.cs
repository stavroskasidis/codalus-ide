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

    public IClrProcess Launch(NetFxDebugArguments arguments, Action<string, string> output)
    {
        var shim = DbgShimLibrary.Shim;
        var program = arguments.Program!;
        var launched = LaunchedProgram.Start(arguments, output);
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
            if (!attached.Task.Wait(RuntimeStartTimeout))
            {
                throw new ClrException($"{Path.GetFileName(program)}'s .NET runtime didn't start in {RuntimeStartTimeout.TotalSeconds:0} seconds: is it a .NET program?");
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

    /// <summary>The runtime's version, from its folder (<c>shared/Microsoft.NETCore.App/10.0.13/</c>).</summary>
    private static string RuntimeVersion(DbgShim shim, int processId) =>
        LoadedRuntime(shim, processId) is { } path ? VersionOf(path) : "CoreCLR";

    private static string VersionOf(string runtimePath) =>
        Path.GetFileName(Path.GetDirectoryName(runtimePath)) is { Length: > 0 } folder && char.IsAsciiDigit(folder[0]) ? folder : "CoreCLR";
}
