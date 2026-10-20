using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Codalus.Platform;

/// <summary>
/// Brings a window to the foreground on Windows when another program's window is there (the debugger stopped while the
/// browser or the program had the focus). Windows refuses <c>SetForegroundWindow</c> to a background process and only
/// flashes its taskbar button, unless the caller's input is attached to the foreground window's thread for the call.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsForeground
{
    /// <summary>Restores <paramref name="window"/> if minimized and makes it the foreground window; false when Windows refused.</summary>
    public static bool Bring(IntPtr window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == window)
        {
            return true;
        }

        var ours = GetCurrentThreadId();
        var theirs = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, 1) != 0;
        try
        {
            if (IsIconic(window) != 0)
            {
                _ = ShowWindow(window, SwRestore);
            }

            _ = BringWindowToTop(window);
            return SetForegroundWindow(window) != 0;
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(ours, theirs, 0);
            }
        }
    }

    private const int SwRestore = 9;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    private static partial int AttachThreadInput(uint attach, uint attachTo, int doAttach);

    [LibraryImport("user32.dll")]
    private static partial int IsIconic(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    private static partial int BringWindowToTop(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(IntPtr window);
}
