using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Codalus.Platform;

/// <summary>
/// macOS LaunchServices, for the opt-in file types (M51, ADR 0084): the type a filename extension has, the app that opens
/// it, and making an app that one. Tests use a stand-in (<see cref="ILaunchServices"/>); nothing here runs off macOS.
/// </summary>
public interface ILaunchServices
{
    /// <summary>The uniform type identifier of files ending in <paramref name="extension"/> (no dot), or null.</summary>
    string? TypeForExtension(string extension);

    /// <summary>The bundle identifier of the app that opens <paramref name="contentType"/> by default, or null.</summary>
    string? DefaultHandler(string contentType);

    /// <summary>Every app that says it can open <paramref name="contentType"/>, by bundle identifier.</summary>
    IReadOnlyList<string> Handlers(string contentType);

    /// <summary>Makes <paramref name="bundleId"/> the app that opens <paramref name="contentType"/>; false when LaunchServices refused.</summary>
    bool SetDefaultHandler(string contentType, string bundleId);
}

/// <summary>The real LaunchServices, through CoreServices' C functions.</summary>
[SupportedOSPlatform("macos")]
public sealed unsafe partial class MacLaunchServices : ILaunchServices
{
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint CFStringEncodingUtf8 = 0x08000100;

    /// <summary><c>kLSRolesAll</c>: viewer, editor and shell roles alike.</summary>
    private const uint RolesAll = 0xFFFFFFFF;

    public string? TypeForExtension(string extension)
    {
        var tagClass = CFString("public.filename-extension"); // kUTTagClassFilenameExtension's value
        var tag = CFString(extension.TrimStart('.'));
        try
        {
            var type = UTTypeCreatePreferredIdentifierForTag(tagClass, tag, 0);
            return type == 0 ? null : FromCFString(type, release: true);
        }
        finally
        {
            CFRelease(tag);
            CFRelease(tagClass);
        }
    }

    public string? DefaultHandler(string contentType)
    {
        var type = CFString(contentType);
        try
        {
            var handler = LSCopyDefaultRoleHandlerForContentType(type, RolesAll);
            return handler == 0 ? null : FromCFString(handler, release: true);
        }
        finally
        {
            CFRelease(type);
        }
    }

    public IReadOnlyList<string> Handlers(string contentType)
    {
        var type = CFString(contentType);
        try
        {
            var array = LSCopyAllRoleHandlersForContentType(type, RolesAll);
            if (array == 0)
            {
                return [];
            }

            try
            {
                var handlers = new List<string>();
                var count = CFArrayGetCount(array);
                for (nint i = 0; i < count; i++)
                {
                    if (FromCFString(CFArrayGetValueAtIndex(array, i), release: false) is { } id)
                    {
                        handlers.Add(id);
                    }
                }

                return handlers;
            }
            finally
            {
                CFRelease(array);
            }
        }
        finally
        {
            CFRelease(type);
        }
    }

    public bool SetDefaultHandler(string contentType, string bundleId)
    {
        var type = CFString(contentType);
        var bundle = CFString(bundleId);
        try
        {
            return LSSetDefaultRoleHandlerForContentType(type, RolesAll, bundle) == 0;
        }
        finally
        {
            CFRelease(bundle);
            CFRelease(type);
        }
    }

    private static nint CFString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* pointer = bytes)
        {
            return CFStringCreateWithCString(0, pointer, CFStringEncodingUtf8);
        }
    }

    private static string? FromCFString(nint value, bool release)
    {
        try
        {
            var length = CFStringGetLength(value);
            var size = (length * 4) + 1;
            var buffer = new byte[size];
            fixed (byte* pointer = buffer)
            {
                if (!CFStringGetCString(value, pointer, size, CFStringEncodingUtf8))
                {
                    return null;
                }
            }

            return Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
        }
        finally
        {
            if (release)
            {
                CFRelease(value);
            }
        }
    }

    [LibraryImport(CoreServices)]
    private static partial nint UTTypeCreatePreferredIdentifierForTag(nint tagClass, nint tag, nint conformingTo);

    [LibraryImport(CoreServices)]
    private static partial nint LSCopyDefaultRoleHandlerForContentType(nint contentType, uint role);

    [LibraryImport(CoreServices)]
    private static partial nint LSCopyAllRoleHandlersForContentType(nint contentType, uint role);

    [LibraryImport(CoreServices)]
    private static partial int LSSetDefaultRoleHandlerForContentType(nint contentType, uint role, nint handlerBundleId);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringCreateWithCString(nint allocator, byte* text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint text);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}

/// <summary>Windows: tells Explorer that file associations changed (<c>SHChangeNotify(SHCNE_ASSOCCHANGED)</c>), so it reads them again.</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsShellNotify
{
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    public static void AssociationsChanged() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
