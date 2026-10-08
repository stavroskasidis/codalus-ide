using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace Daedalus.Rpc;

/// <summary>
/// Bidirectional JSON-RPC 2.0 over a pair of streams with LSP <c>Content-Length</c> framing.
/// All serialization goes through source-generated <see cref="JsonTypeInfo{T}"/>, so it is safe under
/// Native AOT and trimming.
/// <list type="bullet">
/// <item>Outgoing requests can be cancelled; cancellation sends LSP's <c>$/cancelRequest</c>.</item>
/// <item>Incoming notifications are dispatched strictly in arrival order; an incoming request starts in
/// arrival order and may complete concurrently with later messages.</item>
/// <item>Incoming <c>$/cancelRequest</c> cancels the matching in-flight handler.</item>
/// </list>
/// Register handlers before calling <see cref="Start"/>.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800;

    private static readonly byte[] s_headerTerminator = "\r\n\r\n"u8.ToArray();

    private readonly Stream _reader;
    private readonly Stream _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<Action<Utf8JsonWriter>?>>> _requestHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, Task>> _notificationHandlers = new(StringComparer.Ordinal);
    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _shutdown = new();
    private Task _readLoop = Task.CompletedTask;
    private Task _dispatchLoop = Task.CompletedTask;
    private long _nextId;
    private int _started;

    public JsonRpcConnection(Stream reader, Stream writer)
    {
        _reader = reader;
        _writer = writer;
    }

    /// <summary>Raised once when the connection is lost (stream closed, peer died, or protocol error).</summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Raised when an incoming handler throws (the peer receives an error response).</summary>
    public event EventHandler<Exception>? HandlerFailed;

    /// <summary>Completes when the connection has stopped reading.</summary>
    public Task Completion => _readLoop;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The connection is already started.");
        }

        _dispatchLoop = Task.Run(DispatchLoopAsync);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    // ------------------------------------------------------------------ incoming

    /// <summary>Handles an incoming request whose result is serialized with <paramref name="resultInfo"/>.</summary>
    public void OnRequest<TParams, TResult>(string method, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, Func<TParams, CancellationToken, Task<TResult>> handler)
    {
        EnsureNotStarted();
        _requestHandlers[method] = async (element, cancellationToken) =>
        {
            var parameters = Deserialize(element, paramsInfo);
            var result = await handler(parameters, cancellationToken).ConfigureAwait(false);
            return writer => JsonSerializer.Serialize(writer, result, resultInfo);
        };
    }

    /// <summary>Handles an incoming request with a raw JSON result (null for none).</summary>
    public void OnRequest(string method, Func<JsonElement, CancellationToken, Task<JsonElement?>> handler)
    {
        EnsureNotStarted();
        _requestHandlers[method] = async (element, cancellationToken) =>
        {
            var result = await handler(element, cancellationToken).ConfigureAwait(false);
            return result is { } value ? value.WriteTo : null;
        };
    }

    public void OnNotification<TParams>(string method, JsonTypeInfo<TParams> paramsInfo, Func<TParams, Task> handler)
    {
        EnsureNotStarted();
        _notificationHandlers[method] = element => handler(Deserialize(element, paramsInfo));
    }

    public void OnNotification<TParams>(string method, JsonTypeInfo<TParams> paramsInfo, Action<TParams> handler) =>
        OnNotification(method, paramsInfo, p =>
        {
            handler(p);
            return Task.CompletedTask;
        });

    private void EnsureNotStarted()
    {
        if (Volatile.Read(ref _started) != 0)
        {
            throw new InvalidOperationException("Handlers must be registered before Start().");
        }
    }

    private static T Deserialize<T>(JsonElement element, JsonTypeInfo<T> info) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? JsonSerializer.Deserialize("{}", info)!
            : element.Deserialize(info) ?? throw new JsonException("Parameters deserialized to null.");

    // ------------------------------------------------------------------ outgoing

    public async Task<TResult> InvokeAsync<TParams, TResult>(
        string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        var element = await InvokeCoreAsync(method, writer =>
        {
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters, paramsInfo);
        }, cancellationToken).ConfigureAwait(false);
        return element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default!
            : element.Deserialize(resultInfo)!;
    }

    /// <summary>Sends a request without parameters (e.g. <c>shutdown</c>) and waits for it to complete.</summary>
    public Task InvokeAsync(string method, CancellationToken cancellationToken) =>
        InvokeCoreAsync(method, static _ => { }, cancellationToken);

    /// <summary>Sends a request with raw JSON parameters and returns the raw result.</summary>
    public Task<JsonElement> InvokeRawAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
        InvokeCoreAsync(method, writer =>
        {
            writer.WritePropertyName("params");
            parameters.WriteTo(writer);
        }, cancellationToken);

    private async Task<JsonElement> InvokeCoreAsync(string method, Action<Utf8JsonWriter> writeParams, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteMessageAsync(writer =>
            {
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                writeParams(writer);
            }, cancellationToken).ConfigureAwait(false);

            try
            {
                return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !completion.Task.IsCompleted)
            {
                // Sent here rather than from a token registration: a registration can be disposed (and skipped)
                // when WaitAsync's own cancellation callback resumes this method inline first.
                _ = CancelRemoteAsync(id);
                throw;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync<TParams>(string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, CancellationToken cancellationToken) =>
        WriteMessageAsync(writer =>
        {
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters, paramsInfo);
        }, cancellationToken);

    public Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        WriteMessageAsync(writer => writer.WriteString("method", method), cancellationToken);

    private Task CancelRemoteAsync(long id) =>
        WriteMessageAsync(writer =>
        {
            writer.WriteString("method", "$/cancelRequest");
            writer.WriteStartObject("params");
            writer.WriteNumber("id", id);
            writer.WriteEndObject();
        }, CancellationToken.None).ContinueWith(static _ => { }, TaskScheduler.Default);

    private async Task WriteMessageAsync(Action<Utf8JsonWriter> writeBody, CancellationToken cancellationToken)
    {
        var body = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writeBody(writer);
            writer.WriteEndObject();
        }

        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"Content-Length: {body.WrittenCount}\r\n\r\n"));
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(body.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The peer is gone (e.g. a crashed host's stdin pipe closed): one exception type for callers.
            throw new JsonRpcConnectionLostException("The connection was lost.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ------------------------------------------------------------------ read and dispatch loops

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            var reader = new MessageReader(_reader);
            while (!_shutdown.IsCancellationRequested)
            {
                var payload = await reader.ReadMessageAsync(_shutdown.Token).ConfigureAwait(false);
                if (payload is null)
                {
                    break;
                }

                if (!TryCompleteResponse(payload))
                {
                    await _inbox.Writer.WriteAsync(payload, _shutdown.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException or InvalidDataException)
        {
            failure = ex;
        }

        _inbox.Writer.TryComplete();
        var lost = new JsonRpcConnectionLostException("The connection was lost.", failure);
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(lost);
        }

        foreach (var incoming in _incoming.Values)
        {
            incoming.Cancel();
        }

        Disconnected?.Invoke(this, failure);
    }

    /// <summary>Responses are completed directly on the read loop so they never wait behind slow handlers.</summary>
    private bool TryCompleteResponse(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.TryGetProperty("method", out _) || !root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (_pending.TryGetValue(idElement.GetInt64(), out var completion))
        {
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                completion.TrySetException(new JsonRpcRemoteException(code, message ?? string.Empty));
            }
            else
            {
                completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
        }

        return true;
    }

    private async Task DispatchLoopAsync()
    {
        await foreach (var payload in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            JsonElement root;
            using (var document = JsonDocument.Parse(payload))
            {
                root = document.RootElement.Clone();
            }

            var method = root.GetProperty("method").GetString() ?? string.Empty;
            var parameters = root.TryGetProperty("params", out var p) ? p : default;
            if (root.TryGetProperty("id", out var id))
            {
                // Started in order; completes concurrently.
                _ = HandleRequestAsync(id.Clone(), method, parameters);
            }
            else if (method == "$/cancelRequest")
            {
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("id", out var cancelId)
                    && _incoming.TryGetValue(cancelId.GetRawText(), out var cts))
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
            }
            else if (_notificationHandlers.TryGetValue(method, out var handler))
            {
                try
                {
                    await handler(parameters).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    HandlerFailed?.Invoke(this, ex);
                }
            }
        }
    }

    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement parameters)
    {
        var key = id.GetRawText();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _incoming[key] = cts;
        try
        {
            if (!_requestHandlers.TryGetValue(method, out var handler))
            {
                await WriteErrorAsync(id, MethodNotFound, $"Method not found: {method}").ConfigureAwait(false);
                return;
            }

            Action<Utf8JsonWriter>? writeResult;
            try
            {
                writeResult = await handler(parameters, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                await WriteErrorAsync(id, RequestCancelled, "Request cancelled.").ConfigureAwait(false);
                return;
            }
            catch (JsonException ex)
            {
                await WriteErrorAsync(id, InvalidParams, ex.Message).ConfigureAwait(false);
                return;
            }
            catch (JsonRpcHandlerException ex)
            {
                await WriteErrorAsync(id, ex.Code, ex.Message).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HandlerFailed?.Invoke(this, ex);
                await WriteErrorAsync(id, InternalError, ex.Message).ConfigureAwait(false);
                return;
            }

            await WriteMessageAsync(writer =>
            {
                writer.WritePropertyName("id");
                id.WriteTo(writer);
                writer.WritePropertyName("result");
                if (writeResult is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writeResult(writer);
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonRpcConnectionLostException)
        {
            // The connection went away while answering; Disconnected reports it.
        }
        finally
        {
            _incoming.TryRemove(key, out _);
        }
    }

    private Task WriteErrorAsync(JsonElement id, int code, string message) =>
        WriteMessageAsync(writer =>
        {
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        await _reader.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
            await _dispatchLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }

    /// <summary>Reads <c>Content-Length</c>-framed messages.</summary>
    private sealed class MessageReader(Stream stream)
    {
        private byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        public async Task<byte[]?> ReadMessageAsync(CancellationToken cancellationToken)
        {
            int contentLength;
            while (true)
            {
                var headerEnd = _buffer.AsSpan(_start, _end - _start).IndexOf(s_headerTerminator);
                if (headerEnd >= 0)
                {
                    contentLength = ParseContentLength(_buffer.AsSpan(_start, headerEnd));
                    _start += headerEnd + s_headerTerminator.Length;
                    break;
                }

                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
            }

            while (_end - _start < contentLength)
            {
                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("Stream ended in the middle of a message.");
                }
            }

            var message = _buffer.AsSpan(_start, contentLength).ToArray();
            _start += contentLength;
            return message;
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            _end += read;
            return read > 0;
        }

        private static int ParseContentLength(ReadOnlySpan<byte> headers)
        {
            foreach (var line in Encoding.ASCII.GetString(headers).Split("\r\n"))
            {
                var separator = line.IndexOf(':', StringComparison.Ordinal);
                if (separator > 0 && line.AsSpan(0, separator).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    return int.Parse(line.AsSpan(separator + 1).Trim(), CultureInfo.InvariantCulture);
                }
            }

            throw new InvalidDataException("Message header has no Content-Length.");
        }
    }
}

/// <summary>The peer answered a request with a JSON-RPC error.</summary>
public sealed class JsonRpcRemoteException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Thrown by an incoming request handler to answer with a specific JSON-RPC error code.</summary>
public sealed class JsonRpcHandlerException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed class JsonRpcConnectionLostException(string message, Exception? inner) : Exception(message, inner);
