using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Daedalus.Shell.Controls;

/// <summary>
/// Renders the Markdown subset of plugin Markdown views (ADR 0003) to native controls, so they follow the theme
/// and stay accessible: ATX headings, paragraphs, bulleted and numbered lists, block quotes, fenced code,
/// horizontal rules, and inline <c>**bold**</c>, <c>*italic*</c>, <c>`code`</c> and <c>[links](target)</c>.
/// Link targets are <c>https://…</c>, <c>http://…</c> or <c>command:&lt;id&gt;</c>; others render as text.
/// </summary>
public static partial class MarkdownRenderer
{
    private static readonly FontFamily s_monospace = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    public static Control Render(string markdown, Action<string> openLink)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                panel.Children.Add(Text(string.Join(' ', paragraph), openLink));
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new List<string>();
                while (++i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                }

                panel.Children.Add(CodeBlock(string.Join('\n', code)));
            }
            else if (line.Length == 0)
            {
                FlushParagraph();
            }
            else if (Heading().Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                var level = heading.Groups[1].Length;
                var text = Text(heading.Groups[2].Value, openLink);
                text.FontSize = level switch { 1 => 22, 2 => 18, 3 => 15, _ => 13 };
                text.FontWeight = FontWeight.SemiBold;
                text.Margin = new Thickness(0, level <= 2 ? 6 : 2, 0, 0);
                AutomationProperties.SetHeadingLevel(text, level);
                panel.Children.Add(text);
            }
            else if (line is "---" or "***" or "___")
            {
                FlushParagraph();
                var rule = new Border { Height = 1, Margin = new Thickness(0, 4) };
                rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable("Shell.Border"));
                panel.Children.Add(rule);
            }
            else if (Bullet().Match(line) is { Success: true } bullet)
            {
                FlushParagraph();
                panel.Children.Add(ListItem("•", Text(bullet.Groups[1].Value, openLink)));
            }
            else if (Numbered().Match(line) is { Success: true } numbered)
            {
                FlushParagraph();
                panel.Children.Add(ListItem(numbered.Groups[1].Value + ".", Text(numbered.Groups[2].Value, openLink)));
            }
            else if (line.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2), Child = Text(line.TrimStart('>', ' '), openLink) };
                quote.Bind(Border.BorderBrushProperty, quote.GetResourceObservable("Shell.Border"));
                panel.Children.Add(quote);
            }
            else
            {
                paragraph.Add(line);
            }
        }

        FlushParagraph();
        return panel;
    }

    private static Grid ListItem(string marker, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var bullet = new TextBlock { Text = marker, Margin = new Thickness(4, 0, 8, 0), MinWidth = 12 };
        Grid.SetColumn(content, 1);
        grid.Children.Add(bullet);
        grid.Children.Add(content);
        return grid;
    }

    private static ScrollViewer CodeBlock(string code)
    {
        var border = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(4),
            Child = new SelectableTextBlock { Text = code, FontFamily = s_monospace, TextWrapping = TextWrapping.NoWrap },
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Shell.Hover"));
        return new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = border };
    }

    /// <summary>A wrapped text block with inline emphasis, code spans and links.</summary>
    private static TextBlock Text(string source, Action<string> openLink)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var inlines = new InlineCollection();
        var run = new StringBuilder();
        var bold = false;
        var italic = false;

        void Flush()
        {
            if (run.Length > 0)
            {
                inlines.Add(new Run(run.ToString())
                {
                    FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                    FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
                });
                run.Clear();
            }
        }

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '`' && source.IndexOf('`', i + 1) is var end and > 0)
            {
                Flush();
                inlines.Add(new Run(source[(i + 1)..end]) { FontFamily = s_monospace });
                i = end;
            }
            else if (c == '*' && i + 1 < source.Length && source[i + 1] == '*')
            {
                Flush();
                bold = !bold;
                i++;
            }
            else if ((c == '*' || (c == '_' && !IsIntraWord(source, i))) && Toggles(source, i, italic))
            {
                Flush();
                italic = !italic;
            }
            else if (c == '[' && Link().Match(source, i) is { Success: true, Index: var index } link && index == i)
            {
                Flush();
                inlines.Add(LinkInline(link.Groups[1].Value, link.Groups[2].Value, openLink));
                i += link.Length - 1;
            }
            else
            {
                run.Append(c);
            }
        }

        Flush();
        block.Inlines = inlines;
        return block;
    }

    /// <summary>An emphasis marker opens only before a non-space and closes only after one.</summary>
    private static bool Toggles(string source, int i, bool open) =>
        open ? i > 0 && !char.IsWhiteSpace(source[i - 1]) : i + 1 < source.Length && !char.IsWhiteSpace(source[i + 1]);

    /// <summary>Underscores inside words (snake_case) are literal.</summary>
    private static bool IsIntraWord(string source, int i) =>
        i > 0 && i + 1 < source.Length && char.IsLetterOrDigit(source[i - 1]) && char.IsLetterOrDigit(source[i + 1]);

    private static Inline LinkInline(string text, string target, Action<string> openLink)
    {
        if (!target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("command:", StringComparison.Ordinal))
        {
            return new Run(text);
        }

        var button = new Button { Content = text, VerticalAlignment = VerticalAlignment.Bottom };
        button.Classes.Add("link");
        ToolTip.SetTip(button, target);
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => openLink(target);
        return new InlineUIContainer(button);
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\d+)[.)]\s+(.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex Link();
}
