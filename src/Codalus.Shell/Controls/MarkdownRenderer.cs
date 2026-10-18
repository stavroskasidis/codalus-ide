using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Codalus.Shell.Controls;

/// <summary>
/// Renders the Markdown subset of plugin Markdown views (ADR 0003) to native controls, so they follow the theme
/// and stay accessible: ATX headings, paragraphs, bulleted and numbered lists, block quotes, fenced code,
/// horizontal rules, and inline <c>**bold**</c>, <c>*italic*</c>, <c>`code`</c> and <c>[links](target)</c>.
/// Link targets are <c>https://…</c>, <c>http://…</c> or <c>command:&lt;id&gt;</c>; others render as text. Inline code is a
/// <c>Run.code</c> (the code font on <c>Shell.Badge</c>, ShellStyles.axaml).
/// </summary>
public static partial class MarkdownRenderer
{
    private static readonly FontFamily s_monospace = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    /// <summary>The code font of inline code and of code outside an editor.</summary>
    public static FontFamily Monospace => s_monospace;

    public static Control Render(string markdown, Action<string> openLink) => Render(markdown, openLink, MarkdownOptions.Default);

    /// <summary>Renders <paramref name="markdown"/> as <paramref name="options"/> say (AI Chat's answers, M34h).</summary>
    public static Control Render(string markdown, Action<string> openLink, MarkdownOptions options)
    {
        var panel = new StackPanel { Spacing = options.Spacing, Margin = options.Margin };
        var blockIndex = 0;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<string>();
        TextBlock Inline(string source) => Text(source, openLink, options.Selectable);

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                panel.Children.Add(Inline(string.Join(' ', paragraph)));
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // A nested list item keeps its depth: two spaces (or a tab) of indentation a level.
            var depth = Math.Min(4, lines[i].Replace("\t", "  ", StringComparison.Ordinal).TakeWhile(c => c == ' ').Count() / 2);
            if (Fence().Match(line) is { Success: true } fence)
            {
                FlushParagraph();
                var marker = fence.Groups[1].Value;
                var code = new List<string>();
                while (++i < lines.Length && !(lines[i].Trim().StartsWith(marker, StringComparison.Ordinal) && lines[i].Trim().Trim(marker[0]).Length == 0))
                {
                    code.Add(lines[i]);
                }

                var block = string.Join('\n', code);
                panel.Children.Add(options.CodeBlock?.Invoke(blockIndex, fence.Groups[2].Value.Trim(), block) ?? CodeBlock(block));
                blockIndex++;
            }
            else if (line.Length == 0)
            {
                FlushParagraph();
            }
            else if (Heading().Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                var level = heading.Groups[1].Length;
                var text = Inline(heading.Groups[2].Value);
                text.FontSize = options.CompactHeadings
                    ? level switch { 1 => 16, 2 => 14, 3 => 13, _ => 12 }
                    : level switch { 1 => 22, 2 => 18, 3 => 15, _ => 13 };
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
                panel.Children.Add(ListItem(depth % 2 == 0 ? "•" : "◦", Inline(bullet.Groups[1].Value), depth));
            }
            else if (Numbered().Match(line) is { Success: true } numbered)
            {
                FlushParagraph();
                panel.Children.Add(ListItem(numbered.Groups[1].Value + ".", Inline(numbered.Groups[2].Value), depth));
            }
            else if (line.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2), Child = Inline(line.TrimStart('>', ' ')) };
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

    private static Grid ListItem(string marker, Control content, int depth)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(depth * 16, 0, 0, 0) };
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

    /// <summary>A wrapped text block with inline emphasis, code spans and links; selectable for AI Chat's answers.</summary>
    private static TextBlock Text(string source, Action<string> openLink, bool selectable)
    {
        var block = selectable ? new SelectableTextBlock { TextWrapping = TextWrapping.Wrap } : new TextBlock { TextWrapping = TextWrapping.Wrap };
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
            if (c == '\\' && i + 1 < source.Length && IsEscapable(source[i + 1]))
            {
                // A backslash escape: the next character is literal.
                run.Append(source[++i]);
            }
            else if (c == '`' && source.IndexOf('`', i + 1) is var end and > 0)
            {
                Flush();
                inlines.Add(new Run(source[(i + 1)..end]) { FontFamily = s_monospace, Classes = { "code" } });
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

    /// <summary>CommonMark's escapable characters: ASCII punctuation.</summary>
    private static bool IsEscapable(char c) => c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';

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

    [GeneratedRegex(@"^(`{3,}|~{3,})([^`]*)$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\d+)[.)]\s+(.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex Link();
}

/// <summary>How <see cref="MarkdownRenderer"/> renders for a host other than a Markdown view (AI Chat's answers, M34h).</summary>
public sealed class MarkdownOptions
{
    /// <summary>A Markdown view's: a document's headings, 12 px around, plain code blocks.</summary>
    public static MarkdownOptions Default { get; } = new();

    /// <summary>The text can be selected and copied (each paragraph, heading and list item).</summary>
    public bool Selectable { get; init; }

    /// <summary>Headings sized for a tool window (16, 14, 13 and 12 px) instead of a document's.</summary>
    public bool CompactHeadings { get; init; }

    /// <summary>
    /// A fenced code block's control, from its index (from 0, in the order of <c>AiCodeBlocks.Parse</c>), its info string
    /// (the language and any <c>path=</c>) and its code; null draws the plain block.
    /// </summary>
    public Func<int, string, string, Control>? CodeBlock { get; init; }

    public Thickness Margin { get; init; } = new(12);

    /// <summary>The space between blocks.</summary>
    public double Spacing { get; init; } = 8;
}
