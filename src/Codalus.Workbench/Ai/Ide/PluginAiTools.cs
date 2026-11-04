using System.Collections.Concurrent;
using System.Text.Json;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Plugins;

namespace Codalus.Workbench.Ai.Ide;

/// <summary>
/// Plugins' tools for AI agents (proposed, ADR 0092): declared in a manifest's <c>contributes.aiTools</c>, so agents see
/// them before the plugin runs; the first call activates the plugin (<c>onAiTool:&lt;name&gt;</c>) and waits for it to
/// register the tool, then sends <c>aiTools/invoke</c>. A tool whose name the IDE uses already is left out.
/// </summary>
public sealed class PluginAiTools : IAiToolSink
{
    private static readonly TimeSpan s_registrationTimeout = TimeSpan.FromSeconds(30);
    private readonly PluginManager _plugins;
    private readonly ConcurrentDictionary<string, IHostingConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IHostingConnection>> _waiting = new(StringComparer.Ordinal);

    public PluginAiTools(PluginManager plugins)
    {
        _plugins = plugins;
        plugins.AiToolSink = this;
    }

    /// <summary>The tools the usable plugins declare, as agents see them; never one named like a built-in tool.</summary>
    public IReadOnlyList<IdeToolDefinition> Definitions =>
        [.. _plugins.AiToolDeclarations
            .Where(d => IdeTools.Find(d.Tool.Name) is null)
            .GroupBy(d => d.Tool.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(d => new IdeToolDefinition(
                d.Tool.Name,
                d.Tool.Description,
                d.Tool.InputSchema is { } schema ? schema.GetRawText() : """{"type":"object","properties":{}}""",
                IdeToolKind.Plugin,
                d.Plugin.Id))];

    /// <summary>Calls the plugin's tool <paramref name="name"/>.</summary>
    public async Task<IdeToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(name, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await connection.InvokeAsync(
                AiToolMethods.Invoke,
                new AiToolInvokeParams(name, arguments.ValueKind == JsonValueKind.Object ? arguments : JsonElement.Parse("{}")),
                PluginJsonContext.Default.AiToolInvokeParams,
                PluginJsonContext.Default.AiToolResultDto,
                cancellationToken).ConfigureAwait(false);
            return new IdeToolResult(result.Text, result.IsError);
        }
        catch (JsonRpcRemoteException ex)
        {
            return IdeToolResult.Error(ex.Message);
        }
        catch (JsonRpcConnectionLostException ex)
        {
            return IdeToolResult.Error("The plugin host stopped: " + ex.Message);
        }
    }

    private async Task<IHostingConnection> ConnectAsync(string name, CancellationToken cancellationToken)
    {
        if (_connections.TryGetValue(name, out var connection))
        {
            return connection;
        }

        var declaration = _plugins.AiToolDeclarations.FirstOrDefault(d => d.Tool.Name == name);
        if (declaration.Plugin is null)
        {
            throw new IdeToolException($"No plugin provides the tool '{name}'.");
        }

        var waiting = _waiting.GetOrAdd(name, _ => new TaskCompletionSource<IHostingConnection>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_connections.TryGetValue(name, out connection))
        {
            return connection;
        }

        if (!await _plugins.ActivateAsync(declaration.Plugin, "onAiTool:" + name, cancellationToken).ConfigureAwait(false))
        {
            _waiting.TryRemove(name, out _);
            throw new IdeToolException($"The plugin {declaration.Plugin.Id} isn't available: {declaration.Plugin.Error}");
        }

        try
        {
            return await waiting.Task.WaitAsync(s_registrationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _waiting.TryRemove(name, out _);
            throw new IdeToolException($"The plugin {declaration.Plugin.Id} didn't register its tool '{name}'.");
        }
    }

    public void ToolRegistered(string pluginId, string name, IHostingConnection connection)
    {
        _connections[name] = connection;
        if (_waiting.TryRemove(name, out var waiting))
        {
            waiting.TrySetResult(connection);
        }
    }

    public void ToolUnregistered(string name) => _connections.TryRemove(name, out _);
}
