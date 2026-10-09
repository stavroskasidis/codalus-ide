using System.Globalization;
using System.Text;

namespace DaedalusStudio.Terminal;

/// <summary>Mouse reporting the program asked for (DECSET 9, 1000, 1002, 1003).</summary>
public enum MouseTracking
{
    None,
    X10,
    Normal,
    ButtonEvent,
    AnyEvent,
}

/// <summary>DECSCUSR cursor shapes.</summary>
public enum CursorStyle
{
    Block,
    Underline,
    Bar,
}

/// <summary>
/// An xterm-compatible terminal (ADR 0014): feed it the program's output with <see cref="Feed"/>, read the screen and
/// scrollback under <see cref="SyncRoot"/>. Replies to queries (cursor position, device attributes) come out through
/// <see cref="Reply"/>, to be written back to the program.
/// </summary>
public sealed class VirtualTerminal : IVtHandler
{
    public const int DefaultScrollback = 10_000;

    private static readonly Encoding s_utf8 = new UTF8Encoding(false, throwOnInvalidBytes: false);

    private readonly VtParser _parser;
    private readonly Decoder _decoder = s_utf8.GetDecoder();
    private readonly char[] _chars = new char[8192];
    private readonly List<string> _replies = [];
    private RowBuffer _primary;
    private RowBuffer _alternate;
    private bool[] _tabStops = [];
    private int _scrollback;

    // Pen and cursor.
    private TerminalColor _foreground;
    private TerminalColor _background;
    private CellAttributes _attributes;
    private string? _hyperlink;
    private int _hyperlinkStart = -1;
    private bool _pendingWrap;
    private int _lastPrinted;
    private SavedCursor _savedPrimary;
    private SavedCursor _savedAlternate;

    // Charsets: G0, G1 and which one is invoked.
    private bool _g0Graphics;
    private bool _g1Graphics;
    private bool _shiftOut;
    private char _highSurrogate;

    private readonly record struct SavedCursor(int X, int Y, TerminalColor Foreground, TerminalColor Background, CellAttributes Attributes, bool OriginMode, bool G0Graphics, bool G1Graphics, bool ShiftOut, bool PendingWrap);

    public VirtualTerminal(int columns = 80, int rows = 24, int scrollback = DefaultScrollback)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        _scrollback = Math.Max(0, scrollback);
        _primary = new RowBuffer(Rows + _scrollback, Columns, Rows);
        _alternate = new RowBuffer(Rows, Columns, Rows);
        _parser = new VtParser(this);
        ScrollBottom = Rows - 1;
        ResetTabStops();
    }

    /// <summary>Lock it to read the screen while output may be fed on another thread.</summary>
    public object SyncRoot { get; } = new();

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int CursorX { get; private set; }

    public int CursorY { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    public CursorStyle CursorStyle { get; private set; } = CursorStyle.Block;

    public bool CursorBlink { get; private set; } = true;

    public int ScrollTop { get; private set; }

    public int ScrollBottom { get; private set; }

    public bool IsAlternateScreen { get; private set; }

    // Modes.
    public bool ApplicationCursorKeys { get; private set; }

    public bool ApplicationKeypad { get; private set; }

    public bool AutoWrap { get; private set; } = true;

    public bool OriginMode { get; private set; }

    public bool InsertMode { get; private set; }

    public bool NewLineMode { get; private set; }

    public bool BracketedPaste { get; private set; }

    public bool FocusReporting { get; private set; }

    public bool ReverseVideo { get; private set; }

    public MouseTracking MouseTracking { get; private set; }

    public bool SgrMouse { get; private set; }

    /// <summary>The window title (OSC 0/2).</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The program's working directory from OSC 7 (or VS Code's OSC 633;P), when it reports one.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>Default colours the renderer uses (for OSC 10/11 queries), as 0xRRGGBB.</summary>
    public uint DefaultForegroundRgb { get; set; } = 0xCCCCCC;

    public uint DefaultBackgroundRgb { get; set; } = 0x1E1E1E;

    /// <summary>Increments whenever the content changes (the renderer compares it).</summary>
    public long Version { get; private set; }

    /// <summary>Number of rows that scrolled off into the scrollback since the last <see cref="TakeScrolled"/>.</summary>
    private int _scrolled;

    public int ScrollbackLimit
    {
        get => _scrollback;
        set
        {
            lock (SyncRoot)
            {
                _scrollback = Math.Max(0, value);
                _primary.SetCapacity(Rows + _scrollback);
            }
        }
    }

    /// <summary>Total rows of the active buffer, scrollback included (the screen is the last <see cref="Rows"/>).</summary>
    public int TotalRows => Buffer.Count;

    /// <summary>Rows in the scrollback (0 on the alternate screen).</summary>
    public int ScrollbackRows => Buffer.Count - Rows;

    private RowBuffer Buffer => IsAlternateScreen ? _alternate : _primary;

    /// <summary>A row of the active buffer: 0 is the oldest scrollback row.</summary>
    public TerminalRow GetRow(int index) => Buffer[index];

    /// <summary>A row of the screen (0 is the top).</summary>
    public TerminalRow ScreenRow(int y) => Buffer[Buffer.Count - Rows + y];

    /// <summary>Text to send back to the program (query replies). Raised after <see cref="Feed"/> returns.</summary>
    public event Action<string>? Reply;

    public event Action? Bell;

    public event Action? TitleChanged;

    public event Action? WorkingDirectoryChanged;

    /// <summary>An OSC 133 / 633 shell-integration mark arrived (on the cursor row).</summary>
    public event Action<CommandMark>? MarkReceived;

    /// <summary>Content changed (raised after <see cref="Feed"/>); the renderer schedules a frame.</summary>
    public event Action? Changed;

    /// <summary>Returns and resets how many rows scrolled into the scrollback since the last call.</summary>
    public int TakeScrolled()
    {
        lock (SyncRoot)
        {
            var scrolled = _scrolled;
            _scrolled = 0;
            return scrolled;
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>Parses UTF-8 output of the program.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        lock (SyncRoot)
        {
            while (data.Length > 0)
            {
                var take = Math.Min(data.Length, _chars.Length / 2);
                var count = _decoder.GetChars(data[..take], _chars, flush: false);
                data = data[take..];
                FeedChars(_chars.AsSpan(0, count));
            }

            Version++;
        }

        RaiseAfterFeed();
    }

    /// <summary>Parses text (tests and echo).</summary>
    public void Feed(string text)
    {
        lock (SyncRoot)
        {
            FeedChars(text.AsSpan());
            Version++;
        }

        RaiseAfterFeed();
    }

    private void FeedChars(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (char.IsHighSurrogate(c))
            {
                _highSurrogate = c;
                continue;
            }

            if (char.IsLowSurrogate(c) && _highSurrogate != 0)
            {
                _parser.Feed(char.ConvertToUtf32(_highSurrogate, c));
                _highSurrogate = '\0';
                continue;
            }

            _highSurrogate = '\0';
            _parser.Feed(c);
        }
    }

    private void RaiseAfterFeed()
    {
        string[] replies;
        lock (SyncRoot)
        {
            replies = [.. _replies];
            _replies.Clear();
        }

        foreach (var reply in replies)
        {
            Reply?.Invoke(reply);
        }

        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ resizing

    /// <summary>
    /// Changes the size. Rows: shrinking moves the top rows into the scrollback (keeping the cursor visible), growing
    /// pulls rows back. Columns: rows are cut or extended (no reflow).
    /// </summary>
    public void Resize(int columns, int rows)
    {
        lock (SyncRoot)
        {
            columns = Math.Max(2, columns);
            rows = Math.Max(1, rows);
            if (columns == Columns && rows == Rows)
            {
                return;
            }

            ResizeBuffer(_primary, columns, rows, primary: true);
            ResizeBuffer(_alternate, columns, rows, primary: false);
            Columns = columns;
            Rows = rows;
            _primary.SetCapacity(Math.Max(_primary.Count, rows + _scrollback));
            _primary.SetCapacity(rows + _scrollback);
            ScrollTop = 0;
            ScrollBottom = rows - 1;
            CursorX = Math.Min(CursorX, columns - 1);
            CursorY = Math.Clamp(CursorY, 0, rows - 1);
            _pendingWrap = false;
            ResetTabStops();
            Version++;
        }

        Changed?.Invoke();
    }

    private void ResizeBuffer(RowBuffer buffer, int columns, int rows, bool primary)
    {
        for (var i = 0; i < buffer.Count; i++)
        {
            buffer[i].Resize(columns, default);
        }

        var active = primary != IsAlternateScreen;
        if (rows < Rows)
        {
            // Rows below the cursor go away first; then the top rows scroll into the scrollback.
            var remove = Rows - rows;
            var below = active ? Math.Min(remove, Rows - 1 - CursorY) : remove;
            for (var i = 0; i < below; i++)
            {
                buffer.Pop();
            }

            if (active)
            {
                CursorY -= remove - below;
            }

            if (!primary)
            {
                buffer.TrimTo(rows);
            }
        }
        else if (rows > Rows)
        {
            var add = rows - Rows;
            var fromScrollback = primary ? Math.Min(add, buffer.Count - Rows) : 0;
            if (active)
            {
                CursorY += fromScrollback;
            }

            if (!primary)
            {
                buffer.SetCapacity(rows);
            }

            for (var i = 0; i < add - fromScrollback; i++)
            {
                buffer.Push(new TerminalRow(columns));
            }
        }
    }

    // ------------------------------------------------------------------ IVtHandler

    void IVtHandler.Print(int rune)
    {
        if ((_shiftOut ? _g1Graphics : _g0Graphics) && rune is >= 0x5F and <= 0x7E)
        {
            rune = DecSpecialGraphics(rune);
        }

        var width = CharWidth.Of(rune);
        if (width < 0)
        {
            return;
        }

        if (width == 0)
        {
            Combine(rune);
            return;
        }

        if (_pendingWrap && AutoWrap)
        {
            ScreenRow(CursorY).Wrapped = true;
            CursorX = 0;
            LineFeed();
        }

        _pendingWrap = false;
        if (width == 2 && CursorX == Columns - 1)
        {
            if (!AutoWrap)
            {
                return;
            }

            ScreenRow(CursorY).Cells[CursorX] = Cell.Blank(_background);
            ScreenRow(CursorY).Wrapped = true;
            CursorX = 0;
            LineFeed();
        }

        var row = ScreenRow(CursorY);
        if (InsertMode)
        {
            ShiftRight(row, CursorX, width);
        }

        ClearWideAt(row, CursorX);
        if (width == 2)
        {
            ClearWideAt(row, CursorX + 1);
        }

        row.Cells[CursorX] = new Cell { Rune = rune, Foreground = _foreground, Background = _background, Attributes = _attributes, Width = (byte)width };
        row.SetExtra(CursorX, null);
        if (width == 2)
        {
            row.Cells[CursorX + 1] = new Cell { Rune = 0, Foreground = _foreground, Background = _background, Attributes = _attributes, Width = 0 };
            row.SetExtra(CursorX + 1, null);
        }

        _lastPrinted = rune;
        CursorX += width;
        if (CursorX >= Columns)
        {
            CursorX = Columns - 1;
            _pendingWrap = AutoWrap;
        }
    }

    /// <summary>A zero-width character joins the previous cell's grapheme.</summary>
    private void Combine(int rune)
    {
        var row = ScreenRow(CursorY);
        var x = _pendingWrap ? CursorX : CursorX - 1;
        if (x < 0)
        {
            return;
        }

        if (row.Cells[x].Width == 0 && x > 0)
        {
            x--; // the continuation of a wide character
        }

        if (row.Cells[x].Rune == 0)
        {
            return;
        }

        var text = row.Extras?.GetValueOrDefault(x) ?? char.ConvertFromUtf32(row.Cells[x].Rune);
        row.SetExtra(x, text + char.ConvertFromUtf32(rune));

        // VS16 turns a text-style symbol into a two-cell emoji.
        if (rune == 0xFE0F && row.Cells[x].Width == 1 && x + 1 < Columns && CharWidth.Of(row.Cells[x].Rune) == 1 && row.Cells[x].Rune >= 0x2000)
        {
            row.Cells[x].Width = 2;
            row.Cells[x + 1] = new Cell { Width = 0, Foreground = row.Cells[x].Foreground, Background = row.Cells[x].Background, Attributes = row.Cells[x].Attributes };
            if (!_pendingWrap)
            {
                CursorX = Math.Min(CursorX + 1, Columns - 1);
                if (x + 2 >= Columns)
                {
                    _pendingWrap = AutoWrap;
                }
            }
        }
    }

    /// <summary>Overwriting either half of a wide character blanks the other half.</summary>
    private void ClearWideAt(TerminalRow row, int x)
    {
        if (x >= Columns)
        {
            return;
        }

        if (row.Cells[x].Width == 0 && x > 0 && row.Cells[x - 1].Width == 2)
        {
            row.Cells[x - 1] = Cell.Blank(row.Cells[x - 1].Background);
            row.SetExtra(x - 1, null);
        }
        else if (row.Cells[x].Width == 2 && x + 1 < Columns)
        {
            row.Cells[x + 1] = Cell.Blank(row.Cells[x + 1].Background);
        }
    }

    void IVtHandler.Execute(int control)
    {
        switch (control)
        {
            case 0x07:
                Bell?.Invoke();
                break;
            case 0x08: // BS
                if (_pendingWrap)
                {
                    _pendingWrap = false;
                }
                else if (CursorX > 0)
                {
                    CursorX--;
                }

                break;
            case 0x09: // HT
                CursorX = NextTabStop(CursorX);
                _pendingWrap = false;
                break;
            case 0x0A: // LF
            case 0x0B: // VT
            case 0x0C: // FF
                LineFeed();
                if (NewLineMode)
                {
                    CursorX = 0;
                }

                _pendingWrap = false;
                break;
            case 0x0D: // CR
                CursorX = 0;
                _pendingWrap = false;
                break;
            case 0x0E: // SO
                _shiftOut = true;
                break;
            case 0x0F: // SI
                _shiftOut = false;
                break;
        }
    }

    void IVtHandler.EscDispatch(string intermediates, char final)
    {
        switch (intermediates, final)
        {
            case ("", '7'):
                SaveCursor();
                break;
            case ("", '8'):
                RestoreCursor();
                break;
            case ("", 'D'): // IND
                LineFeed();
                break;
            case ("", 'E'): // NEL
                CursorX = 0;
                LineFeed();
                break;
            case ("", 'H'): // HTS
                _tabStops[CursorX] = true;
                break;
            case ("", 'M'): // RI
                ReverseIndex();
                break;
            case ("", 'c'): // RIS
                FullReset();
                break;
            case ("", '='):
                ApplicationKeypad = true;
                break;
            case ("", '>'):
                ApplicationKeypad = false;
                break;
            case ("(", var set):
                _g0Graphics = set == '0';
                break;
            case (")", var set):
                _g1Graphics = set == '0';
                break;
            case ("#", '8'): // DECALN
                for (var y = 0; y < Rows; y++)
                {
                    var row = ScreenRow(y);
                    for (var x = 0; x < Columns; x++)
                    {
                        row.Cells[x] = new Cell { Rune = 'E', Width = 1 };
                    }
                }

                break;
        }

        _pendingWrap = false;
    }

    void IVtHandler.CsiDispatch(ReadOnlySpan<int> p, ReadOnlySpan<bool> sub, char marker, string intermediates, char final)
    {

        if (intermediates.Length > 0)
        {
            switch (intermediates, final)
            {
                case (" ", 'q'): // DECSCUSR
                    var style = Param0(p, 0);
                    CursorStyle = style switch { 3 or 4 => CursorStyle.Underline, 5 or 6 => CursorStyle.Bar, _ => CursorStyle.Block };
                    CursorBlink = style is 0 or 1 or 3 or 5;
                    break;
                case ("!", 'p'): // DECSTR
                    SoftReset();
                    break;
            }

            return;
        }

        if (marker is '?')
        {
            switch (final)
            {
                case 'h':
                    for (var i = 0; i < p.Length; i++)
                    {
                        SetPrivateMode(p[i], true);
                    }

                    break;
                case 'l':
                    for (var i = 0; i < p.Length; i++)
                    {
                        SetPrivateMode(p[i], false);
                    }

                    break;
                case 'n' when Param0(p, 0) == 6: // DECXCPR
                    _replies.Add($"\x1b[?{CursorY + 1};{CursorX + 1}R");
                    break;
            }

            return;
        }

        if (marker is '>' or '=' or '<')
        {
            if (marker == '>' && final == 'c')
            {
                _replies.Add("\x1b[>0;10;1c"); // secondary device attributes
            }

            return;
        }

        switch (final)
        {
            case '@': // ICH
                ShiftRight(ScreenRow(CursorY), CursorX, Param(p, 0));
                break;
            case 'A': // CUU
                CursorY = Math.Max(CursorY < ScrollTop ? 0 : ScrollTop, CursorY - Param(p, 0));
                break;
            case 'B': // CUD
            case 'e': // VPR
                CursorY = Math.Min(CursorY > ScrollBottom ? Rows - 1 : ScrollBottom, CursorY + Param(p, 0));
                break;
            case 'C': // CUF
            case 'a': // HPR
                CursorX = Math.Min(Columns - 1, CursorX + Param(p, 0));
                break;
            case 'D': // CUB
                CursorX = Math.Max(0, CursorX - Param(p, 0));
                break;
            case 'E': // CNL
                CursorY = Math.Min(ScrollBottom, CursorY + Param(p, 0));
                CursorX = 0;
                break;
            case 'F': // CPL
                CursorY = Math.Max(ScrollTop, CursorY - Param(p, 0));
                CursorX = 0;
                break;
            case 'G': // CHA
            case '`': // HPA
                CursorX = Math.Clamp(Param(p, 0) - 1, 0, Columns - 1);
                break;
            case 'H': // CUP
            case 'f': // HVP
                MoveTo(Param(p, 1) - 1, Param(p, 0) - 1);
                break;
            case 'I': // CHT
                for (var i = 0; i < Param(p, 0); i++)
                {
                    CursorX = NextTabStop(CursorX);
                }

                break;
            case 'J':
                EraseInDisplay(Param0(p, 0));
                break;
            case 'K':
                EraseInLine(Param0(p, 0));
                break;
            case 'L': // IL
                InsertLines(Param(p, 0));
                break;
            case 'M': // DL
                DeleteLines(Param(p, 0));
                break;
            case 'P': // DCH
                DeleteChars(Param(p, 0));
                break;
            case 'S': // SU
                ScrollUp(Param(p, 0));
                break;
            case 'T' when p.Length <= 1: // SD
                ScrollDown(Param(p, 0));
                break;
            case 'X': // ECH
                ScreenRow(CursorY).Clear(CursorX, CursorX + Param(p, 0), _background);
                break;
            case 'Z': // CBT
                for (var i = 0; i < Param(p, 0); i++)
                {
                    CursorX = PreviousTabStop(CursorX);
                }

                break;
            case 'b': // REP
                if (_lastPrinted != 0)
                {
                    for (var i = 0; i < Math.Min(Param(p, 0), Columns * Rows); i++)
                    {
                        ((IVtHandler)this).Print(_lastPrinted);
                    }
                }

                break;
            case 'c' when Param0(p, 0) == 0: // DA1: VT220 with ANSI colour
                _replies.Add("\x1b[?62;22c");
                break;
            case 'd': // VPA
                MoveTo(CursorX, Param(p, 0) - 1);
                break;
            case 'g': // TBC
                if (Param0(p, 0) == 0)
                {
                    _tabStops[CursorX] = false;
                }
                else if (Param0(p, 0) == 3)
                {
                    Array.Clear(_tabStops);
                }

                break;
            case 'h':
            case 'l':
                for (var i = 0; i < p.Length; i++)
                {
                    switch (p[i])
                    {
                        case 4:
                            InsertMode = final == 'h';
                            break;
                        case 20:
                            NewLineMode = final == 'h';
                            break;
                    }
                }

                break;
            case 'm':
                SelectGraphicRendition(p, sub);
                break;
            case 'n':
                if (Param0(p, 0) == 5)
                {
                    _replies.Add("\x1b[0n");
                }
                else if (Param0(p, 0) == 6)
                {
                    var y = OriginMode ? CursorY - ScrollTop : CursorY;
                    _replies.Add($"\x1b[{y + 1};{CursorX + 1}R");
                }

                break;
            case 'r': // DECSTBM
                var top = Param(p, 0) - 1;
                var bottom = Param(p, 1, Rows) - 1;
                if (bottom > top && bottom < Rows)
                {
                    ScrollTop = top;
                    ScrollBottom = bottom;
                    MoveTo(0, 0);
                }

                break;
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
            case 't':
                if (Param0(p, 0) == 18)
                {
                    _replies.Add($"\x1b[8;{Rows};{Columns}t");
                }

                break;
        }

        if (final is not 'b' and not 'm')
        {
            _pendingWrap = false;
        }
    }

    private static int Param(ReadOnlySpan<int> p, int i, int fallback = 1) => i < p.Length && p[i] > 0 ? p[i] : fallback;

    private static int Param0(ReadOnlySpan<int> p, int i) => i < p.Length && p[i] >= 0 ? p[i] : 0;

    void IVtHandler.OscDispatch(string data)
    {
        var separator = data.IndexOf(';', StringComparison.Ordinal);
        var command = separator < 0 ? data : data[..separator];
        var argument = separator < 0 ? string.Empty : data[(separator + 1)..];
        switch (command)
        {
            case "0":
            case "2":
                Title = argument;
                TitleChanged?.Invoke();
                break;
            case "7":
                SetWorkingDirectory(UriToPath(argument));
                break;
            case "8":
                SetHyperlink(argument);
                break;
            case "10" when argument == "?":
                _replies.Add($"\x1b]10;{ColorSpec(DefaultForegroundRgb)}\x1b\\");
                break;
            case "11" when argument == "?":
                _replies.Add($"\x1b]11;{ColorSpec(DefaultBackgroundRgb)}\x1b\\");
                break;
            case "133":
                Mark(argument);
                break;
            case "633": // VS Code's shell integration, which some users' profiles already emit
                if (argument.StartsWith("P;Cwd=", StringComparison.Ordinal))
                {
                    SetWorkingDirectory(argument[6..]);
                }
                else if (argument.Length > 0 && argument[0] is 'A' or 'B' or 'C' or 'D')
                {
                    Mark(argument);
                }

                break;
        }
    }

    // ------------------------------------------------------------------ OSC helpers

    private void SetWorkingDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == WorkingDirectory)
        {
            return;
        }

        WorkingDirectory = path;
        WorkingDirectoryChanged?.Invoke();
    }

    /// <summary><c>file://host/path</c> → a local path (Windows drive letters keep their colon).</summary>
    internal static string? UriToPath(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = uri[7..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var path = slash < 0 ? "/" : Uri.UnescapeDataString(rest[slash..]);
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
        {
            path = path[1..].Replace('/', '\\');
        }

        return path;
    }

    private void SetHyperlink(string argument)
    {
        // OSC 8 ; params ; uri — an empty uri ends the link.
        var separator = argument.IndexOf(';', StringComparison.Ordinal);
        var uri = separator < 0 ? string.Empty : argument[(separator + 1)..];
        EndHyperlink();
        if (uri.Length > 0)
        {
            _hyperlink = uri;
            _hyperlinkStart = CursorX;
        }
    }

    private void EndHyperlink()
    {
        if (_hyperlink is not null && CursorX > _hyperlinkStart)
        {
            ScreenRow(CursorY).AddLink(_hyperlinkStart, _pendingWrap ? CursorX + 1 : CursorX, _hyperlink);
        }

        _hyperlink = null;
        _hyperlinkStart = -1;
    }

    private void Mark(string argument)
    {
        var parts = argument.Split(';');
        CommandMark? mark = parts[0] switch
        {
            "A" => new CommandMark(CommandMarkKind.PromptStart),
            "B" => new CommandMark(CommandMarkKind.CommandStart),
            "C" => new CommandMark(CommandMarkKind.OutputStart),
            "D" => new CommandMark(CommandMarkKind.CommandEnd, parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null),
            _ => null,
        };
        if (mark is null)
        {
            return;
        }

        if (mark.Kind == CommandMarkKind.PromptStart)
        {
            ScreenRow(CursorY).Mark = mark;
        }

        MarkReceived?.Invoke(mark);
    }

    private static string ColorSpec(uint rgb) =>
        $"rgb:{(rgb >> 16) & 0xFF:x2}{(rgb >> 16) & 0xFF:x2}/{(rgb >> 8) & 0xFF:x2}{(rgb >> 8) & 0xFF:x2}/{rgb & 0xFF:x2}{rgb & 0xFF:x2}";

    // ------------------------------------------------------------------ modes

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = on;
                break;
            case 5:
                ReverseVideo = on;
                break;
            case 6:
                OriginMode = on;
                MoveTo(0, 0);
                break;
            case 7:
                AutoWrap = on;
                break;
            case 9:
                MouseTracking = on ? MouseTracking.X10 : MouseTracking.None;
                break;
            case 12:
                CursorBlink = on;
                break;
            case 25:
                CursorVisible = on;
                break;
            case 47:
            case 1047:
                SwitchScreen(on, saveCursor: false, clear: mode == 1047 && !on);
                break;
            case 1049:
                SwitchScreen(on, saveCursor: true, clear: on);
                break;
            case 1000:
                MouseTracking = on ? MouseTracking.Normal : MouseTracking.None;
                break;
            case 1002:
                MouseTracking = on ? MouseTracking.ButtonEvent : MouseTracking.None;
                break;
            case 1003:
                MouseTracking = on ? MouseTracking.AnyEvent : MouseTracking.None;
                break;
            case 1004:
                FocusReporting = on;
                break;
            case 1006:
                SgrMouse = on;
                break;
            case 2004:
                BracketedPaste = on;
                break;
        }
    }

    private void SwitchScreen(bool alternate, bool saveCursor, bool clear)
    {
        if (alternate == IsAlternateScreen)
        {
            return;
        }

        if (alternate)
        {
            if (saveCursor)
            {
                SaveCursor();
            }

            IsAlternateScreen = true;
            if (clear)
            {
                for (var y = 0; y < Rows; y++)
                {
                    ScreenRow(y).Clear(0, Columns, _background);
                    ScreenRow(y).Wrapped = false;
                }
            }
        }
        else
        {
            if (clear)
            {
                for (var y = 0; y < Rows; y++)
                {
                    ScreenRow(y).Clear(0, Columns, default);
                }
            }

            IsAlternateScreen = false;
            if (saveCursor)
            {
                RestoreCursor();
            }
        }

        _pendingWrap = false;
    }

    private void SaveCursor()
    {
        var saved = new SavedCursor(CursorX, CursorY, _foreground, _background, _attributes, OriginMode, _g0Graphics, _g1Graphics, _shiftOut, _pendingWrap);
        if (IsAlternateScreen)
        {
            _savedAlternate = saved;
        }
        else
        {
            _savedPrimary = saved;
        }
    }

    private void RestoreCursor()
    {
        var saved = IsAlternateScreen ? _savedAlternate : _savedPrimary;
        CursorX = Math.Min(saved.X, Columns - 1);
        CursorY = Math.Min(saved.Y, Rows - 1);
        _foreground = saved.Foreground;
        _background = saved.Background;
        _attributes = saved.Attributes;
        OriginMode = saved.OriginMode;
        _g0Graphics = saved.G0Graphics;
        _g1Graphics = saved.G1Graphics;
        _shiftOut = saved.ShiftOut;
        _pendingWrap = saved.PendingWrap;
    }

    private void SoftReset()
    {
        CursorVisible = true;
        InsertMode = false;
        OriginMode = false;
        AutoWrap = true;
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        _foreground = default;
        _background = default;
        _attributes = CellAttributes.None;
        _g0Graphics = _g1Graphics = _shiftOut = false;
        _savedPrimary = _savedAlternate = default;
    }

    private void FullReset()
    {
        SwitchScreen(false, saveCursor: false, clear: false);
        SoftReset();
        MouseTracking = MouseTracking.None;
        SgrMouse = false;
        BracketedPaste = false;
        FocusReporting = false;
        NewLineMode = false;
        ReverseVideo = false;
        Title = string.Empty;
        _primary = new RowBuffer(Rows + _scrollback, Columns, Rows);
        _alternate = new RowBuffer(Rows, Columns, Rows);
        CursorX = CursorY = 0;
        _pendingWrap = false;
        ResetTabStops();
    }

    // ------------------------------------------------------------------ SGR

    private void SelectGraphicRendition(ReadOnlySpan<int> p, ReadOnlySpan<bool> sub)
    {
        if (p.Length == 0)
        {
            ResetPen();
            return;
        }

        for (var i = 0; i < p.Length; i++)
        {
            var code = Math.Max(p[i], 0);
            switch (code)
            {
                case 0:
                    ResetPen();
                    break;
                case 1:
                    _attributes |= CellAttributes.Bold;
                    break;
                case 2:
                    _attributes |= CellAttributes.Dim;
                    break;
                case 3:
                    _attributes |= CellAttributes.Italic;
                    break;
                case 4:
                    // 4:0 none, 4:1 single, 4:2 double, 4:3 curly (shown single)…
                    var style = i + 1 < p.Length && sub[i + 1] ? Math.Max(p[++i], 0) : 1;
                    _attributes &= ~(CellAttributes.Underline | CellAttributes.DoubleUnderline);
                    if (style == 2)
                    {
                        _attributes |= CellAttributes.DoubleUnderline;
                    }
                    else if (style != 0)
                    {
                        _attributes |= CellAttributes.Underline;
                    }

                    break;
                case 5:
                case 6:
                    _attributes |= CellAttributes.Blink;
                    break;
                case 7:
                    _attributes |= CellAttributes.Inverse;
                    break;
                case 8:
                    _attributes |= CellAttributes.Invisible;
                    break;
                case 9:
                    _attributes |= CellAttributes.Strikethrough;
                    break;
                case 21:
                    _attributes |= CellAttributes.DoubleUnderline;
                    break;
                case 22:
                    _attributes &= ~(CellAttributes.Bold | CellAttributes.Dim);
                    break;
                case 23:
                    _attributes &= ~CellAttributes.Italic;
                    break;
                case 24:
                    _attributes &= ~(CellAttributes.Underline | CellAttributes.DoubleUnderline);
                    break;
                case 25:
                    _attributes &= ~CellAttributes.Blink;
                    break;
                case 27:
                    _attributes &= ~CellAttributes.Inverse;
                    break;
                case 28:
                    _attributes &= ~CellAttributes.Invisible;
                    break;
                case 29:
                    _attributes &= ~CellAttributes.Strikethrough;
                    break;
                case >= 30 and <= 37:
                    _foreground = TerminalColor.Palette(code - 30);
                    break;
                case 38:
                    _foreground = ExtendedColor(p, sub, ref i) ?? _foreground;
                    break;
                case 39:
                    _foreground = default;
                    break;
                case >= 40 and <= 47:
                    _background = TerminalColor.Palette(code - 40);
                    break;
                case 48:
                    _background = ExtendedColor(p, sub, ref i) ?? _background;
                    break;
                case 49:
                    _background = default;
                    break;
                case 53:
                    _attributes |= CellAttributes.Overline;
                    break;
                case 55:
                    _attributes &= ~CellAttributes.Overline;
                    break;
                case 58:
                    _ = ExtendedColor(p, sub, ref i); // underline colour: parsed, not shown
                    break;
                case >= 90 and <= 97:
                    _foreground = TerminalColor.Palette(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _background = TerminalColor.Palette(code - 100 + 8);
                    break;
            }
        }
    }

    /// <summary>38/48: <c>;5;n</c>, <c>;2;r;g;b</c>, and the colon forms <c>:5:n</c>, <c>:2::r:g:b</c>, <c>:2:r:g:b</c>.</summary>
    private static TerminalColor? ExtendedColor(ReadOnlySpan<int> p, ReadOnlySpan<bool> sub, ref int i)
    {
        if (i + 1 >= p.Length)
        {
            return null;
        }

        var colon = sub[i + 1];
        var kind = p[i + 1];
        if (kind == 5 && i + 2 < p.Length)
        {
            var index = Math.Clamp(p[i + 2], 0, 255);
            i += 2;
            return TerminalColor.Palette(index);
        }

        if (kind == 2)
        {
            // The colon form may carry a colour-space id: 2::r:g:b or 2:id:r:g:b.
            var start = i + 2;
            var remaining = 0;
            for (var k = start; k < p.Length && (!colon || sub[k]); k++)
            {
                remaining++;
            }

            if (colon && remaining >= 4)
            {
                start++;
            }

            if (start + 2 < p.Length)
            {
                var color = TerminalColor.FromRgb((byte)Math.Clamp(p[start], 0, 255), (byte)Math.Clamp(p[start + 1], 0, 255), (byte)Math.Clamp(p[start + 2], 0, 255));
                i = start + 2;
                return color;
            }
        }

        i = p.Length;
        return null;
    }

    private void ResetPen()
    {
        _foreground = default;
        _background = default;
        _attributes = CellAttributes.None;
    }

    // ------------------------------------------------------------------ cursor and scrolling

    private void MoveTo(int x, int y)
    {
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = OriginMode ? Math.Clamp(ScrollTop + y, ScrollTop, ScrollBottom) : Math.Clamp(y, 0, Rows - 1);

        _pendingWrap = false;
    }

    private void LineFeed()
    {
        if (_hyperlink is not null)
        {
            EndHyperlink();
        }

        if (CursorY == ScrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorY < Rows - 1)
        {
            CursorY++;
        }
    }

    private void ReverseIndex()
    {
        if (CursorY == ScrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorY > 0)
        {
            CursorY--;
        }
    }

    /// <summary>Scrolls the region up; on the primary screen with a full-screen region, rows go into the scrollback.</summary>
    private void ScrollUp(int count)
    {
        count = Math.Min(count, ScrollBottom - ScrollTop + 1);
        var buffer = Buffer;
        var screenStart = buffer.Count - Rows;
        if (!IsAlternateScreen && ScrollTop == 0 && ScrollBottom == Rows - 1)
        {
            for (var i = 0; i < count; i++)
            {
                if (_scrollback == 0)
                {
                    // No scrollback: rotate in place.
                    var top = buffer[screenStart];
                    for (var y = 0; y < Rows - 1; y++)
                    {
                        buffer[screenStart + y] = buffer[screenStart + y + 1];
                    }

                    top.Clear(0, Columns, _background);
                    top.Wrapped = false;
                    top.Mark = null;
                    buffer[screenStart + Rows - 1] = top;
                }
                else
                {
                    // A full scrollback drops its oldest row: reuse it for the new one (no allocation per line).
                    if (buffer.Count == buffer.Capacity)
                    {
                        var recycled = buffer[0];
                        recycled.Reset(Columns, _background);
                        buffer.Push(recycled);
                    }
                    else
                    {
                        buffer.Push(new TerminalRow(Columns, _background));
                    }

                    _scrolled++;
                }
            }

            return;
        }

        for (var i = 0; i < count; i++)
        {
            var top = buffer[screenStart + ScrollTop];
            for (var y = ScrollTop; y < ScrollBottom; y++)
            {
                buffer[screenStart + y] = buffer[screenStart + y + 1];
            }

            top.Clear(0, Columns, _background);
            top.Wrapped = false;
            top.Mark = null;
            buffer[screenStart + ScrollBottom] = top;
        }
    }

    private void ScrollDown(int count)
    {
        count = Math.Min(count, ScrollBottom - ScrollTop + 1);
        var buffer = Buffer;
        var screenStart = buffer.Count - Rows;
        for (var i = 0; i < count; i++)
        {
            var bottom = buffer[screenStart + ScrollBottom];
            for (var y = ScrollBottom; y > ScrollTop; y--)
            {
                buffer[screenStart + y] = buffer[screenStart + y - 1];
            }

            bottom.Clear(0, Columns, _background);
            bottom.Wrapped = false;
            bottom.Mark = null;
            buffer[screenStart + ScrollTop] = bottom;
        }
    }

    private void InsertLines(int count)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
        {
            return;
        }

        var top = ScrollTop;
        ScrollTop = CursorY;
        ScrollDown(count);
        ScrollTop = top;
        CursorX = 0;
    }

    private void DeleteLines(int count)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom)
        {
            return;
        }

        var top = ScrollTop;
        ScrollTop = CursorY;
        var buffer = Buffer;
        var screenStart = buffer.Count - Rows;
        count = Math.Min(count, ScrollBottom - ScrollTop + 1);
        for (var i = 0; i < count; i++)
        {
            var removed = buffer[screenStart + ScrollTop];
            for (var y = ScrollTop; y < ScrollBottom; y++)
            {
                buffer[screenStart + y] = buffer[screenStart + y + 1];
            }

            removed.Clear(0, Columns, _background);
            removed.Wrapped = false;
            buffer[screenStart + ScrollBottom] = removed;
        }

        ScrollTop = top;
        CursorX = 0;
    }

    private void ShiftRight(TerminalRow row, int x, int count)
    {
        count = Math.Min(count, Columns - x);
        Array.Copy(row.Cells, x, row.Cells, x + count, Columns - x - count);
        row.Clear(x, x + count, _background);
    }

    private void DeleteChars(int count)
    {
        var row = ScreenRow(CursorY);
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(row.Cells, CursorX + count, row.Cells, CursorX, Columns - CursorX - count);
        row.Clear(Columns - count, Columns, _background);
    }

    private void EraseInLine(int mode)
    {
        var row = ScreenRow(CursorY);
        switch (mode)
        {
            case 0:
                row.Clear(CursorX, Columns, _background);
                row.Wrapped = false;
                break;
            case 1:
                row.Clear(0, CursorX + 1, _background);
                break;
            case 2:
                row.Clear(0, Columns, _background);
                row.Wrapped = false;
                break;
        }
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var y = CursorY + 1; y < Rows; y++)
                {
                    ScreenRow(y).Clear(0, Columns, _background);
                    ScreenRow(y).Wrapped = false;
                }

                break;
            case 1:
                EraseInLine(1);
                for (var y = 0; y < CursorY; y++)
                {
                    ScreenRow(y).Clear(0, Columns, _background);
                }

                break;
            case 2:
                for (var y = 0; y < Rows; y++)
                {
                    ScreenRow(y).Clear(0, Columns, _background);
                    ScreenRow(y).Wrapped = false;
                }

                break;
            case 3:
                // Clear the scrollback (xterm's E3), keeping the screen.
                Buffer.TrimTo(Rows);
                break;
        }
    }

    /// <summary>Clears the scrollback and the screen, keeping the cursor row on top (Clear Terminal).</summary>
    public void Clear()
    {
        lock (SyncRoot)
        {
            var cursorRow = ScreenRow(CursorY).Clone();
            Buffer.TrimTo(Rows);
            for (var y = 0; y < Rows; y++)
            {
                ScreenRow(y).Clear(0, Columns, default);
                ScreenRow(y).Wrapped = false;
                ScreenRow(y).Mark = null;
            }

            Buffer[^Rows] = cursorRow;
            CursorY = 0;
            Version++;
        }

        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ tabs and charsets

    private void ResetTabStops()
    {
        _tabStops = new bool[Columns];
        for (var x = 8; x < Columns; x += 8)
        {
            _tabStops[x] = true;
        }
    }

    private int NextTabStop(int x)
    {
        for (var i = x + 1; i < Columns; i++)
        {
            if (_tabStops[i])
            {
                return i;
            }
        }

        return Columns - 1;
    }

    private int PreviousTabStop(int x)
    {
        for (var i = x - 1; i > 0; i--)
        {
            if (_tabStops[i])
            {
                return i;
            }
        }

        return 0;
    }

    private static readonly string s_decGraphics = "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    /// <summary>DEC Special Graphics (line drawing) for <c>0x5F</c>–<c>0x7E</c>.</summary>
    private static int DecSpecialGraphics(int rune) => rune == 0x5F ? 0x00A0 : s_decGraphics[rune - 0x60];

    // ------------------------------------------------------------------ text

    /// <summary>
    /// Text of rows <paramref name="from"/>..<paramref name="to"/> (buffer indices, inclusive) with columns cut on the first
    /// and last row; wrapped rows join without a line break.
    /// </summary>
    public string GetText(int from, int fromColumn, int to, int toColumn)
    {
        lock (SyncRoot)
        {
            var builder = new StringBuilder();
            for (var y = Math.Max(0, from); y <= Math.Min(to, TotalRows - 1); y++)
            {
                var row = GetRow(y);
                var start = y == from ? fromColumn : 0;
                var end = y == to ? toColumn : Columns;
                builder.Append(row.GetText(start, end));
                if (y < to && !row.Wrapped)
                {
                    builder.Append('\n');
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>The screen's text, one line per row (tests, the accessibility peer).</summary>
    public string ScreenText()
    {
        lock (SyncRoot)
        {
            return string.Join("\n", Enumerable.Range(0, Rows).Select(y => ScreenRow(y).GetText()));
        }
    }
}
