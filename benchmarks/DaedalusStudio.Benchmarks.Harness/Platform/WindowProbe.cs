using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DaedalusStudio.Benchmarks.Harness.Platform;

/// <summary>
/// Black-box window probes for Windows. A target is "ready" when its main window is visible,
/// answers WM_NULL, and its own rendered content (PrintWindow, never a screen grab) has been
/// visually stable for <see cref="StableFor"/>. The reported time is when it became stable.
/// </summary>
internal static partial class WindowProbe
{
    public static readonly TimeSpan StableFor = TimeSpan.FromMilliseconds(1000);
    private const int ThumbWidth = 160;
    private const int ThumbHeight = 100;

    /// <summary>
    /// Largest visible, titled top-level window owned by the process tree that <paramref name="accept"/> (title, class)
    /// accepts. Targets use it to exclude splash screens and error dialogs.
    /// </summary>
    public static nint FindMainWindow(int rootPid, Func<string, string, bool>? accept = null)
    {
        var pids = ProcessTree.Descendants(rootPid).Select(p => (uint)p).ToHashSet();
        nint best = 0;
        long bestArea = 0;
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid) && Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd) && Win32.GetWindowTextLength(hwnd) > 0
                && Win32.GetWindowRect(hwnd, out var rect) && rect.Width >= 400 && rect.Height >= 300
                && (accept is null || accept(Text(hwnd), ClassName(hwnd))))
            {
                var area = (long)rect.Width * rect.Height;
                if (area > bestArea)
                {
                    (best, bestArea) = (hwnd, area);
                }
            }

            return true;
        }, 0);
        return best;
    }

    /// <summary>
    /// Polls until the window is ready; returns the elapsed time (from <paramref name="launch"/>) at which
    /// it became visually stable, or null on timeout.
    /// </summary>
    public static async Task<(TimeSpan Ready, nint Window)?> WaitUntilReadyAsync(int rootPid, long launch, TimeSpan timeout, Func<string, string, bool>? accept, CancellationToken cancellationToken)
    {
        byte[]? lastThumb = null;
        TimeSpan? stableSince = null;
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Stopwatch.GetElapsedTime(launch);
            var hwnd = FindMainWindow(rootPid, accept);
            if (hwnd != 0 && Win32.IsResponsive(hwnd) && Capture(hwnd) is { } thumb && HasContent(thumb))
            {
                if (lastThumb is not null && Similar(lastThumb, thumb))
                {
                    stableSince ??= now;
                    if (now - stableSince.Value >= StableFor)
                    {
                        return (stableSince.Value, hwnd);
                    }
                }
                else
                {
                    stableSince = null;
                }

                lastThumb = thumb;
            }
            else
            {
                stableSince = null;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Downscaled grayscale capture of the window's own content.</summary>
    public static byte[]? Capture(nint hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        using var full = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            var ok = Win32.PrintWindowFull(hwnd, hdc);
            g.ReleaseHdc(hdc);
            if (!ok)
            {
                return null;
            }
        }

        using var thumb = new Bitmap(full, ThumbWidth, ThumbHeight);
        var data = thumb.LockBits(new Rectangle(0, 0, ThumbWidth, ThumbHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * ThumbHeight];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var gray = new byte[ThumbWidth * ThumbHeight];
            for (var i = 0; i < gray.Length; i++)
            {
                var o = (i / ThumbWidth * data.Stride) + (i % ThumbWidth * 4);
                gray[i] = (byte)((bytes[o] + bytes[o + 1] + bytes[o + 2]) / 3);
            }

            return gray;
        }
        finally
        {
            thumb.UnlockBits(data);
        }
    }

    /// <summary>
    /// A window that has rendered real UI, not a uniform placeholder (Electron and many toolkits show an empty,
    /// background-coloured window before their first real frame).
    /// </summary>
    public static bool HasContent(byte[] gray)
    {
        double mean = 0;
        foreach (var g in gray)
        {
            mean += g;
        }

        mean /= gray.Length;
        double variance = 0;
        foreach (var g in gray)
        {
            variance += (g - mean) * (g - mean);
        }

        return Math.Sqrt(variance / gray.Length) > 6;
    }

    /// <summary>
    /// Asks every visible top-level window of the tree to close (WM_CLOSE) and waits for the root to exit.
    /// IDEs that are killed outright can leave stale locks (JetBrains' AF_UNIX .port socket), so this always comes first.
    /// </summary>
    public static bool CloseGracefully(int rootPid, TimeSpan timeout)
    {
        const uint WmClose = 0x0010;
        var pids = ProcessTree.Descendants(rootPid).Select(p => (uint)p).ToHashSet();
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid) && Win32.IsWindowVisible(hwnd))
            {
                PostMessage(hwnd, WmClose, 0, 0);
            }

            return true;
        }, 0);
        try
        {
            using var root = Process.GetProcessById(rootPid);
            return root.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static unsafe string Text(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetWindowText(hwnd, buffer, 256);
        return new string(buffer, 0, Math.Max(0, length));
    }

    private static unsafe string ClassName(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetClassName(hwnd, buffer, 256);
        return new string(buffer, 0, Math.Max(0, length));
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(nint hwnd, char* text, int max);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(nint hwnd, char* text, int max);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>Less than 0.5% of thumbnail pixels changed noticeably (ignores caret blink and spinners' anti-aliasing).</summary>
    public static bool Similar(byte[] a, byte[] b)
    {
        var changed = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - b[i]) > 24)
            {
                changed++;
            }
        }

        return changed <= a.Length / 200;
    }

    /// <summary>
    /// Brings the window to the foreground without synthesizing input (AttachThreadInput), places it at a fixed
    /// size so every target renders the same area, and verifies the result.
    /// </summary>
    public static bool BringToForeground(nint hwnd, int width = 1600, int height = 1000)
    {
        const int SwRestore = 9;
        Win32.ShowWindow(hwnd, SwRestore);
        SetWindowPos(hwnd, 0, 40, 40, width, height, 0x0040 /* SWP_SHOWWINDOW */);
        var foreground = Win32.GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = Win32.GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            Win32.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        Thread.Sleep(300);
        return IsForeground(hwnd);
    }

    public static bool IsForeground(nint hwnd)
    {
        var foreground = Win32.GetForegroundWindow();
        const uint GaRoot = 2;
        return foreground != 0 && (foreground == hwnd || Win32.GetAncestor(foreground, GaRoot) == hwnd);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
