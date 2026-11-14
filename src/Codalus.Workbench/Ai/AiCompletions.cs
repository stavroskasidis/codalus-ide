using System.Text;
using Codalus.Plugins.Protocol;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Ai;

/// <summary>What the editor asks to complete: the file, and the text before and after the caret.</summary>
/// <param name="Path">The file (checked against the exclusions).</param>
/// <param name="LanguageId">Its language, for the prompt.</param>
/// <param name="Prefix">The text before the caret.</param>
/// <param name="Suffix">The text after it.</param>
public sealed record AiCompletionRequest(string Path, string? LanguageId, string Prefix, string Suffix);

/// <summary>
/// Inline completions (M28b, ADR 0062): the text to insert at the caret, from the chosen provider's chat with a
/// completion prompt, or AI Chat's external agent (ADR 0097), through <see cref="AiService"/>'s boundary (an excluded file
/// sends nothing; secrets are redacted).
/// Its own switch, <c>ai.completions.enabled</c>, is off by default: completions send code as you type.
/// </summary>
public sealed class AiCompletions(AiService ai, SettingsService settings)
{
    public const string EnabledSetting = "ai.completions.enabled";
    public const string ModelSetting = "ai.completions.model";
    public const string DelaySetting = "ai.completions.delayMilliseconds";
    public const string CursorMarker = "<|cursor|>";

    /// <summary>The most text before the caret that is sent.</summary>
    public const int MaxPrefix = 6_000;

    /// <summary>The most text after the caret that is sent.</summary>
    public const int MaxSuffix = 2_000;

    /// <summary>The most lines a suggestion has.</summary>
    public const int MaxLines = 16;

    public const string SystemPrompt =
        """
        You are the code completion engine of an IDE. The user's message is a file with the cursor marked <|cursor|>.
        Reply with only the text to insert at the cursor: no explanation, no code fences, and nothing that is already before or after the cursor.
        Complete the current statement, or the next few lines when the intent is clear (at most a short block).
        Match the file's style and indentation. If nothing sensible fits, reply with nothing.
        """;

    public AiService Ai { get; } = ai;

    /// <summary>How long typing pauses before a suggestion is asked for, unless the user set it: a provider's.</summary>
    public const int DefaultDelay = 400;

    /// <summary>The same with AI Chat's external agent (ADR 0097): its turn costs more, so it waits for a longer pause.</summary>
    public const int DefaultAgentDelay = 1_000;

    /// <summary>Completions may be asked for: AI can answer (a provider, or AI Chat's agent) and completions are turned on.</summary>
    public bool IsEnabled => Ai.CanAnswer && settings.GetBoolean(EnabledSetting, false);

    /// <summary>How long typing pauses before a completion is asked for.</summary>
    public TimeSpan Delay => SuggestionDelay(Ai, settings);

    /// <summary>
    /// How long typing pauses before a suggestion (a completion or a next edit) is asked for: <c>ai.completions.delayMilliseconds</c>
    /// when the user set it, else <see cref="DefaultDelay"/>, or <see cref="DefaultAgentDelay"/> with an external agent.
    /// </summary>
    public static TimeSpan SuggestionDelay(AiService ai, SettingsService settings)
    {
        var set = settings.GetLayerValue(DelaySetting, SettingScope.User) is not null || settings.GetLayerValue(DelaySetting, SettingScope.Workspace) is not null;
        var milliseconds = set ? settings.GetInt32(DelaySetting, DefaultDelay) : ai.ExternalAgent is not null ? DefaultAgentDelay : DefaultDelay;
        return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 100, 5_000));
    }

    /// <summary><c>ai.completions.model</c> for a provider; an external agent uses its model for suggestions instead (ADR 0097).</summary>
    public static string? ModelOverride(AiService ai, SettingsService settings) =>
        ai.ExternalAgent is null && settings.GetString(ModelSetting) is { Length: > 0 } model ? model : null;

    /// <summary>
    /// A suggestion may be asked for on demand (Trigger Inline Suggestion): AI can answer, with a provider or AI Chat's
    /// agent. The user asks for it, so it doesn't need <c>ai.completions.enabled</c>, which governs asking while typing.
    /// </summary>
    public bool CanCompleteOnDemand => Ai.CanAnswer;

    /// <summary>
    /// The suggestion at the caret, or null (none, an excluded file, AI off, a failure). <paramref name="onDemand"/>: the
    /// user asked for it, so <see cref="CanCompleteOnDemand"/> is enough.
    /// </summary>
    public async Task<string?> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken, bool onDemand = false)
    {
        if (!(onDemand ? CanCompleteOnDemand : IsEnabled) || !Ai.IsWorkspaceTrusted || await Ai.Exclusions.IsExcludedAsync(request.Path, Ai.WorkspaceRoot, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var prefix = request.Prefix.Length > MaxPrefix ? request.Prefix[^MaxPrefix..] : request.Prefix;
        var suffix = request.Suffix.Length > MaxSuffix ? request.Suffix[..MaxSuffix] : request.Suffix;
        var (redactedPrefix, before) = SecretRedactor.Redact(prefix);
        var (redactedSuffix, after) = SecretRedactor.Redact(suffix);
        var name = Ai.WorkspaceRoot is { } root && System.IO.Path.GetRelativePath(root, request.Path) is var relative && !relative.StartsWith("..", StringComparison.Ordinal)
            ? relative.Replace('\\', '/')
            : System.IO.Path.GetFileName(request.Path);
        var user = new StringBuilder()
            .Append("File: ").Append(name).Append(request.LanguageId is { } language ? $" ({language})" : "").Append('\n')
            .Append(redactedPrefix).Append(CursorMarker).Append(redactedSuffix)
            .ToString();
        var context = new AiPreparedContext([new AiContextItem(AiContextKind.CurrentFile, System.IO.Path.GetFileName(request.Path), "") { Path = request.Path }], [], before + after);
        try
        {
            var reply = await Ai.ChatAsync(
                "completion",
                [new ChatMessageDto("system", SystemPrompt, null, null, false), new ChatMessageDto("user", user, null, null, false)],
                context,
                _ => { },
                cancellationToken,
                maxOutputTokens: 256,
                modelOverride: ModelOverride(Ai, settings),
                quiet: true,
                route: AiRoute.Suggestion).ConfigureAwait(false);
            return Clean(reply.Text, prefix, suffix);
        }
        catch (AiUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// The insertion a model's reply means: without code fences or a repeated cursor marker, without the current line's
    /// start when the model wrote it again, without the start of the text after the caret, at most <see cref="MaxLines"/>
    /// lines; null when nothing is left.
    /// </summary>
    public static string? Clean(string reply, string prefix, string suffix)
    {
        var text = reply.Replace("\r\n", "\n", StringComparison.Ordinal).Replace(CursorMarker, "", StringComparison.Ordinal);

        // A fenced reply: its first block.
        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = text.IndexOf('\n', fence);
            var end = start < 0 ? -1 : text.IndexOf("```", start, StringComparison.Ordinal);
            text = start < 0 ? "" : end < 0 ? text[(start + 1)..] : text[(start + 1)..end];
        }

        // The model repeated the current line's start.
        var lineStart = prefix.LastIndexOf('\n') + 1;
        var currentLine = prefix[lineStart..];
        if (currentLine.Trim().Length > 0)
        {
            var trimmed = currentLine.TrimStart();
            if (text.StartsWith(currentLine, StringComparison.Ordinal))
            {
                text = text[currentLine.Length..];
            }
            else if (text.TrimStart().StartsWith(trimmed, StringComparison.Ordinal) && text.TrimStart().Length > trimmed.Length)
            {
                text = text.TrimStart()[trimmed.Length..];
            }
        }
        else if (currentLine.Length > 0 && text.StartsWith(currentLine, StringComparison.Ordinal))
        {
            // The caret is after the indentation and the model indented again.
            text = text[currentLine.Length..];
        }

        // The model wrote the text after the caret too: cut it where the suffix's first non-blank line starts.
        var suffixLine = suffix.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (suffixLine.Length > 0)
        {
            var at = text.LastIndexOf(suffixLine, StringComparison.Ordinal);
            if (at >= 0 && text[at..].Trim() == suffixLine)
            {
                text = text[..at];
            }
        }

        var lines = text.Split('\n');
        if (lines.Length > MaxLines)
        {
            text = string.Join('\n', lines.Take(MaxLines));
        }

        text = text.TrimEnd();
        return text.Trim().Length == 0 ? null : text;
    }
}
