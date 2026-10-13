using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Plugins.Protocol;

namespace Codalus.Workbench.Ai;

/// <summary>An MCP server the user configured (<c>ai.mcpServers</c>): a program that speaks MCP over stdio.</summary>
public sealed record McpServerConfig(string Name, string Command, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>The servers of the setting's value: <c>{ name: { command, args, env } }</c>, as Claude Desktop and VS Code write them.</summary>
    public static IReadOnlyList<McpServerConfig> Parse(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } servers)
        {
            return [];
        }

        var list = new List<McpServerConfig>();
        foreach (var server in servers.EnumerateObject())
        {
            if (server.Value.ValueKind != JsonValueKind.Object
                || !server.Value.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String || command.GetString() is not { Length: > 0 } program)
            {
                continue;
            }

            var args = server.Value.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            var env = server.Value.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object
                ? e.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.String).ToDictionary(x => x.Name, x => x.Value.GetString()!)
                : [];
            list.Add(new McpServerConfig(server.Name, program, args, env));
        }

        return list;
    }
}

/// <summary>A tool an MCP server offers.</summary>
public sealed record McpTool(string Name, string Description, string InputSchema);

/// <summary>
/// A client of one stdio MCP server (ADR 0063): JSON-RPC messages, one per line, on the program's standard input and
/// output. <c>initialize</c>, then <c>tools/list</c> and <c>tools/call</c>; requests from the server are answered
/// (<c>ping</c>) or declined.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

    private readonly Process? _process;
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Action<string>? _log;
    private long _nextId;

    private McpClient(McpServerConfig config, TextReader reader, TextWriter writer, Process? process, Action<string>? log)
    {
        Config = config;
        _reader = reader;
        _writer = writer;
        _process = process;
        _log = log;
    }

    public McpServerConfig Config { get; }

    /// <summary>Starts the server and initializes the session.</summary>
    public static async Task<McpClient> StartAsync(McpServerConfig config, string? workingDirectory, Action<string>? log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var start = new ProcessStartInfo(config.Command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        if (workingDirectory is not null && Directory.Exists(workingDirectory))
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in config.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in config.Environment)
        {
            start.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"The MCP server {config.Name} didn't start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"The MCP server {config.Name} couldn't start ({config.Command}): {ex.Message}", ex);
        }

        return await InitializeAsync(new McpClient(config, process.StandardOutput, process.StandardInput, process, log), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A session over streams already connected to a server (tests, and servers in this process).</summary>
    internal static Task<McpClient> ConnectAsync(McpServerConfig config, TextReader reader, TextWriter writer, CancellationToken cancellationToken) =>
        InitializeAsync(new McpClient(config, reader, writer, null, null), cancellationToken);

    private static async Task<McpClient> InitializeAsync(McpClient client, CancellationToken cancellationToken)
    {
        client.StartReading();
        try
        {
            await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "Codalus IDE", ["version"] = typeof(McpClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
            }, cancellationToken).ConfigureAwait(false);
            await client.NotifyAsync("notifications/initialized", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return client;
    }

    /// <summary>The server's tools (every page).</summary>
    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        do
        {
            var result = await RequestAsync("tools/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, cancellationToken).ConfigureAwait(false);
            foreach (var tool in result?["tools"]?.AsArray() ?? [])
            {
                if (tool?["name"]?.GetValue<string>() is { Length: > 0 } name)
                {
                    tools.Add(new McpTool(name, tool["description"]?.GetValue<string>() ?? string.Empty, tool["inputSchema"]?.ToJsonString() ?? """{"type":"object"}"""));
                }
            }

            cursor = result?["nextCursor"]?.GetValue<string>();
        }
        while (cursor is { Length: > 0 });
        return tools;
    }

    /// <summary>Calls a tool; returns its text content (an error's text says so).</summary>
    public async Task<string> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder();
        foreach (var content in result?["content"]?.AsArray() ?? [])
        {
            var type = content?["type"]?.GetValue<string>();
            if (type == "text")
            {
                text.Append(content!["text"]?.GetValue<string>()).Append('\n');
            }
            else if (type is not null)
            {
                text.Append('(').Append(type).Append(" content)\n");
            }
        }

        var isError = result?["isError"] is JsonValue e && e.TryGetValue<bool>(out var error) && error;
        return (isError ? "The tool reported an error: " : string.Empty) + text.ToString().TrimEnd();
    }

    private async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        try
        {
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken).ConfigureAwait(false);
            return await pending.Task.WaitAsync(s_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"The MCP server {Config.Name} didn't answer {method}.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }, cancellationToken);

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync((message.ToJsonString() + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new InvalidOperationException($"The MCP server {Config.Name} stopped.", ex);
        }
        finally
        {
            _write.Release();
        }
    }

    private void StartReading()
    {
        if (_process is { } process)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 } line)
                {
                    _log?.Invoke($"[mcp {Config.Name}] {line}");
                }
            };
            process.BeginErrorReadLine();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (await _reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.Trim().Length > 0)
                    {
                        await OnMessageAsync(line).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }

            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new InvalidOperationException($"The MCP server {Config.Name} stopped."));
            }
        });
    }

    private async Task OnMessageAsync(string line)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            _log?.Invoke($"[mcp {Config.Name}] not JSON: {line}");
            return;
        }

        var id = message?["id"];
        if (message?["method"]?.GetValue<string>() is { } method)
        {
            if (id is null)
            {
                return; // a notification: nothing here needs one
            }

            // A request from the server: answer ping, decline the rest (no sampling, roots or elicitation).
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            if (method == "ping")
            {
                reply["result"] = new JsonObject();
            }
            else
            {
                reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Codalus IDE doesn't support {method}." };
            }

            try
            {
                await WriteAsync(reply, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        if (id is JsonValue value && value.TryGetValue<long>(out var number) && _pending.TryGetValue(number, out var pending))
        {
            if (message!["error"] is { } error)
            {
                pending.TrySetException(new InvalidOperationException($"The MCP server {Config.Name}: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}"));
            }
            else
            {
                pending.TrySetResult(message["result"]);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _writer.Close();
        if (_process is { } process)
        {
            try
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            process.Dispose();
        }

        _write.Dispose();
    }
}

/// <summary>
/// The configured MCP servers for agent mode: started when first needed and kept until the window closes; their tools
/// join the agent's as <c>server__tool</c>, each call approved by the user (once, or for the session per server).
/// </summary>
public sealed class McpServers(Action<string>? log = null) : IAsyncDisposable
{
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Why servers couldn't start, by name.</summary>
    public IReadOnlyDictionary<string, string> Failures => _failed;

    /// <summary>The tools of <paramref name="configs"/>' servers, started as needed (a server that fails is left out and noted).</summary>
    public async Task<IReadOnlyList<AiAgentTool>> ToolsAsync(IReadOnlyList<McpServerConfig> configs, string? workingDirectory, IAiAgentHost host, CancellationToken cancellationToken)
    {
        var tools = new List<AiAgentTool>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var config in configs)
            {
                if (_clients.TryGetValue(config.Name, out var running) && running.Config != config)
                {
                    await running.DisposeAsync().ConfigureAwait(false);
                    _clients.Remove(config.Name);
                }

                if (!_clients.TryGetValue(config.Name, out var client))
                {
                    try
                    {
                        client = await McpClient.StartAsync(config, workingDirectory, log, cancellationToken).ConfigureAwait(false);
                        _clients[config.Name] = client;
                        _failed.Remove(config.Name);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _failed[config.Name] = ex.Message;
                        log?.Invoke("[mcp] " + ex.Message);
                        continue;
                    }
                }

                IReadOnlyList<McpTool> serverTools;
                try
                {
                    serverTools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    _failed[config.Name] = ex.Message;
                    continue;
                }

                var prefix = ToolName(config.Name);
                foreach (var tool in serverTools)
                {
                    var name = (prefix + "__" + ToolName(tool.Name)) is var full && full.Length > 64 ? full[..64] : full;
                    var server = config.Name;
                    var mcp = client;
                    tools.Add(new AiAgentTool(new AiToolDto(name, $"({config.Name}) {tool.Description}", tool.InputSchema), async (args, ct) =>
                    {
                        if (!_allowed.Contains(server))
                        {
                            switch (await host.ApproveToolAsync(server, tool.Name, args.ToJsonString(), ct).ConfigureAwait(false))
                            {
                                case AiApproval.Deny:
                                    return "The user didn't allow this call.";
                                case AiApproval.Session:
                                    _allowed.Add(server);
                                    break;
                            }
                        }

                        return AiAgentTools.Cut(await mcp.CallToolAsync(tool.Name, args, ct).ConfigureAwait(false));
                    }));
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return tools;
    }

    /// <summary>A name the providers accept: letters, digits, <c>_</c> and <c>-</c>.</summary>
    internal static string ToolName(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            text.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _clients.Clear();
        _gate.Dispose();
    }
}
