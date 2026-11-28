using System.Globalization;
using System.Runtime.InteropServices;
using ClrDebug;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// The debugging API of an Android app's CoreCLR (ADR 0116), for the debugger running on the device or in the emulator
/// as the app's own user (<c>run-as</c>). There is no dbgshim for Android, so this does its two jobs: the runtime is
/// the <c>libcoreclr.so</c> the process maps, and its ICorDebug comes from the <c>libmscordbi.so</c> next to it (a Debug
/// build's package has it, with <c>libmscordaccore.so</c>), through its own export (the one Microsoft's guide for
/// debuggers on mobile platforms names, there from inside the app). The runtime made the debugger's pipes in the app's temporary folder, its
/// <c>cache</c>, which the debugger is started with as its <c>TMPDIR</c>.
/// </summary>
internal static unsafe class AndroidRuntime
{
    private const string DebugLibrary = "libmscordbi.so";
    private const string DataAccessLibrary = "libmscordaccore.so";
    private const uint ProcessAttach = 1; // DLL_PROCESS_ATTACH

    /// <summary>
    /// Attaches to an app's process. .NET's own <c>Process</c> doesn't find processes on Android, so the process is
    /// looked at through <c>/proc</c>: the debugger runs as the app's user, which may read it.
    /// </summary>
    public static CorDebugTarget Attach(int processId, bool justMyCode, string? name = null)
    {
        var id = processId.ToString(CultureInfo.InvariantCulture);
        if (!Directory.Exists("/proc/" + id))
        {
            throw new ClrException(name is null ? $"There's no process {id}." : $"{name} ended before the debugger could attach to it.");
        }

        name ??= CommandName(processId) ?? id;
        if (RuntimeLocator.Find(processId) is not [var runtime, ..])
        {
            throw new ClrException($"Process {id} ({name}) isn't running .NET's CoreCLR, or isn't this user's to read (the debugger runs as the app it debugs).");
        }

        CorDebug corDebug;
        try
        {
            corDebug = CreateCorDebug(processId, runtime);
        }
        catch (DebugException ex)
        {
            throw new ClrException($"The debugger couldn't load the debugging API of {name}'s runtime: {ex.Message}", ex);
        }

        var target = new CorDebugTarget(corDebug, "CoreCLR", justMyCode);
        try
        {
            target.Attach(processId, name);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>The process's name: its command line's first word (an app's is its package), else its <c>comm</c>.</summary>
    private static string? CommandName(int processId)
    {
        try
        {
            var id = processId.ToString(CultureInfo.InvariantCulture);
            var commandLine = File.ReadAllText($"/proc/{id}/cmdline");
            var end = commandLine.IndexOf('\0');
            var first = end < 0 ? commandLine : commandLine[..end];
            return first.Length > 0 ? first : File.ReadAllText($"/proc/{id}/comm").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The ICorDebug of the runtime at <paramref name="runtime"/> in <paramref name="processId"/>, initialized.</summary>
    public static CorDebug CreateCorDebug(int processId, string runtime)
    {
        var address = BaseAddress(File.ReadLines($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/maps"), runtime)
            ?? throw new ClrException($"Process {processId.ToString(CultureInfo.InvariantCulture)} no longer maps {runtime}.");
        var library = Path.Combine(Path.GetDirectoryName(runtime)!, DebugLibrary);
        if (!File.Exists(library))
        {
            throw new ClrException($"The app has no debugging library ({DebugLibrary}) next to its runtime: only a Debug build's package has it.");
        }

        // The debugging library needs the data access library by its bare name, and has no search path of its own: once
        // that one is loaded from the same folder, the loader takes it for the name.
        nint export;
        try
        {
            var dataAccess = NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(runtime)!, DataAccessLibrary));
            var handle = NativeLibrary.Load(library);
            export = NativeLibrary.GetExport(handle, "CoreCLRCreateCordbObjectEx");
            AllowProcessChecks(library);
            AllowProcessChecks(Path.Combine(Path.GetDirectoryName(runtime)!, DataAccessLibrary));
            GC.KeepAlive(dataAccess);

            // dbgshim loads the library through its platform layer, which calls the library's DllMain: that is where
            // the library sets up its own. Loaded directly, nothing calls it, and the first call into it waits forever.
            var main = (delegate* unmanaged<nint, uint, nint, int>)NativeLibrary.GetExport(handle, "DllMain");
            if (main(handle, ProcessAttach, 0) == 0)
            {
                throw new ClrException($"The app's debugging library ({library}) didn't start.");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new ClrException($"The app's debugging library ({library}) couldn't be loaded: {ex.Message}", ex);
        }

        nint unknown;
        var create = (delegate* unmanaged<int, uint, char*, nint, nint*, int>)export;
        var result = (HRESULT)create((int)CorDebugInterfaceVersion.CorDebugVersion_4_0, (uint)processId, null, (nint)address, &unknown);
        if (result != HRESULT.S_OK)
        {
            throw new DebugException(result);
        }

        var corDebug = new CorDebug(ClrDebug.Extensions.GetObjectForIUnknown<ICorDebug>(unknown));
        corDebug.Initialize();
        return corDebug;
    }

    /// <summary>
    /// Android lets the app's user (<c>run-as</c>) read the app's memory, stop it and end it, but not ask whether it
    /// is there: <c>kill(pid, 0)</c> is refused (<c>EACCES</c>; the policy gives <c>runas_app</c> the signals a debugger
    /// sends and not the null one). The debugging libraries ask that way, and take the refusal for a failure. So their
    /// import of <c>kill</c> goes through <see cref="Kill"/>, which answers the null signal from <c>/proc</c>.
    /// </summary>
    private static void AllowProcessChecks(string library)
    {
        if (BaseAddress(File.ReadLines("/proc/self/maps"), library) is { } address)
        {
            s_kill = (delegate* unmanaged<int, int, int>)NativeLibrary.GetExport(NativeLibrary.Load("libc.so"), "kill");
            ElfImports.Redirect((nint)address, "kill", (nint)(delegate* unmanaged<int, int, int>)&Kill);
        }
    }

    private static delegate* unmanaged<int, int, int> s_kill;

    private const int AccessDenied = 13; // EACCES

    [UnmanagedCallersOnly]
    private static int Kill(int processId, int signal)
    {
        var result = s_kill(processId, signal);
        var error = Marshal.GetLastSystemError();
        if (result != 0 && signal == 0 && error == AccessDenied && Directory.Exists("/proc/" + processId.ToString(CultureInfo.InvariantCulture)))
        {
            return 0;
        }

        Marshal.SetLastSystemError(error);
        return result;
    }

    /// <summary>
    /// Where <paramref name="file"/> starts in a process, from its <c>/proc/&lt;pid&gt;/maps</c>: the lowest address of
    /// its mappings (<c>start-end perms offset dev inode path</c>).
    /// </summary>
    internal static ulong? BaseAddress(IEnumerable<string> maps, string file)
    {
        ulong? lowest = null;
        foreach (var line in maps)
        {
            var path = line.IndexOf('/');
            var dash = line.IndexOf('-');
            if (path < 0 || dash <= 0 || !line.AsSpan(path).TrimEnd().SequenceEqual(file))
            {
                continue;
            }

            if (ulong.TryParse(line.AsSpan(0, dash), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var start) && (lowest is null || start < lowest))
            {
                lowest = start;
            }
        }

        return lowest;
    }
}
