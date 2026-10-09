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

/// <summary>
/// A Debug Adapter Protocol client over a pair of streams (<c>Content-Length</c> framing, like LSP). Requests are
/// answered in any order; events are delivered in arrival order on one background loop. Reverse requests from the
/// adapter (<c>runInTerminal</c>…) are refused. All serialization is source-generated (AOT-safe).
/// </summary>
public sealed class DapConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<DapEvent> _events = Channel.CreateUnbounded<DapEvent>(new UnboundedChannelOptions { SingleReader = true });
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

    /// <summary>Completes when the adapter's output ends (it exited or closed the stream).</summary>
    public Task Closed => _closed.Task;

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

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(FramedMessageWriter.Header(body.WrittenCount), cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(body.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
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
                    case "request":
                        await RefuseAsync(message).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException or InvalidDataException or DapConnectionLostException)
        {
            failure = ex;
        }

        _events.Writer.TryComplete();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new DapConnectionLostException("The debug adapter exited.", failure));
        }

        _closed.TrySetResult();
    }

    /// <summary>Reverse requests (runInTerminal, startDebugging) aren't supported: answer them with an error.</summary>
    private Task RefuseAsync(JsonElement request) => WriteAsync(writer =>
    {
        writer.WriteNumber("seq", Interlocked.Increment(ref _seq));
        writer.WriteString("type", "response");
        writer.WriteNumber("request_seq", request.TryGetProperty("seq", out var seq) ? seq.GetInt32() : 0);
        writer.WriteBoolean("success", false);
        writer.WriteString("command", request.TryGetProperty("command", out var command) ? command.GetString() : string.Empty);
        writer.WriteString("message", "Not supported by Daedalus Studio.");
    }, _shutdown.Token);

    private async Task EventLoopAsync()
    {
        await foreach (var e in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (EventReceived is { } handler)
            {
                try
                {
                    await handler(e).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    HandlerFailed?.Invoke(this, ex);
                }
            }
        }
    }

    /// <summary>An event handler threw (the loop goes on).</summary>
    public event EventHandler<Exception>? HandlerFailed;

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
            await _eventLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }
}
