namespace Codalus.Plugins.Proposed;

/// <summary>A model a provider offers.</summary>
/// <param name="Id">The id the provider's API takes.</param>
/// <param name="Label">The name the user sees.</param>
public sealed record AiModel(string Id, string Label);

/// <summary>A message of a conversation.</summary>
/// <param name="Role"><c>system</c>, <c>user</c> or <c>assistant</c>.</param>
/// <param name="Content">Its text.</param>
public sealed record AiMessage(string Role, string Content);

/// <summary>A chat request: the model and the conversation so far, the newest message last.</summary>
/// <param name="Model">One of the provider's <see cref="AiModel.Id"/>s.</param>
/// <param name="Messages">The conversation, with the IDE's system message first.</param>
public sealed record AiChatRequest(string Model, IReadOnlyList<AiMessage> Messages)
{
    /// <summary>The most tokens the answer may use, when the user set a limit.</summary>
    public int? MaxTokens { get; init; }
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
}

/// <summary>AI model providers (proposed, M28a).</summary>
public interface IAi
{
    /// <summary>Offers <paramref name="provider"/> as <paramref name="id"/> (the setting <c>ai.provider</c>) until disposed.</summary>
    IDisposable RegisterModelProvider(string id, string label, IAiModelProvider provider);
}
