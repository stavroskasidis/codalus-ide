using System.Collections.Concurrent;
using Codalus.Plugins.Protocol;
using Codalus.Plugins.Proposed;
using Codalus.Rpc;

namespace Codalus.Plugins.Host.Proposed;

/// <summary>
/// The host's side of AI model providers (proposed, M28a, ADR 0062): the plugins' providers by id, their models, their
/// configuration, and chat answers streamed to the shell as <c>ai/chunk</c> notifications; the request's answer says how
/// many were sent, since the shell may still be handling the last ones when it arrives.
/// </summary>
internal sealed class HostAiRegistry(PluginHostServer server)
{
    private readonly ConcurrentDictionary<string, IAiModelProvider> _providers = new(StringComparer.Ordinal);

    public IDisposable Register(string pluginId, string id, string label, IAiModelProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[id] = provider;
        server.Post(AiMethods.Register, new AiProviderParams(pluginId, id, label), PluginJsonContext.Default.AiProviderParams);
        return new Unregistration(() =>
        {
            if (_providers.TryRemove(new KeyValuePair<string, IAiModelProvider>(id, provider)))
            {
                server.Post(AiMethods.Unregister, new AiProviderParams(pluginId, id, label), PluginJsonContext.Default.AiProviderParams);
            }
        });
    }

    public void RegisterHandlers(JsonRpcConnection connection)
    {
        var json = PluginJsonContext.Default;
        connection.OnRequest(AiMethods.Models, json.AiProviderIdParams, json.AiModelsResult, async (p, ct) =>
            new AiModelsResult([.. (await Provider(p.ProviderId).GetModelsAsync(ct).ConfigureAwait(false)).Select(m => new AiModelDto(m.Id, m.Label))]));
        connection.OnRequest(AiMethods.Configure, json.AiProviderIdParams, json.AiConfigureResult, async (p, ct) =>
            new AiConfigureResult(await Provider(p.ProviderId).ConfigureAsync(ct).ConfigureAwait(false)));
        connection.OnRequest(AiMethods.Chat, json.AiChatParams, json.AiChatResult, async (p, ct) =>
        {
            var request = new AiChatRequest(p.Model, [.. p.Messages.Select(m => new AiMessage(m.Role, m.Content)
            {
                ToolCalls = m.ToolCalls is null ? null : [.. m.ToolCalls.Select(c => new AiToolCall(c.Id, c.Name, c.Arguments))],
                ToolCallId = m.ToolCallId,
            })])
            {
                MaxTokens = p.MaxTokens,
                Tools = p.Tools is null ? null : [.. p.Tools.Select(t => new AiTool(t.Name, t.Description, t.InputSchema))],
            };
            var chunks = 0;
            if (p.Events)
            {
                await foreach (var item in Provider(p.ProviderId).ChatEventsAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (item.Text is { Length: 0 } && item.ToolCall is null)
                    {
                        continue;
                    }

                    var call = item.ToolCall is { } c ? new AiToolCallDto(c.Id, c.Name, c.Arguments) : null;
                    await connection.NotifyAsync(AiMethods.Event, new AiEventParams(p.StreamId, chunks, item.Text, call), json.AiEventParams, ct).ConfigureAwait(false);
                    chunks++;
                }

                return new AiChatResult(chunks);
            }

            await foreach (var text in Provider(p.ProviderId).ChatAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                if (text.Length > 0)
                {
                    await connection.NotifyAsync(AiMethods.Chunk, new AiChunkParams(p.StreamId, chunks, text), json.AiChunkParams, ct).ConfigureAwait(false);
                    chunks++;
                }
            }

            return new AiChatResult(chunks);
        });
    }

    private IAiModelProvider Provider(string id) =>
        _providers.TryGetValue(id, out var provider) ? provider : throw new JsonRpcHandlerException(PluginErrorCodes.ViewNotFound, $"No AI provider '{id}'.");

    private sealed class Unregistration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>A plugin's view of the AI registry.</summary>
internal sealed class PluginAi(HostAiRegistry registry, string pluginId) : IAi
{
    public IDisposable RegisterModelProvider(string id, string label, IAiModelProvider provider) => registry.Register(pluginId, id, label, provider);
}
