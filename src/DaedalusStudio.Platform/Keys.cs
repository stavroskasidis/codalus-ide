using System.Text;

namespace DaedalusStudio.Platform;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,

    /// <summary>⌘ on macOS, the Windows key elsewhere.</summary>
    Meta = 8,
}

/// <summary>One key press with modifiers, e.g. <c>ctrl+shift+p</c>. Keys are normalized lower-case names.</summary>
public readonly record struct KeyChord(KeyModifiers Modifiers, string Key)
{
    private static readonly Dictionary<string, string> s_aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "escape", ["return"] = "enter", ["del"] = "delete", ["ins"] = "insert", ["pgup"] = "pageup",
        ["pgdn"] = "pagedown", ["arrowup"] = "up", ["arrowdown"] = "down", ["arrowleft"] = "left", ["arrowright"] = "right",
        ["plus"] = "=", ["minus"] = "-", ["oem_3"] = "`",
    };

    /// <summary>
    /// Parses <c>mod+mod+key</c>. Modifiers: <c>ctrl</c>, <c>shift</c>, <c>alt</c>/<c>option</c>, <c>meta</c>/<c>cmd</c>/<c>win</c>,
    /// and <c>primary</c>, which is ⌘ on macOS and Ctrl elsewhere.
    /// </summary>
    public static KeyChord Parse(string text, PlatformKind platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var parts = text.Trim().ToLowerInvariant().Split('+');
        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i].Trim() switch
            {
                "ctrl" or "control" => KeyModifiers.Ctrl,
                "shift" => KeyModifiers.Shift,
                "alt" or "option" or "opt" => KeyModifiers.Alt,
                "meta" or "cmd" or "command" or "win" or "super" => KeyModifiers.Meta,
                "primary" or "mod" => platform == PlatformKind.MacOS ? KeyModifiers.Meta : KeyModifiers.Ctrl,
                var unknown => throw new FormatException($"Unknown modifier '{unknown}' in '{text}'."),
            };
        }

        var key = parts[^1].Trim();
        if (key.Length == 0)
        {
            // "ctrl++" style: the key itself is '+'.
            key = text.EndsWith("++", StringComparison.Ordinal) ? "+" : throw new FormatException($"Missing key in '{text}'.");
        }

        return new KeyChord(modifiers, s_aliases.GetValueOrDefault(key, key));
    }

    public string ToLabel(PlatformKind platform)
    {
        var sb = new StringBuilder();
        if (platform == PlatformKind.MacOS)
        {
            // Apple's canonical order: ⌃ ⌥ ⇧ ⌘
            if (Modifiers.HasFlag(KeyModifiers.Ctrl))
            {
                sb.Append('⌃');
            }

            if (Modifiers.HasFlag(KeyModifiers.Alt))
            {
                sb.Append('⌥');
            }

            if (Modifiers.HasFlag(KeyModifiers.Shift))
            {
                sb.Append('⇧');
            }

            if (Modifiers.HasFlag(KeyModifiers.Meta))
            {
                sb.Append('⌘');
            }

            return sb.Append(KeyLabel(Key, mac: true)).ToString();
        }

        foreach (var (flag, name) in new[] { (KeyModifiers.Ctrl, "Ctrl"), (KeyModifiers.Alt, "Alt"), (KeyModifiers.Shift, "Shift"), (KeyModifiers.Meta, platform == PlatformKind.Windows ? "Win" : "Super") })
        {
            if (Modifiers.HasFlag(flag))
            {
                sb.Append(name).Append('+');
            }
        }

        return sb.Append(KeyLabel(Key, mac: false)).ToString();
    }

    /// <summary>The canonical, platform-independent text (used in keybindings.json).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (flag, name) in new[] { (KeyModifiers.Ctrl, "ctrl"), (KeyModifiers.Shift, "shift"), (KeyModifiers.Alt, "alt"), (KeyModifiers.Meta, "meta") })
        {
            if (Modifiers.HasFlag(flag))
            {
                sb.Append(name).Append('+');
            }
        }

        return sb.Append(Key).ToString();
    }

    private static string KeyLabel(string key, bool mac) => key switch
    {
        "enter" => mac ? "↩" : "Enter",
        "escape" => mac ? "⎋" : "Esc",
        "tab" => mac ? "⇥" : "Tab",
        "backspace" => mac ? "⌫" : "Backspace",
        "delete" => mac ? "⌦" : "Delete",
        "up" => "↑",
        "down" => "↓",
        "left" => "←",
        "right" => "→",
        "pageup" => mac ? "⇞" : "PageUp",
        "pagedown" => mac ? "⇟" : "PageDown",
        "home" => mac ? "↖" : "Home",
        "end" => mac ? "↘" : "End",
        "space" => mac ? "Space" : "Space",
        _ when key.Length == 1 => key.ToUpperInvariant(),
        _ when key.StartsWith('f') && int.TryParse(key.AsSpan(1), out _) => key.ToUpperInvariant(),
        _ => char.ToUpperInvariant(key[0]) + key[1..],
    };
}

/// <summary>One or more chords pressed in sequence, e.g. <c>ctrl+k ctrl+s</c>.</summary>
public sealed record KeySequence(IReadOnlyList<KeyChord> Chords)
{
    public static KeySequence Parse(string text, PlatformKind platform) =>
        new([.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => KeyChord.Parse(c, platform))]);

    public string ToLabel(PlatformKind platform) => string.Join(' ', Chords.Select(c => c.ToLabel(platform)));

    public override string ToString() => string.Join(' ', Chords.Select(c => c.ToString()));

    public bool Equals(KeySequence? other) => other is not null && Chords.SequenceEqual(other.Chords);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var chord in Chords)
        {
            hash.Add(chord);
        }

        return hash.ToHashCode();
    }
}
