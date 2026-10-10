using DaedalusStudio.Terminal;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>The VT engine (ADR 0014): parsing, the screen model, input encoding and links.</summary>
public sealed class TerminalEmulatorTests
{
    private static VirtualTerminal Term(int columns = 20, int rows = 5, int scrollback = 100) => new(columns, rows, scrollback);

    private static string Row(VirtualTerminal t, int y) => t.ScreenRow(y).GetText();

    [Fact]
    public void Text_wraps_at_the_right_margin_and_lines_feed()
    {
        var t = Term(10, 3);
        t.Feed("hello\r\nworld");
        Assert.Equal(("hello", "world"), (Row(t, 0), Row(t, 1)));
        Assert.Equal((5, 1), (t.CursorX, t.CursorY));

        // Ten characters fill the last row; the eleventh wraps and the screen scrolls one row.
        t.Feed("\r\n0123456789AB");
        Assert.Equal(("world", "0123456789", "AB"), (Row(t, 0), Row(t, 1), Row(t, 2)));
        Assert.True(t.ScreenRow(1).Wrapped);
        Assert.False(t.ScreenRow(2).Wrapped);
        Assert.Equal(1, t.ScrollbackRows);
        Assert.Equal("hello", t.GetRow(0).GetText());
        Assert.Equal("0123456789AB", t.GetText(t.TotalRows - 2, 0, t.TotalRows - 1, 10));
    }

    [Fact]
    public void Pending_wrap_lets_the_last_column_be_written_without_scrolling()
    {
        var t = Term(5, 2, scrollback: 0);
        t.Feed("abcde");
        Assert.Equal((4, 0), (t.CursorX, t.CursorY));
        t.Feed("\r\nx");
        Assert.Equal(("abcde", "x"), (Row(t, 0), Row(t, 1)));
    }

    [Fact]
    public void Cursor_movement_and_erasing()
    {
        var t = Term(10, 4);
        t.Feed("aaaaaaaaaa\r\nbbbbbbbbbb\r\ncccccccccc");
        t.Feed("\x1b[2;3H");
        Assert.Equal((2, 1), (t.CursorX, t.CursorY));
        t.Feed("\x1b[K");
        Assert.Equal("bb", Row(t, 1));
        t.Feed("\x1b[1;5H\x1b[1K");
        Assert.Equal("     aaaaa", t.ScreenRow(0).GetText());
        t.Feed("\x1b[3;1H\x1b[2P");
        Assert.Equal("cccccccc", Row(t, 2));
        t.Feed("\x1b[2@");
        Assert.Equal("  cccccccc", Row(t, 2));
        t.Feed("\x1b[J");
        Assert.Equal(string.Empty, Row(t, 2));
        t.Feed("\x1b[2J");
        Assert.Equal(string.Empty, t.ScreenText().Trim());
    }

    [Fact]
    public void Sgr_sets_palette_256_and_true_colours_and_attributes()
    {
        var t = Term();
        t.Feed("\x1b[1;31mA\x1b[38;5;200;48;2;10;20;30mB\x1b[38:2::1:2:3mC\x1b[0;7;4mD\x1b[mE\x1b[93;104mF");
        var cells = t.ScreenRow(0).Cells;
        Assert.Equal((TerminalColor.Palette(1), CellAttributes.Bold), (cells[0].Foreground, cells[0].Attributes));
        Assert.Equal(TerminalColor.Palette(200), cells[1].Foreground);
        Assert.Equal(TerminalColor.FromRgb(10, 20, 30), cells[1].Background);
        Assert.Equal(TerminalColor.FromRgb(1, 2, 3), cells[2].Foreground);
        Assert.Equal(CellAttributes.Inverse | CellAttributes.Underline, cells[3].Attributes);
        Assert.True(cells[4].Foreground.IsDefault && cells[4].Attributes == CellAttributes.None);
        Assert.Equal((TerminalColor.Palette(11), TerminalColor.Palette(12)), (cells[5].Foreground, cells[5].Background));
    }

    [Fact]
    public void Scroll_regions_scroll_only_their_rows()
    {
        var t = Term(10, 5);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5");
        t.Feed("\x1b[2;4r\x1b[4;1H\n");
        Assert.Equal(["1", "3", "4", "", "5"], Enumerable.Range(0, 5).Select(y => Row(t, y)));
        Assert.Equal(0, t.ScrollbackRows); // region scrolls never reach the scrollback
        t.Feed("\x1b[2;1H\x1bM");
        Assert.Equal(["1", "", "3", "4", "5"], Enumerable.Range(0, 5).Select(y => Row(t, y)));
    }

    [Fact]
    public void Scrollback_is_capped()
    {
        var t = Term(10, 3, scrollback: 10);
        for (var i = 0; i < 50; i++)
        {
            t.Feed($"line {i}\r\n");
        }

        Assert.Equal(10, t.ScrollbackRows);
        Assert.Equal("line 38", t.GetRow(0).GetText());
        t.Feed("\x1b[3J");
        Assert.Equal(0, t.ScrollbackRows);
    }

    [Fact]
    public void The_alternate_screen_keeps_the_primary_screen()
    {
        var t = Term(10, 3);
        t.Feed("shell$ ");
        t.Feed("\x1b[?1049h\x1b[Hfull screen");
        Assert.True(t.IsAlternateScreen);
        Assert.Equal("full scree", Row(t, 0));
        t.Feed("\x1b[?1049l");
        Assert.False(t.IsAlternateScreen);
        Assert.Equal("shell$", Row(t, 0));
        Assert.Equal((7, 0), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void Wide_characters_take_two_cells_and_combining_marks_join()
    {
        var t = Term(10, 2);
        t.Feed("日本x");
        var row = t.ScreenRow(0);
        Assert.Equal((2, 0, 2, 0), (row.Cells[0].Width, row.Cells[1].Width, row.Cells[2].Width, row.Cells[3].Width));
        Assert.Equal('x', row.Cells[4].Rune);
        Assert.Equal("日本x", row.GetText());
        Assert.Equal(5, t.CursorX);

        t.Feed("\r\ne\u0301!");
        Assert.Equal("e\u0301", t.ScreenRow(1).TextAt(0));
        Assert.Equal('!', t.ScreenRow(1).Cells[1].Rune);
        Assert.Equal("e\u0301!", t.ScreenRow(1).GetText());

        // A wide character that doesn't fit wraps.
        var narrow = Term(3, 2);
        narrow.Feed("ab日");
        Assert.Equal(("ab", "日"), (narrow.ScreenRow(0).GetText(), narrow.ScreenRow(1).GetText()));
        Assert.Equal(2, CharWidth.Of(0x1F600)); // 😀
        Assert.Equal(0, CharWidth.Of(0x0301));
    }

    [Fact]
    public void Queries_are_answered()
    {
        var t = Term();
        var replies = new List<string>();
        t.Reply += replies.Add;
        t.Feed("ab\x1b[6n\x1b[c\x1b[>c\x1b[5n\x1b]11;?\x07");
        Assert.Equal(["\x1b[1;3R", "\x1b[?62;22c", "\x1b[>0;10;1c", "\x1b[0n"], replies.Take(4));
        Assert.StartsWith("\x1b]11;rgb:", replies[4], StringComparison.Ordinal);
    }

    [Fact]
    public void Osc_sets_the_title_cwd_marks_and_hyperlinks()
    {
        var t = Term(30, 3);
        var marks = new List<CommandMark>();
        t.MarkReceived += marks.Add;
        t.Feed("\x1b]0;my title\x07");
        Assert.Equal("my title", t.Title);
        t.Feed("\x1b]7;file://host/home/ada/src\x1b\\");
        Assert.Equal("/home/ada/src", t.WorkingDirectory);
        t.Feed("\x1b]7;file://host/C:/Users/ada\x07");
        Assert.Equal(@"C:\Users\ada", t.WorkingDirectory);
        t.Feed("\x1b]133;A\x07$ \x1b]133;B\x07ls\r\n\x1b]133;C\x07out\r\n\x1b]133;D;1\x07");
        Assert.Equal([CommandMarkKind.PromptStart, CommandMarkKind.CommandStart, CommandMarkKind.OutputStart, CommandMarkKind.CommandEnd], marks.Select(m => m.Kind));
        Assert.Equal(1, marks[^1].ExitCode);
        Assert.NotNull(t.ScreenRow(0).Mark);

        t.Feed("\x1b]8;;https://example.com\x07link\x1b]8;;\x07 after");
        var link = Assert.Single(t.ScreenRow(t.CursorY).Links!);
        Assert.Equal((0, 4, "https://example.com"), link);
    }

    [Fact]
    public void Line_drawing_charset_and_tabs()
    {
        var t = Term(20, 2);
        t.Feed("\x1b(0lqk\x1b(Bx\tY");
        Assert.Equal("┌─┐x    Y", Row(t, 0));
    }

    [Fact]
    public void Resizing_keeps_the_cursor_row_and_moves_rows_to_the_scrollback()
    {
        var t = Term(10, 5);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5");
        t.Resize(8, 3);
        Assert.Equal((3, 8), (t.Rows, t.Columns));
        Assert.Equal(["3", "4", "5"], Enumerable.Range(0, 3).Select(y => Row(t, y)));
        Assert.Equal(2, t.CursorY);
        t.Resize(8, 5);
        Assert.Equal(["1", "2", "3", "4", "5"], Enumerable.Range(0, 5).Select(y => Row(t, y)));
    }

    [Fact]
    public void Split_utf8_sequences_and_escape_sequences_survive_chunking()
    {
        var t = Term(20, 2);
        var bytes = "\x1b[31mé日\x1b[0m!"u8.ToArray();
        foreach (var b in bytes)
        {
            t.Feed([b]);
        }

        Assert.Equal("é日!", Row(t, 0));
        Assert.Equal(TerminalColor.Palette(1), t.ScreenRow(0).Cells[0].Foreground);
    }

    // ------------------------------------------------------------------ recorded streams

    [Fact]
    public void A_progress_bar_redraws_one_line()
    {
        var t = Term(30, 3);
        for (var i = 0; i <= 100; i += 25)
        {
            t.Feed($"\rDownloading [{new string('#', i / 10),-10}] {i,3}%");
        }

        t.Feed("\r\n");
        Assert.Equal("Downloading [##########] 100%", Row(t, 0));
        Assert.Equal(1, t.CursorY);
    }

    [Fact]
    public void A_full_screen_app_draws_and_restores()
    {
        // What a vi-like program sends: alternate screen, clear, a status line, cursor addressing, insert, quit.
        var t = Term(20, 4);
        t.Feed("$ vi notes\r\n");
        t.Feed("\x1b[?1049h\x1b[22;0;0t\x1b[?1h\x1b=\x1b[H\x1b[2J\x1b[4;1H\x1b[7m-- INSERT --\x1b[0m\x1b[1;1Hhello\x1b[2;1H~\x1b[3;1H~\x1b[1;6H");
        Assert.Equal(["hello", "~", "~", "-- INSERT --"], Enumerable.Range(0, 4).Select(y => Row(t, y)));
        Assert.True(t.ApplicationCursorKeys);
        Assert.Equal(CellAttributes.Inverse, t.ScreenRow(3).Cells[0].Attributes);
        t.Feed("\x1b[?1l\x1b>\x1b[?1049l$ ");
        Assert.Equal(["$ vi notes", "$"], Enumerable.Range(0, 2).Select(y => Row(t, y)));
        Assert.False(t.ApplicationCursorKeys);
    }

    [Fact]
    public void Colored_ls_and_build_output_render()
    {
        var t = Term(60, 4);
        t.Feed("\x1b[0m\x1b[01;34mbin\x1b[0m  \x1b[01;32mbuild.sh\x1b[0m  README.md\r\n");
        t.Feed("  Build succeeded.\r\n\x1b[33m    1 Warning(s)\x1b[39m\r\n");
        Assert.Equal("bin  build.sh  README.md", Row(t, 0));
        Assert.Equal(TerminalColor.Palette(4), t.ScreenRow(0).Cells[0].Foreground);
        Assert.Equal(CellAttributes.Bold, t.ScreenRow(0).Cells[0].Attributes);
        Assert.True(t.ScreenRow(0).Cells[15].Foreground.IsDefault);
        Assert.Equal(TerminalColor.Palette(3), t.ScreenRow(2).Cells[4].Foreground);
    }

    // ------------------------------------------------------------------ input and links

    [Fact]
    public void Keys_are_encoded_like_xterm()
    {
        Assert.Equal("\x1b[A", TerminalInput.Key(TerminalKey.Up, TerminalModifiers.None, applicationCursorKeys: false));
        Assert.Equal("\x1bOA", TerminalInput.Key(TerminalKey.Up, TerminalModifiers.None, applicationCursorKeys: true));
        Assert.Equal("\x1b[1;5C", TerminalInput.Key(TerminalKey.Right, TerminalModifiers.Control, false));
        Assert.Equal("\x1b[3~", TerminalInput.Key(TerminalKey.Delete, TerminalModifiers.None, false));
        Assert.Equal("\x1b[15;2~", TerminalInput.Key(TerminalKey.F5, TerminalModifiers.Shift, false));
        Assert.Equal("\x1bOP", TerminalInput.Key(TerminalKey.F1, TerminalModifiers.None, false));
        Assert.Equal("\x1b[Z", TerminalInput.Key(TerminalKey.Tab, TerminalModifiers.Shift, false));
        Assert.Equal("\x7f", TerminalInput.Key(TerminalKey.Backspace, TerminalModifiers.None, false));
        Assert.Equal("\x03", TerminalInput.Character('c', TerminalModifiers.Control));
        Assert.Equal("\x1bx", TerminalInput.Character('x', TerminalModifiers.Alt));
        Assert.Equal("\x1b[200~a\rb\x1b[201~", TerminalInput.Paste("a\r\nb", bracketed: true));
        Assert.Equal("a\rb", TerminalInput.Paste("a\nb\x1b[201~", bracketed: false).Replace("\x1b[201~", string.Empty, StringComparison.Ordinal));
        Assert.Equal("\x1b[<0;3;2M", TerminalInput.Mouse(MouseTracking.Normal, sgr: true, MouseButton.Left, MouseEventKind.Press, 2, 1, TerminalModifiers.None));
        Assert.Equal("\x1b[<0;3;2m", TerminalInput.Mouse(MouseTracking.Normal, sgr: true, MouseButton.Left, MouseEventKind.Release, 2, 1, TerminalModifiers.None));
        Assert.Null(TerminalInput.Mouse(MouseTracking.Normal, sgr: true, MouseButton.None, MouseEventKind.Move, 2, 1, TerminalModifiers.None));
    }

    [Fact]
    public void Links_find_urls_and_file_locations()
    {
        var links = TerminalLinks.Find(@"src\App\Program.cs(12,5): error CS1002 see https://learn.microsoft.com/x.");
        Assert.Equal((TerminalLinkKind.File, @"src\App\Program.cs", 12, 5), (links[0].Kind, links[0].Target, links[0].Line, links[0].Column));
        Assert.Equal(("https://learn.microsoft.com/x", TerminalLinkKind.Url), (links[1].Target, links[1].Kind));

        var unix = TerminalLinks.Find("  at Foo() in /home/ada/src/Foo.cs:line 7 and ./build/out.txt and Program.cs:3:9");
        Assert.Contains(unix, l => l is { Target: "/home/ada/src/Foo.cs", Kind: TerminalLinkKind.File });
        Assert.Contains(unix, l => l is { Target: "./build/out.txt" });
        Assert.Contains(unix, l => l is { Target: "Program.cs", Line: 3, Column: 9 });
        Assert.DoesNotContain(TerminalLinks.Find("version 1.2.3 of nuget.org"), l => l.Kind == TerminalLinkKind.File);
    }

    [Fact]
    public void Logical_lines_join_soft_wrapped_rows_for_links()
    {
        var terminal = Term(columns: 20);
        terminal.Feed("> \r\n/var/folders/jg/abc/T/Linked.cs(3,2): error XX0001: boom\r\nnext"u8);

        var row = Enumerable.Range(0, terminal.TotalRows).First(i => terminal.GetRow(i).GetText().Contains("XX0001", StringComparison.Ordinal));
        var (text, cells) = terminal.LogicalLine(row);
        Assert.Equal("/var/folders/jg/abc/T/Linked.cs(3,2): error XX0001: boom", text);
        var link = Assert.Single(TerminalLinks.Find(text), l => l.Kind == TerminalLinkKind.File);
        Assert.Equal(("/var/folders/jg/abc/T/Linked.cs", 3), (link.Target, link.Line));
        var first = Enumerable.Range(0, terminal.TotalRows).First(i => terminal.GetRow(i).GetText().StartsWith("/var/", StringComparison.Ordinal));
        Assert.True(first < row);
        Assert.Equal((first, 0), cells[0]); // the path starts rows above the one with the error code
        Assert.Equal("next", terminal.LogicalLine(row + 1).Text);
    }
}
