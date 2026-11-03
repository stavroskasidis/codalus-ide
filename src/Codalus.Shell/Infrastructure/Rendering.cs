using System.Runtime.InteropServices;
using Avalonia;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// How the window is drawn. With a GPU, Avalonia's defaults: ANGLE (Direct3D) through WinUI composition on Windows, GLX
/// or a software framebuffer on X11. Without one, Windows has only WARP, Direct3D drawn on the CPU, and ANGLE on WARP
/// redraws slower than Skia's own software renderer: on the GPU-less Windows CI runner the self-driven scroll ran 210
/// frames in 10 s through ANGLE and 470 through software, p50 38.3 against 16.1 ms, and typing's p50 went from 42.1 to
/// 20.1 ms (run 41548402294, 8 runs each, alternating). So a Windows whose first adapter (the one ANGLE would use) is
/// WARP draws in software. <see cref="Variable"/> overrides the choice either way.
/// </summary>
internal static partial class Rendering
{
    /// <summary><c>software</c> or <c>gpu</c>: overrides the choice (unset or anything else: the adapter decides).</summary>
    public const string Variable = "CODALUS_RENDERING";

    /// <summary>Microsoft's vendor id, and the Basic Render Driver's device id: WARP's adapter.</summary>
    private const uint MicrosoftVendorId = 0x1414;
    private const uint BasicRenderDriverDeviceId = 0x8C;

    /// <summary>DXGI_ADAPTER_FLAG_SOFTWARE.</summary>
    private const uint SoftwareAdapterFlag = 2;

    /// <summary>The X11 options: the framebuffer retained (see <c>Program.BuildAvaloniaApp</c>), software when asked for.</summary>
    public static X11PlatformOptions X11(string? setting)
    {
        var options = new X11PlatformOptions { UseRetainedFramebuffer = true };
        if (Choice(setting) is true)
        {
            options.RenderingMode = [X11RenderingMode.Software];
        }

        return options;
    }

    /// <summary>The Windows options: software when asked for, or when the adapter ANGLE would use is WARP.</summary>
    public static Win32PlatformOptions Windows(string? setting, Func<bool?> firstAdapterIsSoftware) =>
        UsesSoftware(setting, firstAdapterIsSoftware)
            ? new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software], CompositionMode = [Win32CompositionMode.RedirectionSurface] }
            : new Win32PlatformOptions();

    /// <summary>Whether Windows draws in software: the setting's choice, else whether the first adapter is WARP.</summary>
    internal static bool UsesSoftware(string? setting, Func<bool?> firstAdapterIsSoftware) =>
        Choice(setting) ?? firstAdapterIsSoftware() == true;

    /// <summary>True for <c>software</c>, false for <c>gpu</c>, otherwise null (the machine decides).</summary>
    internal static bool? Choice(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        "software" => true,
        "gpu" => false,
        _ => null,
    };

    /// <summary>Whether an adapter is WARP: flagged as software, or the Basic Render Driver.</summary>
    internal static bool IsSoftware(uint vendorId, uint deviceId, uint flags) =>
        (flags & SoftwareAdapterFlag) != 0 || (vendorId == MicrosoftVendorId && deviceId == BasicRenderDriverDeviceId);

    /// <summary>Whether DXGI's first adapter is WARP; null off Windows or when DXGI can't say.</summary>
    public static unsafe bool? FirstAdapterIsSoftware()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        nint factory = 0, adapter = 0;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(in iid, out factory) < 0 || factory == 0)
            {
                return null;
            }

            // IDXGIFactory1::EnumAdapters1 (vtable slot 12), then IDXGIAdapter1::GetDesc1 (slot 10).
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            if (enumAdapters1(factory, 0, &adapter) < 0 || adapter == 0)
            {
                return null;
            }

            AdapterDescription description;
            var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)(*(nint**)adapter)[10];
            return getDesc1(adapter, &description) < 0 ? null : IsSoftware(description.VendorId, description.DeviceId, description.Flags);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Release(adapter);
            Release(factory);
        }
    }

    private static unsafe void Release(nint unknown)
    {
        if (unknown != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2])(unknown); // IUnknown::Release
        }
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    /// <summary>DXGI_ADAPTER_DESC1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }
}
