using System.Runtime.InteropServices;

namespace Daedalus.Benchmarks.Harness.Platform;

/// <summary>
/// Injects keystrokes into one target window. Safety rule: a key is only sent while that window is the
/// foreground window; if focus moves anywhere else the driver stops and the run is discarded, so input can
/// never land in another application.
/// </summary>
internal sealed class InputDriver(nint window)
{
    public const ushort VkReturn = 0x0D;
    public const ushort VkPageDown = 0x22;
    public const ushort VkPageUp = 0x21;
    public const ushort VkEnd = 0x23;
    public const ushort VkHome = 0x24;
    public const ushort VkControl = 0x11;

    /// <summary>QPC timestamps of every key-down that was sent.</summary>
    public List<long> SentAtQpc { get; } = [];

    public bool Aborted { get; private set; }

    public bool Type(char ch) => Send([Win32.Unicode(ch, up: false), Win32.Unicode(ch, up: true)]);

    public bool Press(ushort virtualKey) => Send([Win32.Key(virtualKey, up: false), Win32.Key(virtualKey, up: true)]);

    public bool Chord(ushort modifier, ushort virtualKey) => Send(
    [
        Win32.Key(modifier, up: false), Win32.Key(virtualKey, up: false),
        Win32.Key(virtualKey, up: true), Win32.Key(modifier, up: true),
    ]);

    private bool Send(Win32.Input[] inputs)
    {
        if (Aborted || !WindowProbe.IsForeground(window))
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
}
