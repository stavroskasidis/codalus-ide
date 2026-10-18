using System.Runtime.InteropServices;

namespace Codalus.Platform;

/// <summary>
/// The main windows of the processes on this desktop, on Windows: Attach to Process shows their titles (M43b).
/// </summary>
/// <remarks>
/// This isn't <see cref="System.Diagnostics.Process.MainWindowTitle"/> a process: that enumerates every top-level window
/// again for each process, and reads the title with <c>GetWindowTextLengthW</c>, which can send the window a message
/// and wait for a process that doesn't answer (one stopped in a debugger, say). Here the windows are enumerated once,
/// and each title is read with <c>InternalGetWindowText</c>, which never sends a message.
/// </remarks>
public static partial class ProcessWindows
{
    /// <summary>
    /// The title of each process's main window (its first visible top-level window without an owner, as
    /// <see cref="System.Diagnostics.Process.MainWindowTitle"/> picks it; empty when that window has no title), by process
    /// id. Empty off Windows.
    /// </summary>
    public static IReadOnlyDictionary<int, string> MainWindowTitles()
    {
        var titles = new Dictionary<int, string>();
        if (!OperatingSystem.IsWindows())
        {
            return titles;
        }

        var state = GCHandle.Alloc(titles);
        try
        {
            unsafe
            {
                _ = EnumWindows(&Visit, GCHandle.ToIntPtr(state)); // a failure leaves the titles read so far
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        finally
        {
            state.Free();
        }

        return titles;
    }

    /// <summary>One top-level window: its process's first main window gives the process its title. Nothing in here throws.</summary>
    [UnmanagedCallersOnly]
    private static unsafe int Visit(IntPtr window, IntPtr state)
    {
        if (GCHandle.FromIntPtr(state).Target is Dictionary<int, string> titles
            && GetWindowThreadProcessId(window, out var processId) != 0
            && !titles.ContainsKey((int)processId)
            && GetWindow(window, GwOwner) == IntPtr.Zero
            && IsWindowVisible(window) != 0)
        {
            var text = stackalloc char[TitleLength];
            var length = InternalGetWindowText(window, text, TitleLength);
            titles[(int)processId] = length > 0 ? new string(text, 0, Math.Min(length, TitleLength)) : string.Empty;
        }

        return 1; // go on
    }

    private const uint GwOwner = 4;

    private const int TitleLength = 512;

    [LibraryImport("user32.dll")]
    private static unsafe partial int EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> callback, IntPtr state);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr window, uint command);

    [LibraryImport("user32.dll")]
    private static partial int IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll", EntryPoint = "InternalGetWindowText")]
    private static unsafe partial int InternalGetWindowText(IntPtr window, char* text, int length);
}
