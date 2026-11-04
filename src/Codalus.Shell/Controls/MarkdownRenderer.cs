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
/// Renders Markdown (the Markdown preview, plugin Markdown views of ADR 0003, AI Chat's answers, READMEs, descriptions) to
/// native controls, so it follows the theme and stays accessible: ATX and setext headings, paragraphs with hard line breaks,
/// bulleted, numbered and task lists, block quotes (Markdown of their own), fenced code, horizontal rules, GitHub's tables,
/// and inline <c>**bold**</c>, <c>*italic*</c>, <c>~~strikethrough~~</c>, <c>`code`</c>, <c>[links](target)</c>,
/// <c>&lt;https://…&gt;</c> and bare web addresses, images as a link with their description, and <c>&lt;br&gt;</c>.
/// HTML comments aren't shown; other HTML is text. Link targets are <c>https://…</c>, <c>http://…</c> or
/// <c>command:&lt;id&gt;</c>; others render as text. Inline code is a <c>Run.code</c> (the code font on
/// <c>Shell.Badge</c>, ShellStyles.axaml).
/// </summary>
public static partial class MarkdownRenderer
{
    private static readonly FontFamily s_monospace = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Noto Sans Mono, Ubuntu Mono, Liberation Mono, Noto Mono, monospace");

    /// <summary>The code font of inline code and of code outside an editor.</summary>
    public static FontFamily Monospace => s_monospace;

    public static Control Render(string markdown, Action<string> openLink) => Render(markdown, openLink, MarkdownOptions.Default);

    /// <summary>Renders <paramref name="markdown"/> as <paramref name="options"/> say (AI Chat's answers, M34h).</summary>
    public static Control Render(string markdown, Action<string> openLink, MarkdownOptions options)
    {
        var panel = new StackPanel { Spacing = options.Spacing, Margin = options.Margin };
        var blockIndex = 0;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<(string Text, bool Break)>();
        TextBlock Inline(string source) => Text(source, openLink, options.Selectable);

        // A paragraph's lines are one text; a line that ends in two spaces or a backslash breaks it there.
        string ParagraphText()
        {
            var text = new StringBuilder();
            for (var p = 0; p < paragraph.Count; p++)
            {
                text.Append(paragraph[p].Text);
                if (p < paragraph.Count - 1)
                {
                    text.Append(paragraph[p].Break ? '\n' : ' ');
                }
            }

            paragraph.Clear();
            return text.ToString();
        }

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                panel.Children.Add(Inline(ParagraphText()));
            }
        }

        void AddHeading(string source, int level)
        {
            var text = Inline(source);
            text.FontSize = options.CompactHeadings
                ? level switch { 1 => 16, 2 => 14, 3 => 13, _ => 12 }
                : level switch { 1 => 22, 2 => 18, 3 => 15, _ => 13 };
            text.FontWeight = FontWeight.SemiBold;
            text.Margin = new Thickness(0, level <= 2 ? 6 : 2, 0, 0);
            AutomationProperties.SetHeadingLevel(text, level);
            panel.Children.Add(text);
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
                var closed = i < lines.Length; // its closing fence came (an answer being written may not have it yet)
                panel.Children.Add(options.CodeBlock?.Invoke(new MarkdownCodeBlock(blockIndex, fence.Groups[2].Value.Trim(), block, closed)) ?? CodeBlock(block));
                blockIndex++;
            }
            else if (line.Length == 0)
            {
                FlushParagraph();
            }
            else if (line.StartsWith("<!--", StringComparison.Ordinal))
            {
                // An HTML comment isn't shown, to the line that ends it.
                FlushParagraph();
                for (var rest = line[4..]; !rest.Contains("-->", StringComparison.Ordinal) && i + 1 < lines.Length;)
                {
                    rest = lines[++i];
                }
            }
            else if (paragraph.Count > 0 && SetextUnderline().IsMatch(line))
            {
                // The paragraph above is a heading: === under it for level 1, --- for level 2.
                AddHeading(ParagraphText(), line[0] == '=' ? 1 : 2);
            }
            else if (Heading().Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                AddHeading(heading.Groups[2].Value, heading.Groups[1].Length);
            }
            else if (Rule().IsMatch(line))
            {
                FlushParagraph();
                var rule = new Border { Height = 1, Margin = new Thickness(0, 4) };
                rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable("Shell.Border"));
                panel.Children.Add(rule);
            }
            else if (TableAt(lines, i) is { } alignments)
            {
                FlushParagraph();
                var rows = new List<string[]> { Cells(lines[i]) };
                for (i += 2; i < lines.Length && lines[i].Trim() is { Length: > 0 } row && !EndsTable(row); i++)
                {
                    rows.Add(Cells(lines[i]));
                }

                i--;
                panel.Children.Add(Table(rows, alignments, Inline));
            }
            else if (Bullet().Match(line) is { Success: true } bullet)
            {
                FlushParagraph();
                var item = bullet.Groups[1].Value;
                panel.Children.Add(TaskItem().Match(item) is { Success: true } task
                    ? ListItem(task.Groups[1].Value == " " ? "☐" : "☑", Inline(task.Groups[2].Value), depth)
                    : ListItem(depth % 2 == 0 ? "•" : "◦", Inline(item), depth));
            }
            else if (Numbered().Match(line) is { Success: true } numbered)
            {
                FlushParagraph();
                panel.Children.Add(ListItem(numbered.Groups[1].Value + ".", Inline(numbered.Groups[2].Value), depth));
            }
            else if (line.StartsWith('>'))
            {
                // Consecutive quoted lines are one quote, whose content is Markdown of its own (paragraphs, lists, code).
                FlushParagraph();
                var quoted = new List<string> { Unquote(lines[i]) };
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('>'))
                {
                    quoted.Add(Unquote(lines[++i]));
                }

                var content = Render(string.Join('\n', quoted), openLink, new MarkdownOptions
                {
                    Selectable = options.Selectable,
                    CompactHeadings = options.CompactHeadings,
                    Margin = default,
                    Spacing = options.Spacing,
                });
                var quote = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2), Child = content };
                quote.Bind(Border.BorderBrushProperty, quote.GetResourceObservable("Shell.Border"));
                panel.Children.Add(quote);
            }
            else
            {
                var hardBreak = lines[i].EndsWith("  ", StringComparison.Ordinal) || (line.EndsWith('\\') && !line.EndsWith(@"\\", StringComparison.Ordinal));
                paragraph.Add((hardBreak && line.EndsWith('\\') ? line[..^1] : line, hardBreak));
            }
        }

        FlushParagraph();
        return panel;
    }

    /// <summary>A quoted line without its <c>&gt;</c> and the space after it.</summary>
    private static string Unquote(string line)
    {
        var text = line.TrimStart()[1..];
        return text.StartsWith(' ') ? text[1..] : text;
    }

    /// <summary>
    /// The columns' alignments when a table (GitHub's) starts at <paramref name="i"/>: a row with <c>|</c> over a
    /// delimiter row (<c>---</c>, <c>:--</c>, <c>:-:</c>, <c>--:</c>) with as many cells; otherwise null.
    /// </summary>
    private static TextAlignment[]? TableAt(string[] lines, int i)
    {
        if (i + 1 >= lines.Length || !lines[i].Contains('|', StringComparison.Ordinal) || !TableDelimiter().IsMatch(lines[i + 1].Trim()))
        {
            return null;
        }

        var delimiters = Cells(lines[i + 1]);
        return delimiters.Length == Cells(lines[i]).Length
            ? [.. delimiters.Select(d => d.StartsWith(':') && d.EndsWith(':') ? TextAlignment.Center : d.EndsWith(':') ? TextAlignment.Right : TextAlignment.Left)]
            : null;
    }

    /// <summary>A table row's cells: split at each <c>|</c> but an escaped one, without the row's outer pipes.</summary>
    private static string[] Cells(string row)
    {
        var line = row.Trim();
        if (line.StartsWith('|'))
        {
            line = line[1..];
        }

        if (line.EndsWith('|') && !line.EndsWith(@"\|", StringComparison.Ordinal))
        {
            line = line[..^1];
        }

        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var k = 0; k < line.Length; k++)
        {
            if (line[k] == '\\' && k + 1 < line.Length && line[k + 1] == '|')
            {
                // An escaped pipe is the cell's own, in code spans too.
                cell.Append('|');
                k++;
            }
            else if (line[k] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(line[k]);
            }
        }

        cells.Add(cell.ToString().Trim());
        return [.. cells];
    }

    /// <summary>A line that starts another block ends a table; any other line until a blank one is a row.</summary>
    private static bool EndsTable(string line) =>
        line.StartsWith('>') || line.StartsWith("<!--", StringComparison.Ordinal) || Fence().IsMatch(line) || Heading().IsMatch(line) || Rule().IsMatch(line);

    /// <summary>
    /// A table: its first row the header (semibold, on <c>Shell.Hover</c>), every cell bordered in <c>Shell.Border</c> and
    /// aligned as its column's delimiter says. A row with fewer cells gets empty ones; more are left out.
    /// </summary>
    private static Border Table(List<string[]> rows, TextAlignment[] alignments, Func<string, TextBlock> inline)
    {
        var table = new MarkdownTable(alignments.Length);
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < alignments.Length; c++)
            {
                var text = inline(c < rows[r].Length ? rows[r][c] : string.Empty);

                // Its words stay whole: the table makes each column at least as wide as its longest word.
                text.TextWrapping = TextWrapping.WrapWithOverflow;
                text.TextAlignment = alignments[c];
                var cell = new Border { BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(8, 4), Child = text };
                cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("Shell.Border"));
                if (r == 0)
                {
                    text.FontWeight = FontWeight.SemiBold;
                    cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable("Shell.Hover"));
                }

                table.Children.Add(cell);
            }
        }

        var frame = new Border { BorderThickness = new Thickness(1, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = table };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Shell.Border"));
        return frame;
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
        var struck = false;

        void Flush()
        {
            if (run.Length > 0)
            {
                // Only what the source marks is set, so a heading's or a table header's weight carries to its runs.
                var text = new Run(run.ToString());
                if (bold)
                {
                    text.FontWeight = FontWeight.Bold;
                }

                if (italic)
                {
                    text.FontStyle = FontStyle.Italic;
                }

                if (struck)
                {
                    text.TextDecorations = TextDecorations.Strikethrough;
                }

                inlines.Add(text);
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
            else if (c == '\n')
            {
                // A paragraph's hard line break.
                Flush();
                inlines.Add(new LineBreak());
            }
            else if (c == '<' && LineBreakTag().Match(source, i) is { Success: true } tag)
            {
                // HTML's <br>, common in table cells.
                Flush();
                inlines.Add(new LineBreak());
                i += tag.Length - 1;
            }
            else if (c == '`' && source.IndexOf('`', i + 1) is var end and > 0)
            {
                Flush();
                inlines.Add(new Run(source[(i + 1)..end]) { FontFamily = s_monospace, Classes = { "code" } });
                i = end;
            }
            else if (c == '_' && i + 1 < source.Length && source[i + 1] == '_' && IsIntraWordPair(source, i))
            {
                run.Append("__");
                i++;
            }
            else if ((c == '*' || c == '_') && i + 1 < source.Length && source[i + 1] == c)
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
            else if (c == '~' && i + 1 < source.Length && source[i + 1] == '~')
            {
                Flush();
                struck = !struck;
                i++;
            }
            else if (c == '!' && Link().Match(source, i + 1) is { Success: true } image)
            {
                // An image: its description, a link to it when it's on the web (images aren't loaded).
                Flush();
                inlines.Add(LinkInline(image.Groups[1].Value.Length > 0 ? image.Groups[1].Value : image.Groups[2].Value, image.Groups[2].Value, openLink));
                i += image.Length;
            }
            else if (c == '[' && Link().Match(source, i) is { Success: true } link && link.Groups[1].Length > 0)
            {
                Flush();
                inlines.Add(LinkInline(link.Groups[1].Value, link.Groups[2].Value, openLink));
                i += link.Length - 1;
            }
            else if (c == '<' && AutoLink().Match(source, i) is { Success: true } autoLink)
            {
                Flush();
                inlines.Add(LinkInline(autoLink.Groups[1].Value, autoLink.Groups[1].Value, openLink));
                i += autoLink.Length - 1;
            }
            else if (c == 'h' && (i == 0 || !char.IsLetterOrDigit(source[i - 1])) && Url().Match(source, i) is { Success: true } url)
            {
                // A bare web address, without the punctuation after it.
                var address = TrimUrl(url.Value);
                Flush();
                inlines.Add(LinkInline(address, address, openLink));
                i += address.Length - 1;
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

    /// <summary>Two underscores inside a word (<c>a__b</c>) are literal.</summary>
    private static bool IsIntraWordPair(string source, int i) =>
        i > 0 && i + 2 < source.Length && char.IsLetterOrDigit(source[i - 1]) && char.IsLetterOrDigit(source[i + 2]);

    /// <summary>A bare address without the punctuation that ends its sentence, nor a parenthesis it doesn't open.</summary>
    private static string TrimUrl(string url)
    {
        while (url.Length > 0 && (url[^1] is '.' or ',' or ':' or ';' or '!' or '?' or '*' or '_' or '~' or '\'' or '"'
            || (url[^1] == ')' && url.Count(ch => ch == ')') > url.Count(ch => ch == '('))))
        {
            url = url[..^1];
        }

        return url;
    }

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

    /// <summary>An ATX heading; closing <c>#</c>s after a space aren't its text.</summary>
    [GeneratedRegex(@"^(#{1,6})\s+(.*?)(?:\s+#+)?$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(=+|-+)$")]
    private static partial Regex SetextUnderline();

    /// <summary>Three or more of one of <c>-</c>, <c>*</c> or <c>_</c>, spaces between them allowed.</summary>
    [GeneratedRegex(@"^([-*_])(?:\s*\1){2,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?$")]
    private static partial Regex TableDelimiter();

    [GeneratedRegex(@"^[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    /// <summary>A task list item's box, <c>[ ]</c> or <c>[x]</c>, and its text.</summary>
    [GeneratedRegex(@"^\[([ xX])\]\s+(.*)$")]
    private static partial Regex TaskItem();

    [GeneratedRegex(@"^(\d+)[.)]\s+(.*)$")]
    private static partial Regex Numbered();

    /// <summary>A link where the match starts: its text and target, an optional title after the target ignored.</summary>
    [GeneratedRegex(@"\G\[([^\]]*)\]\(\s*<?([^)\s>]+)>?(?:\s+(?:""[^""]*""|'[^']*'))?\s*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\G<(https?://[^\s<>]+)>")]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"\Ghttps?://[^\s<>]+")]
    private static partial Regex Url();

    [GeneratedRegex(@"\G<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();
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

    /// <summary>A fenced code block's control; null draws the plain block.</summary>
    public Func<MarkdownCodeBlock, Control>? CodeBlock { get; init; }

    public Thickness Margin { get; init; } = new(12);

    /// <summary>The space between blocks.</summary>
    public double Spacing { get; init; } = 8;
}

/// <summary>A fenced code block of rendered Markdown, for <see cref="MarkdownOptions.CodeBlock"/>.</summary>
/// <param name="Index">Its index, from 0, in the order of <c>AiCodeBlocks.Parse</c>.</param>
/// <param name="Info">Its info string: the language and any <c>path=</c>.</param>
/// <param name="Code">Its code.</param>
/// <param name="IsClosed">Its closing fence came: the block is complete even while the text around it is still written.</param>
public sealed record MarkdownCodeBlock(int Index, string Info, string Code, bool IsClosed);
