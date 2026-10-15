using System.Text;
using System.Text.Json;
using Codalus.Plugins.Protocol;
using Codalus.Workbench.Ai.Agent;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Ai.Mcp;

/// <summary>
/// The MCP servers the user configured (<c>ai.mcp.servers</c>, user settings only: a workspace can't add a process or a
/// tool; ADR 0063). They start when agent mode first asks for their tools and stop when the settings change or the IDE
/// closes. A server that fails is logged and left out; the others still work.
/// </summary>
public sealed class McpServers : IAsyncDisposable
{
    public const string ServersSetting = "ai.mcp.servers";

    private readonly SettingsService _settings;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.Ordinal);
    private string? _configured;

    public McpServers(SettingsService settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
    }

    /// <summary>The configured servers, read from the user's settings only.</summary>
    public IReadOnlyDictionary<string, McpServerConfig> Configured => Parse(_settings.GetLayerValue(ServersSetting, SettingScope.User));

    /// <summary>Parses <c>{ "name": { "command": "…", "args": ["…"], "env": { "K": "V" } } }</c>; entries without a command are skipped.</summary>
    public static IReadOnlyDictionary<string, McpServerConfig> Parse(JsonElement? value)
    {
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        if (value is not { ValueKind: JsonValueKind.Object } root)
        {
            return servers;
        }

        foreach (var server in root.EnumerateObject())
        {
            if (server.Value.ValueKind != JsonValueKind.Object || !server.Value.TryGetProperty("command", out var command) || command.GetString() is not { Length: > 0 } commandText)
            {
                continue;
            }

            var args = server.Value.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (server.Value.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                foreach (var variable in e.EnumerateObject().Where(v => v.Value.ValueKind == JsonValueKind.String))
                {
                    env[variable.Name] = variable.Value.GetString()!;
                }
            }

            servers[server.Name] = new McpServerConfig(commandText, args, env);
        }

        return servers;
    }

    /// <summary>The servers' tools for agent mode, as <c>&lt;server&gt;__&lt;tool&gt;</c>, starting the servers that aren't running.</summary>
    public async Task<IReadOnlyList<ExternalAgentTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configured = Configured;
            var fingerprint = string.Join('|', configured.Select(c => c.Key + "=" + c.Value.Command + " " + string.Join(' ', c.Value.Args) + " " + string.Join(',', c.Value.Env.Select(v => v.Key + "=" + v.Value))));
            if (fingerprint != _configured)
            {
                await StopAllAsync().ConfigureAwait(false);
                _configured = fingerprint;
            }

            foreach (var (name, config) in configured)
            {
                if (_clients.ContainsKey(name))
                {
                    continue;
                }

                try
                {
                    var client = await McpClient.StartAsync(name, config, cancellationToken).ConfigureAwait(false);
                    _clients[name] = client;
                    _log($"    MCP server '{name}': {client.Tools.Count} tool(s)");
                }
                catch (McpException ex)
                {
                    _log($"    MCP server '{name}' failed: {ex.Message}");
                }
            }

            var tools = new List<ExternalAgentTool>();
            foreach (var client in _clients.Values)
            {
                foreach (var tool in client.Tools)
                {
                    var name = ToolName(client.Name, tool.Name);
                    tools.Add(new ExternalAgentTool(new ChatToolDto(name, tool.Description, tool.InputSchema), client.Name, (args, ct) => client.CallAsync(tool.Name, args, ct)));
                }
            }

            return tools;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A tool's name for the model: <c>server__tool</c>, letters, digits, <c>_</c> and <c>-</c> only, at most 64 characters.</summary>
    public static string ToolName(string server, string tool)
    {
        var sb = new StringBuilder();
        foreach (var c in server + "__" + tool)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return sb.Length > 64 ? sb.ToString(0, 64) : sb.ToString();
    }

    private async Task StopAllAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _clients.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAllAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
