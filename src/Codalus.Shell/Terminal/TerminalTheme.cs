using Avalonia.Media;
using Codalus.Terminal;

namespace Codalus.Shell.Terminal;

/// <summary>
/// Terminal colours for a shell theme: the 16 ANSI colours (VS Code's defaults per theme), the 6×6×6 cube and the
/// grayscale ramp of the 256-colour palette, and the default foreground and background (the IDE's editor colours).
/// </summary>
public sealed class TerminalTheme
{
    private readonly Color[] _palette = new Color[256];
    private readonly Dictionary<uint, IBrush> _brushes = [];

    private TerminalTheme(uint[] ansi, Color foreground, Color background, Color selection, Color cursor)
    {
        for (var i = 0; i < 16; i++)
        {
            _palette[i] = Color.FromUInt32(0xFF000000 | ansi[i]);
        }

        byte Level(int v) => (byte)(v == 0 ? 0 : 55 + (v * 40));
        for (var i = 0; i < 216; i++)
        {
            _palette[16 + i] = Color.FromRgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
        }

        for (var i = 0; i < 24; i++)
        {
            var gray = (byte)(8 + (i * 10));
            _palette[232 + i] = Color.FromRgb(gray, gray, gray);
        }

        Foreground = foreground;
        Background = background;
        Selection = new SolidColorBrush(selection);
        Cursor = new SolidColorBrush(cursor);
        BackgroundBrush = new SolidColorBrush(background);
    }

    public Color Foreground { get; }

    public Color Background { get; }

    public IBrush BackgroundBrush { get; }

    public IBrush Selection { get; }

    public IBrush Cursor { get; }

    public static TerminalTheme For(string theme, Color? foreground = null, Color? background = null) => theme switch
    {
        "light" or "blue" => new TerminalTheme(
            [0x000000, 0xCD3131, 0x00BC00, 0x949800, 0x0451A5, 0xBC05BC, 0x0598BC, 0x555555, 0x666666, 0xCD3131, 0x14CE14, 0xB5BA00, 0x0451A5, 0xBC05BC, 0x0598BC, 0xA5A5A5],
            foreground ?? Color.FromRgb(0x33, 0x33, 0x33), background ?? Colors.White, Color.FromArgb(0x60, 0x00, 0x78, 0xD4), Color.FromRgb(0x00, 0x00, 0x00)),
        "highContrast" => new TerminalTheme(
            [0x000000, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF, 0x808080, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF],
            foreground ?? Colors.White, background ?? Colors.Black, Color.FromArgb(0x80, 0xF3, 0x8B, 0x00), Colors.White),
        _ => new TerminalTheme(
            [0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5, 0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xE5E5E5],
            foreground ?? Color.FromRgb(0xCC, 0xCC, 0xCC), background ?? Color.FromRgb(0x1E, 0x1E, 0x1E), Color.FromArgb(0x60, 0x26, 0x4F, 0x78), Color.FromRgb(0xAE, 0xAF, 0xAD)),
    };

    /// <summary>The colour of a cell's foreground or background; <paramref name="bold"/> brightens the first eight colours.</summary>
    public Color Resolve(TerminalColor color, bool foreground, bool bold = false)
    {
        if (color.IsDefault)
        {
            return foreground ? Foreground : Background;
        }

        if (color.IsRgb)
        {
            var rgb = color.Rgb;
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        var index = color.PaletteIndex;
        return _palette[foreground && bold && index < 8 ? index + 8 : index];
    }

    public IBrush Brush(Color color)
    {
        var key = color.ToUInt32();
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(color).ToImmutable();
            _brushes[key] = brush;
        }

        return brush;
    }

    public uint ForegroundRgb => Foreground.ToUInt32() & 0xFFFFFF;

    public uint BackgroundRgb => Background.ToUInt32() & 0xFFFFFF;
}
