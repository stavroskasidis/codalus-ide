namespace Codalus.Plugins.Protocol;

#pragma warning disable CA1819 // Properties should not return arrays: wire DTOs.

// Language model providers (docs/plugin-protocol.md#language-model-providers, ADR 0062). Proposed, M28a.

/// <summary>Method names of the language model API (proposed).</summary>
public static class LanguageModelMethods
{
    // host → shell (notifications)
    public const string RegisterProvider = "languageModels/registerProvider";
    public const string UnregisterProvider = "languageModels/unregisterProvider";
    public const string ChatPart = "languageModels/chatPart";

    // shell → host (requests)
    public const string Models = "languageModels/models";
    public const string Status = "languageModels/status";
    public const string Configure = "languageModels/configure";
    public const string Chat = "languageModels/chat";
}

/// <summary>JSON-RPC error codes of the language model requests.</summary>
public static class LanguageModelErrorCodes
{
    /// <summary>No such provider is registered (its plugin isn't active).</summary>
    public const int NoProvider = -33020;

    /// <summary>The service failed or refused (the message says why).</summary>
    public const int Failed = -33021;

    /// <summary>The provider needs a key or an endpoint: configure it.</summary>
    public const int NeedsConfiguration = -33022;
}

/// <summary><c>languageModels/registerProvider</c> and <c>unregisterProvider</c>.</summary>
public sealed record LanguageModelProviderParams(string PluginId, string ProviderId);

/// <summary>A request that only names the provider (<c>models</c>, <c>status</c>, <c>configure</c>).</summary>
public sealed record LanguageModelProviderIdParams(string ProviderId);

public sealed record LanguageModelDto(string Id, string Name, int? ContextWindow, int? MaxOutputTokens, bool SupportsTools);

public sealed record LanguageModelsResult(LanguageModelDto[] Models);

public sealed record LanguageModelStatusDto(bool IsReady, string? Message);

public sealed record ChatToolDto(string Name, string Description, System.Text.Json.JsonElement Parameters);

public sealed record ChatToolCallDto(string Id, string Name, string Arguments);

/// <summary>A message; <see cref="Role"/> is <c>system</c>, <c>user</c>, <c>assistant</c> or <c>tool</c>.</summary>
public sealed record ChatMessageDto(string Role, string Content, ChatToolCallDto[]? ToolCalls, string? ToolCallId, bool IsError);

/// <summary><c>languageModels/chat</c>: the parts come as <c>languageModels/chatPart</c> with the same <see cref="RequestId"/>.</summary>
public sealed record ChatParams(
    string ProviderId,
    string RequestId,
    string ModelId,
    ChatMessageDto[] Messages,
    ChatToolDto[]? Tools,
    int? MaxOutputTokens,
    string[]? Stop,
    Dictionary<string, string>? Options);

/// <summary><c>languageModels/chatPart</c>: text to append, or a complete tool call.</summary>
public sealed record ChatPartParams(string RequestId, string? Text, ChatToolCallDto? ToolCall);

/// <summary>
/// The end of a chat; <see cref="FinishReason"/> is <c>stop</c>, <c>length</c>, <c>toolCalls</c> or <c>refused</c>.
/// <see cref="PartCount"/> is how many <c>chatPart</c> notifications preceded it: the shell may handle the response before
/// the last of them, so it waits for that many.
/// </summary>
public sealed record ChatResultDto(string FinishReason, int? InputTokens, int? OutputTokens, int PartCount);

#pragma warning restore CA1819
