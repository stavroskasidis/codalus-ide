using System.Globalization;
using System.Text;
using Codalus.Plugins.Protocol;
using Codalus.Workbench.Output;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Ai;

/// <summary>What a piece of context is.</summary>
public enum AiContextKind
{
    Selection,
    CurrentFile,
    OpenFile,
    Diagnostics,
    BuildOutput,
    Solution,
}

/// <summary>A piece of context the user can see and turn off before it is sent.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Label">How the user sees it (a file name, "Errors in 3 files").</param>
/// <param name="Content">The text.</param>
public sealed record AiContextItem(AiContextKind Kind, string Label, string Content)
{
    /// <summary>The file it comes from, checked against the exclusions.</summary>
    public string? Path { get; init; }

    /// <summary>The first line of <see cref="Content"/> in its file (1-based), for a selection.</summary>
    public int? StartLine { get; init; }

    /// <summary>The language id, for the code fence.</summary>
    public string? Language { get; init; }
}

/// <summary>An item that was left out, and why.</summary>
public sealed record AiExcludedItem(AiContextItem Item, string Reason);

/// <summary>What will be sent: the items that passed the boundary (redacted, within the budget) and those that didn't.</summary>
public sealed record AiPreparedContext(IReadOnlyList<AiContextItem> Included, IReadOnlyList<AiExcludedItem> Excluded, int Redactions)
{
    public static AiPreparedContext None { get; } = new([], [], 0);
}

/// <summary>Why AI can't run now.</summary>
public enum AiState
{
    /// <summary><c>ai.enabled</c> is off (for the user, or for this workspace).</summary>
    Off,

    /// <summary>On, but no provider is chosen: nothing is sent.</summary>
    NoProvider,

    /// <summary>On with a provider.</summary>
    Ready,
}

/// <summary>AI isn't available: off, no provider, or a failed request.</summary>
public sealed class AiUnavailableException(string message, bool needsConfiguration = false) : Exception(message)
{
    public bool NeedsConfiguration { get; } = needsConfiguration;
}

/// <summary>The end of a request.</summary>
public sealed record AiReply(string Text, string FinishReason, int? InputTokens, int? OutputTokens);

/// <summary>
/// AI assistance's data boundary (ADR 0062): the only code that sends workspace content to a language model. It checks
/// that AI is on (for the user and the workspace) and a provider is chosen, drops excluded files, redacts secrets, keeps
/// within the size budget, logs each request to the <c>AI</c> Output channel, and sends through
/// <see cref="LanguageModelService"/>. Nothing is counted or sent anywhere else.
/// </summary>
public sealed class AiService
{
    public const string EnabledSetting = "ai.enabled";
    public const string ProviderSetting = "ai.provider";
    public const string ModelSetting = "ai.model";
    public const string ExcludeSetting = "ai.exclude";
    public const string MaxContextSetting = "ai.chat.maxContextCharacters";
    public const string OutputChannelId = "ai";

    /// <summary>The most characters one item may bring (a long file is cut, and says so).</summary>
    public const int MaxItemCharacters = 60_000;

    private readonly SettingsService _settings;
    private readonly OutputService _output;
    private readonly Func<string?> _workspaceRoot;
    private readonly Func<bool> _workspaceTrusted;
    private int _busy;

    /// <param name="workspaceRoot">The open solution's or folder's directory, or null.</param>
    /// <param name="workspaceTrusted">Whether that folder is trusted (restricted mode sends none of its files).</param>
    public AiService(SettingsService settings, LanguageModelService models, OutputService output, Func<string?> workspaceRoot, Func<bool> workspaceTrusted)
    {
        _settings = settings;
        Models = models;
        _output = output;
        _workspaceRoot = workspaceRoot;
        _workspaceTrusted = workspaceTrusted;
        Exclusions = new AiExclusions(() => _settings.GetStrings(ExcludeSetting));
        _settings.Changed += (_, e) =>
        {
            if (e.Affects("ai"))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
        models.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public LanguageModelService Models { get; }

    public AiExclusions Exclusions { get; }

    /// <summary>The state, the provider or the model changed (any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>A request started or ended (any thread).</summary>
    public event EventHandler? BusyChanged;

    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>
    /// On for the user and not turned off for this workspace. Only the user's settings turn it on: a workspace's (a cloned
    /// repository's <c>.codalus/settings.json</c>) can only turn it off.
    /// </summary>
    public bool IsEnabled =>
        _settings.GetLayerValue(EnabledSetting, SettingScope.User) is { ValueKind: System.Text.Json.JsonValueKind.True } &&
        _settings.GetLayerValue(EnabledSetting, SettingScope.Workspace) is not { ValueKind: System.Text.Json.JsonValueKind.False };

    /// <summary>The user turned AI on, but this workspace's settings turn it off.</summary>
    public bool IsOffForWorkspace =>
        _settings.GetLayerValue(EnabledSetting, SettingScope.User) is { ValueKind: System.Text.Json.JsonValueKind.True } &&
        _settings.GetLayerValue(EnabledSetting, SettingScope.Workspace) is { ValueKind: System.Text.Json.JsonValueKind.False };

    public string? ProviderId => _settings.GetString(ProviderSetting) is { Length: > 0 } id ? id : null;

    public string? ModelId => _settings.GetString(ModelSetting) is { Length: > 0 } id ? id : null;

    public AiState State => !IsEnabled ? AiState.Off : ProviderId is null ? AiState.NoProvider : AiState.Ready;

    public LanguageModelProviderInfo? Provider => ProviderId is { } id ? Models.Provider(id) : null;

    public bool IsWorkspaceTrusted => _workspaceTrusted();

    public string? WorkspaceRoot => _workspaceRoot();

    /// <summary>
    /// Applies the boundary to <paramref name="items"/>: drops files that are excluded (or every file of an untrusted
    /// workspace), redacts secrets, and cuts what goes over the budget.
    /// </summary>
    public AiPreparedContext Prepare(IEnumerable<AiContextItem> items)
    {
        var included = new List<AiContextItem>();
        var excluded = new List<AiExcludedItem>();
        var redactions = 0;
        var budget = Math.Max(1_000, _settings.GetInt32(MaxContextSetting, 120_000));
        var root = WorkspaceRoot;
        var trusted = IsWorkspaceTrusted;
        foreach (var item in items)
        {
            if (!trusted && item.Kind != AiContextKind.Diagnostics && item.Kind != AiContextKind.BuildOutput)
            {
                excluded.Add(new AiExcludedItem(item, "restricted mode"));
                continue;
            }

            if (item.Path is { } path && Exclusions.ExclusionReason(path, root) is { } reason)
            {
                excluded.Add(new AiExcludedItem(item, reason));
                continue;
            }

            var (content, count) = SecretRedactor.Redact(item.Content);
            redactions += count;
            if (content.Length > MaxItemCharacters)
            {
                content = content[..MaxItemCharacters] + "\n[… cut: the rest wasn't sent]";
            }

            if (content.Length > budget)
            {
                if (budget < 2_000)
                {
                    excluded.Add(new AiExcludedItem(item, "over the size budget"));
                    continue;
                }

                content = content[..budget] + "\n[… cut: the rest wasn't sent]";
            }

            budget -= content.Length;
            included.Add(item with { Content = content });
        }

        return new AiPreparedContext(included, excluded, redactions);
    }

    /// <summary>A user message with its context in front of the question, as it is sent (and kept in the conversation).</summary>
    public static string ComposeUserMessage(string question, AiPreparedContext context)
    {
        if (context.Included.Count == 0)
        {
            return question;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<context>");
        foreach (var item in context.Included)
        {
            sb.Append("## ").Append(Describe(item));
            sb.AppendLine();
            var fence = item.Content.Contains("```", StringComparison.Ordinal) ? "````" : "```";
            sb.Append(fence).Append(item.Language ?? "").AppendLine();
            sb.AppendLine(item.Content.TrimEnd('\n', '\r'));
            sb.AppendLine(fence);
        }

        sb.AppendLine("</context>");
        sb.AppendLine();
        sb.Append(question);
        return sb.ToString();
    }

    private static string Describe(AiContextItem item) => item.Kind switch
    {
        AiContextKind.Selection when item.StartLine is { } line =>
            string.Create(CultureInfo.InvariantCulture, $"Selection in {item.Label} (from line {line})"),
        AiContextKind.Selection => "Selection in " + item.Label,
        AiContextKind.CurrentFile => "Current file: " + item.Label,
        AiContextKind.OpenFile => "Open file: " + item.Label,
        AiContextKind.Diagnostics => "Errors and warnings: " + item.Label,
        AiContextKind.BuildOutput => "Build output: " + item.Label,
        _ => "Solution: " + item.Label,
    };

    /// <summary>
    /// Sends a conversation (system message first) to the chosen provider and model, reporting text as it streams.
    /// Throws <see cref="AiUnavailableException"/> when AI is off, no provider is chosen, or the request fails.
    /// </summary>
    public async Task<AiReply> ChatAsync(
        string feature,
        IReadOnlyList<ChatMessageDto> messages,
        AiPreparedContext context,
        Action<string> onText,
        CancellationToken cancellationToken,
        int? maxOutputTokens = null)
    {
        switch (State)
        {
            case AiState.Off:
                throw new AiUnavailableException(IsOffForWorkspace ? "AI assistance is turned off for this workspace." : "AI assistance is turned off (Tools → Options → AI).");
            case AiState.NoProvider:
                throw new AiUnavailableException("Choose an AI provider first: nothing is sent until you do.", needsConfiguration: true);
        }

        var providerId = ProviderId!;
        var modelId = ModelId ?? await FirstModelAsync(providerId, cancellationToken).ConfigureAwait(false);
        var size = messages.Sum(m => m.Content.Length);
        Log(string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {feature}: {providerId}/{modelId}, {messages.Count} messages, {size:N0} characters"));
        foreach (var item in context.Included)
        {
            Log("    sent: " + Describe(item));
        }

        foreach (var item in context.Excluded)
        {
            Log($"    not sent: {Describe(item.Item)} ({item.Reason})");
        }

        if (context.Redactions > 0)
        {
            Log(string.Create(CultureInfo.InvariantCulture, $"    redacted: {context.Redactions} secret(s)"));
        }

        var text = new StringBuilder();
        Interlocked.Increment(ref _busy);
        BusyChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var result = await Models.ChatAsync(providerId, modelId, messages, part =>
            {
                if (part.Text is { Length: > 0 } chunk)
                {
                    text.Append(chunk);
                    onText(chunk);
                }
            }, cancellationToken, maxOutputTokens: maxOutputTokens).ConfigureAwait(false);
            Log(string.Create(CultureInfo.InvariantCulture, $"    done: {result.FinishReason}, {result.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"} tokens in, {result.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"} out"));
            return new AiReply(text.ToString(), result.FinishReason, result.InputTokens, result.OutputTokens);
        }
        catch (LanguageModelRequestException ex)
        {
            Log("    failed: " + ex.Message);
            throw new AiUnavailableException(ex.Message, ex.NeedsConfiguration);
        }
        catch (OperationCanceledException)
        {
            Log("    stopped");
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The provider's first model (when <c>ai.model</c> is empty).</summary>
    public async Task<string> FirstModelAsync(string providerId, CancellationToken cancellationToken)
    {
        try
        {
            var models = await Models.GetModelsAsync(providerId, cancellationToken).ConfigureAwait(false);
            return models.Count > 0 ? models[0].Id : throw new AiUnavailableException("The provider offers no models.", needsConfiguration: true);
        }
        catch (LanguageModelRequestException ex)
        {
            throw new AiUnavailableException(ex.Message, ex.NeedsConfiguration);
        }
    }

    /// <summary>Turns AI off for this workspace (or on again, removing the workspace's value).</summary>
    public Task SetOffForWorkspaceAsync(bool off, CancellationToken cancellationToken) =>
        off
            ? _settings.SetAsync(EnabledSetting, false, SettingScope.Workspace, cancellationToken)
            : _settings.SetAsync(EnabledSetting, (System.Text.Json.JsonElement?)null, SettingScope.Workspace, cancellationToken);

    private void Log(string line) => _output.GetOrCreate(OutputChannelId, "AI").AppendLine(line);
}
