using System.Text.RegularExpressions;

namespace Codalus.Shell.Tests;

/// <summary>
/// Text beside an icon is centered on it (M65's gallery review, 2026-11-18): in a horizontal <c>StackPanel</c> a
/// <c>TextBlock</c> is stretched to the row's height and draws its text at the top, while the icon is centered. Where the
/// row is taller than the text, as with macOS's font in the Error List's filters, the text sat above its icon; on Windows
/// and Linux the heights matched and nothing showed. Text that wraps keeps its own alignment.
/// </summary>
public sealed partial class IconTextAlignmentTests
{
    [Fact]
    public void Text_beside_an_icon_in_a_horizontal_stack_panel_is_centered()
    {
        var uncentered = new List<string>();
        var views = Directory.EnumerateFiles(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell"), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        foreach (var file in views)
        {
            var text = File.ReadAllText(file);
            foreach (Match panel in HorizontalStackPanel().Matches(text))
            {
                var body = panel.Groups[1].Value;
                if (!body.Contains("<controls:Icon", StringComparison.Ordinal) || body.Contains("<StackPanel", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match block in TextBlockTag().Matches(body))
                {
                    if (!block.Value.Contains("VerticalAlignment", StringComparison.Ordinal) && !block.Value.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal))
                    {
                        uncentered.Add($"{Path.GetFileName(file)}:{text[..panel.Index].Count(c => c == '\n') + 1} {block.Value}");
                    }
                }
            }
        }

        Assert.True(uncentered.Count == 0, "Set VerticalAlignment=\"Center\" on the text beside an icon:\n" + string.Join('\n', uncentered));
    }

    /// <summary>The Error List's rows: the severity icon and the row's text on one line.</summary>
    [Fact]
    public void The_error_lists_row_centers_its_text_on_the_severity_icon()
    {
        var view = File.ReadAllText(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "Views", "ErrorListView.axaml"));
        var row = view[view.IndexOf("<Grid ColumnDefinitions=\"28,90,*,140,200,130\"", StringComparison.Ordinal)..];
        row = row[..row.IndexOf("</Grid>", StringComparison.Ordinal)];
        var blocks = TextBlockTag().Matches(row);
        Assert.Equal(5, blocks.Count);
        Assert.All(blocks, b => Assert.Contains("VerticalAlignment=\"Center\"", b.Value, StringComparison.Ordinal));
    }

    [GeneratedRegex("<StackPanel[^>]*Orientation=\"Horizontal\"[^>]*>(.*?)</StackPanel>", RegexOptions.Singleline)]
    private static partial Regex HorizontalStackPanel();

    [GeneratedRegex(@"<TextBlock\b[^>]*?/?>", RegexOptions.Singleline)]
    private static partial Regex TextBlockTag();
}
