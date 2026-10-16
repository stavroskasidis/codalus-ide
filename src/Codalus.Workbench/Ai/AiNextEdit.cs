using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Ai;

/// <summary>An edit the user made: the old and new text of the lines it touched.</summary>
/// <param name="Line">The first changed line (1-based).</param>
/// <param name="OldText">Those lines before.</param>
/// <param name="NewText">Those lines after.</param>
public sealed record AiRecentEdit(int Line, string OldText, string NewText);

/// <summary>A predicted next edit: replace lines <see cref="StartLine"/>..<see cref="EndLine"/> (1-based, inclusive) with <see cref="NewText"/>.</summary>
public sealed record AiEditSuggestion(int StartLine, int EndLine, string OldText, string NewText);

/// <summary>
/// Next-edit suggestions (M28b, ADR 0062): after an edit, the model predicts the next one nearby (the same rename further
/// down, a matching change) from the recent edits and the lines around the caret. Its own switch,
/// <c>ai.nextEdit.enabled</c>, is off by default.
/// </summary>
public sealed class AiNextEdit(AiService ai, SettingsService settings)
{
    public const string EnabledSetting = "ai.nextEdit.enabled";

    /// <summary>The lines above and below the caret that are sent.</summary>
    public const int Radius = 40;

    public const string SystemPrompt =
        """
        You predict a programmer's next edit. You get their recent edits (old and new lines) and the lines around the cursor, each starting with its line number and a bar.
        If their recent edits imply another change in the shown lines (the same rename elsewhere, a matching parameter, an updated call, a repeated pattern), reply with only a JSON object:
        {"startLine": <first line to replace>, "endLine": <last line to replace>, "newText": "<the replacement lines, without line numbers>"}
        Otherwise reply with {"none": true}. Never suggest the edit they just made, and never change lines they didn't imply.
        """;

    public AiService Ai { get; } = ai;

    public bool IsEnabled => Ai.State == AiState.Ready && settings.GetBoolean(EnabledSetting, false);

    /// <summary>The next edit in <paramref name="text"/> around <paramref name="caretLine"/> (1-based), or null.</summary>
    public async Task<AiEditSuggestion?> SuggestAsync(string path, string? languageId, string text, int caretLine, IReadOnlyList<AiRecentEdit> recent, CancellationToken cancellationToken)
    {
        if (!IsEnabled || recent.Count == 0 || !Ai.IsWorkspaceTrusted || await Ai.Exclusions.IsExcludedAsync(path, Ai.WorkspaceRoot, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Math.Max(1, caretLine - Radius);
        var end = Math.Min(lines.Length, caretLine + Radius);
        var region = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            region.Append(i.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(lines[i - 1]).Append('\n');
        }

        var edits = new StringBuilder();
        foreach (var edit in recent.TakeLast(5))
        {
            edits.Append("Line ").Append(edit.Line.ToString(CultureInfo.InvariantCulture)).Append(":\n- ").Append(edit.OldText.Replace("\n", "\n- ", StringComparison.Ordinal))
                .Append("\n+ ").Append(edit.NewText.Replace("\n", "\n+ ", StringComparison.Ordinal)).Append('\n');
        }

        var name = Path.GetFileName(path);
        var items = new List<AiContextItem>
        {
            new(AiContextKind.Changes, "recent edits in " + name, edits.ToString()) { Path = path },
            new(AiContextKind.Selection, name, region.ToString()) { Path = path, StartLine = start, Language = languageId },
        };
        try
        {
            var context = await Ai.PrepareAsync(items, cancellationToken).ConfigureAwait(false);
            if (context.Included.Count < 2)
            {
                return null;
            }

            var reply = await Ai.ChatAsync(
                "next edit",
                [new Codalus.Plugins.Protocol.ChatMessageDto("system", SystemPrompt, null, null, false), new Codalus.Plugins.Protocol.ChatMessageDto("user", AiService.ComposeUserMessage("The cursor is on line " + caretLine.ToString(CultureInfo.InvariantCulture) + ". What is the next edit?", context), null, null, false)],
                context,
                _ => { },
                cancellationToken,
                maxOutputTokens: 600,
                modelOverride: settings.GetString(AiCompletions.ModelSetting) is { Length: > 0 } model ? model : null,
                quiet: true).ConfigureAwait(false);
            return Parse(reply.Text, lines, start, end);
        }
        catch (AiUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// The suggestion a reply holds, checked against the file: its lines must be among those shown, and it must change
    /// something. Null otherwise.
    /// </summary>
    public static AiEditSuggestion? Parse(string reply, string[] lines, int firstShown, int lastShown)
    {
        var open = reply.IndexOf('{', StringComparison.Ordinal);
        var close = reply.LastIndexOf('}');
        if (open < 0 || close <= open)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(reply[open..(close + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("none", out _)
                || !root.TryGetProperty("startLine", out var startElement) || !startElement.TryGetInt32(out var start)
                || !root.TryGetProperty("newText", out var newElement) || newElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var end = root.TryGetProperty("endLine", out var endElement) && endElement.TryGetInt32(out var e) ? e : start;
            if (start < firstShown || end > lastShown || end < start)
            {
                return null;
            }

            var oldText = string.Join('\n', lines[(start - 1)..end]);
            var newText = newElement.GetString()!.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
            return newText == oldText ? null : new AiEditSuggestion(start, end, oldText, newText);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
