using System.Text;

namespace Codalus.Terminal;

/// <summary>Cell attributes (SGR).</summary>
[Flags]
public enum CellAttributes : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Invisible = 64,
    Strikethrough = 128,
    DoubleUnderline = 256,
    Overline = 512,
}

/// <summary>
/// A terminal colour: the default colour, one of the 256 palette entries, or a 24-bit RGB value. Encoded in a
/// <see cref="uint"/>: 0 is the default; <c>0x01_0000_ii</c> a palette index; <c>0x02_rrggbb</c> true colour.
/// </summary>
public readonly record struct TerminalColor(uint Value)
{
    public static TerminalColor Default => default;

    public bool IsDefault => Value == 0;

    public bool IsPalette => Value >> 24 == 1;

    public bool IsRgb => Value >> 24 == 2;

    public int PaletteIndex => (int)(Value & 0xFF);

    public uint Rgb => Value & 0xFFFFFF;

    public static TerminalColor Palette(int index) => new(0x0100_0000u | (uint)(index & 0xFF));

    public static TerminalColor FromRgb(byte r, byte g, byte b) => new(0x0200_0000u | ((uint)r << 16) | ((uint)g << 8) | b);
}

/// <summary>
/// One cell of the grid (16 bytes). <see cref="Rune"/> is the character (0 for an empty cell); a wide character takes
/// two cells, the second with <see cref="Width"/> 0. Combining marks and multi-code-point emoji are kept in the
/// row's <see cref="TerminalRow.Extras"/>.
/// </summary>
#pragma warning disable CA1051 // Fields: the grid is millions of cells, accessed by ref in hot loops.
public struct Cell
{
    public int Rune;
    public TerminalColor Foreground;
    public TerminalColor Background;
    public CellAttributes Attributes;
    public byte Width;

    public static Cell Blank(TerminalColor background) => new() { Background = background, Width = 1 };

    public readonly bool IsEmpty => Rune == 0;
}
#pragma warning restore CA1051

/// <summary>A row of the grid. <see cref="Wrapped"/>: the line continues on the next row (soft wrap).</summary>
public sealed class TerminalRow
{
    public TerminalRow(int columns, TerminalColor background = default)
    {
        Cells = new Cell[columns];
        Clear(0, columns, background);
    }

    public Cell[] Cells { get; private set; }

    public bool Wrapped { get; set; }

    /// <summary>Full text of cells whose grapheme has more than one code point (column → text).</summary>
    public Dictionary<int, string>? Extras { get; private set; }

    /// <summary>OSC 8 hyperlinks: columns [start, end) → URI.</summary>
    public List<(int Start, int End, string Uri)>? Links { get; private set; }

    /// <summary>A command mark (OSC 133) on this row, for jumping between commands.</summary>
    public CommandMark? Mark { get; set; }

    public int Columns => Cells.Length;

    public void Clear(int from, int to, TerminalColor background)
    {
        for (var i = Math.Max(0, from); i < Math.Min(to, Cells.Length); i++)
        {
            Cells[i] = Cell.Blank(background);
            Extras?.Remove(i);
        }

        Links?.RemoveAll(l => l.Start >= from && l.End <= to);
    }

    /// <summary>Makes the row blank (for reuse when the scrollback drops it).</summary>
    public void Reset(int columns, TerminalColor background)
    {
        if (Cells.Length != columns)
        {
            Cells = new Cell[columns];
        }

        Array.Fill(Cells, Cell.Blank(background));
        Extras?.Clear();
        Links?.Clear();
        Wrapped = false;
        Mark = null;
    }

    public void Resize(int columns, TerminalColor background)
    {
        if (columns == Cells.Length)
        {
            return;
        }

        var cells = new Cell[columns];
        Array.Copy(Cells, cells, Math.Min(columns, Cells.Length));
        for (var i = Cells.Length; i < columns; i++)
        {
            cells[i] = Cell.Blank(background);
        }

        Cells = cells;
        Extras?.Keys.Where(k => k >= columns).ToList().ForEach(k => Extras.Remove(k));
    }

    public void SetExtra(int column, string? text)
    {
        if (text is null)
        {
            Extras?.Remove(column);
        }
        else
        {
            (Extras ??= [])[column] = text;
        }
    }

    public void AddLink(int start, int end, string uri) => (Links ??= []).Add((start, end, uri));

    /// <summary>The text of a cell (its full grapheme), or null for an empty or continuation cell.</summary>
    public string? TextAt(int column)
    {
        ref var cell = ref Cells[column];
        if (cell.Width == 0 || cell.Rune == 0)
        {
            return null;
        }

        return Extras?.GetValueOrDefault(column) ?? char.ConvertFromUtf32(cell.Rune);
    }

    /// <summary>The row's text, trailing blanks and spaces trimmed (as copying from xterm does); wide characters count once.</summary>
    public string GetText(int from = 0, int to = int.MaxValue)
    {
        var builder = new StringBuilder(Cells.Length);
        to = Math.Min(to, Cells.Length);
        var lastNonBlank = -1;
        for (var i = from; i < to; i++)
        {
            ref var cell = ref Cells[i];
            if (cell.Width == 0)
            {
                continue;
            }

            if (cell.Rune == 0)
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(Extras?.GetValueOrDefault(i) ?? char.ConvertFromUtf32(cell.Rune));
                if (cell.Rune != ' ')
                {
                    lastNonBlank = builder.Length;
                }
            }
        }

        return lastNonBlank < 0 ? string.Empty : builder.ToString(0, lastNonBlank);
    }

    /// <summary>The column of each character of <see cref="GetText"/> (for mapping matches back to cells).</summary>
    public int[] TextColumns()
    {
        var columns = new List<int>(Cells.Length);
        for (var i = 0; i < Cells.Length; i++)
        {
            ref var cell = ref Cells[i];
            if (cell.Width == 0)
            {
                continue;
            }

            var length = cell.Rune == 0 ? 1 : (Extras?.GetValueOrDefault(i)?.Length ?? (cell.Rune > 0xFFFF ? 2 : 1));
            for (var k = 0; k < length; k++)
            {
                columns.Add(i);
            }
        }

        return [.. columns];
    }

    public TerminalRow Clone()
    {
        var copy = new TerminalRow(0) { Cells = (Cell[])Cells.Clone(), Wrapped = Wrapped, Mark = Mark };
        if (Extras is not null)
        {
            copy.Extras = new(Extras);
        }

        if (Links is not null)
        {
            copy.Links = [.. Links];
        }

        return copy;
    }
}

public enum CommandMarkKind
{
    PromptStart,
    CommandStart,
    OutputStart,
    CommandEnd,
}

/// <summary>OSC 133 shell-integration mark: <see cref="ExitCode"/> is set on <see cref="CommandMarkKind.CommandEnd"/>.</summary>
public sealed record CommandMark(CommandMarkKind Kind, int? ExitCode = null);

/// <summary>Unicode display width (wcwidth), compact: wide East Asian and emoji presentation are 2, combining marks 0.</summary>
public static class CharWidth
{
    private static readonly (int From, int To)[] s_wide =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3),
        (0x25FD, 0x25FE), (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1),
        (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA),
        (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B),
        (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x2E80, 0x303E),
        (0x3041, 0x33FF), (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xA000, 0xA4CF), (0xA960, 0xA97F), (0xAC00, 0xD7A3),
        (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE6F), (0xFF00, 0xFF60), (0xFFE0, 0xFFE6), (0x16FE0, 0x16FE4),
        (0x17000, 0x18CFF), (0x1AFF0, 0x1B2FF), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E),
        (0x1F191, 0x1F19A), (0x1F200, 0x1F251), (0x1F300, 0x1F64F), (0x1F680, 0x1F6FF), (0x1F7E0, 0x1F7EB),
        (0x1F90C, 0x1F9FF), (0x1FA70, 0x1FAFF), (0x20000, 0x2FFFD), (0x30000, 0x3FFFD),
    ];

    /// <summary>0 for combining and zero-width characters, 2 for wide ones, 1 otherwise; -1 for controls.</summary>
    public static int Of(int rune)
    {
        if (rune < 0x20 || (rune >= 0x7F && rune < 0xA0))
        {
            return -1;
        }

        if (rune < 0x300)
        {
            return 1;
        }

        if (rune is 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF || (rune >= 0xFE00 && rune <= 0xFE0F) || (rune >= 0xE0100 && rune <= 0xE01EF))
        {
            return 0; // zero-width space, joiners, variation selectors
        }

        var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune);
        if (category is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark or System.Globalization.UnicodeCategory.Format)
        {
            return 0;
        }

        // Emoji skin tone modifiers join the previous emoji.
        if (rune >= 0x1F3FB && rune <= 0x1F3FF)
        {
            return 0;
        }

        var lo = 0;
        var hi = s_wide.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (rune < s_wide[mid].From)
            {
                hi = mid - 1;
            }
            else if (rune > s_wide[mid].To)
            {
                lo = mid + 1;
            }
            else
            {
                return 2;
            }
        }

        return 1;
    }
}
