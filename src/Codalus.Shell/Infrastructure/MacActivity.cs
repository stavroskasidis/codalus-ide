using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Keeps macOS from napping the app while the smoke test drives it: an <c>NSProcessInfo</c> activity, user initiated
/// and latency critical, held until the process ends. App Nap throttles the timers of an app it considers out of the
/// user's sight, and a smoke test's window on a CI runner is in nobody's sight: on the macOS runner every
/// <c>Task.Delay(20)</c> of the command sweep took 40 to 120 ms (a key binding's run, five of them and nothing else,
/// 532 ms at the median against 105 on Linux and 171 on Windows), so the sweep took 722 s against 238 on Linux, and the
/// whole smoke ran out of its 40 minutes on a slow runner (runs 43617802812 and 43914513384, docs/ci-flakes.md).
/// Only the smoke test: an app the user isn't looking at may nap.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class MacActivity
{
    // NSActivityOptions: NSActivityUserInitiated (0x00FFFFFF, which includes NSActivityIdleSystemSleepDisabled) and
    // NSActivityLatencyCritical (0xFF00000000).
    private const ulong UserInitiatedLatencyCritical = 0x00FFFFFFUL | 0xFF00000000UL;
    private static nint s_activity;

    /// <summary>Begins the activity once; false (and nothing changed) where the runtime's Objective-C calls fail.</summary>
    public static bool Begin(string reason)
    {
        if (s_activity != 0)
        {
            return true;
        }

        try
        {
            var info = Send(GetClass("NSProcessInfo"), Selector("processInfo"));
            var text = SendString(GetClass("NSString"), Selector("stringWithUTF8String:"), reason);
            var activity = SendActivity(info, Selector("beginActivityWithOptions:reason:"), UserInitiatedLatencyCritical, text);
            if (activity == 0)
            {
                return false;
            }

            // The activity lasts while its object lives, and the call returns it autoreleased: the run loop's pool
            // would end it at once without this reference, held for the process's life.
            s_activity = Send(activity, Selector("retain"));
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SendString(nint receiver, nint selector, string text);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendActivity(nint receiver, nint selector, ulong options, nint reason);
}
