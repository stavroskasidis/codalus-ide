using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Codalus.Terminal;
using Codalus.Workbench.Terminal;
using TerminalKey = Codalus.Terminal.TerminalKey;
using TerminalMouseButton = Codalus.Terminal.MouseButton;

namespace Codalus.Shell.Terminal;

/// <summary>A link the user Ctrl+clicked: a URL, or a file with an optional one-based line and column.</summary>
public sealed record TerminalLinkActivation(TerminalLinkKind Kind, string Target, int Line, int Column);

/// <summary>
/// Draws a <see cref="TerminalSession"/>'s screen and sends keyboard, paste, mouse and focus input to it (ADR 0014).
/// Rows are drawn as runs of same-styled cells with cached text layouts; output schedules at most one frame. The viewport
/// follows the output unless the user scrolled back.
/// </summary>
public sealed class TerminalControl : Control
{
    private const double Padding = 4;
    private static readonly TimeSpan s_blink = TimeSpan.FromMilliseconds(530);

    private readonly Dictionary<(string Text, uint Color, CellAttributes Attributes), FormattedText> _layouts = [];
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _resizeTimer;
    private TerminalSession? _session;
    private TerminalTheme _theme = TerminalTheme.For("dark");
    private FontFamily _fontFamily = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Noto Sans Mono, Ubuntu Mono, Liberation Mono, Noto Mono, monospace");
    private double _fontSize = 13;
    private Typeface _typeface;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private int _top;
    private bool _follow = true;
    private int _renderQueued;
    private bool _cursorOn = true;
    private (int Row, int Column)? _selectionStart;
    private (int Row, int Column)? _selectionEnd;
    private bool _selecting;
    private MouseTracking _pressedTracking;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Avalonia.Input.Cursor(StandardCursorType.Ibeam);
        _typeface = new Typeface(_fontFamily);
        _blinkTimer = new DispatcherTimer { Interval = s_blink };
        _blinkTimer.Tick += (_, _) =>
        {
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        };
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            ApplySize();
        };
        UpdateMetrics();
    }

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value))
            {
                return;
            }

            if (_session is { } old)
            {
                old.Terminal.Changed -= OnTerminalChanged;
            }

            _session = value;
            _selectionStart = _selectionEnd = null;
            _follow = true;
            if (value is { } session)
            {
                session.Terminal.Changed += OnTerminalChanged;
                session.Terminal.DefaultForegroundRgb = _theme.ForegroundRgb;
                session.Terminal.DefaultBackgroundRgb = _theme.BackgroundRgb;
                ApplySize();
            }

            InvalidateVisual();
        }
    }

    public TerminalTheme Palette
    {
        get => _theme;
        set
        {
            _theme = value;
            _layouts.Clear();
            if (_session is { } session)
            {
                session.Terminal.DefaultForegroundRgb = value.ForegroundRgb;
                session.Terminal.DefaultBackgroundRgb = value.BackgroundRgb;
            }

            InvalidateVisual();
        }
    }

    public void SetFont(FontFamily family, double size)
    {
        _fontFamily = family;
        _fontSize = size;
        UpdateMetrics();
        ApplySize();
        InvalidateVisual();
    }

    /// <summary>Copy the selection when it ends (setting <c>terminal.copyOnSelect</c>).</summary>
    public bool CopyOnSelect { get; set; }

    /// <summary>A link was Ctrl+clicked (relative file paths are resolved against the session's directory).</summary>
    public event EventHandler<TerminalLinkActivation>? LinkActivated;

    /// <summary>The viewport moved (scrollbar sync).</summary>
    public event EventHandler? ViewportChanged;

    public int Columns { get; private set; } = 80;

    public int Rows { get; private set; } = 24;

    /// <summary>The first buffer row shown.</summary>
    public int ViewportTop => _top;

    public int MaximumTop => _session is { } s ? Math.Max(0, s.Terminal.TotalRows - s.Terminal.Rows) : 0;

    public bool HasSelection => _selectionStart is not null && _selectionEnd is not null && _selectionStart != _selectionEnd;

    // ------------------------------------------------------------------ metrics and size

    private void UpdateMetrics()
    {
        _typeface = new Typeface(_fontFamily);
        var probe = new FormattedText("WWWWWWWWWW", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.Black);
        _cellWidth = Math.Max(1, probe.WidthIncludingTrailingWhitespace / 10);
        _cellHeight = Math.Max(1, Math.Ceiling(probe.Height));
        _layouts.Clear();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void ApplySize()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var columns = Math.Max(2, (int)((Bounds.Width - (2 * Padding)) / _cellWidth));
        var rows = Math.Max(1, (int)((Bounds.Height - (2 * Padding)) / _cellHeight));
        Columns = columns;
        Rows = rows;
        if (_session is { } session && (session.Terminal.Columns != columns || session.Terminal.Rows != rows))
        {
            session.Resize(columns, rows);
            _top = MaximumTop;
            _follow = true;
        }

        InvalidateVisual();
    }

    // ------------------------------------------------------------------ output

    private void OnTerminalChanged()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            if (_session is not { } session)
            {
                return;
            }

            var scrolled = session.Terminal.TakeScrolled();
            if (_follow)
            {
                _top = MaximumTop;
            }
            else if (session.Terminal.TotalRows >= session.Terminal.Rows + session.Terminal.ScrollbackLimit)
            {
                // The scrollback is full: old rows dropped off the top, so the rows the user looks at moved up.
                _top = Math.Max(0, _top - scrolled);
            }

            _cursorOn = true;
            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }, DispatcherPriority.Render);
    }

    /// <summary>Scrolls the viewport (positive: towards the output).</summary>
    public void ScrollBy(int rows) => ScrollTo(_top + rows);

    public void ScrollTo(int top)
    {
        _top = Math.Clamp(top, 0, MaximumTop);
        _follow = _top >= MaximumTop;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects buffer cells and scrolls them into view (find).</summary>
    public void Select(int row, int column, int length)
    {
        _selectionStart = (row, column);
        _selectionEnd = (row, column + length);
        if (row < _top || row >= _top + Rows)
        {
            ScrollTo(row - (Rows / 2));
        }

        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _selectionStart = _selectionEnd = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ rendering

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_theme.BackgroundBrush, new Rect(Bounds.Size));
        if (_session is not { } session)
        {
            return;
        }

        var terminal = session.Terminal;
        lock (terminal.SyncRoot)
        {
            var top = Math.Clamp(_top, 0, Math.Max(0, terminal.TotalRows - terminal.Rows));
            var (selectionFrom, selectionTo) = OrderedSelection();
            for (var y = 0; y < terminal.Rows; y++)
            {
                var index = top + y;
                if (index >= terminal.TotalRows)
                {
                    break;
                }

                DrawRow(context, terminal.GetRow(index), y, index, terminal.ReverseVideo, selectionFrom, selectionTo);
            }

            var cursorRow = terminal.TotalRows - terminal.Rows + terminal.CursorY;
            if (terminal.CursorVisible && cursorRow >= top && cursorRow < top + terminal.Rows && (_cursorOn || !terminal.CursorBlink || !IsFocused))
            {
                DrawCursor(context, terminal, cursorRow - top);
            }
        }
    }

    private (int Row, int Column)? _hoverLink;

    private void DrawRow(DrawingContext context, TerminalRow row, int y, int index, bool reverseVideo, (int Row, int Column)? from, (int Row, int Column)? to)
    {
        var cells = row.Cells;
        var top = Padding + (y * _cellHeight);
        var x = 0;
        while (x < cells.Length)
        {
            ref var first = ref cells[x];
            var (foreground, background) = Colors(first, reverseVideo);
            var start = x;
            var single = first.Width != 1 || row.Extras?.ContainsKey(x) == true;
            x++;
            if (!single)
            {
                while (x < cells.Length && cells[x].Width == 1 && row.Extras?.ContainsKey(x) != true
                       && cells[x].Attributes == first.Attributes && cells[x].Foreground == first.Foreground && cells[x].Background == first.Background)
                {
                    x++;
                }
            }
            else if (first.Width == 2)
            {
                x++;
            }

            var left = Padding + (start * _cellWidth);
            var width = (x - start) * _cellWidth;
            if (background != _theme.Background)
            {
                context.FillRectangle(_theme.Brush(background), new Rect(left, top, width, _cellHeight));
            }

            if ((first.Attributes & CellAttributes.Invisible) != 0)
            {
                continue;
            }

            var text = single ? row.TextAt(start) : RunText(cells, start, x);
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var color = (first.Attributes & CellAttributes.Dim) != 0 ? Color.FromArgb(0xA0, foreground.R, foreground.G, foreground.B) : foreground;
            context.DrawText(Layout(text, color, first.Attributes), new Point(left, top));
            if ((first.Attributes & (CellAttributes.Underline | CellAttributes.DoubleUnderline)) != 0 || IsHoveredLink(index, start))
            {
                var underline = top + _cellHeight - 2;
                context.DrawLine(new Pen(_theme.Brush(color), 1), new Point(left, underline), new Point(left + width, underline));
                if ((first.Attributes & CellAttributes.DoubleUnderline) != 0)
                {
                    context.DrawLine(new Pen(_theme.Brush(color), 1), new Point(left, underline - 2), new Point(left + width, underline - 2));
                }
            }

            if ((first.Attributes & CellAttributes.Strikethrough) != 0)
            {
                var middle = top + (_cellHeight / 2);
                context.DrawLine(new Pen(_theme.Brush(color), 1), new Point(left, middle), new Point(left + width, middle));
            }
        }

        // Selection on top of the text.
        if (from is { } a && to is { } b && index >= a.Row && index <= b.Row)
        {
            var startColumn = index == a.Row ? a.Column : 0;
            var endColumn = index == b.Row ? b.Column : cells.Length;
            if (endColumn > startColumn)
            {
                context.FillRectangle(_theme.Selection, new Rect(Padding + (startColumn * _cellWidth), top, (endColumn - startColumn) * _cellWidth, _cellHeight));
            }
        }
    }

    private bool IsHoveredLink(int row, int column) => _hoverLink is { } link && link.Row == row && column >= link.Column && column <= link.Column;

    private static string RunText(Cell[] cells, int from, int to)
    {
        Span<char> buffer = stackalloc char[(to - from) * 2];
        var length = 0;
        for (var i = from; i < to; i++)
        {
            var rune = cells[i].Rune == 0 ? ' ' : cells[i].Rune;
            length += new System.Text.Rune(rune).EncodeToUtf16(buffer[length..]);
        }

        return new string(buffer[..length]);
    }

    private (Color Foreground, Color Background) Colors(in Cell cell, bool reverseVideo)
    {
        var bold = (cell.Attributes & CellAttributes.Bold) != 0;
        var foreground = _theme.Resolve(cell.Foreground, foreground: true, bold);
        var background = _theme.Resolve(cell.Background, foreground: false);
        var inverse = ((cell.Attributes & CellAttributes.Inverse) != 0) != reverseVideo;
        return inverse ? (background, foreground) : (foreground, background);
    }

    private FormattedText Layout(string text, Color color, CellAttributes attributes)
    {
        var key = (text, color.ToUInt32(), attributes & (CellAttributes.Bold | CellAttributes.Italic));
        if (!_layouts.TryGetValue(key, out var layout))
        {
            if (_layouts.Count > 4000)
            {
                _layouts.Clear();
            }

            var typeface = new Typeface(_fontFamily, (attributes & CellAttributes.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal, (attributes & CellAttributes.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal);
            layout = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, _fontSize, _theme.Brush(color));
            _layouts[key] = layout;
        }

        return layout;
    }

    private void DrawCursor(DrawingContext context, VirtualTerminal terminal, int y)
    {
        var x = Padding + (terminal.CursorX * _cellWidth);
        var top = Padding + (y * _cellHeight);
        var width = terminal.ScreenRow(terminal.CursorY).Cells[terminal.CursorX].Width == 2 ? 2 * _cellWidth : _cellWidth;
        if (!IsFocused)
        {
            context.DrawRectangle(null, new Pen(_theme.Cursor, 1), new Rect(x + 0.5, top + 0.5, width - 1, _cellHeight - 1));
            return;
        }

        switch (terminal.CursorStyle)
        {
            case CursorStyle.Bar:
                context.FillRectangle(_theme.Cursor, new Rect(x, top, 2, _cellHeight));
                break;
            case CursorStyle.Underline:
                context.FillRectangle(_theme.Cursor, new Rect(x, top + _cellHeight - 2, width, 2));
                break;
            default:
                context.FillRectangle(_theme.Cursor, new Rect(x, top, width, _cellHeight));
                var row = terminal.ScreenRow(terminal.CursorY);
                if (row.TextAt(terminal.CursorX) is { } text && !string.IsNullOrWhiteSpace(text))
                {
                    context.DrawText(Layout(text, _theme.Background, row.Cells[terminal.CursorX].Attributes & CellAttributes.Bold), new Point(x, top));
                }

                break;
        }
    }

    // ------------------------------------------------------------------ focus

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _blinkTimer.Start();
        _cursorOn = true;
        if (_session is { Terminal.FocusReporting: true } session)
        {
            session.Write(TerminalInput.Focus(true));
        }

        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _blinkTimer.Stop();
        if (_session is { Terminal.FocusReporting: true } session)
        {
            session.Write(TerminalInput.Focus(false));
        }

        InvalidateVisual();
    }

    // ------------------------------------------------------------------ keyboard

    private static bool IsPrimary(KeyModifiers modifiers) =>
        OperatingSystem.IsMacOS() ? (modifiers & KeyModifiers.Meta) != 0 : (modifiers & KeyModifiers.Control) != 0;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _session is not { } session)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        var shift = (modifiers & KeyModifiers.Shift) != 0;

        // Clipboard: Ctrl+Shift+C/V everywhere, Cmd+C/V on macOS, Ctrl+V on Windows, Ctrl+C with a selection.
        if (e.Key == Key.C && (IsPrimary(modifiers) && (shift || OperatingSystem.IsMacOS() || HasSelection)))
        {
            _ = CopyAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.V && IsPrimary(modifiers) && (shift || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()))
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        // Shift+PageUp/PageDown/Home/End scroll the viewport.
        if (shift && !session.Terminal.IsAlternateScreen && e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            ScrollTo(e.Key switch { Key.PageUp => _top - Rows, Key.PageDown => _top + Rows, Key.Home => 0, _ => MaximumTop });
            e.Handled = true;
            return;
        }

        var terminalModifiers = (shift ? TerminalModifiers.Shift : 0) | ((modifiers & KeyModifiers.Alt) != 0 ? TerminalModifiers.Alt : 0) | ((modifiers & KeyModifiers.Control) != 0 ? TerminalModifiers.Control : 0);
        string? sequence = null;
        if (MapKey(e.Key) is { } key)
        {
            sequence = TerminalInput.Key(key, terminalModifiers, session.Terminal.ApplicationCursorKeys);
        }
        else if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0 && KeyChar(e.Key, shift) is { } c)
        {
            sequence = TerminalInput.Character(c, terminalModifiers);
        }

        if (sequence is not null)
        {
            Send(sequence);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!e.Handled && !string.IsNullOrEmpty(e.Text) && _session is not null)
        {
            Send(e.Text);
            e.Handled = true;
        }
    }

    private void Send(string text)
    {
        if (_session is not { } session)
        {
            return;
        }

        ClearSelection();
        if (!_follow)
        {
            ScrollTo(MaximumTop);
        }

        session.Write(text);
    }

    private static TerminalKey? MapKey(Key key) => key switch
    {
        Key.Enter => TerminalKey.Enter,
        Key.Tab => TerminalKey.Tab,
        Key.Back => TerminalKey.Backspace,
        Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up,
        Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left,
        Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home,
        Key.End => TerminalKey.End,
        Key.Insert => TerminalKey.Insert,
        Key.Delete => TerminalKey.Delete,
        Key.PageUp => TerminalKey.PageUp,
        Key.PageDown => TerminalKey.PageDown,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
        _ => null,
    };

    /// <summary>The character a key types (for Ctrl and Alt combinations, which produce no text input).</summary>
    private static char? KeyChar(Key key, bool shift) => key switch
    {
        >= Key.A and <= Key.Z => (char)((shift ? 'A' : 'a') + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        Key.Space => ' ',
        Key.OemOpenBrackets => '[',
        Key.OemCloseBrackets => ']',
        Key.OemPipe or Key.OemBackslash => '\\',
        Key.OemMinus => shift ? '_' : '-',
        Key.OemQuestion => shift ? '?' : '/',
        Key.OemPeriod => '.',
        Key.OemComma => ',',
        _ => null,
    };

    public async Task CopyAsync()
    {
        if (SelectedText() is { Length: > 0 } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await Codalus.Shell.Infrastructure.SafeClipboard.TrySetTextAsync(clipboard, text).ConfigureAwait(true);
        }

        ClearSelection();
    }

    public async Task PasteAsync()
    {
        if (_session is { } session && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
            && await Codalus.Shell.Infrastructure.SafeClipboard.TryGetTextAsync(clipboard).ConfigureAwait(true) is { Length: > 0 } text)
        {
            Send(TerminalInput.Paste(text, session.Terminal.BracketedPaste));
        }
    }

    // ------------------------------------------------------------------ mouse

    private (int Row, int Column) CellAt(Point point)
    {
        var column = Math.Clamp((int)((point.X - Padding) / _cellWidth), 0, Math.Max(0, Columns - 1));
        var row = Math.Clamp((int)((point.Y - Padding) / _cellHeight), 0, Math.Max(0, Rows - 1));
        return (_top + row, column);
    }

    private static TerminalModifiers Modifiers(KeyModifiers modifiers) =>
        ((modifiers & KeyModifiers.Shift) != 0 ? TerminalModifiers.Shift : 0) | ((modifiers & KeyModifiers.Alt) != 0 ? TerminalModifiers.Alt : 0) | ((modifiers & KeyModifiers.Control) != 0 ? TerminalModifiers.Control : 0);

    private bool ReportsMouse(KeyModifiers modifiers) => _session is { Terminal.MouseTracking: not MouseTracking.None } && (modifiers & KeyModifiers.Shift) == 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_session is not { } session)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var (row, column) = CellAt(point.Position);
        if (ReportsMouse(e.KeyModifiers))
        {
            var button = point.Properties.IsRightButtonPressed ? TerminalMouseButton.Right : point.Properties.IsMiddleButtonPressed ? TerminalMouseButton.Middle : TerminalMouseButton.Left;
            _pressedTracking = session.Terminal.MouseTracking;
            if (TerminalInput.Mouse(session.Terminal.MouseTracking, session.Terminal.SgrMouse, button, MouseEventKind.Press, column, row - _top, Modifiers(e.KeyModifiers)) is { } report)
            {
                session.Write(report);
            }

            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && (e.KeyModifiers & (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) != 0 && LinkAt(row, column) is { } link)
        {
            LinkActivated?.Invoke(this, link);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            // Right-click: copy the selection if there is one, else paste (Windows Terminal's behaviour).
            _ = HasSelection ? CopyAsync() : PasteAsync();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            SelectWord(row, column);
        }
        else if (e.ClickCount >= 3)
        {
            _selectionStart = (row, 0);
            _selectionEnd = (row, session.Terminal.Columns);
        }
        else
        {
            _selectionStart = _selectionEnd = (row, column);
            _selecting = true;
            e.Pointer.Capture(this);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_session is not { } session)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var (row, column) = CellAt(point.Position);
        if (_pressedTracking != MouseTracking.None || (ReportsMouse(e.KeyModifiers) && session.Terminal.MouseTracking == MouseTracking.AnyEvent))
        {
            var button = point.Properties.IsLeftButtonPressed ? TerminalMouseButton.Left : point.Properties.IsRightButtonPressed ? TerminalMouseButton.Right : point.Properties.IsMiddleButtonPressed ? TerminalMouseButton.Middle : TerminalMouseButton.None;
            if (TerminalInput.Mouse(session.Terminal.MouseTracking, session.Terminal.SgrMouse, button, MouseEventKind.Move, column, row - _top, Modifiers(e.KeyModifiers)) is { } report)
            {
                session.Write(report);
            }

            return;
        }

        if (_selecting)
        {
            _selectionEnd = (row, column + (point.Position.X > Padding + ((column + 0.5) * _cellWidth) ? 1 : 0));
            if (point.Position.Y < 0)
            {
                ScrollBy(-1);
            }
            else if (point.Position.Y > Bounds.Height)
            {
                ScrollBy(1);
            }

            InvalidateVisual();
        }

        var hover = (e.KeyModifiers & (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) != 0 && LinkAt(row, column) is not null ? (row, column) : ((int, int)?)null;
        if (hover != _hoverLink)
        {
            _hoverLink = hover;
            Cursor = new Avalonia.Input.Cursor(hover is null ? StandardCursorType.Ibeam : StandardCursorType.Hand);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_session is not { } session)
        {
            return;
        }

        if (_pressedTracking != MouseTracking.None)
        {
            var (row, column) = CellAt(e.GetPosition(this));
            var button = e.InitialPressMouseButton switch { Avalonia.Input.MouseButton.Right => TerminalMouseButton.Right, Avalonia.Input.MouseButton.Middle => TerminalMouseButton.Middle, _ => TerminalMouseButton.Left };
            if (TerminalInput.Mouse(session.Terminal.MouseTracking, session.Terminal.SgrMouse, button, MouseEventKind.Release, column, row - _top, Modifiers(e.KeyModifiers)) is { } report)
            {
                session.Write(report);
            }

            _pressedTracking = MouseTracking.None;
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            if (CopyOnSelect && HasSelection)
            {
                _ = CopyAsync();
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is not { } session)
        {
            return;
        }

        var lines = (int)Math.Round(-e.Delta.Y * 3);
        if (lines == 0)
        {
            return;
        }

        if (ReportsMouse(e.KeyModifiers))
        {
            var (row, column) = CellAt(e.GetPosition(this));
            var button = lines < 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
            if (TerminalInput.Mouse(session.Terminal.MouseTracking, session.Terminal.SgrMouse, button, MouseEventKind.Press, column, row - _top, Modifiers(e.KeyModifiers)) is { } report)
            {
                session.Write(report);
            }
        }
        else if (session.Terminal.IsAlternateScreen)
        {
            // Full-screen programs without mouse support (less, man) scroll with the arrow keys.
            var key = TerminalInput.Key(lines < 0 ? TerminalKey.Up : TerminalKey.Down, TerminalModifiers.None, session.Terminal.ApplicationCursorKeys)!;
            session.Write(string.Concat(Enumerable.Repeat(key, Math.Abs(lines))));
        }
        else
        {
            ScrollBy(lines);
        }

        e.Handled = true;
    }

    private void SelectWord(int row, int column)
    {
        if (_session is not { } session)
        {
            return;
        }

        lock (session.Terminal.SyncRoot)
        {
            var cells = session.Terminal.GetRow(row).Cells;
            static bool Word(in Cell c) => c.Rune != 0 && !char.IsWhiteSpace((char)Math.Min(c.Rune, 0xFFFF)) && c.Rune is not ('"' or '\'' or '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or '|' or ';' or ',');
            var start = column;
            var end = column;
            while (start > 0 && Word(cells[start - 1]))
            {
                start--;
            }

            while (end < cells.Length && Word(cells[end]))
            {
                end++;
            }

            _selectionStart = (row, start);
            _selectionEnd = (row, Math.Max(end, start + 1));
        }
    }

    private ((int Row, int Column)? From, (int Row, int Column)? To) OrderedSelection()
    {
        if (_selectionStart is not { } a || _selectionEnd is not { } b || a == b)
        {
            return (null, null);
        }

        return a.Row < b.Row || (a.Row == b.Row && a.Column <= b.Column) ? (a, b) : (b, a);
    }

    public string? SelectedText()
    {
        var (from, to) = OrderedSelection();
        if (from is not { } a || to is not { } b || _session is not { } session)
        {
            return null;
        }

        return session.Terminal.GetText(a.Row, a.Column, b.Row, b.Column);
    }

    /// <summary>The link under a cell: an OSC 8 hyperlink, or a URL or file reference in the row's text.</summary>
    private TerminalLinkActivation? LinkAt(int row, int column)
    {
        if (_session is not { } session)
        {
            return null;
        }

        string text;
        (int Row, int Column)[] cells;
        lock (session.Terminal.SyncRoot)
        {
            if (row < 0 || row >= session.Terminal.TotalRows)
            {
                return null;
            }

            var terminalRow = session.Terminal.GetRow(row);
            if (terminalRow.Links?.FirstOrDefault(l => column >= l.Start && column < l.End) is { Uri: { } uri })
            {
                return new TerminalLinkActivation(TerminalLinkKind.Url, uri, 0, 0);
            }

            // The whole logical line: a long path wraps over several rows.
            (text, cells) = session.Terminal.LogicalLine(row);
        }

        static int Order((int Row, int Column) cell) => (cell.Row * 100_000) + cell.Column;
        var target = Order((row, column));
        foreach (var link in TerminalLinks.Find(text))
        {
            var start = link.Start < cells.Length ? Order(cells[link.Start]) : int.MaxValue;
            var end = link.Start + link.Length - 1 < cells.Length ? Order(cells[link.Start + link.Length - 1]) : int.MaxValue;
            if (target >= start && target <= end)
            {
                if (link.Kind == TerminalLinkKind.Url)
                {
                    return new TerminalLinkActivation(TerminalLinkKind.Url, link.Target, 0, 0);
                }

                var path = link.Target.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), link.Target[2..]) : link.Target;
                var full = Path.IsPathRooted(path) ? path : Path.Combine(session.CurrentDirectory, path);
                return File.Exists(full) ? new TerminalLinkActivation(TerminalLinkKind.File, Path.GetFullPath(full), link.Line, link.Column) : null;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ accessibility

    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalAutomationPeer(this);

    /// <summary>Screen readers read the visible screen as text (read-only).</summary>
    private sealed class TerminalAutomationPeer(TerminalControl owner) : ControlAutomationPeer(owner), IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

        protected override string GetNameCore() => "Terminal" + (owner._session is { } s ? ": " + s.Title : string.Empty);

        public bool IsReadOnly => true;

        public string? Value => owner._session?.Terminal.ScreenText();

        public void SetValue(string? value)
        {
        }
    }
}
