using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Codalus.Debugging.Host;

/// <summary>
/// <c>codalus-debughost --activate-app &lt;AUMID&gt; [--arguments &lt;text&gt;]</c> (M44, ADR 0074): starts a registered
/// packaged app through Windows' <c>IApplicationActivationManager</c>, the way the Start menu does, and prints its
/// process id, which the IDE then follows or attaches netcoredbg to. A packaged app can't be started as a plain
/// process: it needs its package identity, which only an activation gives it. Here rather than in the shell, which
/// stays free of COM interop (Native AOT), and in the companion every package already ships.
/// </summary>
internal static partial class AppActivation
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var at = Array.IndexOf(args, "--activate-app");
        if (at < 0 || at + 1 >= args.Length || args[at + 1].Length == 0)
        {
            error.WriteLine("Usage: codalus-debughost --activate-app <application user model id> [--arguments <text>]");
            return 2;
        }

        var aumid = args[at + 1];
        var argumentsAt = Array.IndexOf(args, "--arguments");
        var arguments = argumentsAt >= 0 && argumentsAt + 1 < args.Length ? args[argumentsAt + 1] : null;
        if (!OperatingSystem.IsWindows())
        {
            error.WriteLine("Packaged apps start only on Windows.");
            return 3;
        }

        try
        {
            var processId = Activate(aumid, arguments);
            output.WriteLine(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidCastException or NotSupportedException)
        {
            error.WriteLine($"Windows didn't start {aumid}: {ex.Message} (0x{ex.HResult:X8}).");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static uint Activate(string aumid, string? arguments)
    {
        // COM on this thread (the multithreaded apartment); S_FALSE or RPC_E_CHANGED_MODE mean it already is.
        _ = CoInitializeEx(0, CoinitMultithreaded);
        var clsid = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"); // ApplicationActivationManager
        var iid = typeof(IApplicationActivationManager).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, ClsctxLocalServer | ClsctxInprocServer, in iid, out var pointer));
        try
        {
            var manager = (IApplicationActivationManager)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(aumid, arguments, ActivateOptions.None, out var processId));
            return processId;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private const uint ClsctxInprocServer = 0x1;
    private const uint ClsctxLocalServer = 0x4;
    private const uint CoinitMultithreaded = 0x0;

    [LibraryImport("ole32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    internal enum ActivateOptions
    {
        None = 0,
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    internal partial interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(string appUserModelId, string? arguments, ActivateOptions options, out uint processId);

        [PreserveSig]
        int ActivateForFile(string appUserModelId, nint itemArray, string verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol(string appUserModelId, nint itemArray, out uint processId);
    }
}
