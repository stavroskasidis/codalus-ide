using System.Globalization;
using System.Text;

namespace Codalus.Terminal;

public enum TerminalKey
{
    Enter,
    Tab,
    Backspace,
    Escape,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    Insert,
    Delete,
    PageUp,
    PageDown,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
}

[Flags]
public enum TerminalModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
}

public enum MouseButton
{
    Left,
    Middle,
    Right,
    None,
    WheelUp,
    WheelDown,
}

public enum MouseEventKind
{
    Press,
    Release,
    Move,
}

/// <summary>Encodes keyboard, paste, focus and mouse input the way xterm sends it to the program.</summary>
public static class TerminalInput
{
    /// <summary>A special key; null when the key sends nothing.</summary>
    public static string? Key(TerminalKey key, TerminalModifiers modifiers, bool applicationCursorKeys)
    {
        var m = 1 + ((modifiers & TerminalModifiers.Shift) != 0 ? 1 : 0) + ((modifiers & TerminalModifiers.Alt) != 0 ? 2 : 0) + ((modifiers & TerminalModifiers.Control) != 0 ? 4 : 0);
        var alt = (modifiers & TerminalModifiers.Alt) != 0 ? "\x1b" : string.Empty;

        string Cursor(char final) => m > 1 ? $"\x1b[1;{m}{final}" : applicationCursorKeys ? $"\x1bO{final}" : $"\x1b[{final}";
        string Tilde(int code) => m > 1 ? $"\x1b[{code};{m}~" : $"\x1b[{code}~";
        string Ss3(char final) => m > 1 ? $"\x1b[1;{m}{final}" : $"\x1bO{final}";

        return key switch
        {
            TerminalKey.Enter => alt + "\r",
            TerminalKey.Tab => (modifiers & TerminalModifiers.Shift) != 0 ? "\x1b[Z" : alt + "\t",
            TerminalKey.Backspace => (modifiers & TerminalModifiers.Control) != 0 ? alt + "\x08" : alt + "\x7f",
            TerminalKey.Escape => alt + "\x1b",
            TerminalKey.Up => Cursor('A'),
            TerminalKey.Down => Cursor('B'),
            TerminalKey.Right => Cursor('C'),
            TerminalKey.Left => Cursor('D'),
            TerminalKey.Home => Cursor('H'),
            TerminalKey.End => Cursor('F'),
            TerminalKey.Insert => Tilde(2),
            TerminalKey.Delete => Tilde(3),
            TerminalKey.PageUp => Tilde(5),
            TerminalKey.PageDown => Tilde(6),
            TerminalKey.F1 => Ss3('P'),
            TerminalKey.F2 => Ss3('Q'),
            TerminalKey.F3 => Ss3('R'),
            TerminalKey.F4 => Ss3('S'),
            TerminalKey.F5 => Tilde(15),
            TerminalKey.F6 => Tilde(17),
            TerminalKey.F7 => Tilde(18),
            TerminalKey.F8 => Tilde(19),
            TerminalKey.F9 => Tilde(20),
            TerminalKey.F10 => Tilde(21),
            TerminalKey.F11 => Tilde(23),
            TerminalKey.F12 => Tilde(24),
            _ => null,
        };
    }

    /// <summary>
    /// A character typed with Control and/or Alt: Ctrl+A…Z and the punctuation xterm maps become C0 controls, Alt prefixes
    /// ESC. Null when the combination sends nothing.
    /// </summary>
    public static string? Character(char c, TerminalModifiers modifiers)
    {
        var text = c.ToString();
        if ((modifiers & TerminalModifiers.Control) != 0)
        {
            var control = char.ToLowerInvariant(c) switch
            {
                >= 'a' and <= 'z' => (char)(char.ToLowerInvariant(c) - 'a' + 1),
                '@' or ' ' or '2' => '\0',
                '[' or '3' => '\x1b',
                '\\' or '4' => '\x1c',
                ']' or '5' => '\x1d',
                '^' or '6' => '\x1e',
                '_' or '-' or '7' => '\x1f',
                '?' or '8' => '\x7f',
                _ => (char?)null,
            };
            if (control is null)
            {
                return null;
            }

            text = control.Value.ToString();
        }

        return (modifiers & TerminalModifiers.Alt) != 0 ? "\x1b" + text : text;
    }

    /// <summary>
    /// Pasted text: line breaks become CR (as typing Enter would), and with bracketed paste the text is wrapped so the
    /// program can tell it was pasted; an end marker inside the text is removed so it can't escape the bracket.
    /// </summary>
    public static string Paste(string text, bool bracketed)
    {
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (!bracketed)
        {
            return normalized;
        }

        return "\x1b[200~" + normalized.Replace("\x1b[201~", string.Empty, StringComparison.Ordinal) + "\x1b[201~";
    }

    public static string Focus(bool focused) => focused ? "\x1b[I" : "\x1b[O";

    /// <summary>
    /// A mouse event at a zero-based cell, in the program's tracking mode; null when the mode doesn't report it. SGR
    /// encoding (1006) when the program asked for it, otherwise the X10/normal byte encoding (columns up to 223).
    /// </summary>
    public static string? Mouse(MouseTracking tracking, bool sgr, MouseButton button, MouseEventKind kind, int column, int row, TerminalModifiers modifiers)
    {
        if (tracking == MouseTracking.None
            || (kind == MouseEventKind.Move && tracking is not (MouseTracking.ButtonEvent or MouseTracking.AnyEvent))
            || (kind == MouseEventKind.Move && tracking == MouseTracking.ButtonEvent && button == MouseButton.None)
            || (kind == MouseEventKind.Release && tracking == MouseTracking.X10))
        {
            return null;
        }

        var code = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Middle => 1,
            MouseButton.Right => 2,
            MouseButton.WheelUp => 64,
            MouseButton.WheelDown => 65,
            _ => 3,
        };
        if (kind == MouseEventKind.Move)
        {
            code += 32;
        }

        if (tracking != MouseTracking.X10)
        {
            code += ((modifiers & TerminalModifiers.Shift) != 0 ? 4 : 0) + ((modifiers & TerminalModifiers.Alt) != 0 ? 8 : 0) + ((modifiers & TerminalModifiers.Control) != 0 ? 16 : 0);
        }

        if (sgr)
        {
            var final = kind == MouseEventKind.Release ? 'm' : 'M';
            return string.Create(CultureInfo.InvariantCulture, $"\x1b[<{code};{column + 1};{row + 1}{final}");
        }

        if (kind == MouseEventKind.Release)
        {
            code = 3 + (code & ~3);
        }

        if (column > 222 || row > 222)
        {
            return null;
        }

        var builder = new StringBuilder("\x1b[M");
        builder.Append((char)(32 + code)).Append((char)(33 + column)).Append((char)(33 + row));
        return builder.ToString();
    }
}
