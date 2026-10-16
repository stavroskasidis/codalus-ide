using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// Deferred work (M12b): the hex viewer. Binary files open in it, any file opens in it through Open With Hex Viewer, it
/// reads only the rows it shows, and Reopen With Text Editor goes back.
/// </summary>
public sealed class HexViewerTests
{
    [Fact]
    public void Rows_show_the_offset_the_bytes_and_their_printable_characters()
    {
        var full = HexDocument.FormatRow(0, Enumerable.Range(0x41, 16).Select(b => (byte)b).ToArray());
        Assert.Equal("00000000  41 42 43 44 45 46 47 48  49 4A 4B 4C 4D 4E 4F 50  ABCDEFGHIJKLMNOP", full);

        // A short last row: the characters stay in their column.
        var partial = HexDocument.FormatRow(16, [0x48, 0x69, 0x00, 0xFF]);
        Assert.StartsWith("00000010  48 69 00 FF ", partial, StringComparison.Ordinal);
        Assert.EndsWith("Hi..", partial, StringComparison.Ordinal);
        Assert.Equal(full.IndexOf("ABCD", StringComparison.Ordinal), partial.IndexOf("Hi..", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Binary_files_open_in_the_hex_viewer_and_text_files_can_too()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(Path.GetTempPath(), "codalus-hex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // 1 MB of bytes: only the visible rows are read.
            var binary = Path.Combine(folder, "data.bin");
            await File.WriteAllBytesAsync(binary, [.. Enumerable.Range(0, 1 << 20).Select(i => (byte)i)], TestContext.Current.CancellationToken);
            var unicode = Path.Combine(folder, "notes-utf16.txt");
            await File.WriteAllTextAsync(unicode, "Hello", new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true), TestContext.Current.CancellationToken);
            Assert.True(HexDocument.LooksBinary(binary));
            Assert.False(HexDocument.LooksBinary(unicode)); // NULs, but a byte order mark: text

            Assert.Null(await f.Shell.OpenFileAsync(binary, preview: false, null));
            var hex = Assert.IsType<HexDocument>(f.Shell.Layout.ActiveDocument);
            Assert.Equal(binary, hex.Path);
            await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<HexRows>().Any());
            var rows = f.Window.GetVisualDescendants().OfType<HexRows>().Single();
            Assert.Equal(1 << 20, rows.Length);
            Assert.Equal((1 << 20) / 16, rows.RowCount);
            rows.Select(0x12345);
            ShellFixture.Pump();
            Assert.Equal((byte)0x45, rows.ByteAt(0x12345));
            Assert.InRange(rows.FirstRow * 16, 0x12345 - rows.VisibleRows * 16, 0x12345); // scrolled into view

            // Reopen With Text Editor, and back with Open With Hex Viewer.
            f.Shell.Run(ShellHost.ReopenWithTextEditorCommand);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == binary);
            f.Shell.OpenWithHexViewer();
            ShellFixture.Pump();
            Assert.IsType<HexDocument>(f.Shell.Layout.ActiveDocument);

            // A text file opens as text.
            Assert.NotNull(await f.Shell.OpenFileAsync(unicode, preview: false, null));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
