using System.Text.Json;

namespace Codalus.Plugins.Proposed;

/// <summary>
/// Language model providers (proposed, M28a, ADR 0062): chat models from any vendor, local or remote, behind one
/// interface. A provider is declared in the manifest (<c>contributes.languageModelProviders</c>) and registered when its
/// plugin activates (<c>onLanguageModelProvider:&lt;id&gt;</c>, implicit), which happens only when the user picks it.
/// The shell decides what is sent (the data boundary); the provider only talks to its service.
/// </summary>
public interface ILanguageModels
{
    /// <summary>Registers the provider whose <see cref="LanguageModelProvider.Id"/> the manifest declares. Dispose to unregister.</summary>
    IDisposable RegisterProvider(LanguageModelProvider provider);
}

/// <summary>
/// A source of chat models. Calls come on any thread and are cancelled when the user stops a request. Keep keys in
/// <see cref="IPluginContext.Secrets"/>.
/// </summary>
public abstract class LanguageModelProvider
{
    /// <summary>Creates a provider for the manifest's <c>languageModelProviders[].id</c>.</summary>
    protected LanguageModelProvider(string id) => Id = id;

    /// <summary>The id in the manifest, e.g. <c>openai</c>.</summary>
    public string Id { get; }

    /// <summary>The models it offers now. Throw <see cref="LanguageModelException"/> when the service can't be reached.</summary>
    public abstract Task<IReadOnlyList<LanguageModelInfo>> GetModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends <paramref name="request"/> and reports the reply's parts as they arrive: text, and complete tool calls.
    /// Returns how the reply ended.
    /// </summary>
    public abstract Task<ChatResult> ChatAsync(ChatRequest request, IProgress<ChatResponsePart> progress, CancellationToken cancellationToken);

    /// <summary>Whether the provider can serve requests now (a key is stored, the endpoint is set). No network call needed.</summary>
    public virtual Task<LanguageModelStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new LanguageModelStatus(true));

    /// <summary>Asks the user for what the provider needs (a key, an endpoint) with the plugin's own prompts, and stores it.</summary>
    public virtual Task<LanguageModelStatus> ConfigureAsync(CancellationToken cancellationToken) => GetStatusAsync(cancellationToken);
}

/// <summary>A model a provider offers.</summary>
/// <param name="Id">The id sent back in <see cref="ChatRequest.ModelId"/>.</param>
/// <param name="Name">The name the user sees.</param>
public sealed record LanguageModelInfo(string Id, string Name)
{
    /// <summary>How many tokens of input it takes, when known.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>How many tokens it writes at most, when known.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Whether it takes <see cref="ChatRequest.Tools"/>.</summary>
    public bool SupportsTools { get; init; }
}

/// <summary>Whether a provider is ready.</summary>
/// <param name="IsReady">It can serve requests.</param>
/// <param name="Message">What is missing, or where it sends requests (e.g. "Key stored", "http://localhost:11434").</param>
public sealed record LanguageModelStatus(bool IsReady, string? Message = null);

/// <summary>Who wrote a message.</summary>
public enum ChatRole
{
    /// <summary>Instructions from the IDE.</summary>
    System,

    /// <summary>The user, and the context the IDE attached for them.</summary>
    User,

    /// <summary>The model.</summary>
    Assistant,

    /// <summary>The result of a tool call (<see cref="ChatMessage.ToolCallId"/>).</summary>
    Tool,
}

/// <summary>A message of the conversation.</summary>
/// <param name="Role">Who wrote it.</param>
/// <param name="Content">Its text.</param>
public sealed record ChatMessage(ChatRole Role, string Content)
{
    /// <summary>The tool calls an assistant message made.</summary>
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; init; }

    /// <summary>For a <see cref="ChatRole.Tool"/> message: the call it answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>For a <see cref="ChatRole.Tool"/> message: the tool failed, and the content says why.</summary>
    public bool IsError { get; init; }
}

/// <summary>A tool the model may call.</summary>
/// <param name="Name">Its name (letters, digits, <c>_</c> and <c>-</c>).</param>
/// <param name="Description">What it does and when to use it.</param>
/// <param name="Parameters">A JSON schema of its arguments (an object schema).</param>
public sealed record ChatTool(string Name, string Description, JsonElement Parameters);

/// <summary>A call of a tool.</summary>
/// <param name="Id">The provider's id of the call, sent back in <see cref="ChatMessage.ToolCallId"/>.</param>
/// <param name="Name">The tool.</param>
/// <param name="Arguments">Its arguments, a JSON object as text.</param>
public sealed record ChatToolCall(string Id, string Name, string Arguments);

/// <summary>What to send.</summary>
/// <param name="ModelId">One of <see cref="LanguageModelProvider.GetModelsAsync"/>'s ids.</param>
/// <param name="Messages">The conversation, the system message first when there is one.</param>
public sealed record ChatRequest(string ModelId, IReadOnlyList<ChatMessage> Messages)
{
    /// <summary>Tools the model may call; none when null.</summary>
    public IReadOnlyList<ChatTool>? Tools { get; init; }

    /// <summary>The most tokens to write; the provider's default when null.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Stop writing at any of these.</summary>
    public IReadOnlyList<string>? Stop { get; init; }

    /// <summary>Provider-specific settings (e.g. <c>effort</c>), ignored by providers that don't know them.</summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

/// <summary>A part of a streamed reply: text, or a complete tool call.</summary>
/// <param name="Text">Text to append; null for a tool call.</param>
public sealed record ChatResponsePart(string? Text)
{
    /// <summary>A complete tool call.</summary>
    public ChatToolCall? ToolCall { get; init; }
}

/// <summary>How a reply ended.</summary>
public enum ChatFinishReason
{
    /// <summary>The model finished.</summary>
    Stop,

    /// <summary>It reached <see cref="ChatRequest.MaxOutputTokens"/> or its own limit.</summary>
    Length,

    /// <summary>It called tools and waits for their results.</summary>
    ToolCalls,

    /// <summary>The service declined to answer.</summary>
    Refused,
}

/// <summary>The end of a reply.</summary>
/// <param name="FinishReason">How it ended.</param>
public sealed record ChatResult(ChatFinishReason FinishReason)
{
    /// <summary>Tokens read, when the service says.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Tokens written, when the service says.</summary>
    public int? OutputTokens { get; init; }
}

/// <summary>A provider's service failed: it can't be reached, refused the key, or answered with an error.</summary>
public sealed class LanguageModelException : Exception
{
    /// <summary>Creates the exception.</summary>
    public LanguageModelException()
    {
    }

    /// <summary>Creates the exception with the message the user sees.</summary>
    public LanguageModelException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the message the user sees and its cause.</summary>
    public LanguageModelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The key is missing or was refused: the user must configure the provider again.</summary>
    public bool NeedsConfiguration { get; init; }
}
