using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Codalus.Workbench.Ai.Mcp;

/// <summary>
/// The MCP server <c>codalus mcp</c> runs for an external agent (M28c, ADR 0063): newline-delimited JSON-RPC 2.0 on
/// standard input and output. It offers the IDE's tools (<see cref="Tools"/>) and relays each call to the running IDE
/// through <see cref="McpBridge"/>; without one, a call answers with what to do.
/// </summary>
public sealed class McpStdioServer(TextReader input, TextWriter output, Func<string, JsonElement, CancellationToken, Task<string>> call)
{
    /// <summary>The tools: name, description, input schema.</summary>
    public static readonly IReadOnlyList<(string Name, string Description, string Schema)> Tools =
    [
        ("get_solution", "The solution open in Codalus IDE: its path and its projects, with their target frameworks, output types and whether they are test projects.", """{"type":"object","properties":{}}"""),
        ("get_open_files", "The files open in Codalus IDE's editor, the active one, and its selected text.", """{"type":"object","properties":{}}"""),
        ("get_diagnostics", "The errors and warnings in Codalus IDE's Error List, of one file or all.", """{"type":"object","properties":{"path":{"type":"string","description":"A file path; empty for all."}}}"""),
        ("get_build_output", "The end of the last build's output in Codalus IDE.", """{"type":"object","properties":{}}"""),
        ("build", "Build the solution in Codalus IDE (the user approves it there first) and return the result with its errors and warnings.", """{"type":"object","properties":{}}"""),
    ];

    /// <summary>Answers requests until the input ends.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
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
                await WriteAsync(w => Error(w, null, -32700, "Parse error")).ConfigureAwait(false);
                continue;
            }

            if (!message.TryGetProperty("id", out var id) || !message.TryGetProperty("method", out var methodElement))
            {
                continue; // a notification (notifications/initialized) or a response
            }

            var method = methodElement.GetString();
            var args = message.TryGetProperty("params", out var p) ? p : default;
            switch (method)
            {
                case "initialize":
                    await WriteAsync(w => Result(w, id, r =>
                    {
                        r.WriteString("protocolVersion", args.ValueKind == JsonValueKind.Object && args.TryGetProperty("protocolVersion", out var v) ? v.GetString() : McpClient.ProtocolVersion);
                        r.WriteStartObject("capabilities");
                        r.WriteStartObject("tools");
                        r.WriteEndObject();
                        r.WriteEndObject();
                        r.WriteStartObject("serverInfo");
                        r.WriteString("name", "codalus");
                        r.WriteString("version", typeof(McpStdioServer).Assembly.GetName().Version?.ToString() ?? "0");
                        r.WriteEndObject();
                    })).ConfigureAwait(false);
                    break;
                case "ping":
                    await WriteAsync(w => Result(w, id, _ => { })).ConfigureAwait(false);
                    break;
                case "tools/list":
                    await WriteAsync(w => Result(w, id, r =>
                    {
                        r.WriteStartArray("tools");
                        foreach (var (name, description, schema) in Tools)
                        {
                            r.WriteStartObject();
                            r.WriteString("name", name);
                            r.WriteString("description", description);
                            r.WritePropertyName("inputSchema");
                            using var document = JsonDocument.Parse(schema);
                            document.RootElement.WriteTo(r);
                            r.WriteEndObject();
                        }

                        r.WriteEndArray();
                    })).ConfigureAwait(false);
                    break;
                case "tools/call":
                    var tool = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var arguments = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("arguments", out var a) ? a : default;
                    string text;
                    var isError = false;
                    try
                    {
                        text = Tools.Any(t => t.Name == tool) ? await call(tool, arguments, cancellationToken).ConfigureAwait(false) : $"There is no tool '{tool}'.";
                        isError = !Tools.Any(t => t.Name == tool);
                    }
                    catch (Exception ex) when (ex is McpException or IOException or TimeoutException)
                    {
                        text = ex.Message;
                        isError = true;
                    }

                    await WriteAsync(w => Result(w, id, r =>
                    {
                        r.WriteStartArray("content");
                        r.WriteStartObject();
                        r.WriteString("type", "text");
                        r.WriteString("text", text);
                        r.WriteEndObject();
                        r.WriteEndArray();
                        r.WriteBoolean("isError", isError);
                    })).ConfigureAwait(false);
                    break;
                default:
                    await WriteAsync(w => Error(w, id, -32601, $"Method not found: {method}")).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task WriteAsync(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        await output.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static void Result(Utf8JsonWriter w, JsonElement id, Action<Utf8JsonWriter> result)
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WritePropertyName("id");
        id.WriteTo(w);
        w.WriteStartObject("result");
        result(w);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void Error(Utf8JsonWriter w, JsonElement? id, int code, string message)
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WritePropertyName("id");
        if (id is { } value)
        {
            value.WriteTo(w);
        }
        else
        {
            w.WriteNullValue();
        }

        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>
/// The pipe between <c>codalus mcp</c> and the running IDE (M28c, ADR 0063): for the current user only, open only while
/// <c>ai.mcpServer.enabled</c> is on. A call is one line of JSON, <c>{"tool": …, "arguments": …}</c>, and the answer one
/// line, <c>{"text": …}</c> or <c>{"error": …}</c>.
/// </summary>
public sealed class McpBridge : IDisposable
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;
    private readonly Func<string, JsonElement, CancellationToken, Task<string>> _handler;

    private McpBridge(string pipeName, Func<string, JsonElement, CancellationToken, Task<string>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;

        // The first instance exists before Listen returns, so a call made right after it connects even when the
        // thread pool is slow to start the loop.
        var first = CreatePipe();
        _ = Task.Run(() => ListenAsync(first));
    }

    /// <summary>The bridge's pipe for a user-data folder's pipe name (the single-instance pipe's, with <c>-mcp</c>).</summary>
    public static string PipeName(string instancePipeName) => instancePipeName + "-mcp";

    /// <summary>
    /// The pipe of one external agent's chat session (M48, ADR 0079): the instance pipe's name, <c>-a</c> and the session's
    /// token. Kept short: on Unix a pipe is a socket file whose path (in the temporary folder) has a length limit.
    /// </summary>
    public static string SessionPipeName(string instancePipeName, string sessionToken) => instancePipeName + "-a" + sessionToken;

    /// <summary>A new session token: 12 hexadecimal characters.</summary>
    public static string NewSessionToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>Answers calls with <paramref name="handler"/> (on a background thread) until disposed.</summary>
    public static McpBridge Listen(string pipeName, Func<string, JsonElement, CancellationToken, Task<string>> handler) => new(pipeName, handler);

    /// <summary>Sends a call to the running IDE; throws <see cref="McpException"/> when none answers or the call fails.</summary>
    public static Task<string> CallAsync(string pipeName, string tool, JsonElement arguments, CancellationToken cancellationToken) =>
        CallAsync(pipeName, tool, arguments, null, cancellationToken);

    /// <summary>A call, with what to say when nobody listens on the pipe (an agent's session that ended).</summary>
    public static async Task<string> CallAsync(string pipeName, string tool, JsonElement arguments, string? notListening, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            await pipe.ConnectAsync(s_connectTimeout, cancellationToken).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("tool", tool);
                json.WritePropertyName("arguments");
                (arguments.ValueKind == JsonValueKind.Object ? arguments : JsonElement.Parse("{}")).WriteTo(json);
                json.WriteEndObject();
            }

            await writer.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? throw new McpException("Codalus IDE closed the connection.");
            using var answer = JsonDocument.Parse(line);
            return answer.RootElement.TryGetProperty("error", out var error) ? throw new McpException(error.GetString() ?? "The call failed.")
                : answer.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new McpException(notListening ?? "Codalus IDE isn't running, or its MCP server is off: turn on Tools → Options → AI → MCP Server (ai.mcpServer.enabled).");
        }
    }

    private NamedPipeServerStream CreatePipe() =>
        new(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream? first)
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = first;
            first = null;
            try
            {
                pipe ??= CreatePipe();
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected));
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (IOException)
            {
                pipe?.Dispose();
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                if (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is not { } line)
                {
                    return;
                }

                string? text = null;
                string? error = null;
                try
                {
                    using var request = JsonDocument.Parse(line);
                    var tool = request.RootElement.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";
                    var arguments = request.RootElement.TryGetProperty("arguments", out var a) ? a.Clone() : default;
                    text = await _handler(tool, arguments, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException or McpException or InvalidOperationException)
                {
                    error = ex.Message;
                }

                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer))
                {
                    json.WriteStartObject();
                    if (error is not null)
                    {
                        json.WriteString("error", error);
                    }
                    else
                    {
                        json.WriteString("text", text);
                    }

                    json.WriteEndObject();
                }

                await writer.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
