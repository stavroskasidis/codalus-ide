# The helpers eng/showcase/record.ps1 gives a story (eng/showcase/<name>.ps1), the Windows counterparts of record.sh's:
# the pointer, the keyboard, the screen's pixels and stills, through Win32. Dot-source it with $Work set to also drive a
# window by hand while writing a story (Shot shows what the recording would).
#
# Coordinates are the screen's, in physical pixels: the window is at 0,0 and 1280x800, as on Linux.

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;

public static class CodalusShowcase
{
    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct Input { public uint Type; public KeyboardInput Key; public long Pad; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect rect, int size);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);

    static void Send(params Input[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

    static Input Key(ushort vk, ushort scan, uint flags) => new Input { Type = 1, Key = new KeyboardInput { Vk = vk, Scan = scan, Flags = flags } };

    // A key by its virtual-key code: the layout doesn't matter, so Ctrl+S is Ctrl+S on any keyboard.
    public static void Down(ushort vk) => Send(Key(vk, 0, Extended(vk)));
    public static void Up(ushort vk) => Send(Key(vk, 0, Extended(vk) | 2));

    static uint Extended(ushort vk) => vk is >= 0x21 and <= 0x2E ? 1u : 0u;

    // A character as itself (KEYEVENTF_UNICODE): typed text doesn't depend on the keyboard layout either.
    public static void Char(char c)
    {
        if (c == '\n') { Down(0x0D); Up(0x0D); return; }
        Send(Key(0, c, 4), Key(0, c, 4 | 2));
    }

    public static void Button(bool down, bool right) => mouse_event(right ? (down ? 8u : 16u) : (down ? 2u : 4u), 0, 0, 0, IntPtr.Zero);

    public static void Wheel(int notches) => mouse_event(0x800, 0, 0, notches * 120, IntPtr.Zero);

    public static int[] Pixel(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        try { var c = GetPixel(dc, x, y); return new[] { (int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF) }; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
}
'@
Add-Type -AssemblyName System.Windows.Forms
[void][CodalusShowcase]::SetProcessDPIAware()

$script:KeyCodes = @{
    ctrl = 0x11; shift = 0x10; alt = 0x12; win = 0x5B
    Return = 0x0D; Enter = 0x0D; Tab = 0x09; Escape = 0x1B; Esc = 0x1B; space = 0x20; BackSpace = 0x08; Delete = 0x2E
    Left = 0x25; Up = 0x26; Right = 0x27; Down = 0x28; Home = 0x24; End = 0x23; Prior = 0x21; Next = 0x22
    period = 0xBE; comma = 0xBC; minus = 0xBD; plus = 0xBB; slash = 0xBF
}
1..12 | ForEach-Object { $script:KeyCodes["F$_"] = 0x6F + $_ }

function Get-KeyCode([string] $Name) {
    if ($script:KeyCodes.ContainsKey($Name)) { return [uint16]$script:KeyCodes[$Name] }
    if ($Name.Length -eq 1 -and $Name -match '[A-Za-z0-9]') { return [uint16][char]$Name.ToUpperInvariant() }
    throw "unknown key '$Name'"
}

# Mark <label>: the time of a moment in the story (start and end cut the video).
function Mark([string] $Label) {
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0
    Add-Content -LiteralPath (Join-Path $Work 'marks.txt') -Value ($now.ToString('F3', [Globalization.CultureInfo]::InvariantCulture) + ' ' + $Label)
}

# Wait <seconds>
function Wait([double] $Seconds) { Start-Sleep -Milliseconds ([int]($Seconds * 1000)) }

# Hover <x> <y>: the pointer goes there in a short glide, as a hand moves it.
function Hover([int] $X, [int] $Y) {
    $from = [System.Windows.Forms.Cursor]::Position
    $steps = 12
    for ($i = 1; $i -le $steps; $i++) {
        $t = $i / $steps
        $t = $t * $t * (3 - 2 * $t)
        [void][CodalusShowcase]::SetCursorPos([int]($from.X + ($X - $from.X) * $t), [int]($from.Y + ($Y - $from.Y) * $t))
        Start-Sleep -Milliseconds 15
    }
}

# Rest: the pointer goes where it hides nothing: $RestAt, 1180,420 unless the story says otherwise. Not over text: gdigrab
# draws the I-beam (an inverting cursor) as a white box.
$script:RestAt = 1180, 420
function Rest { Hover $script:RestAt[0] $script:RestAt[1] }

# Click <x> <y> [-Right] [-Double]
function Click([int] $X, [int] $Y, [switch] $Right, [switch] $Double) {
    Hover $X $Y
    Wait 0.3
    $times = if ($Double) { 2 } else { 1 }
    for ($i = 0; $i -lt $times; $i++) {
        [CodalusShowcase]::Button($true, $Right)
        Start-Sleep -Milliseconds 40
        [CodalusShowcase]::Button($false, $Right)
        Start-Sleep -Milliseconds 60
    }
    Wait 0.2
}

# Drag <x1> <y1> <x2> <y2>: the left button held from one point to the other.
function Drag([int] $X1, [int] $Y1, [int] $X2, [int] $Y2) {
    Hover $X1 $Y1
    Wait 0.2
    [CodalusShowcase]::Button($true, $false)
    Wait 0.2
    Hover ([int](($X1 + $X2) / 2)) ([int](($Y1 + $Y2) / 2))
    Hover $X2 $Y2
    Wait 0.4
    [CodalusShowcase]::Button($false, $false)
    Wait 0.2
}

# Scroll <notches>: the wheel, down for a negative count.
function Scroll([int] $Notches) { [CodalusShowcase]::Wheel($Notches) }

# Typed <delay ms> <text>: typed like a person, a key at a time.
function Typed([int] $Delay, [string] $Text) {
    foreach ($c in $Text.ToCharArray()) {
        [CodalusShowcase]::Char($c)
        Start-Sleep -Milliseconds $Delay
    }
}

# Key <chord> [...]: keys as xdotool names them, a chord with +, e.g. Key ctrl+s; Key ctrl+e t; Key F5.
function Key {
    foreach ($chord in $args) {
        $codes = @("$chord".Split('+') | ForEach-Object { Get-KeyCode $_ })
        foreach ($code in $codes) { [CodalusShowcase]::Down($code); Start-Sleep -Milliseconds 20 }
        [array]::Reverse($codes)
        foreach ($code in $codes) { [CodalusShowcase]::Up($code); Start-Sleep -Milliseconds 20 }
        Start-Sleep -Milliseconds 60
    }
}

# Pixel <x> <y>: the screen's color there, as @(r, g, b).
function Pixel([int] $X, [int] $Y) { [CodalusShowcase]::Pixel($X, $Y) }

# UntilPixel <seconds> <x> <y> <test>: waits until the test (a script block on $r, $g and $b) holds at that point.
function UntilPixel([double] $Seconds, [int] $X, [int] $Y, [scriptblock] $Test) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $r, $g, $b = Pixel $X $Y
        if (& $Test) { return }
        Start-Sleep -Milliseconds 300
    }
    throw "timed out waiting for {$Test} at ($X, $Y)"
}

# Debugging: the status bar is orange (a debug session is paused or running).
function Debugging([double] $Seconds = 60) { UntilPixel $Seconds 8 796 { $r -gt 150 -and $b -lt 60 } }

# Shot <name>: a still of the recorded region in the work folder, to check a story while writing it.
function Shot([string] $Name) {
    & ffmpeg -y -loglevel error -f gdigrab -offset_x 0 -offset_y 0 -video_size 1280x800 -i desktop -frames:v 1 (Join-Path $Work "$Name.png")
}

# Window: the shell's main window at 0,0 and 1280x800 (its visible frame, without the invisible resize borders; one row
# more, below the recorded region, for its border, which shows what is behind it), in
# front.
function Window([System.Diagnostics.Process] $App) {
    $App.Refresh()
    $handle = $App.MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw 'the shell has no window' }
    [void][CodalusShowcase]::ShowWindow($handle, 9)
    [void][CodalusShowcase]::MoveWindow($handle, 0, 0, 1280, 800, $true)
    Start-Sleep -Milliseconds 300
    $outer = New-Object CodalusShowcase+Rect
    $frame = New-Object CodalusShowcase+Rect
    [void][CodalusShowcase]::GetWindowRect($handle, [ref]$outer)
    if ([CodalusShowcase]::DwmGetWindowAttribute($handle, 9, [ref]$frame, 16) -eq 0) {
        $left = $frame.Left - $outer.Left; $top = $frame.Top - $outer.Top
        $right = $outer.Right - $frame.Right; $bottom = $outer.Bottom - $frame.Bottom
        [void][CodalusShowcase]::MoveWindow($handle, -$left, -$top, 1280 + $left + $right, 801 + $top + $bottom, $true)
    }
    [void][CodalusShowcase]::SetForegroundWindow($handle)
}
