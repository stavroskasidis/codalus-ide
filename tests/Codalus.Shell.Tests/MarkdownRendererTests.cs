using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Tests;

/// <summary>
/// The shell's Markdown (the preview, Markdown views, AI Chat, READMEs): GitHub's tables, laid out to the width they have,
/// and the blocks and inlines a README or a CLAUDE.md uses beyond the first subset (setext headings, rules, quotes of more
/// than one line, task lists, hard line breaks, strikethrough, autolinks, images, HTML comments).
/// </summary>
public sealed class MarkdownRendererTests
{
    /// <summary>The table of the report: a short column of names over a long one of notes.</summary>
    private const string DemoApps = """
        ## Demo Apps

        | App | Notes |
        |---|---|
        | `DemoStandardApp` | wStandard web app. Login: UserName + Password, submit `#btnLogin`. Runs on http://localhost:5100 against its own database, migrated on start. |
        | `DemoApp` | wPoseidon web app. Login: WorkArea + UserName + Password, submit `#btnLogin`. Work area `Dev1` is a cloud server (no local one needed). |
        | `DemoAppLite` | wPoseidon Lite (mobile). Two front ends over one backend. |
        | `DemoStandardAppLite` | wStandard Lite (no wPoseidon). |
        | `DocumentationApp` | Documents and showcases the framework. |

        ### Dev auto login
        """;

    private sealed record Shown(Window Window, Control Content, List<string> Opened) : IDisposable
    {
        public List<TextBlock> Texts => [.. Content.GetVisualDescendants().OfType<TextBlock>()];

        public TextBlock Text(string text) => Texts.Single(t => Plain(t) == text);

        public void Dispose() => Window.Close();
    }

    private static Shown Show(string markdown, double width = 600)
    {
        var opened = new List<string>();
        var content = MarkdownRenderer.Render(markdown, opened.Add);
        var window = new Window
        {
            Width = width,
            Height = 800,
            Content = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = content },
        };
        window.Show();
        ShellFixture.Pump();
        return new Shown(window, content, opened);
    }

    private static string Plain(TextBlock block) => block.Text ?? block.Inlines?.Text ?? string.Empty;

    [AvaloniaFact]
    public void A_table_renders_as_a_grid_of_cells_with_its_header_and_its_long_column_wrapping()
    {
        using var s = Show(DemoApps);

        var table = Assert.Single(s.Content.GetVisualDescendants().OfType<MarkdownTable>());
        Assert.Equal(2, table.Columns);
        Assert.Equal(12, table.Children.Count);
        string[] apps = ["App", "DemoStandardApp", "DemoApp", "DemoAppLite", "DemoStandardAppLite", "DocumentationApp"];
        Assert.Equal(apps, table.Children.Where((_, k) => k % 2 == 0).Select(c => Plain((TextBlock)((Border)c).Child!)));

        // The header: semibold on Shell.Hover; the cells bordered in Shell.Border.
        var header = (Border)table.Children[0];
        Assert.Equal(FontWeight.SemiBold, ((TextBlock)header.Child!).FontWeight);
        Assert.Equal(Resource(s.Window, "Shell.Hover"), ((ISolidColorBrush)header.Background!).Color);
        Assert.All(table.Children.Cast<Border>(), c => Assert.Equal(Resource(s.Window, "Shell.Border"), ((ISolidColorBrush)c.BorderBrush!).Color));
        Assert.Null(((Border)table.Children[2]).Background);

        // The names fit on one line; the notes take the rest of the width and wrap, no wider than the view.
        var names = table.Children.Where((_, k) => k % 2 == 0).Select(c => (TextBlock)((Border)c).Child!).ToList();
        Assert.All(names, n => Assert.True(n.TextLayout.TextLines.Count == 1, $"{Plain(n)} wrapped: {n.TextLayout.TextLines.Count} lines"));
        var notes = (TextBlock)((Border)table.Children[3]).Child!;
        Assert.True(notes.TextLayout.TextLines.Count >= 2, $"the notes didn't wrap: {notes.TextLayout.TextLines.Count} lines");
        Assert.True(table.Bounds.Width <= 600 - 24, $"the table is wider than the view: {table.Bounds.Width}");
        Assert.True(table.Bounds.Width > 500, $"the table doesn't use the width: {table.Bounds.Width}");
        Assert.Equal(names.Max(n => n.DesiredSize.Width) + 16 + 1, table.ColumnWidths[0], tolerance: 1.0);

        // The cells of a row are as high as the row, so their borders line up; code spans and links stay inline.
        Assert.Equal(table.Children[2].Bounds.Height, table.Children[3].Bounds.Height);
        Assert.Contains(notes.Inlines!.OfType<Run>(), r => r.Text == "#btnLogin" && r.Classes.Contains("code"));
        Assert.Contains(notes.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "http://localhost:5100");

        // The blocks around it are untouched.
        Assert.Equal(2, AutomationProperties.GetHeadingLevel(s.Text("Demo Apps")));
        Assert.Equal(3, AutomationProperties.GetHeadingLevel(s.Text("Dev auto login")));
    }

    [AvaloniaFact]
    public void A_table_aligns_its_columns_and_reads_escaped_pipes_short_rows_and_rows_without_outer_pipes()
    {
        using var s = Show("""
            Text just above the table.
            Left | Center | Right
            :--- | :---: | ---:
            a \| b | `x \| y` | 3
            only one
            """);

        Assert.Equal("Text just above the table.", Plain(s.Texts[0]));
        var table = Assert.Single(s.Content.GetVisualDescendants().OfType<MarkdownTable>());
        var cells = table.Children.Cast<Border>().Select(c => (TextBlock)c.Child!).ToList();
        Assert.Equal(9, cells.Count);
        Assert.Equal(new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right }, cells.Take(3).Select(c => c.TextAlignment));
        string[] rows = ["a | b", "x | y", "3", "only one", "", ""];
        Assert.Equal(rows, cells.Skip(3).Select(Plain));
        Assert.Contains("code", cells[4].Inlines!.OfType<Run>().Single().Classes);
    }

    [AvaloniaTheory]
    [InlineData("a | b\nnot a delimiter")]
    [InlineData("a | b\n---|---|---")]
    [InlineData("no pipe\n---")]
    public void Lines_that_are_not_a_table_render_as_before(string markdown)
    {
        using var s = Show(markdown);

        Assert.Empty(s.Content.GetVisualDescendants().OfType<MarkdownTable>());
    }

    [AvaloniaFact]
    public void Table_widths_are_the_widest_when_they_fit_else_the_longest_word_and_a_share_of_the_rest()
    {
        Assert.Equal(new double[] { 100, 200 }, MarkdownTable.Widths([50, 50], [100, 200], 400));
        Assert.Equal(new double[] { 100, 200 }, MarkdownTable.Widths([50, 50], [100, 200], double.PositiveInfinity));
        Assert.Equal(new double[] { 50, 80 }, MarkdownTable.Widths([50, 80], [100, 900], 100));

        // 300 to share: the second column wants 850 more of the 900 more the two want.
        Assert.Equal(new double[] { 66, 333 }, MarkdownTable.Widths([50, 50], [100, 900], 400));
    }

    [AvaloniaFact]
    public void Setext_headings_rules_and_closing_hashes_render_and_headings_keep_their_weight()
    {
        using var s = Show("""
            Title
            =====

            Section **bold**
            ---

            ## Closed ##

            - - -

            *****

            ## C#
            """);

        var title = s.Text("Title");
        Assert.Equal(1, AutomationProperties.GetHeadingLevel(title));
        var section = s.Text("Section bold");
        Assert.Equal(2, AutomationProperties.GetHeadingLevel(section));

        // A heading's runs take its weight, unless the source makes them bold.
        Assert.Equal(FontWeight.SemiBold, section.FontWeight);
        Assert.False(section.Inlines![0].IsSet(TextElement.FontWeightProperty));
        Assert.Equal(FontWeight.Bold, section.Inlines[1].FontWeight);

        Assert.Equal(2, AutomationProperties.GetHeadingLevel(s.Text("Closed")));
        Assert.Equal(2, AutomationProperties.GetHeadingLevel(s.Text("C#")));
        Assert.Equal(2, s.Content.GetVisualDescendants().OfType<Border>().Count(b => b.Height == 1));
    }

    [AvaloniaFact]
    public void Consecutive_quoted_lines_are_one_quote_of_their_own_markdown()
    {
        using var s = Show("""
            > **Note**
            > the first paragraph
            >
            > - a point
            """);

        var quote = Assert.Single(s.Content.GetVisualDescendants().OfType<Border>(), b => b.BorderThickness.Left == 3);
        var texts = quote.GetVisualDescendants().OfType<TextBlock>().Select(Plain).ToList();
        string[] expected = ["Note the first paragraph", "•", "a point"];
        Assert.Equal(expected, texts);
    }

    [AvaloniaFact]
    public void Task_lists_strikethrough_underscores_and_line_breaks_render()
    {
        using var s = Show(string.Join('\n',
            "- [ ] to do",
            "- [x] done",
            "",
            "~~gone~~ __strong__ snake__case_name",
            "first line  ",
            @"second line\",
            "third line<br>fourth",
            "<!-- a comment",
            "over lines -->",
            "after"));

        Assert.Contains(s.Texts, t => Plain(t) == "☐");
        Assert.Contains(s.Texts, t => Plain(t) == "☑");
        Assert.Contains(s.Texts, t => Plain(t) == "to do");
        Assert.DoesNotContain(s.Texts, t => Plain(t).Contains("[x]", StringComparison.Ordinal));

        var paragraph = s.Texts.Single(t => Plain(t).StartsWith("gone", StringComparison.Ordinal));
        var runs = paragraph.Inlines!.ToList();
        Assert.Equal(TextDecorations.Strikethrough, ((Run)runs[0]).TextDecorations);
        Assert.Contains(runs.OfType<Run>(), r => r.Text == "strong" && r.FontWeight == FontWeight.Bold);
        Assert.Contains(runs.OfType<Run>(), r => r.Text!.Contains("snake__case_name", StringComparison.Ordinal));
        Assert.Equal(3, runs.OfType<LineBreak>().Count());
        Assert.DoesNotContain("comment", Plain(paragraph), StringComparison.Ordinal);
        Assert.Contains(s.Texts, t => Plain(t) == "after");
        Assert.DoesNotContain(s.Texts, t => Plain(t).Contains("comment", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Autolinks_bare_addresses_images_and_links_with_titles_are_links()
    {
        using var s = Show("""
            See <https://example.com/a>, https://example.com/b. (https://example.com/c) and [docs](https://example.com/d "Docs").
            ![The logo](https://example.com/logo.png) ![local](images/a.png)
            """);

        var links = s.Content.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("link")).ToList();
        string[] names = ["https://example.com/a", "https://example.com/b", "https://example.com/c", "docs", "The logo"];
        Assert.Equal(names, links.Select(AutomationProperties.GetName));
        links[3].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("https://example.com/d", Assert.Single(s.Opened));

        // A local image is its description (relative targets aren't links yet), without the "!".
        var text = Plain(s.Texts.Single(t => Plain(t).StartsWith("See", StringComparison.Ordinal)));
        Assert.Contains("local", text, StringComparison.Ordinal);
        Assert.DoesNotContain("!", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
        Assert.Contains(") and", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Backslash_escapes_are_literal()
    {
        using var s = Show(@"a \*b\* \_c\_ **d**");

        Assert.Equal("a *b* _c_ d", string.Concat(s.Texts.Single().Inlines!.OfType<Run>().Select(r => r.Text)));
    }

    private static Color Resource(Window window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out var value) && value is ISolidColorBrush brush ? brush.Color : default;
}
