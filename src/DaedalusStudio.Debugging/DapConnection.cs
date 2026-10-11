using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Debugging;

/// <summary>A debug adapter answered a request with <c>success: false</c>.</summary>
public sealed class DapException(string command, string message) : Exception(message)
{
    public string Command { get; } = command;
}

/// <summary>The debug adapter went away.</summary>
public sealed class DapConnectionLostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An event from the adapter: its name and body (default when it has none).</summary>
public sealed record DapEvent(string Name, JsonElement Body);

/// <summary>A reverse request from the adapter that the client accepted (<see cref="DapConnection.Accept"/>).</summary>
public sealed record DapReverseRequest(string Command, JsonElement Arguments);

/// <summary>
/// A Debug Adapter Protocol client over a pair of streams (<c>Content-Length</c> framing, like LSP). Requests are
/// answered in any order; events are delivered in arrival order on one background loop. Reverse requests from the
/// adapter are refused, except those accepted with <see cref="Accept"/> or answered by a <see cref="Handle"/> handler. All
/// serialization is source-generated (AOT-safe).
/// </summary>
public sealed class DapConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<object> _events = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> _accepted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _seq;
    private Task _readLoop = Task.CompletedTask;
    private Task _eventLoop = Task.CompletedTask;

    public DapConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Raised for each adapter event, in order, on the event loop. Handlers must not block for long.</summary>
    public event Func<DapEvent, Task>? EventReceived;

    /// <summary>Raised for each accepted reverse request, on the event loop, in order with the events.</summary>
    public event Func<DapReverseRequest, Task>? ReverseRequestReceived;

    /// <summary>Completes when the adapter's output ends (it exited or closed the stream).</summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Accepts the reverse request <paramref name="command"/> (<c>startDebugging</c>): it is answered with success at
    /// once and raised as <see cref="ReverseRequestReceived"/>, so its handler can't hold up this connection. Call
    /// before <see cref="Start"/>.
    /// </summary>
    public void Accept(string command) => _accepted.Add(command);

    /// <summary>
    /// Answers the reverse request <paramref name="command"/> (<c>runInTerminal</c>, M14c) with the body
    /// <paramref name="handler"/> returns; a handler that throws answers with an error carrying its message. The handler
    /// runs off the read loop, so it may take its time. Call before <see cref="Start"/>.
    /// </summary>
    public void Handle(string command, Func<JsonElement, CancellationToken, Task<JsonElement>> handler) => _handlers[command] = handler;

    public void Start()
    {
        _readLoop = Task.Run(ReadLoopAsync);
        _eventLoop = Task.Run(EventLoopAsync);
    }

    /// <summary>Sends a request and returns the response's body (default when it has none).</summary>
    public async Task<JsonElement> RequestAsync<TArgs>(string command, TArgs arguments, JsonTypeInfo<TArgs> info, CancellationToken cancellationToken = default)
    {
        var seq = Interlocked.Increment(ref _seq);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = response;
        try
        {
            await WriteAsync(writer =>
            {
                writer.WriteNumber("seq", seq);
                writer.WriteString("type", "request");
                writer.WriteString("command", command);
                writer.WritePropertyName("arguments");
                JsonSerializer.Serialize(writer, arguments, info);
            }, cancellationToken).ConfigureAwait(false);
            await using var registration = cancellationToken.Register(() => response.TrySetCanceled(cancellationToken)).ConfigureAwait(false);
            var result = await response.Task.ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                var message = result.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (result.TryGetProperty("body", out var errorBody) && errorBody.ValueKind == JsonValueKind.Object
                    && errorBody.TryGetProperty("error", out var error) && error.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String)
                {
                    message = format.GetString();
                }

                throw new DapException(command, string.IsNullOrEmpty(message) ? $"The debug adapter failed '{command}'." : message);
            }

            return result.TryGetProperty("body", out var body) ? body : default;
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
    }

    /// <summary>Sends a request and deserializes the response's body.</summary>
    public async Task<TResult> RequestAsync<TArgs, TResult>(string command, TArgs arguments, JsonTypeInfo<TArgs> info, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken = default)
    {
        var body = await RequestAsync(command, arguments, info, cancellationToken).ConfigureAwait(false);
        if (body.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new DapException(command, $"The debug adapter's '{command}' response has no body.");
        }

        return body.Deserialize(resultInfo) ?? throw new DapException(command, $"The debug adapter's '{command}' response could not be read.");
    }

    private async Task WriteAsync(Action<Utf8JsonWriter> writeBody, CancellationToken cancellationToken)
    {
        var body = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new DapConnectionLostException("The debug adapter exited.", ex);
        }

        try
        {
            // Never cancel a frame halfway (a header without its body corrupts the stream); only the lock wait is cancellable.
            await _output.WriteAsync(FramedMessageWriter.Header(body.WrittenCount), CancellationToken.None).ConfigureAwait(false);
            await _output.WriteAsync(body.WrittenMemory, CancellationToken.None).ConfigureAwait(false);
            await _output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new DapConnectionLostException("The debug adapter is not running.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            var reader = new FramedMessageReader(_input);
            while (await reader.ReadMessageAsync(_shutdown.Token).ConfigureAwait(false) is { } payload)
            {
                JsonElement message;
                using (var document = JsonDocument.Parse(payload))
                {
                    message = document.RootElement.Clone();
                }

                var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "response":
                        if (message.TryGetProperty("request_seq", out var requestSeq) && _pending.TryGetValue(requestSeq.GetInt32(), out var pending))
                        {
                            pending.TrySetResult(message);
                        }

                        break;
                    case "event":
                        var name = message.TryGetProperty("event", out var e) ? e.GetString() ?? string.Empty : string.Empty;
                        _events.Writer.TryWrite(new DapEvent(name, message.TryGetProperty("body", out var body) ? body : default));
                        break;
                    case "request" when message.TryGetProperty("command", out var handled) && handled.GetString() is { } handledName && _handlers.TryGetValue(handledName, out var handler):
                        _ = HandleAsync(message, handler);
                        break;
                    case "request" when message.TryGetProperty("command", out var command) && command.GetString() is { } accepted && _accepted.Contains(accepted):
                        await AnswerAsync(message, success: true).ConfigureAwait(false);
                        _events.Writer.TryWrite(new DapReverseRequest(accepted, message.TryGetProperty("arguments", out var arguments) ? arguments : default));
                        break;
                    case "request":
                        await AnswerAsync(message, success: false).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure ends the connection visibly (a malformed message too): a loop that stopped quietly would
            // leave every pending request waiting forever.
            failure = ex;
        }

        _events.Writer.TryComplete();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new DapConnectionLostException("The debug adapter exited.", failure));
        }

        _closed.TrySetResult();
    }

    /// <summary>Runs a handled reverse request and answers it with the handler's body, or its failure.</summary>
    private async Task HandleAsync(JsonElement request, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        try
        {
            JsonElement body;
            try
            {
                body = await handler(request.TryGetProperty("arguments", out var arguments) ? arguments : default, _shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await AnswerAsync(request, success: false, message: ex.Message).ConfigureAwait(false);
                return;
            }

            await AnswerAsync(request, success: true, body: body).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapConnectionLostException or OperationCanceledException or ObjectDisposedException)
        {
            // The adapter is gone: nobody waits for the answer.
        }
    }

    /// <summary>Answers a reverse request: accepted ones with success, the rest with an error.</summary>
    private Task AnswerAsync(JsonElement request, bool success, JsonElement body = default, string? message = null) => WriteAsync(writer =>
    {
        writer.WriteNumber("seq", Interlocked.Increment(ref _seq));
        writer.WriteString("type", "response");
        writer.WriteNumber("request_seq", request.TryGetProperty("seq", out var seq) ? seq.GetInt32() : 0);
        writer.WriteBoolean("success", success);
        writer.WriteString("command", request.TryGetProperty("command", out var command) ? command.GetString() : string.Empty);
        if (!success)
        {
            writer.WriteString("message", message ?? "Not supported by Daedalus Studio.");
        }
        else if (body.ValueKind == JsonValueKind.Object)
        {
            writer.WritePropertyName("body");
            body.WriteTo(writer);
        }
    }, _shutdown.Token);

    private async Task EventLoopAsync()
    {
        await foreach (var item in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (item)
                {
                    case DapEvent e when EventReceived is { } handler:
                        await handler(e).ConfigureAwait(false);
                        break;
                    case DapReverseRequest r when ReverseRequestReceived is { } handler:
                        await handler(r).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HandlerFailed?.Invoke(this, ex);
            }
        }
    }

    /// <summary>An event handler threw (the loop goes on).</summary>
    public event EventHandler<Exception>? HandlerFailed;

    /// <summary>
    /// Ends the connection without waiting for the end of the adapter's output: for an adapter that exited while a
    /// process it started still holds that output open (on Unix a debuggee inherits netcoredbg's pipes, M33). Pending
    /// requests fail, the events end, and <see cref="Closed"/> completes.
    /// </summary>
    public void Abandon()
    {
        _events.Writer.TryComplete();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new DapConnectionLostException("The debug adapter exited."));
        }

        _closed.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        var ended = true;
        try
        {
            // A read from a pipe can't be cancelled on Unix: one that another process keeps open is left to end with it.
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            ended = false;
            Abandon();
        }

        try
        {
            await _eventLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            ended = false;
        }

        if (ended)
        {
            _shutdown.Dispose();
            _writeLock.Dispose();
        }
    }
}
