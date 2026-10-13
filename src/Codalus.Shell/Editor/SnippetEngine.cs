using AvaloniaEdit.Editing;
using AvaloniaEdit.Snippets;
using Codalus.Workbench.Snippets;
using Snippet = AvaloniaEdit.Snippets.Snippet;

namespace Codalus.Shell.Editor;

/// <summary>
/// Inserts TextMate/VS Code snippets with AvaloniaEdit's snippet engine (M12b): placeholders become fields that Tab
/// and Shift+Tab move between, repeated placeholders mirror the first, <c>$0</c> is where the caret ends (Enter or
/// Escape), and <c>$TM_SELECTED_TEXT</c> is the selection (Surround With). Fields are visited in document order.
/// </summary>
public static class SnippetEngine
{
    /// <summary>
    /// Replaces <paramref name="length"/> characters at <paramref name="offset"/> (a completed prefix) with the snippet.
    /// With <paramref name="keepSelection"/>, the editor's selection is the snippet's <c>$TM_SELECTED_TEXT</c>.
    /// </summary>
    public static void Insert(TextArea area, string body, int offset, int length, Func<string, string?> variables, bool keepSelection = false)
    {
        var document = area.Document;
        if (!keepSelection)
        {
            if (length > 0)
            {
                document.Remove(offset, length);
            }

            area.ClearSelection();
            area.Caret.Offset = offset;
        }

        Build(body, variables).Insert(area);
    }

    /// <summary>The AvaloniaEdit snippet for a body (tests).</summary>
    public static Snippet Build(string body, Func<string, string?> variables)
    {
        var snippet = new Snippet();
        var fields = new Dictionary<int, SnippetReplaceableTextElement>();
        var hasCaret = false;
        foreach (var part in SnippetParser.Parse(body))
        {
            switch (part)
            {
                case SnippetText text:
                    snippet.Elements.Add(new SnippetTextElement { Text = text.Text });
                    break;
                case SnippetPlaceholder { Index: 0 }:
                    if (!hasCaret)
                    {
                        snippet.Elements.Add(new SnippetCaretElement());
                        hasCaret = true;
                    }

                    break;
                case SnippetPlaceholder placeholder when fields.TryGetValue(placeholder.Index, out var target):
                    snippet.Elements.Add(new SnippetBoundElement { TargetElement = target });
                    break;
                case SnippetPlaceholder placeholder:
                    var field = new SnippetReplaceableTextElement { Text = placeholder.Default };
                    fields[placeholder.Index] = field;
                    snippet.Elements.Add(field);
                    break;
                case SnippetVariable { Name: "TM_SELECTED_TEXT" or "SELECTION" }:
                    snippet.Elements.Add(new SnippetSelectionElement());
                    break;
                case SnippetVariable variable:
                    snippet.Elements.Add(new SnippetTextElement { Text = variables(variable.Name) ?? variable.Default });
                    break;
            }
        }

        return snippet;
    }

    /// <summary>The standard variables for a document (<c>TM_FILENAME</c>, <c>CURRENT_YEAR</c>, <c>UUID</c>…); null when unknown.</summary>
    public static Func<string, string?> Variables(string? path, TextArea area, string? lineComment, (string Open, string Close)? blockComment, string? clipboard = null) => name =>
    {
        var document = area.Document;
        var line = document.GetLineByOffset(area.Caret.Offset);
        var now = DateTime.Now;
        return name switch
        {
            "TM_FILENAME" => path is null ? null : Path.GetFileName(path),
            "TM_FILENAME_BASE" => path is null ? null : Path.GetFileNameWithoutExtension(path),
            "TM_DIRECTORY" => path is null ? null : Path.GetDirectoryName(path),
            "TM_DIRECTORY_NAME" => path is null ? null : Path.GetFileName(Path.GetDirectoryName(path)),
            "TM_FILEPATH" => path,
            "TM_LINE_INDEX" => (line.LineNumber - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "TM_LINE_NUMBER" => line.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "TM_CURRENT_LINE" => document.GetText(line),
            "TM_CURRENT_WORD" => CurrentWord(area),
            "CLIPBOARD" => clipboard,
            "CURRENT_YEAR" => now.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_YEAR_SHORT" => now.ToString("yy", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_MONTH" => now.ToString("MM", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_MONTH_NAME" => now.ToString("MMMM", System.Globalization.CultureInfo.CurrentCulture),
            "CURRENT_DATE" => now.ToString("dd", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_DAY_NAME" => now.ToString("dddd", System.Globalization.CultureInfo.CurrentCulture),
            "CURRENT_HOUR" => now.ToString("HH", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_MINUTE" => now.ToString("mm", System.Globalization.CultureInfo.InvariantCulture),
            "CURRENT_SECOND" => now.ToString("ss", System.Globalization.CultureInfo.InvariantCulture),
            "UUID" => Guid.NewGuid().ToString(),
            "LINE_COMMENT" => lineComment,
            "BLOCK_COMMENT_START" => blockComment?.Open,
            "BLOCK_COMMENT_END" => blockComment?.Close,
            _ => null,
        };
    };

    private static string CurrentWord(TextArea area)
    {
        var document = area.Document;
        var start = area.Caret.Offset;
        var end = start;
        while (start > 0 && (char.IsLetterOrDigit(document.GetCharAt(start - 1)) || document.GetCharAt(start - 1) == '_'))
        {
            start--;
        }

        while (end < document.TextLength && (char.IsLetterOrDigit(document.GetCharAt(end)) || document.GetCharAt(end) == '_'))
        {
            end++;
        }

        return document.GetText(start, end - start);
    }
}
