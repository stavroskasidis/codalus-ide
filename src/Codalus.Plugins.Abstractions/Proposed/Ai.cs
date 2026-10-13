namespace Codalus.Plugins.Proposed;

/// <summary>A model a provider offers.</summary>
/// <param name="Id">The id the provider's API takes.</param>
/// <param name="Label">The name the user sees.</param>
public sealed record AiModel(string Id, string Label);

/// <summary>A message of a conversation.</summary>
/// <param name="Role"><c>system</c>, <c>user</c>, <c>assistant</c> or <c>tool</c> (a tool's result).</param>
/// <param name="Content">Its text.</param>
public sealed record AiMessage(string Role, string Content)
{
    /// <summary>The tools an assistant message called (M28d).</summary>
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }

    /// <summary>The call a <c>tool</c> message answers.</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>A tool the model may call (M28d, agent mode).</summary>
/// <param name="Name">Its name.</param>
/// <param name="Description">What it does, for the model.</param>
/// <param name="InputSchema">The JSON schema of its input, as text.</param>
public sealed record AiTool(string Name, string Description, string InputSchema);

/// <summary>A call of a tool by the model.</summary>
/// <param name="Id">The call's id, which its result names.</param>
/// <param name="Name">The tool.</param>
/// <param name="Arguments">Its input, as JSON text.</param>
public sealed record AiToolCall(string Id, string Name, string Arguments);

/// <summary>A piece of an answer: text, or a tool call (M28d).</summary>
/// <param name="Text">Text, when it is text.</param>
/// <param name="ToolCall">A tool call, when it is one.</param>
public sealed record AiEvent(string? Text, AiToolCall? ToolCall);

/// <summary>A chat request: the model and the conversation so far, the newest message last.</summary>
/// <param name="Model">One of the provider's <see cref="AiModel.Id"/>s.</param>
/// <param name="Messages">The conversation, with the IDE's system message first.</param>
public sealed record AiChatRequest(string Model, IReadOnlyList<AiMessage> Messages)
{
    /// <summary>The most tokens the answer may use, when the user set a limit.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>The tools the model may call (M28d); none outside agent mode.</summary>
    public IReadOnlyList<AiTool>? Tools { get; init; }
}

/// <summary>
/// A source of AI models (proposed, M28a, ADR 0062): a vendor's API or a local server. The IDE decides what is sent;
/// the provider only carries it. Keep keys in <see cref="IPluginContext.Secrets"/>.
/// </summary>
public interface IAiModelProvider
{
    /// <summary>The models it offers now (an empty list when it isn't configured).</summary>
    Task<IReadOnlyList<AiModel>> GetModelsAsync(CancellationToken cancellationToken);

    /// <summary>Streams the answer to <paramref name="request"/>, a piece of text at a time.</summary>
    IAsyncEnumerable<string> ChatAsync(AiChatRequest request, CancellationToken cancellationToken);

    /// <summary>Asks the user for what it needs (a key, a server's address); returns whether it is configured now.</summary>
    Task<bool> ConfigureAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>
    /// Streams the answer with tool calls (M28d, agent mode): text pieces and the calls of <see cref="AiChatRequest.Tools"/>.
    /// The default carries <see cref="ChatAsync"/>'s text, for providers without tools.
    /// </summary>
    IAsyncEnumerable<AiEvent> ChatEventsAsync(AiChatRequest request, CancellationToken cancellationToken) => AiEvents.FromText(ChatAsync(request, cancellationToken), cancellationToken);
}

/// <summary>Helpers for <see cref="AiEvent"/> streams.</summary>
public static class AiEvents
{
    /// <summary>A text stream as events.</summary>
    public static async IAsyncEnumerable<AiEvent> FromText(IAsyncEnumerable<string> text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await foreach (var piece in text.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new AiEvent(piece, null);
        }
    }
}

/// <summary>AI model providers (proposed, M28a).</summary>
public interface IAi
{
    /// <summary>Offers <paramref name="provider"/> as <paramref name="id"/> (the setting <c>ai.provider</c>) until disposed.</summary>
    IDisposable RegisterModelProvider(string id, string label, IAiModelProvider provider);
}
