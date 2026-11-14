using System.Collections.Concurrent;
using Codalus.Plugins.Proposed;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;

namespace Codalus.Plugins.Host.Proposed;

/// <summary>
/// The host's tools for AI agents (ADR 0092): registrations go to the shell, and the shell's <c>aiTools/invoke</c>
/// requests come to the tool they name.
/// </summary>
internal sealed class HostAiToolRegistry(PluginHostServer server)
{
    private readonly ConcurrentDictionary<string, AiTool> _tools = new(StringComparer.Ordinal);

    public IDisposable Register(string pluginId, AiTool tool)
    {
        _tools[tool.Name] = tool;
        server.Post(AiToolMethods.RegisterTool, new AiToolParams(pluginId, tool.Name), PluginJsonContext.Default.AiToolParams);
        return new PluginHostServer.Disposable(() =>
        {
            if (_tools.TryRemove(new KeyValuePair<string, AiTool>(tool.Name, tool)))
            {
                server.Post(AiToolMethods.UnregisterTool, new AiToolParams(pluginId, tool.Name), PluginJsonContext.Default.AiToolParams);
            }
        });
    }

    public void RegisterHandlers(JsonRpcConnection connection)
    {
        var json = PluginJsonContext.Default;
        connection.OnRequest(AiToolMethods.Invoke, json.AiToolInvokeParams, json.AiToolResultDto, async (p, ct) =>
        {
            if (!_tools.TryGetValue(p.Name, out var tool))
            {
                throw new JsonRpcHandlerException(AiToolErrorCodes.NoTool, $"No AI tool '{p.Name}' is registered.");
            }

            try
            {
                var result = await tool.InvokeAsync(p.Arguments, ct).ConfigureAwait(false);
                return new AiToolResultDto(result.Text, result.IsError);
            }
            catch (AiToolException ex)
            {
                return new AiToolResultDto(ex.Message, IsError: true);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                server.Log(null, "warning", $"AI tool '{p.Name}': {ex}");
                throw new JsonRpcHandlerException(AiToolErrorCodes.Failed, ex.Message);
            }
        });
    }
}

/// <summary>One plugin's view of the AI tools: its registrations end when it deactivates.</summary>
internal sealed class PluginAiTools(HostAiToolRegistry registry, string pluginId) : IAiTools, IDisposable
{
    private readonly List<IDisposable> _registrations = [];

    public IDisposable RegisterTool(AiTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var registration = registry.Register(pluginId, tool);
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
