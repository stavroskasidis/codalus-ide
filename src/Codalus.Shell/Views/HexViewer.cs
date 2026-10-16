using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Codalus.Shell.Controls;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Views;

/// <summary>
/// A file in the hex viewer (deferred from M12b): binary files open in it (NUL bytes and no byte order mark in their
/// first 8 KB), and any file through Open With Hex Viewer. Read-only.
/// </summary>
public sealed class HexDocument : ShellDocument
{
    public HexDocument(string path, EditorFont font)
    {
        Path = path;
        Font = font;
        Id = IdFor(path);
        Title = System.IO.Path.GetFileName(path);
    }

    public override string Kind => "hex";

    public string Path { get; }

    public EditorFont Font { get; }

    public override string? FilePath => Path;

    public static string IdFor(string path) => "hex:" + path;

    /// <summary>Whether a file looks binary: a NUL byte in its first 8 KB, and no byte order mark (UTF-16 text has NULs).</summary>
    public static bool LooksBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[8192];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var span = buffer.AsSpan(0, read);
            var bom = span.StartsWith((byte[])[0xEF, 0xBB, 0xBF]) || span.StartsWith((byte[])[0xFF, 0xFE]) || span.StartsWith((byte[])[0xFE, 0xFF]);
            return !bom && span.Contains((byte)0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>One row of the viewer: the offset, 16 bytes in hex (a gap after 8), and the printable ASCII.</summary>
    public static string FormatRow(long offset, ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(80);
        text.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
        for (var i = 0; i < BytesPerRow; i++)
        {
            text.Append(i < bytes.Length ? bytes[i].ToString("X2", CultureInfo.InvariantCulture) : "  ").Append(i == 7 ? "  " : " ");
        }

        text.Append(' ');
        foreach (var b in bytes)
        {
            text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return text.ToString();
    }

    public const int BytesPerRow = 16;
}

/// <summary>
/// The rows of a <see cref="HexDocument"/>: only the visible ones are read from the file, so large files open at once.
/// A click selects a byte; the bar shows its offset and value. The wheel, the scroll bar, the arrows, Page Up/Down,
/// Home and End move.
/// </summary>
public sealed class HexViewerView : UserControl, IPaneView
{
    private readonly HexDocument _document;
    private readonly HexRows _rows;
    private readonly ScrollBar _scrollBar = new() { Orientation = Orientation.Vertical, AllowAutoHide = false };
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" }, Margin = new Thickness(8, 2) };

    public HexViewerView(HexDocument document)
    {
        _document = document;
        _rows = new HexRows(document) { Focusable = true };
        Avalonia.Automation.AutomationProperties.SetName(_rows, document.Title);
        _rows.Changed += Update;
        _scrollBar.Scroll += (_, e) => _rows.FirstRow = (long)e.NewValue;
        Avalonia.Automation.AutomationProperties.SetName(_scrollBar, "Rows");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("*,Auto") };
        var surface = new Border { Child = _rows };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("Shell.Editor.Background"));
        grid.Children.Add(surface);
        Grid.SetColumn(_scrollBar, 1);
        grid.Children.Add(_scrollBar);
        Grid.SetRow(_info, 1);
        Grid.SetColumnSpan(_info, 2);
        grid.Children.Add(_info);
        Content = grid;
        Update();
    }

    public void FocusPrimary() => _rows.Focus();

    private void Update()
    {
        _scrollBar.Maximum = Math.Max(0, _rows.RowCount - _rows.VisibleRows);
        _scrollBar.ViewportSize = _rows.VisibleRows;
        _scrollBar.LargeChange = Math.Max(1, _rows.VisibleRows - 1);
        _scrollBar.SmallChange = 1;
        _scrollBar.Value = _rows.FirstRow;
        var size = $"{_rows.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes";
        _info.Text = _rows.Selected is { } offset && _rows.ByteAt(offset) is { } value
            ? $"{size} · offset 0x{offset:X} ({offset.ToString(CultureInfo.InvariantCulture)}) · 0x{value:X2} = {value}{(value is >= 0x20 and < 0x7F ? $" '{(char)value}'" : string.Empty)}"
            : _document.Path is var path && !File.Exists(path) ? "The file no longer exists." : size;
    }
}

/// <summary>Draws the visible rows of the file.</summary>
public sealed class HexRows : Control
{
    private readonly HexDocument _document;
    private long _firstRow;

    public HexRows(HexDocument document)
    {
        _document = document;
        ClipToBounds = true;
        Length = File.Exists(document.Path) ? new FileInfo(document.Path).Length : 0;
        document.Font.PropertyChanged += (_, _) => InvalidateVisual();
    }

    public event Action? Changed;

    public long Length { get; }

    public long RowCount => (Length + HexDocument.BytesPerRow - 1) / HexDocument.BytesPerRow;

    public long? Selected { get; private set; }

    public int VisibleRows => Math.Max(1, (int)(Bounds.Height / LineHeight));

    public long FirstRow
    {
        get => _firstRow;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, RowCount - VisibleRows));
            if (clamped != _firstRow)
            {
                _firstRow = clamped;
                InvalidateVisual();
                Changed?.Invoke();
            }
        }
    }

    private double LineHeight => _document.Font.Size * 1.4;

    private Typeface Typeface => new(_document.Font.Family);

    private double CharWidth => new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, _document.Font.Size, Brushes.Black).WidthIncludingTrailingWhitespace;

    /// <summary>The byte at <paramref name="offset"/>, read from the file.</summary>
    public byte? ByteAt(long offset) => Read(offset, 1) is [var b] ? b : null;

    private byte[] Read(long offset, int count)
    {
        if (offset < 0 || offset >= Length)
        {
            return [];
        }

        try
        {
            using var handle = File.OpenHandle(_document.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(count, Length - offset)];
            var read = RandomAccess.Read(handle, buffer, offset);
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        FirstRow = _firstRow; // clamp to the new height
        Changed?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // hit-testable everywhere
        var foreground = this.FindResource("Shell.Editor.Foreground") as IBrush ?? Brushes.Gray;
        var muted = this.FindResource("Shell.MutedForeground") as IBrush ?? Brushes.Gray;
        var highlight = this.FindResource("Shell.Selection") as IBrush ?? Brushes.LightBlue;
        var rows = VisibleRows + 1;
        var bytes = Read(_firstRow * HexDocument.BytesPerRow, rows * HexDocument.BytesPerRow);
        var charWidth = CharWidth;
        for (var row = 0; row < rows && row * HexDocument.BytesPerRow < bytes.Length; row++)
        {
            var start = row * HexDocument.BytesPerRow;
            var offset = (_firstRow + row) * HexDocument.BytesPerRow;
            var slice = bytes.AsSpan(start, Math.Min(HexDocument.BytesPerRow, bytes.Length - start));
            var y = row * LineHeight;
            if (Selected is { } selected && selected >= offset && selected < offset + slice.Length)
            {
                var column = (int)(selected - offset);
                context.FillRectangle(highlight, new Rect((10 + column * 3 + (column >= 8 ? 1 : 0)) * charWidth, y, 2 * charWidth, LineHeight));
                context.FillRectangle(highlight, new Rect((10 + 16 * 3 + 2 + column) * charWidth, y, charWidth, LineHeight));
            }

            var line = HexDocument.FormatRow(offset, slice);
            context.DrawText(new FormattedText(line[..8], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, _document.Font.Size, muted), new Point(0, y));
            context.DrawText(new FormattedText(line[8..], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, _document.Font.Size, foreground), new Point(8 * charWidth, y));
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        FirstRow -= (long)Math.Round(e.Delta.Y * 3);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var row = _firstRow + (long)(point.Y / LineHeight);
        var column = (int)(point.X / CharWidth);
        int? index = column switch
        {
            >= 10 and < 10 + 8 * 3 => (column - 10) / 3,
            >= 10 + 8 * 3 + 1 and < 10 + 16 * 3 + 1 => (column - 11) / 3,
            >= 10 + 16 * 3 + 2 and < 10 + 16 * 3 + 2 + 16 => column - (10 + 16 * 3 + 2),
            _ => null,
        };
        Select(index is { } i ? row * HexDocument.BytesPerRow + i : null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var current = Selected ?? _firstRow * HexDocument.BytesPerRow;
        long? target = e.Key switch
        {
            Key.Right => current + 1,
            Key.Left => current - 1,
            Key.Down => current + HexDocument.BytesPerRow,
            Key.Up => current - HexDocument.BytesPerRow,
            Key.PageDown => current + (long)HexDocument.BytesPerRow * Math.Max(1, VisibleRows - 1),
            Key.PageUp => current - (long)HexDocument.BytesPerRow * Math.Max(1, VisibleRows - 1),
            Key.Home => e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0 : current - current % HexDocument.BytesPerRow,
            Key.End => e.KeyModifiers.HasFlag(KeyModifiers.Control) ? Length - 1 : Math.Min(Length - 1, current - current % HexDocument.BytesPerRow + HexDocument.BytesPerRow - 1),
            _ => null,
        };
        if (target is { } offset && Length > 0)
        {
            Select(Math.Clamp(offset, 0, Length - 1));
            e.Handled = true;
        }
    }

    /// <summary>Selects a byte (null: none) and scrolls it into view.</summary>
    public void Select(long? offset)
    {
        Selected = offset is { } o && o >= 0 && o < Length ? o : null;
        if (Selected is { } selected)
        {
            var row = selected / HexDocument.BytesPerRow;
            if (row < _firstRow)
            {
                FirstRow = row;
            }
            else if (row >= _firstRow + VisibleRows)
            {
                FirstRow = row - VisibleRows + 1;
            }
        }

        InvalidateVisual();
        Changed?.Invoke();
    }
}
