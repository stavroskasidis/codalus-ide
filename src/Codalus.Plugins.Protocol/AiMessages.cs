namespace Codalus.Plugins.Protocol;

#pragma warning disable CA1819 // Properties should not return arrays: wire DTOs.

// AI model providers (proposed, M28a, ADR 0062).

/// <summary>Method names of the AI provider API.</summary>
public static class AiMethods
{
    /// <summary>Host → shell: a provider is offered, or withdrawn (<see cref="Unregister"/>).</summary>
    public const string Register = "ai/register";
    public const string Unregister = "ai/unregister";

    /// <summary>Shell → host (requests): the provider's models; configure it; a chat answer streamed as <see cref="Chunk"/>s.</summary>
    public const string Models = "ai/models";
    public const string Configure = "ai/configure";
    public const string Chat = "ai/chat";

    /// <summary>Host → shell: a piece of a chat answer.</summary>
    public const string Chunk = "ai/chunk";

    /// <summary>Host → shell: a piece of an answer with tools: text or a tool call (M28d).</summary>
    public const string Event = "ai/event";
}

public sealed record AiProviderParams(string PluginId, string ProviderId, string Label);

public sealed record AiProviderIdParams(string ProviderId);

public sealed record AiModelDto(string Id, string Label);

public sealed record AiModelsResult(AiModelDto[] Models);

public sealed record AiConfigureResult(bool Configured);

public sealed record AiMessageDto(string Role, string Content)
{
    public AiToolCallDto[]? ToolCalls { get; init; }

    public string? ToolCallId { get; init; }
}

public sealed record AiToolDto(string Name, string Description, string InputSchema);

public sealed record AiToolCallDto(string Id, string Name, string Arguments);

/// <summary>A piece of an answer with tools (<c>ai/event</c>): text or a tool call, numbered as chunks are.</summary>
public sealed record AiEventParams(string StreamId, int Index, string? Text, AiToolCallDto? ToolCall);

/// <summary>A chat request; <see cref="StreamId"/> names the chunks that answer it.</summary>
public sealed record AiChatParams(string ProviderId, string StreamId, string Model, AiMessageDto[] Messages)
{
    public int? MaxTokens { get; init; }

    /// <summary>Stream <c>ai/event</c>s (text and tool calls) instead of <c>ai/chunk</c>s (M28d).</summary>
    public bool Events { get; init; }

    public AiToolDto[]? Tools { get; init; }
}

/// <summary>A piece of an answer; <see cref="Index"/> orders them, since notifications may be handled out of order.</summary>
public sealed record AiChunkParams(string StreamId, int Index, string Text);

/// <summary>A chat's end: how many chunks were sent, so the shell can wait for any still being handled.</summary>
public sealed record AiChatResult(int Chunks);

#pragma warning restore CA1819
