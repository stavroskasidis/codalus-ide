using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Codalus.Workbench.Ai.Ide;

namespace Codalus.Workbench.Ai.Mcp;

/// <summary>
/// The MCP server <c>codalus mcp</c> runs for an external agent (M28c, ADR 0063, ADR 0092): newline-delimited JSON-RPC 2.0
/// on standard input and output. It lists the IDE's tools (<see cref="IdeTools"/>, and the plugins' tools, asked of the
/// running IDE) and relays each call to it through <see cref="McpBridge"/>; without one, a call answers with what to do.
/// Its <c>initialize</c> answer carries <see cref="IdeTools.Instructions"/>, so an agent prefers the IDE's tools to grep.
/// </summary>
public sealed class McpStdioServer
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Func<CancellationToken, Task<IReadOnlyList<IdeToolDefinition>>> _list;
    private readonly Func<string, JsonElement, CancellationToken, Task<IdeToolResult>> _call;
    private IReadOnlyList<IdeToolDefinition> _listed = IdeTools.BuiltIn;

    /// <summary>A server whose tools are the built-in ones and whose calls answer text (tests, and older callers).</summary>
    public McpStdioServer(TextReader input, TextWriter output, Func<string, JsonElement, CancellationToken, Task<string>> call)
        : this(input, output, _ => Task.FromResult(IdeTools.BuiltIn), async (tool, args, ct) => new IdeToolResult(await call(tool, args, ct).ConfigureAwait(false)))
    {
    }

    /// <summary>A server that asks <paramref name="list"/> for its tools (the IDE's, with its plugins') and relays calls to <paramref name="call"/>.</summary>
    public McpStdioServer(TextReader input, TextWriter output, Func<CancellationToken, Task<IReadOnlyList<IdeToolDefinition>>> list, Func<string, JsonElement, CancellationToken, Task<IdeToolResult>> call)
    {
        _input = input;
        _output = output;
        _list = list;
        _call = call;
    }

    /// <summary>The built-in tools: name, description, input schema.</summary>
    public static IReadOnlyList<(string Name, string Description, string Schema)> Tools { get; } = [.. IdeTools.BuiltIn.Select(t => (t.Name, t.Description, t.Schema))];

    /// <summary>Answers requests until the input ends.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
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
                        r.WriteString("instructions", IdeTools.Instructions);
                    })).ConfigureAwait(false);
                    break;
                case "ping":
                    await WriteAsync(w => Result(w, id, _ => { })).ConfigureAwait(false);
                    break;
                case "tools/list":
                    try
                    {
                        // The running IDE's list (its plugins' tools too); the built-in list when it doesn't answer.
                        _listed = await _list(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is McpException or IOException or TimeoutException or JsonException)
                    {
                        _listed = IdeTools.BuiltIn;
                    }

                    await WriteAsync(w => Result(w, id, r =>
                    {
                        r.WriteStartArray("tools");
                        foreach (var tool in _listed)
                        {
                            r.WriteStartObject();
                            r.WriteString("name", tool.Name);
                            r.WriteString("description", tool.Description);
                            r.WritePropertyName("inputSchema");
                            using var document = JsonDocument.Parse(tool.Schema);
                            document.RootElement.WriteTo(r);
                            r.WriteEndObject();
                        }

                        r.WriteEndArray();
                    })).ConfigureAwait(false);
                    break;
                case "tools/call":
                    var tool = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var arguments = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("arguments", out var a) ? a : default;
                    IdeToolResult result;
                    try
                    {
                        // Every name is relayed, a plugin's tool added since the list was read too: the IDE knows its
                        // tools and answers an unknown one with an error.
                        result = await _call(tool, arguments, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is McpException or IOException or TimeoutException)
                    {
                        result = IdeToolResult.Error(ex.Message);
                    }

                    await WriteAsync(w => Result(w, id, r =>
                    {
                        r.WriteStartArray("content");
                        r.WriteStartObject();
                        r.WriteString("type", "text");
                        r.WriteString("text", result.Text);
                        r.WriteEndObject();
                        foreach (var image in result.Images)
                        {
                            r.WriteStartObject();
                            r.WriteString("type", "image");
                            r.WriteString("data", image.Data);
                            r.WriteString("mimeType", image.MimeType);
                            r.WriteEndObject();
                        }

                        r.WriteEndArray();
                        r.WriteBoolean("isError", result.IsError);
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

        await _output.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
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
/// line, <c>{"text": …, "isError"?: true, "images"?: [{"data", "mimeType"}]}</c> or <c>{"error": …}</c>. The tool
/// <see cref="ListToolsCall"/> answers the IDE's tools as <see cref="IdeTools.ToJson"/>'s array (ADR 0092).
/// </summary>
public sealed class McpBridge : IDisposable
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;
    private readonly Func<string, JsonElement, CancellationToken, Task<IdeToolResult>> _handler;

    /// <summary>The call that lists the IDE's tools (its plugins' too) instead of calling one.</summary>
    public const string ListToolsCall = "$tools/list";

    private McpBridge(string pipeName, Func<string, JsonElement, CancellationToken, Task<IdeToolResult>> handler)
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
    public static McpBridge Listen(string pipeName, Func<string, JsonElement, CancellationToken, Task<string>> handler) =>
        new(pipeName, async (tool, args, ct) => new IdeToolResult(await handler(tool, args, ct).ConfigureAwait(false)));

    /// <summary>Answers calls with results that may be errors or carry images.</summary>
    public static McpBridge Listen(string pipeName, Func<string, JsonElement, CancellationToken, Task<IdeToolResult>> handler) => new(pipeName, handler);

    /// <summary>The running IDE's tools; throws <see cref="McpException"/> when none answers.</summary>
    public static async Task<IReadOnlyList<IdeToolDefinition>> ListToolsAsync(string pipeName, string? notListening, CancellationToken cancellationToken) =>
        IdeTools.FromJson((await CallToolAsync(pipeName, ListToolsCall, default, notListening, cancellationToken).ConfigureAwait(false)).Text);

    /// <summary>Sends a call to the running IDE; throws <see cref="McpException"/> when none answers or the call fails.</summary>
    public static Task<string> CallAsync(string pipeName, string tool, JsonElement arguments, CancellationToken cancellationToken) =>
        CallAsync(pipeName, tool, arguments, null, cancellationToken);

    /// <summary>A call, with what to say when nobody listens on the pipe (an agent's session that ended).</summary>
    public static async Task<string> CallAsync(string pipeName, string tool, JsonElement arguments, string? notListening, CancellationToken cancellationToken) =>
        (await CallToolAsync(pipeName, tool, arguments, notListening, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>A call whose result keeps whether it failed and its images.</summary>
    public static async Task<IdeToolResult> CallToolAsync(string pipeName, string tool, JsonElement arguments, string? notListening, CancellationToken cancellationToken)
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
            var root = answer.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw new McpException(error.GetString() ?? "The call failed.");
            }

            var images = root.TryGetProperty("images", out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Select(i => new IdeToolImage(i.GetProperty("data").GetString() ?? "", i.GetProperty("mimeType").GetString() ?? "image/png"))]
                : Array.Empty<IdeToolImage>();
            return new IdeToolResult(root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "", root.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True) { Images = images };
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
        var pipe = first;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                pipe ??= CreatePipe();
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                try
                {
                    // The next instance exists before the connected one is served and ends: with none left, the pipe
                    // closes, and a call connecting just then failed with "isn't running" (Linux CI, run 39510410225).
                    pipe = CreatePipe();
                }
                finally
                {
                    _ = Task.Run(() => ServeAsync(connected));
                }
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (IOException)
            {
                pipe?.Dispose();
                pipe = null;
                await Task.Delay(200).ConfigureAwait(false);
            }
        }

        pipe?.Dispose();
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

                IdeToolResult? result = null;
                string? error = null;
                try
                {
                    using var request = JsonDocument.Parse(line);
                    var tool = request.RootElement.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";
                    var arguments = request.RootElement.TryGetProperty("arguments", out var a) ? a.Clone() : default;
                    result = await _handler(tool, arguments, _stop.Token).ConfigureAwait(false);
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
                        json.WriteString("text", result?.Text);
                        if (result is { IsError: true })
                        {
                            json.WriteBoolean("isError", true);
                        }

                        if (result is { Images.Count: > 0 })
                        {
                            json.WriteStartArray("images");
                            foreach (var image in result.Images)
                            {
                                json.WriteStartObject();
                                json.WriteString("data", image.Data);
                                json.WriteString("mimeType", image.MimeType);
                                json.WriteEndObject();
                            }

                            json.WriteEndArray();
                        }
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
