using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Workbench.Ai.Mcp;

/// <summary>A configured MCP server: the command that starts it.</summary>
public sealed record McpServerConfig(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env);

/// <summary>A tool an MCP server offers.</summary>
public sealed record McpTool(string Name, string Description, JsonElement InputSchema);

/// <summary>An MCP server failed: it didn't start, answered with an error, or a tool call failed.</summary>
public sealed class McpException(string message) : Exception(message);

/// <summary>
/// A client of one MCP server over stdio (M28c, ADR 0063): newline-delimited JSON-RPC 2.0 to the process's standard
/// input and output. It initializes, lists the tools and calls them. The server's own requests are answered (<c>ping</c>)
/// or refused. JSON is written by hand, so it stays Native AOT clean.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private readonly Task _reading;
    private long _nextId;

    private McpClient(string name, Process process)
    {
        Name = name;
        _process = process;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lock (_stderr)
                {
                    if (_stderr.Length < 4000)
                    {
                        _stderr.AppendLine(line);
                    }
                }
            }
        };
        _process.BeginErrorReadLine();
        _reading = Task.Run(ReadAsync);
    }

    public string Name { get; }

    public IReadOnlyList<McpTool> Tools { get; private set; } = [];

    /// <summary>What the server wrote to its standard error (for the log when it fails).</summary>
    public string ErrorOutput
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>Starts the server, initializes the session and reads its tools.</summary>
    public static async Task<McpClient> StartAsync(string name, McpServerConfig config, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(config.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in config.Args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in config.Env)
        {
            start.Environment[key] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new McpException($"The MCP server '{name}' didn't start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new McpException($"The MCP server '{name}' didn't start ({config.Command}: {ex.Message}).");
        }

        var client = new McpClient(name, process);
        try
        {
            await client.RequestAsync("initialize", w =>
            {
                w.WriteString("protocolVersion", ProtocolVersion);
                w.WriteStartObject("capabilities");
                w.WriteEndObject();
                w.WriteStartObject("clientInfo");
                w.WriteString("name", "Codalus IDE");
                w.WriteString("version", typeof(McpClient).Assembly.GetName().Version?.ToString() ?? "0");
                w.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            await client.NotifyAsync("notifications/initialized", cancellationToken).ConfigureAwait(false);
            client.Tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        do
        {
            var page = cursor;
            var result = await RequestAsync("tools/list", w =>
            {
                if (page is not null)
                {
                    w.WriteString("cursor", page);
                }
            }, cancellationToken).ConfigureAwait(false);
            if (result.TryGetProperty("tools", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in list.EnumerateArray())
                {
                    if (tool.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } toolName)
                    {
                        var description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        var schema = tool.TryGetProperty("inputSchema", out var s) ? s.Clone() : JsonElement.Parse("""{"type":"object"}""");
                        tools.Add(new McpTool(toolName, description, schema));
                    }
                }
            }

            cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null && tools.Count < 500);
        return tools;
    }

    /// <summary>Calls a tool; its text content, joined. A tool's error (<c>isError</c>) throws <see cref="McpException"/>.</summary>
    public async Task<string> CallAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("tools/call", w =>
        {
            w.WriteString("name", tool);
            w.WritePropertyName("arguments");
            (arguments.ValueKind == JsonValueKind.Object ? arguments : JsonElement.Parse("{}")).WriteTo(w);
        }, cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "text" && part.TryGetProperty("text", out var value))
                {
                    text.Append(value.GetString()).Append('\n');
                }
                else if (type is not null)
                {
                    text.Append('[').Append(type).Append(" content]\n");
                }
            }
        }

        if (text.Length == 0 && result.TryGetProperty("structuredContent", out var structured))
        {
            text.Append(structured.GetRawText());
        }

        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            throw new McpException(text.ToString().Trim());
        }

        return text.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ JSON-RPC

    private async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter> writeParams, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        try
        {
            await WriteAsync(w =>
            {
                w.WriteString("jsonrpc", "2.0");
                w.WriteNumber("id", id);
                w.WriteString("method", method);
                w.WriteStartObject("params");
                writeParams(w);
                w.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            return await pending.Task.WaitAsync(s_requestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new McpException($"The MCP server '{Name}' didn't answer {method}.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        WriteAsync(w =>
        {
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", method);
        }, cancellationToken);

    private async Task WriteAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        var line = Encoding.UTF8.GetString(buffer.ToArray());
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new McpException($"The MCP server '{Name}' stopped ({ex.Message}). {ErrorOutput}".Trim());
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                JsonElement message;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    message = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue; // a server that logs to stdout: not a message
                }

                var hasId = message.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String;
                if (message.TryGetProperty("method", out var method))
                {
                    if (hasId)
                    {
                        await AnswerAsync(idElement, method.GetString()).ConfigureAwait(false);
                    }

                    continue;
                }

                if (!hasId || !long.TryParse(idElement.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || !_pending.TryGetValue(id, out var pending))
                {
                    continue;
                }

                if (message.TryGetProperty("error", out var error))
                {
                    var text = error.TryGetProperty("message", out var m) ? m.GetString() : error.GetRawText();
                    pending.TrySetException(new McpException($"{Name}: {text}"));
                }
                else
                {
                    pending.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new McpException($"The MCP server '{Name}' stopped. {ErrorOutput}".Trim()));
        }
    }

    /// <summary>The server's requests: <c>ping</c> is answered; anything else is refused.</summary>
    private Task AnswerAsync(JsonElement id, string? method) =>
        WriteAsync(w =>
        {
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            if (method == "ping")
            {
                w.WriteStartObject("result");
                w.WriteEndObject();
            }
            else
            {
                w.WriteStartObject("error");
                w.WriteNumber("code", -32601);
                w.WriteString("message", "Codalus IDE doesn't support " + method + ".");
                w.WriteEndObject();
            }
        }, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            try
            {
                ProcessTree.Kill(_process);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await _reading.ConfigureAwait(false);
        _process.Dispose();
        _write.Dispose();
    }
}
