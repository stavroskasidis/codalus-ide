using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace Codalus.Rpc;

/// <summary>
/// Bidirectional JSON-RPC 2.0 over a pair of streams with LSP <c>Content-Length</c> framing.
/// All serialization goes through source-generated <see cref="JsonTypeInfo{T}"/>, so it is safe under
/// Native AOT and trimming.
/// <list type="bullet">
/// <item>Outgoing requests can be cancelled; cancellation sends LSP's <c>$/cancelRequest</c>.</item>
/// <item>Incoming notifications are dispatched strictly in arrival order; an incoming request starts in
/// arrival order and may complete concurrently with later messages.</item>
/// <item>Incoming <c>$/cancelRequest</c> cancels the matching in-flight handler.</item>
/// <item>A response completes its request as soon as it is read, possibly before the notifications received ahead of
/// it have been handled; <see cref="WhenDispatchedAsync"/> waits for those.</item>
/// </list>
/// Register handlers before calling <see cref="Start"/>.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800;

    private readonly Stream _reader;
    private readonly Stream _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, IncomingRequest> _incoming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<Action<Utf8JsonWriter>?>>> _requestHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, Task>> _notificationHandlers = new(StringComparer.Ordinal);
    /// <summary>Messages read and waiting for the dispatch loop, and the markers <see cref="WhenDispatchedAsync"/> queues behind them.</summary>
    private readonly Channel<Inbound> _inbox = Channel.CreateUnbounded<Inbound>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private Task _readLoop = Task.CompletedTask;
    private Task _dispatchLoop = Task.CompletedTask;
    private long _nextId;
    private int _started;
    private int _disposed;

    /// <summary>Requests being handled: their method and when they started (slow-request reports).</summary>
    private readonly ConcurrentDictionary<string, (string Method, long Started)> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _reportedSlow = new(StringComparer.Ordinal);

    /// <summary>Messages being written: what, when it started, and whether it got the write lock (stuck-write reports).</summary>
    private readonly ConcurrentDictionary<long, (string What, long Started, bool Locked)> _writing = new();
    private readonly ConcurrentDictionary<long, bool> _reportedWrites = new();
    private readonly ConcurrentDictionary<long, (string Method, long Started)> _outgoing = new();

    /// <summary>The notification the dispatch loop is handling (every later message waits for it), or null.</summary>
    private (string Method, long Started)? _notification;

    /// <summary>
    /// The notification handler running, as an identity that the handler's own flow carries in <see cref="s_handling"/>:
    /// <see cref="WhenDispatchedAsync"/> called from it must not wait for the dispatch loop, which waits for it.
    /// </summary>
    private object? _handling;
    private static readonly AsyncLocal<object?> s_handling = new();
    private long _reportedNotification;
    private long _nextWrite;
    private Timer? _watchdog;

    /// <summary>A request handled for longer than this is reported once through <see cref="Slow"/>.</summary>
    public static TimeSpan SlowRequest { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A notification handler running longer than this is reported (it holds up every later message).</summary>
    public static TimeSpan SlowNotification { get; set; } = TimeSpan.FromSeconds(2);

    public JsonRpcConnection(Stream reader, Stream writer)
    {
        _reader = reader;
        _writer = writer;
    }

    /// <summary>Raised once when the connection is lost (stream closed, peer died, or protocol error).</summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>
    /// Raised when an incoming handler throws (a request's peer receives an error response), or when a message can't be
    /// dispatched at all; the messages after it still are.
    /// </summary>
    public event EventHandler<Exception>? HandlerFailed;

    /// <summary>
    /// A request has been handled for longer than <see cref="SlowRequest"/>, or a notification handler took longer than
    /// <see cref="SlowNotification"/> (it holds up the messages after it). For logs: hangs leave a trace.
    /// </summary>
    public event EventHandler<string>? Slow;

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
        _watchdog = new Timer(_ => ReportSlowRequests(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private void ReportSlowRequests()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var (key, (method, started)) in _running)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started, now);
            if (elapsed > SlowRequest && _reportedSlow.TryAdd(key, true))
            {
                Slow?.Invoke(this, $"Request {method} ({key}) has been running for {elapsed.TotalSeconds:0} s; {_running.Count} request(s) in progress.");
            }
        }

        if (_notification is var (notification, since) && System.Diagnostics.Stopwatch.GetElapsedTime(since, now) is var running
            && running > SlowRequest && Interlocked.Exchange(ref _reportedNotification, since) != since)
        {
            Slow?.Invoke(this, $"Notification {notification} has been running for {running.TotalSeconds:0} s; {QueuedMessages} message(s) wait behind it.");
        }

        foreach (var (id, (what, started, locked)) in _writing)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started, now);
            if (elapsed > SlowRequest && _reportedWrites.TryAdd(id, true))
            {
                Slow?.Invoke(this, locked
                    ? $"Writing {what} has been blocked in the stream for {elapsed.TotalSeconds:0} s."
                    : $"Writing {what} has waited {elapsed.TotalSeconds:0} s for the write lock; {_writing.Count} write(s) pending.");
            }
        }
    }

    private int QueuedMessages => _inbox.Reader.CanCount ? _inbox.Reader.Count : -1;

    /// <summary>
    /// What the connection is waiting for, for diagnosing a peer that stopped answering: requests sent and not answered
    /// (the oldest), incoming requests running, the notification being handled, queued messages and pending writes.
    /// </summary>
    public string Describe()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        string Age(long started) => $"{System.Diagnostics.Stopwatch.GetElapsedTime(started, now).TotalSeconds:0} s";
        var parts = new List<string> { $"{_outgoing.Count} request(s) awaiting a reply" };
        if (_outgoing.Values.OrderBy(o => o.Started).FirstOrDefault() is { Method: not null } oldest)
        {
            parts[0] += $" (the oldest: {oldest.Method}, {Age(oldest.Started)})";
        }

        parts.Add($"{_running.Count} incoming request(s) running" + (_running.Values.OrderBy(r => r.Started).FirstOrDefault() is { Method: not null } longest ? $" (the longest: {longest.Method}, {Age(longest.Started)})" : string.Empty));
        if (_notification is var (notification, since))
        {
            parts.Add($"handling notification {notification} for {Age(since)}");
        }

        parts.Add($"{QueuedMessages} message(s) queued");
        parts.Add($"{_writing.Count} write(s) pending");
        return string.Join("; ", parts);
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
        _outgoing[id] = (method, System.Diagnostics.Stopwatch.GetTimestamp());
        try
        {
            await WriteMessageAsync(writer =>
            {
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                writeParams(writer);
            }, cancellationToken, "request " + method).ConfigureAwait(false);

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
            _outgoing.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Completes once every message received so far has been dispatched: the notification handlers have run, and the
    /// requests have started. A response completes its request on the read loop, so it can overtake the notifications
    /// the peer sent before it (a build's diagnostics before its result); a caller that needs them waits for this after
    /// the response. It never fails, and it completes at once when the connection is disposed (a handler that never ends
    /// can't hold the caller past that) and when called from a notification handler of this connection, which would
    /// otherwise wait for itself: every message before the one being handled has been dispatched then.
    /// </summary>
    public Task WhenDispatchedAsync()
    {
        if (Volatile.Read(ref _started) == 0 || _shutdown.IsCancellationRequested)
        {
            return Task.CompletedTask; // nothing has been read, or the connection is being disposed
        }

        if (s_handling.Value is { } handling && ReferenceEquals(handling, Volatile.Read(ref _handling)))
        {
            return Task.CompletedTask; // in the handler the dispatch loop waits for
        }

        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A channel completed by the end of the read loop refuses the marker: nothing is left to dispatch then. One
        // written before that is still read by the dispatch loop, which drains the channel before it ends.
        if (!_inbox.Writer.TryWrite(new Inbound(null, dispatched)))
        {
            return Task.CompletedTask;
        }

        try
        {
            return dispatched.Task.WaitAsync(_shutdown.Token)
                .ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
            return Task.CompletedTask; // disposed meanwhile
        }
    }

    public Task NotifyAsync<TParams>(string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, CancellationToken cancellationToken) =>
        WriteMessageAsync(writer =>
        {
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters, paramsInfo);
        }, cancellationToken, "notification " + method);

    public Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        WriteMessageAsync(writer => writer.WriteString("method", method), cancellationToken, "notification " + method);

    private Task CancelRemoteAsync(long id) =>
        WriteMessageAsync(writer =>
        {
            writer.WriteString("method", "$/cancelRequest");
            writer.WriteStartObject("params");
            writer.WriteNumber("id", id);
            writer.WriteEndObject();
        }, CancellationToken.None).ContinueWith(static _ => { }, TaskScheduler.Default);

    private async Task WriteMessageAsync(Action<Utf8JsonWriter> writeBody, CancellationToken cancellationToken, string what = "a message")
    {
        var body = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writeBody(writer);
            writer.WriteEndObject();
        }

        var header = FramedMessageWriter.Header(body.WrittenCount);
        var write = Interlocked.Increment(ref _nextWrite);
        _writing[write] = (what, System.Diagnostics.Stopwatch.GetTimestamp(), false);
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _writing.TryRemove(write, out _);
            throw;
        }

        _writing[write] = (what, System.Diagnostics.Stopwatch.GetTimestamp(), true);
        try
        {
            // Never cancel a frame halfway: a header without its body corrupts the stream, and the peer drops
            // the connection. Cancellation applies while waiting for the lock only.
            await _writer.WriteAsync(header, CancellationToken.None).ConfigureAwait(false);
            await _writer.WriteAsync(body.WrittenMemory, CancellationToken.None).ConfigureAwait(false);
            await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The peer is gone (e.g. a crashed host's stdin pipe closed): one exception type for callers.
            throw new JsonRpcConnectionLostException("The connection was lost.", ex);
        }
        finally
        {
            _writeLock.Release();
            _writing.TryRemove(write, out _);
            if (_reportedWrites.TryRemove(write, out _))
            {
                Slow?.Invoke(this, $"Writing {what} finished.");
            }
        }
    }

    // ------------------------------------------------------------------ read and dispatch loops

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            var reader = new FramedMessageReader(_reader);
            while (!_shutdown.IsCancellationRequested)
            {
                var payload = await reader.ReadMessageAsync(_shutdown.Token).ConfigureAwait(false);
                if (payload is null)
                {
                    break;
                }

                if (!TryCompleteResponse(payload))
                {
                    await _inbox.Writer.WriteAsync(new Inbound(payload, null), _shutdown.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (_shutdown.IsCancellationRequested && ex is OperationCanceledException or InvalidOperationException)
        {
            // Disposed while reading (a completed pipe reader throws InvalidOperationException).
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure ends the connection visibly (a malformed message too): a loop that stopped quietly would
            // leave every pending request waiting forever.
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
            _ = incoming.CancelAsync();
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
        await foreach (var inbound in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (inbound.Dispatched is { } dispatched)
            {
                dispatched.TrySetResult();
                continue;
            }

            // Nothing one message does may stop the loop: the read loop would go on queueing everything after it,
            // and the peer would wait for answers forever without the connection ever being reported lost.
            try
            {
                await DispatchAsync(inbound.Payload!).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HandlerFailed?.Invoke(this, ex);
            }
        }
    }

    private async Task DispatchAsync(byte[] payload)
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
                && _incoming.TryGetValue(cancelId.GetRawText(), out var incoming))
            {
                await incoming.CancelAsync().ConfigureAwait(false);
            }
        }
        else if (_notificationHandlers.TryGetValue(method, out var handler))
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            _notification = (method, started);
            var handling = new object();
            Volatile.Write(ref _handling, handling);
            s_handling.Value = handling; // flows into the handler; restored when this method returns
            try
            {
                await handler(parameters).ConfigureAwait(false);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (elapsed > SlowNotification)
                {
                    Slow?.Invoke(this, $"Notification {method} took {elapsed.TotalMilliseconds:0} ms; the messages after it waited.");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HandlerFailed?.Invoke(this, ex);
            }
            finally
            {
                _notification = null;
                Volatile.Write(ref _handling, null);
            }
        }
    }

    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement parameters)
    {
        var key = id.GetRawText();
        using var incoming = new IncomingRequest(_shutdown.Token);
        _incoming[key] = incoming;
        _running[key] = (method, System.Diagnostics.Stopwatch.GetTimestamp());
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
                writeResult = await handler(parameters, incoming.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (incoming.Token.IsCancellationRequested)
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
            _running.TryRemove(key, out _);
            if (_reportedSlow.TryRemove(key, out _))
            {
                Slow?.Invoke(this, $"Request {method} ({key}) finished.");
            }
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_watchdog is { } watchdog)
        {
            await watchdog.DisposeAsync().ConfigureAwait(false);
        }
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

    /// <summary>A message read from the peer, or (with <paramref name="Dispatched"/>) a marker that completes once the messages ahead of it are dispatched.</summary>
    private readonly record struct Inbound(byte[]? Payload, TaskCompletionSource? Dispatched);

    /// <summary>
    /// An incoming request's cancellation. A <c>$/cancelRequest</c> (or the connection's end) can find the request
    /// just as it finishes and disposes its token source; cancelling it then must be a no-op, not an exception.
    /// </summary>
    private sealed class IncomingRequest : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private bool _ended;

        public IncomingRequest(CancellationToken shutdown)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            Token = _source.Token;
        }

        public CancellationToken Token { get; }

        public Task CancelAsync()
        {
            lock (_source)
            {
                // The callbacks run on the thread pool, outside the lock.
                return _ended ? Task.CompletedTask : _source.CancelAsync();
            }
        }

        public void Dispose()
        {
            lock (_source)
            {
                _ended = true;
                _source.Dispose();
            }
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
