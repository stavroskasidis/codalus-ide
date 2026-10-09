using System.Text;
using DaedalusStudio.Shell.Editor;

namespace DaedalusStudio.Shell.Tests;

public class TextFilesTests
{
    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' }, "UTF-8 with BOM", true)]
    [InlineData(new byte[] { (byte)'a', 0xC3, 0xA9 }, "UTF-8", false)]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'a', 0 }, "UTF-16 LE", true)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0, (byte)'a' }, "UTF-16 BE", true)]
    [InlineData(new byte[] { (byte)'a', 0xE9 }, "Latin-1", false)]
    public void Detects_the_encoding_and_the_bom(byte[] bytes, string encoding, bool bom)
    {
        var (_, format) = TextFiles.Decode(bytes);
        Assert.Equal(encoding, format.EncodingName);
        Assert.Equal(bom, format.HasBom);
    }

    [Theory]
    [InlineData("a\r\nb\nc", "\r\n")]
    [InlineData("a\nb\r\n", "\n")]
    [InlineData("a\rb", "\r")]
    public void The_first_line_ending_wins(string text, string expected) => Assert.Equal(expected, TextFiles.DetectLineEnding(text));

    [Fact]
    public void Round_trips_bytes_exactly()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("über\r\nmixed\nendings")];
        var (text, format) = TextFiles.Decode(original);
        Assert.Equal(original, TextFiles.Encode(text, format));

        byte[] latin = [(byte)'c', (byte)'a', (byte)'f', 0xE9];
        var (latinText, latinFormat) = TextFiles.Decode(latin);
        Assert.Equal("café", latinText);
        Assert.Equal(latin, TextFiles.Encode(latinText, latinFormat));
    }
}

public class CaretEditsTests
{
    [Fact]
    public void Typing_at_several_carets_shifts_later_carets()
    {
        var (edits, carets) = CaretEdits.Replace([CaretSelection.At(0), CaretSelection.At(5), CaretSelection.At(10)], () => "ab");

        Assert.Equal([(10, 0, "ab"), (5, 0, "ab"), (0, 0, "ab")], edits);
        Assert.Equal([2, 9, 16], carets);
    }

    [Fact]
    public void Selections_are_replaced_and_overlaps_merge()
    {
        var (edits, carets) = CaretEdits.Replace([new CaretSelection(2, 6), new CaretSelection(4, 8), new CaretSelection(10, 12)], () => "x");

        Assert.Equal([(10, 2, "x"), (2, 6, "x")], edits);
        Assert.Equal([3, 6], carets);
    }

    [Fact]
    public void Backspace_widens_empty_selections_and_merges_neighbours()
    {
        // Carets at 3 and 4: each deletes the previous character; the ranges [2,3) and [3,4) don't overlap.
        var (edits, carets) = CaretEdits.Replace([CaretSelection.At(3), CaretSelection.At(4)], () => string.Empty, caret => (caret - 1, caret));

        Assert.Equal([(3, 1, string.Empty), (2, 1, string.Empty)], edits);
        Assert.Equal([2, 2], carets);
    }

    [Fact]
    public void Duplicate_carets_collapse() =>
        Assert.Single(CaretEdits.Normalize([CaretSelection.At(4), CaretSelection.At(4)]));
}

public class MarkdownRendererTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Backslash_escapes_are_literal()
    {
        var control = DaedalusStudio.Shell.Controls.MarkdownRenderer.Render(@"a \*b\* \_c\_ **d**", _ => { });
        var block = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(control).OfType<Avalonia.Controls.TextBlock>().FirstOrDefault()
                    ?? control as Avalonia.Controls.TextBlock
                    ?? ((Avalonia.Controls.Panel)control).Children.OfType<Avalonia.Controls.TextBlock>().First();
        var text = string.Concat(block.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        Assert.Equal("a *b* _c_ d", text);
    }
}
