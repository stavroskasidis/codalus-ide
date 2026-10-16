using System.Collections.Concurrent;
using Codalus.Plugins.Proposed;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;

namespace Codalus.Plugins.Host.Proposed;

/// <summary>
/// The host's language model providers (ADR 0062): registrations go to the shell, the shell's <c>languageModels/*</c>
/// requests come to the provider they name, and a chat's parts go back as <c>languageModels/chatPart</c> notifications
/// before the request's response.
/// </summary>
internal sealed class HostLanguageModelRegistry(PluginHostServer server)
{
    private readonly ConcurrentDictionary<string, LanguageModelProvider> _providers = new(StringComparer.Ordinal);

    public IDisposable Register(string pluginId, LanguageModelProvider provider)
    {
        _providers[provider.Id] = provider;
        server.Post(LanguageModelMethods.RegisterProvider, new LanguageModelProviderParams(pluginId, provider.Id), PluginJsonContext.Default.LanguageModelProviderParams);
        return new PluginHostServer.Disposable(() =>
        {
            if (_providers.TryRemove(new KeyValuePair<string, LanguageModelProvider>(provider.Id, provider)))
            {
                server.Post(LanguageModelMethods.UnregisterProvider, new LanguageModelProviderParams(pluginId, provider.Id), PluginJsonContext.Default.LanguageModelProviderParams);
            }
        });
    }

    public void RegisterHandlers(JsonRpcConnection connection)
    {
        var json = PluginJsonContext.Default;
        connection.OnRequest(LanguageModelMethods.Models, json.LanguageModelProviderIdParams, json.LanguageModelsResult, (p, ct) => Call(p.ProviderId, async provider =>
            new LanguageModelsResult([.. (await provider.GetModelsAsync(ct).ConfigureAwait(false))
                .Select(m => new LanguageModelDto(m.Id, m.Name, m.ContextWindow, m.MaxOutputTokens, m.SupportsTools))])));
        connection.OnRequest(LanguageModelMethods.Status, json.LanguageModelProviderIdParams, json.LanguageModelStatusDto, (p, ct) => Call(p.ProviderId, async provider =>
            ToDto(await provider.GetStatusAsync(ct).ConfigureAwait(false))));
        connection.OnRequest(LanguageModelMethods.Configure, json.LanguageModelProviderIdParams, json.LanguageModelStatusDto, (p, ct) => Call(p.ProviderId, async provider =>
            ToDto(await provider.ConfigureAsync(ct).ConfigureAwait(false))));
        connection.OnRequest(LanguageModelMethods.Chat, json.ChatParams, json.ChatResultDto, (p, ct) => Call(p.ProviderId, async provider =>
        {
            var request = new ChatRequest(p.ModelId, [.. p.Messages.Select(FromDto)])
            {
                Tools = p.Tools is null ? null : [.. p.Tools.Select(t => new ChatTool(t.Name, t.Description, t.Parameters))],
                MaxOutputTokens = p.MaxOutputTokens,
                Stop = p.Stop,
                Options = p.Options,
            };
            var sink = new PartSink(server, p.RequestId);
            var result = await provider.ChatAsync(request, sink, ct).ConfigureAwait(false);

            // The parts were queued as notifications: the response goes after them.
            await server.FlushAsync(ct).ConfigureAwait(false);
            return new ChatResultDto(ToWire(result.FinishReason), result.InputTokens, result.OutputTokens, sink.Count);
        }));
    }

    private async Task<T> Call<T>(string providerId, Func<LanguageModelProvider, Task<T>> body)
    {
        if (!_providers.TryGetValue(providerId, out var provider))
        {
            throw new JsonRpcHandlerException(LanguageModelErrorCodes.NoProvider, $"No language model provider '{providerId}' is registered.");
        }

        try
        {
            return await body(provider).ConfigureAwait(false);
        }
        catch (LanguageModelException ex)
        {
            throw new JsonRpcHandlerException(ex.NeedsConfiguration ? LanguageModelErrorCodes.NeedsConfiguration : LanguageModelErrorCodes.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or JsonRpcHandlerException or OutOfMemoryException))
        {
            server.Log(null, "warning", $"language model provider '{providerId}': {ex}");
            throw new JsonRpcHandlerException(LanguageModelErrorCodes.Failed, ex.Message);
        }
    }

    internal static string ToWire(ChatFinishReason reason) => reason switch
    {
        ChatFinishReason.Length => "length",
        ChatFinishReason.ToolCalls => "toolCalls",
        ChatFinishReason.Refused => "refused",
        _ => "stop",
    };

    internal static string ToWire(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user",
    };

    private static ChatRole RoleOf(string role) => role switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User,
    };

    private static ChatMessage FromDto(ChatMessageDto m) => new(RoleOf(m.Role), m.Content)
    {
        ToolCalls = m.ToolCalls is null ? null : [.. m.ToolCalls.Select(c => new ChatToolCall(c.Id, c.Name, c.Arguments))],
        ToolCallId = m.ToolCallId,
        IsError = m.IsError,
    };

    private static LanguageModelStatusDto ToDto(LanguageModelStatus status) => new(status.IsReady, status.Message);

    /// <summary>Posts each part as a notification, in order.</summary>
    private sealed class PartSink(PluginHostServer server, string requestId) : IProgress<ChatResponsePart>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Report(ChatResponsePart value)
        {
            Interlocked.Increment(ref _count);
            server.Post(
                LanguageModelMethods.ChatPart,
                new ChatPartParams(requestId, value.Text, value.ToolCall is { } call ? new ChatToolCallDto(call.Id, call.Name, call.Arguments) : null),
                PluginJsonContext.Default.ChatPartParams);
        }
    }
}

/// <summary>A plugin's view of the language model registry.</summary>
internal sealed class PluginLanguageModels(HostLanguageModelRegistry registry, string pluginId) : ILanguageModels, IDisposable
{
    private readonly List<IDisposable> _registrations = [];

    public IDisposable RegisterProvider(LanguageModelProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var registration = registry.Register(pluginId, provider);
        lock (_registrations)
        {
            _registrations.Add(registration);
        }

        return registration;
    }

    public void Dispose()
    {
        IDisposable[] registrations;
        lock (_registrations)
        {
            registrations = [.. _registrations];
            _registrations.Clear();
        }

        foreach (var registration in registrations)
        {
            registration.Dispose();
        }
    }
}
