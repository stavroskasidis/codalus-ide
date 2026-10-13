using System.Globalization;
using Avalonia.Input;
using Codalus.Platform;
using AvKeyModifiers = Avalonia.Input.KeyModifiers;
using KeyModifiers = Codalus.Platform.KeyModifiers;

namespace Codalus.Shell.Input;

/// <summary>Translates Avalonia key events to workbench <see cref="KeyChord"/>s (the names used in keybindings.json).</summary>
public static class KeyMapper
{
    /// <summary>The chord for a key press, or null for modifier-only and unsupported keys.</summary>
    public static KeyChord? ToChord(Key key, AvKeyModifiers modifiers)
    {
        if (KeyName(key) is not { } name)
        {
            return null;
        }

        var mods = KeyModifiers.None;
        if (modifiers.HasFlag(AvKeyModifiers.Control))
        {
            mods |= KeyModifiers.Ctrl;
        }

        if (modifiers.HasFlag(AvKeyModifiers.Shift))
        {
            mods |= KeyModifiers.Shift;
        }

        if (modifiers.HasFlag(AvKeyModifiers.Alt))
        {
            mods |= KeyModifiers.Alt;
        }

        if (modifiers.HasFlag(AvKeyModifiers.Meta))
        {
            mods |= KeyModifiers.Meta;
        }

        return new KeyChord(mods, name);
    }

    /// <summary>The Avalonia gesture for a chord (for native menus), or null if the key has no Avalonia equivalent.</summary>
    public static KeyGesture? ToGesture(KeyChord chord)
    {
        var key = s_keysByName.Value.GetValueOrDefault(chord.Key, Key.None);
        if (key == Key.None)
        {
            return null;
        }

        var modifiers = AvKeyModifiers.None;
        if (chord.Modifiers.HasFlag(KeyModifiers.Ctrl))
        {
            modifiers |= AvKeyModifiers.Control;
        }

        if (chord.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            modifiers |= AvKeyModifiers.Shift;
        }

        if (chord.Modifiers.HasFlag(KeyModifiers.Alt))
        {
            modifiers |= AvKeyModifiers.Alt;
        }

        if (chord.Modifiers.HasFlag(KeyModifiers.Meta))
        {
            modifiers |= AvKeyModifiers.Meta;
        }

        return new KeyGesture(key, modifiers);
    }

    private static readonly Lazy<Dictionary<string, Key>> s_keysByName = new(() =>
    {
        var map = new Dictionary<string, Key>(StringComparer.Ordinal);
        foreach (var key in Enum.GetValues<Key>())
        {
            if (KeyName(key) is { } name)
            {
                map.TryAdd(name, key);
            }
        }

        return map;
    });

    public static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "numpad" + (key - Key.NumPad0).ToString(CultureInfo.InvariantCulture),
        >= Key.F1 and <= Key.F24 => "f" + (key - Key.F1 + 1).ToString(CultureInfo.InvariantCulture),
        Key.Enter => "enter",
        Key.Escape => "escape",
        Key.Space => "space",
        Key.Tab => "tab",
        Key.Back => "backspace",
        Key.Delete => "delete",
        Key.Insert => "insert",
        Key.Home => "home",
        Key.End => "end",
        Key.PageUp => "pageup",
        Key.PageDown => "pagedown",
        Key.Up => "up",
        Key.Down => "down",
        Key.Left => "left",
        Key.Right => "right",
        Key.Pause or Key.Cancel => "pause", // Ctrl+Break arrives as Cancel on Windows
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe or Key.OemBackslash => "\\",
        Key.OemTilde => "`",
        Key.Add => "numpad_add",
        Key.Subtract => "numpad_subtract",
        Key.Multiply => "numpad_multiply",
        Key.Divide => "numpad_divide",
        Key.Decimal => "numpad_decimal",
        _ => null,
    };
}
