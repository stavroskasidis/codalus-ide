using System.Runtime.Versioning;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Daedalus.Benchmarks.Harness.Platform;

/// <summary>A composed desktop frame (QPC ticks) whose changed region intersected the watched window.</summary>
public readonly record struct DesktopFrame(long PresentQpc);

/// <summary>
/// Watches DWM's composed desktop frames through the DXGI Desktop Duplication API and records the
/// timestamps of frames whose dirty or move rectangles intersect the target window. Only that
/// metadata is read; pixel data is never copied or stored.
///
/// This works identically for every target, however it renders (DXGI swap chains, DirectComposition,
/// OpenGL/ANGLE), and includes compositor latency, so input → displayed-frame is the closest proxy
/// to photon latency available without external hardware.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopFrameMonitor : IDisposable
{
    private readonly Win32.Rect _window;
    private readonly Thread _thread;
    private readonly List<DesktopFrame> _frames = [];
    private volatile bool _stop;
    private Exception? _error;

    private DesktopFrameMonitor(Win32.Rect window)
    {
        _window = window;
        _thread = new Thread(Run) { IsBackground = true, Name = "desktop-duplication", Priority = ThreadPriority.Highest };
    }

    public static DesktopFrameMonitor Start(nint hwnd)
    {
        Win32.GetWindowRect(hwnd, out var rect);
        var monitor = new DesktopFrameMonitor(rect);
        monitor._thread.Start();
        return monitor;
    }

    public IReadOnlyList<DesktopFrame> Stop()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(5));
        if (_error is not null)
        {
            throw new InvalidOperationException("Desktop duplication failed: " + _error.Message, _error);
        }

        lock (_frames)
        {
            return [.. _frames];
        }
    }

    private void Run()
    {
        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device).CheckError();
            using var _ = device;
            using var dxgiDevice = device!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var output = FindOutput(adapter);
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            using var duplication = output1.DuplicateOutput(device);
            var origin = output.Description.DesktopCoordinates;
            var dirty = new RawRect[256];
            var moves = new OutduplMoveRect[64];

            while (!_stop)
            {
                var result = duplication.AcquireNextFrame(20, out var info, out var resource);
                if (result.Failure)
                {
                    continue; // DXGI_ERROR_WAIT_TIMEOUT: nothing changed.
                }

                try
                {
                    if (info.LastPresentTime != 0 && info.TotalMetadataBufferSize > 0 && Intersects(duplication, dirty, moves, origin))
                    {
                        lock (_frames)
                        {
                            _frames.Add(new DesktopFrame(info.LastPresentTime));
                        }
                    }
                }
                finally
                {
                    resource?.Dispose();
                    duplication.ReleaseFrame();
                }
            }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            _error = ex;
        }
    }

    private IDXGIOutput FindOutput(IDXGIAdapter adapter)
    {
        var centerX = (_window.Left + _window.Right) / 2;
        var centerY = (_window.Top + _window.Bottom) / 2;
        for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
        {
            var r = output!.Description.DesktopCoordinates;
            if (centerX >= r.Left && centerX < r.Right && centerY >= r.Top && centerY < r.Bottom)
            {
                return output;
            }

            output.Dispose();
        }

        throw new InvalidOperationException("No output contains the target window.");
    }

    private bool Intersects(IDXGIOutputDuplication duplication, RawRect[] dirty, OutduplMoveRect[] moves, RawRect origin)
    {
        // Rectangles are relative to the output; the window rect is in virtual-desktop coordinates.
        var left = _window.Left - origin.Left;
        var top = _window.Top - origin.Top;
        var right = _window.Right - origin.Left;
        var bottom = _window.Bottom - origin.Top;
        bool Hit(RawRect r) => r.Left < right && r.Right > left && r.Top < bottom && r.Bottom > top;

        if (duplication.GetFrameDirtyRects((uint)(dirty.Length * 16), dirty, out var dirtyBytes).Success)
        {
            for (var i = 0; i < dirtyBytes / 16; i++)
            {
                if (Hit(dirty[i]))
                {
                    return true;
                }
            }
        }

        if (duplication.GetFrameMoveRects((uint)(moves.Length * 24), moves, out var moveBytes).Success)
        {
            for (var i = 0; i < moveBytes / 24; i++)
            {
                if (Hit(moves[i].DestinationRect))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>For each input (QPC), the latency in ms to the first displayed frame after it; drops inputs with none within the window.</summary>
    public static List<double> InputToDisplay(IReadOnlyList<long> inputsQpc, IReadOnlyList<DesktopFrame> frames, double maxMs = 500)
    {
        var times = frames.Select(f => f.PresentQpc).Order().ToArray();
        var latencies = new List<double>(inputsQpc.Count);
        foreach (var input in inputsQpc)
        {
            var index = Array.BinarySearch(times, input + 1);
            if (index < 0)
            {
                index = ~index;
            }

            if (index < times.Length)
            {
                var ms = (times[index] - input) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms <= maxMs)
                {
                    latencies.Add(ms);
                }
            }
        }

        return latencies;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
