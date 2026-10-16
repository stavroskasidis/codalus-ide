using System.Collections.Concurrent;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Plugins;

namespace Codalus.Workbench.Ai;

/// <summary>A language model provider a plugin declares (<c>contributes.languageModelProviders</c>).</summary>
public sealed record LanguageModelProviderInfo(string Id, string Label, string PluginId, string? Description, bool IsLocal)
{
    /// <summary>Its plugin is active and registered it.</summary>
    public bool IsRegistered { get; init; }
}

/// <summary>A language model request failed: the provider's service, its plugin, or its configuration.</summary>
public sealed class LanguageModelRequestException(string message, bool needsConfiguration = false) : Exception(message)
{
    /// <summary>The provider needs a key or an endpoint (Configure).</summary>
    public bool NeedsConfiguration { get; } = needsConfiguration;
}

/// <summary>
/// The language model providers in the workbench (ADR 0062): the ones plugins declare, activating a provider's plugin only
/// when a request needs it, and requests to it. This is the transport; <see cref="AiService"/> decides what may be sent.
/// </summary>
public sealed class LanguageModelService : ILanguageModelSink
{
    public static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a chat waits for parts that arrive after its response (they are on the wire before it).</summary>
    private static readonly TimeSpan LatePartsTimeout = TimeSpan.FromSeconds(5);

    private readonly PluginManager _plugins;
    private readonly ConcurrentDictionary<string, IHostingConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IHostingConnection>> _waiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChatStream> _chats = new(StringComparer.Ordinal);

    public LanguageModelService(PluginManager plugins)
    {
        _plugins = plugins;
        _plugins.LanguageModelSink = this;
        _plugins.PluginsChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Providers or registrations changed (any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>The declared providers.</summary>
    public IReadOnlyList<LanguageModelProviderInfo> Providers =>
        [.. _plugins.LanguageModelProviderDeclarations
            .GroupBy(d => d.Provider.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(d => new LanguageModelProviderInfo(d.Provider.Id, d.Provider.Label, d.Plugin.Id, d.Provider.Description, d.Provider.Local)
            {
                IsRegistered = _connections.ContainsKey(d.Provider.Id),
            })];

    public LanguageModelProviderInfo? Provider(string id) => Providers.FirstOrDefault(p => p.Id == id);

    // ------------------------------------------------------------------ ILanguageModelSink

    void ILanguageModelSink.ProviderRegistered(string pluginId, string providerId, IHostingConnection connection)
    {
        _connections[providerId] = connection;
        if (_waiting.TryRemove(providerId, out var waiting))
        {
            waiting.TrySetResult(connection);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    void ILanguageModelSink.ProviderUnregistered(string providerId)
    {
        _connections.TryRemove(providerId, out _);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void ILanguageModelSink.ChatPart(ChatPartParams part)
    {
        if (_chats.TryGetValue(part.RequestId, out var chat))
        {
            chat.Received(part);
        }
    }

    // ------------------------------------------------------------------ requests

    public async Task<IReadOnlyList<LanguageModelDto>> GetModelsAsync(string providerId, CancellationToken cancellationToken) =>
        (await InvokeAsync(providerId, LanguageModelMethods.Models, new LanguageModelProviderIdParams(providerId), PluginJsonContext.Default.LanguageModelProviderIdParams, PluginJsonContext.Default.LanguageModelsResult, cancellationToken).ConfigureAwait(false)).Models;

    public Task<LanguageModelStatusDto> GetStatusAsync(string providerId, CancellationToken cancellationToken) =>
        InvokeAsync(providerId, LanguageModelMethods.Status, new LanguageModelProviderIdParams(providerId), PluginJsonContext.Default.LanguageModelProviderIdParams, PluginJsonContext.Default.LanguageModelStatusDto, cancellationToken);

    public Task<LanguageModelStatusDto> ConfigureAsync(string providerId, CancellationToken cancellationToken) =>
        InvokeAsync(providerId, LanguageModelMethods.Configure, new LanguageModelProviderIdParams(providerId), PluginJsonContext.Default.LanguageModelProviderIdParams, PluginJsonContext.Default.LanguageModelStatusDto, cancellationToken);

    /// <summary>
    /// Sends a chat and reports its parts in order on the thread they arrive on; completes once every part was reported.
    /// Cancelling cancels the provider's request.
    /// </summary>
    public async Task<ChatResultDto> ChatAsync(
        string providerId,
        string modelId,
        IReadOnlyList<ChatMessageDto> messages,
        Action<ChatPartParams> onPart,
        CancellationToken cancellationToken,
        IReadOnlyList<ChatToolDto>? tools = null,
        int? maxOutputTokens = null,
        IReadOnlyDictionary<string, string>? options = null)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var stream = new ChatStream(onPart);
        _chats[requestId] = stream;
        try
        {
            var result = await InvokeAsync(
                providerId,
                LanguageModelMethods.Chat,
                new ChatParams(providerId, requestId, modelId, [.. messages], tools is null ? null : [.. tools], maxOutputTokens, null, options is null ? null : new Dictionary<string, string>(options)),
                PluginJsonContext.Default.ChatParams,
                PluginJsonContext.Default.ChatResultDto,
                cancellationToken).ConfigureAwait(false);
            await stream.WaitForAsync(result.PartCount, LatePartsTimeout, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _chats.TryRemove(requestId, out _);
        }
    }

    private async Task<TResult> InvokeAsync<TParams, TResult>(
        string providerId,
        string method,
        TParams parameters,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo,
        CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(providerId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await connection.InvokeAsync(method, parameters, paramsInfo, resultInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex)
        {
            throw new LanguageModelRequestException(ex.Message, ex.Code == LanguageModelErrorCodes.NeedsConfiguration);
        }
        catch (JsonRpcConnectionLostException ex)
        {
            throw new LanguageModelRequestException("The plugin host stopped: " + ex.Message);
        }
    }

    /// <summary>The provider's connection: activates its plugin (<c>onLanguageModelProvider:&lt;id&gt;</c>) and waits for the registration.</summary>
    private async Task<IHostingConnection> ConnectAsync(string providerId, CancellationToken cancellationToken)
    {
        if (_connections.TryGetValue(providerId, out var connection))
        {
            return connection;
        }

        var declaration = _plugins.LanguageModelProviderDeclarations.FirstOrDefault(d => d.Provider.Id == providerId);
        if (declaration.Plugin is null)
        {
            throw new LanguageModelRequestException($"No plugin provides the language model provider '{providerId}'.");
        }

        var waiting = _waiting.GetOrAdd(providerId, _ => new TaskCompletionSource<IHostingConnection>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_connections.TryGetValue(providerId, out connection))
        {
            return connection;
        }

        if (!await _plugins.ActivateAsync(declaration.Plugin, "onLanguageModelProvider:" + providerId, cancellationToken).ConfigureAwait(false))
        {
            _waiting.TryRemove(providerId, out _);
            throw new LanguageModelRequestException($"The {declaration.Provider.Label} plugin isn't available: {declaration.Plugin.Error}");
        }

        try
        {
            return await waiting.Task.WaitAsync(RegistrationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _waiting.TryRemove(providerId, out _);
            throw new LanguageModelRequestException($"The {declaration.Provider.Label} plugin didn't register its provider.");
        }
    }

    /// <summary>A running chat's parts: reported as they come, and counted so the end waits for the late ones.</summary>
    private sealed class ChatStream(Action<ChatPartParams> onPart)
    {
        private readonly Lock _gate = new();
        private int _received;
        private (int Count, TaskCompletionSource Done)? _expected;

        public void Received(ChatPartParams part)
        {
            onPart(part);
            TaskCompletionSource? done = null;
            lock (_gate)
            {
                _received++;
                if (_expected is { } expected && _received >= expected.Count)
                {
                    done = expected.Done;
                }
            }

            done?.TrySetResult();
        }

        public async Task WaitForAsync(int count, TimeSpan timeout, CancellationToken cancellationToken)
        {
            TaskCompletionSource done;
            lock (_gate)
            {
                if (_received >= count)
                {
                    return;
                }

                done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _expected = (count, done);
            }

            try
            {
                await done.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A part was lost (the host went away mid-reply): what arrived is the reply.
            }
        }
    }
}
