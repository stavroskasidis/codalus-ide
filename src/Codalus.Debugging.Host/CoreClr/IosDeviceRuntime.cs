using System.Runtime.InteropServices;
using ClrDebug;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// The debugging API of the app the debugger runs in, on an iPhone or iPad (ADR 0117). iOS starts no other process for
/// an app, so the Codalus debugger is a library in it (the in-app debugger, loaded as a profiler), and its ICorDebug is
/// for its own process: from the app's own <c>libmscordbi</c>, which a Debug build's bundle has, through the export
/// Microsoft's guide for debuggers on mobile platforms names. Each of the runtime's libraries is a framework of its own
/// there (<c>Frameworks/libcoreclr.framework/libcoreclr</c>), so the debugging library is given the data access
/// library's path: it looks for it next to itself by default, as <c>libmscordaccore.dylib</c>.
/// </summary>
internal static unsafe partial class IosDeviceRuntime
{
    private const string DebugLibrary = "libmscordbi";
    private const string DataAccessLibrary = "libmscordaccore";
    private const uint ProcessAttach = 1; // DLL_PROCESS_ATTACH

    /// <summary>Attaches to this process, the app <paramref name="name"/>.</summary>
    public static CorDebugTarget AttachToSelf(bool justMyCode, string? name)
    {
        var processId = Environment.ProcessId;
        name ??= "the app";
        var runtime = RuntimeLibrary() ?? throw new ClrException($"{name} isn't running .NET's CoreCLR: its runtime library wasn't found in the process.");
        CorDebug corDebug;
        try
        {
            corDebug = CreateCorDebug(processId, runtime.Path, runtime.Base);
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

    /// <summary>The runtime library this process loaded, and where: the image that holds <c>coreclr_initialize</c>.</summary>
    private static (string Path, nint Base)? RuntimeLibrary()
    {
        var symbol = dlsym(-2 /* RTLD_DEFAULT */, "coreclr_initialize");
        DlInfo info;
        return symbol != 0 && dladdr(symbol, &info) != 0 && Marshal.PtrToStringUTF8(info.FileName) is { Length: > 0 } path
            ? (path, info.Base)
            : null;
    }

    /// <summary>
    /// The library beside the runtime's, as a framework of its own (<c>Frameworks/&lt;name&gt;.framework/&lt;name&gt;</c>,
    /// a device's bundle) or a file next to it (<c>&lt;name&gt;.dylib</c>, a simulator's).
    /// </summary>
    internal static string Sibling(string runtimePath, string name)
    {
        var folder = Path.GetDirectoryName(runtimePath)!;
        return Path.GetExtension(folder) == ".framework"
            ? Path.Combine(Path.GetDirectoryName(folder)!, name + ".framework", name)
            : Path.Combine(folder, name + ".dylib");
    }

    /// <summary>The ICorDebug of the runtime at <paramref name="runtime"/> (loaded at <paramref name="runtimeBase"/>) in this process, initialized.</summary>
    private static CorDebug CreateCorDebug(int processId, string runtime, nint runtimeBase)
    {
        var library = Sibling(runtime, DebugLibrary);
        var dataAccess = Sibling(runtime, DataAccessLibrary);
        if (!File.Exists(library) || !File.Exists(dataAccess))
        {
            throw new ClrException("The app has no debugging libraries (libmscordbi, libmscordaccore) next to its runtime: only a Debug build has them.");
        }

        nint export;
        try
        {
            var dataAccessHandle = NativeLibrary.Load(dataAccess);
            var handle = NativeLibrary.Load(library);
            GC.KeepAlive(dataAccessHandle);

            // The debugging library sets up its platform layer in its DllMain, which dbgshim's loader calls; loaded
            // directly, nothing does, and its first call crashed on a null environment (ADR 0117, on an iPhone).
            var main = (delegate* unmanaged<nint, uint, nint, int>)NativeLibrary.GetExport(handle, "DllMain");
            if (main(handle, ProcessAttach, 0) == 0)
            {
                throw new ClrException($"The app's debugging library ({library}) didn't start.");
            }

            export = NativeLibrary.GetExport(handle, "CoreCLRCreateCordbObject3");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new ClrException($"The app's debugging library ({library}) couldn't be loaded: {ex.Message}", ex);
        }

        // CoreCLRCreateCordbObject3(version, pid, applicationGroupId, dacModulePath, hmodTargetCLR, ppCordb).
        nint unknown;
        HRESULT result;
        fixed (char* path = dataAccess)
        {
            var create = (delegate* unmanaged<int, uint, char*, char*, nint, nint*, int>)export;
            result = (HRESULT)create((int)CorDebugInterfaceVersion.CorDebugVersion_4_0, (uint)processId, null, path, runtimeBase, &unknown);
        }

        if (result != HRESULT.S_OK)
        {
            throw new DebugException(result);
        }

        var corDebug = new CorDebug(ClrDebug.Extensions.GetObjectForIUnknown<ICorDebug>(unknown));
        corDebug.Initialize();
        return corDebug;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public nint FileName;
        public nint Base;
        public nint SymbolName;
        public nint SymbolAddress;
    }

    [LibraryImport("libSystem.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlsym(nint handle, string name);

    [LibraryImport("libSystem.dylib")]
    private static partial int dladdr(nint address, DlInfo* info);
}
