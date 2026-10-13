using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Codalus.Benchmarks.Harness.Platform;

/// <summary>
/// Injects keystrokes into one target window. Safety rule: a key is only sent while that window is the
/// foreground window. If another window takes the foreground, the driver tries (a bounded number of times)
/// to bring the target back without synthesizing input; if that fails, it stops and the run is discarded,
/// so input can never land in another application.
/// </summary>
internal sealed class InputDriver(nint window, int maxRegains = 5)
{
    public const ushort VkReturn = 0x0D;
    public const ushort VkPageDown = 0x22;
    public const ushort VkPageUp = 0x21;
    public const ushort VkEnd = 0x23;
    public const ushort VkHome = 0x24;
    public const ushort VkControl = 0x11;

    private int _regains;

    /// <summary>QPC timestamps of every key-down that was sent.</summary>
    public List<long> SentAtQpc { get; } = [];

    public bool Aborted { get; private set; }

    /// <summary>Processes that took the foreground away during the run (for the result notes).</summary>
    public List<string> FocusThieves { get; } = [];

    public bool Type(char ch) => Send([Win32.Unicode(ch, up: false), Win32.Unicode(ch, up: true)]);

    public bool Press(ushort virtualKey) => Send([Win32.Key(virtualKey, up: false), Win32.Key(virtualKey, up: true)]);

    public bool Chord(ushort modifier, ushort virtualKey) => Send(
    [
        Win32.Key(modifier, up: false), Win32.Key(virtualKey, up: false),
        Win32.Key(virtualKey, up: true), Win32.Key(modifier, up: true),
    ]);

    private bool Send(Win32.Input[] inputs)
    {
        if (Aborted || !EnsureForeground())
        {
            Aborted = true;
            return false;
        }

        var qpc = Win32.Qpc();
        var sent = Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.Input>());
        if (sent != inputs.Length)
        {
            Aborted = true;
            return false;
        }

        SentAtQpc.Add(qpc);
        return true;
    }

    private bool EnsureForeground()
    {
        if (WindowProbe.IsForeground(window))
        {
            return true;
        }

        FocusThieves.Add(ForegroundProcessName());
        while (_regains < maxRegains)
        {
            _regains++;
            Thread.Sleep(500);
            if (WindowProbe.BringToForeground(window) && WindowProbe.IsForeground(window))
            {
                return true;
            }
        }

        return false;
    }

    private static string ForegroundProcessName()
    {
        Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return $"pid {pid}";
        }
    }
}
