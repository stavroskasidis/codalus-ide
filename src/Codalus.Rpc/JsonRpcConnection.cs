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
/// <item>Every outgoing message (requests, notifications, responses) is queued in call order and serialized, written and
/// flushed by one writer loop (M57): a caller, the UI thread too, pays only for queueing, and a request always follows
/// the messages queued before it.</item>
/// <item>Outgoing requests can be cancelled: one still queued is never written; one written sends LSP's
/// <c>$/cancelRequest</c>.</item>
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
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, IncomingRequest> _incoming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<Action<Utf8JsonWriter>?>>> _requestHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, Task>> _notificationHandlers = new(StringComparer.Ordinal);
    /// <summary>Messages read and waiting for the dispatch loop, and the markers <see cref="WhenDispatchedAsync"/> queues behind them.</summary>
    private readonly Channel<Inbound> _inbox = Channel.CreateUnbounded<Inbound>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Messages waiting for the writer loop, in call order (M57), and what wakes the loop; both under <see cref="_queueGate"/>.</summary>
    private readonly LinkedList<Outgoing> _queue = new();
    private readonly object _queueGate = new(); // not Lock: the .NET 8 designer hosts compile this file too
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private Task? _writeLoop;
    private bool _writesEnded;
    private Task _readLoop = Task.CompletedTask;
    private Task _dispatchLoop = Task.CompletedTask;
    private long _nextId;
    private int _started;
    private int _disposed;
    private int _received;
    private int _readEnded;
    private readonly object _lostGate = new(); // not Lock: the .NET 8 designer hosts compile this file too
    private Task<JsonRpcConnectionLostException>? _lost;

    /// <summary>Requests being handled: their method and when they started (slow-request reports).</summary>
    private readonly ConcurrentDictionary<string, (string Method, long Started)> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _reportedSlow = new(StringComparer.Ordinal);

    /// <summary>Messages queued or being written: what, since when, and whether the loop is writing it (stuck-write reports).</summary>
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
        : this(reader, writer, JsonRpcFraming.ContentLength)
    {
    }

    /// <param name="framing">How messages are delimited: <c>Content-Length</c> headers, or one per line (M48).</param>
    public JsonRpcConnection(Stream reader, Stream writer, JsonRpcFraming framing)
    {
        _reader = reader;
        _writer = writer;
        Framing = framing;
    }

    /// <summary>How messages are delimited on the streams.</summary>
    public JsonRpcFraming Framing { get; }

    /// <summary>
    /// The notification a cancelled outgoing request sends, and the one that cancels an incoming request's handler:
    /// LSP's <c>$/cancelRequest</c> by default, the Agent Client Protocol's <c>$/cancel_request</c> (M48), or null to
    /// send none (a protocol that cancels its own way). Set it before <see cref="Start"/>.
    /// </summary>
    public JsonRpcCancelNotification? CancelNotification { get; set; } = JsonRpcCancelNotification.Lsp;

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

    /// <summary>Whether a message has arrived from the peer (a host that got this far started).</summary>
    public bool HasReceived => Volatile.Read(ref _received) != 0;

    /// <summary>Whether the watchdog runs (tests): from <see cref="Start"/> until the peer is gone.</summary>
    internal bool IsWatching => Volatile.Read(ref _watchdog) is not null;

    /// <summary>
    /// Says why the connection was lost (M49): called once, when reading stops or a write finds the peer gone (not on
    /// <see cref="DisposeAsync"/>), with the failure if there was one. The exception it returns is what pending and later
    /// requests fail with (a supervisor puts the process's exit code and standard error in it); null, or no callback,
    /// keeps "The connection was lost." Register before <see cref="Start"/>.
    /// </summary>
    public Func<Exception?, Task<JsonRpcConnectionLostException?>>? ExplainLoss { get; set; }

    private Task<JsonRpcConnectionLostException> LostAsync(Exception? failure)
    {
        lock (_lostGate)
        {
            return _lost ??= ExplainAsync(failure);
        }
    }

    private async Task<JsonRpcConnectionLostException> ExplainAsync(Exception? failure)
    {
        if (ExplainLoss is { } explain && !_shutdown.IsCancellationRequested)
        {
            try
            {
                if (await explain(failure).ConfigureAwait(false) is { } explained)
                {
                    return explained;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The plain message below: an explanation that fails must not hide the loss itself.
            }
        }

        return new JsonRpcConnectionLostException("The connection was lost.", failure);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The connection is already started.");
        }

        // Before the read loop, which stops it when the peer is gone (a host that exits at once).
        _watchdog = new Timer(_ => ReportSlowRequests(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _dispatchLoop = Task.Run(DispatchLoopAsync);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    private void ReportSlowRequests() => ReportSlow(SlowRequest);

    /// <summary>What the watchdog reports, for what took longer than <paramref name="threshold"/> (tests call it directly).</summary>
    internal void ReportSlow(TimeSpan threshold)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var (key, (method, started)) in _running)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started, now);
            if (elapsed > threshold && _reportedSlow.TryAdd(key, true))
            {
                Slow?.Invoke(this, $"Request {method} ({key}) has been running for {elapsed.TotalSeconds:0} s; {_running.Count} request(s) in progress.");
            }
        }

        if (_notification is var (notification, since) && System.Diagnostics.Stopwatch.GetElapsedTime(since, now) is var running
            && running > threshold && Interlocked.Exchange(ref _reportedNotification, since) != since)
        {
            Slow?.Invoke(this, $"Notification {notification} has been running for {running.TotalSeconds:0} s; {QueuedMessages} message(s) wait behind it.");
        }

        foreach (var (id, (what, started, locked)) in _writing)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started, now);
            if (elapsed > threshold && _reportedWrites.TryAdd(id, true))
            {
                Slow?.Invoke(this, locked
                    ? $"Writing {what} has been in progress for {elapsed.TotalSeconds:0} s (making its parameters, or blocked in the stream)."
                    : $"Writing {what} has waited {elapsed.TotalSeconds:0} s behind the messages queued before it; {_writing.Count} write(s) pending.");
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

    /// <remarks>
    /// The parameters are serialized later, on the writer loop (M57): don't change them after the call. A cancellation
    /// before the request is serialized sends nothing; after it, the connection's cancel notification.
    /// </remarks>
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
        cancellationToken.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        _outgoing[id] = (method, System.Diagnostics.Stopwatch.GetTimestamp());
        try
        {
            var request = new Outgoing(OutgoingKind.Request, "request " + method, writer =>
            {
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                writeParams(writer);
                return true;
            }, null, cancellationToken);
            var written = Enqueue(request);
            try
            {
                await written.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Still queued, or skipped by the loop: no byte of it went out, so the peer has nothing to cancel (M57).
                // Taken by the loop: its frame goes out whole, and the cancellation follows it.
                if (!Unqueue(request))
                {
                    Observe(written);
                    _ = CancelRemoteAsync(id);
                }

                throw;
            }

            if (Volatile.Read(ref _readEnded) != 0)
            {
                // Sent after the reading stopped: no answer can come.
                completion.TrySetException(await LostAsync(null).ConfigureAwait(false));
            }

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

    /// <summary>
    /// Queues a notification, in call order with every other message; the returned task completes when it is written.
    /// The parameters are serialized on the writer loop (M57): don't change them after the call. A queued notification is
    /// never dropped (only the connection's end fails it): the token stops the caller's wait, not the message.
    /// </summary>
    public Task NotifyAsync<TParams>(string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, CancellationToken cancellationToken) =>
        Notify(new Outgoing(OutgoingKind.Notification, "notification " + method, writer =>
        {
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters, paramsInfo);
            return true;
        }, null, cancellationToken));

    /// <summary>
    /// Queues a notification whose parameters are made when it is written, on the writer loop (M57): the caller hands
    /// over something immutable (a text snapshot) and pays nothing for the text or its JSON, while the message keeps its
    /// place in call order. A null from <paramref name="parameters"/> sends nothing. With <paramref name="replaces"/>, it
    /// replaces a notification with the same key still queued behind nothing but responses and cancellations (a
    /// document's whole text, superseded by a newer one); the replaced one's task completes as if written.
    /// </summary>
    public Task NotifyAsync<TParams>(string method, Func<TParams?> parameters, JsonTypeInfo<TParams> paramsInfo, string? replaces, CancellationToken cancellationToken)
        where TParams : class =>
        Notify(new Outgoing(OutgoingKind.Notification, "notification " + method, writer =>
        {
            if (parameters() is not { } value)
            {
                return false;
            }

            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, value, paramsInfo);
            return true;
        }, replaces, cancellationToken));

    public Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        Notify(new Outgoing(OutgoingKind.Notification, "notification " + method, writer =>
        {
            writer.WriteString("method", method);
            return true;
        }, null, cancellationToken));

    private Task Notify(Outgoing notification)
    {
        if (notification.Token.IsCancellationRequested)
        {
            return Task.FromCanceled(notification.Token);
        }

        var written = Enqueue(notification);
        if (!notification.Token.CanBeCanceled || written.IsCompleted)
        {
            return written;
        }

        // The caller may stop waiting (a shutdown's timeout); the message stays queued.
        Observe(written);
        return written.WaitAsync(notification.Token);
    }

    /// <summary>A write's failure that nobody may wait for any more is observed, not left to the finalizer's unobserved-task report.</summary>
    private static void Observe(Task written) =>
        _ = written.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private Task CancelRemoteAsync(long id) =>
        CancelNotification is not { } cancel
            ? Task.CompletedTask
            : Enqueue(new Outgoing(OutgoingKind.Cancellation, "notification " + cancel.Method, writer =>
            {
                writer.WriteString("method", cancel.Method);
                writer.WriteStartObject("params");
                writer.WriteNumber(cancel.IdProperty, id);
                writer.WriteEndObject();
                return true;
            }, null, CancellationToken.None)).ContinueWith(static _ => { }, TaskScheduler.Default);

    private Task WriteResponseAsync(Action<Utf8JsonWriter> writeBody) =>
        Enqueue(new Outgoing(OutgoingKind.Response, "a response", writer =>
        {
            writeBody(writer);
            return true;
        }, null, CancellationToken.None));

    // ------------------------------------------------------------------ the writer loop

    /// <summary>
    /// Queues a message for the writer loop (M57); the task completes when it is written (or skipped, or replaced).
    /// Enqueueing is synchronous, so messages go out in call order: a request always follows the edits made before it.
    /// </summary>
    private Task Enqueue(Outgoing message)
    {
        List<Outgoing>? replaced = null;
        lock (_queueGate)
        {
            if (_writesEnded)
            {
                return FailLostAsync();
            }

            if (message.Replaces is { } key)
            {
                // Only past messages no state of the peer depends on: a request or another notification in between must
                // still see what was queued before it.
                for (var node = _queue.Last; node is not null; node = node.Previous)
                {
                    if (node.Value.Kind == OutgoingKind.Notification && node.Value.Replaces == key)
                    {
                        _queue.Remove(node);
                        node.Value.Node = null;
                        node.Value.State = OutgoingState.Removed;
                        replaced = [node.Value];
                        break;
                    }

                    if (node.Value.Kind is not (OutgoingKind.Response or OutgoingKind.Cancellation))
                    {
                        break;
                    }
                }
            }

            message.Write = Interlocked.Increment(ref _nextWrite);
            _writing[message.Write] = (message.What, System.Diagnostics.Stopwatch.GetTimestamp(), false);
            message.Node = _queue.AddLast(message);
            _writeLoop ??= Task.Run(WriteLoopAsync);
            if (message.Kind == OutgoingKind.Request && message.Token.CanBeCanceled)
            {
                // Under the lock, so the loop never sees the message without it (a cancellation now runs inline, and the
                // lock is reentrant).
                message.Registration = message.Token.Register(static state =>
                {
                    var (connection, request) = ((JsonRpcConnection, Outgoing))state!;
                    connection.Unqueue(request);
                }, (this, message));
            }
        }

        foreach (var old in replaced ?? [])
        {
            Finish(old, null);
        }

        _wake.Writer.TryWrite(true);
        return message.Written.Task;
    }

    /// <summary>
    /// Takes a cancelled request out of the queue. True when it never goes out (taken out now or before, or skipped by
    /// the loop), false when the loop has taken it to write.
    /// </summary>
    private bool Unqueue(Outgoing request)
    {
        lock (_queueGate)
        {
            if (request.State == OutgoingState.Queued)
            {
                _queue.Remove(request.Node!);
                request.Node = null;
                request.State = OutgoingState.Removed;
            }
            else if (request.State != OutgoingState.Removed)
            {
                return false;
            }
        }

        _writing.TryRemove(request.Write, out _);
        request.Written.TrySetCanceled(request.Token);
        return true;
    }

    private async Task FailLostAsync() => throw await LostAsync(null).ConfigureAwait(false);

    /// <summary>
    /// The one place messages are serialized, written and flushed (M57): in queue order, each frame whole, and what was
    /// queued meanwhile written before one flush.
    /// </summary>
    private async Task WriteLoopAsync()
    {
        var unflushed = new List<Outgoing>();
        while (true)
        {
            Outgoing? message;
            lock (_queueGate)
            {
                message = _queue.First?.Value;
                if (message is not null)
                {
                    _queue.RemoveFirst();
                    message.Node = null;
                    // A request cancelled while queued sends nothing, not even its cancellation.
                    message.State = message.Kind == OutgoingKind.Request && message.Token.IsCancellationRequested ? OutgoingState.Removed : OutgoingState.Taken;
                }
            }

            if (message is null)
            {
                if (unflushed.Count > 0)
                {
                    if (!await FlushAsync(unflushed).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                if (!await _wake.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    return;
                }

                _wake.Reader.TryRead(out _);
                continue;
            }

            message.Registration.Dispose();
            if (message.State == OutgoingState.Removed)
            {
                _writing.TryRemove(message.Write, out _);
                message.Written.TrySetCanceled(message.Token);
                continue;
            }

            _writing[message.Write] = (message.What, System.Diagnostics.Stopwatch.GetTimestamp(), true);
            var body = new ArrayBufferWriter<byte>(256);
            try
            {
                using var writer = new Utf8JsonWriter(body);
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                if (!message.WriteBody(writer))
                {
                    Finish(message, null); // nothing to send (a document change the peer has already)
                    continue;
                }

                writer.WriteEndObject();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // This message's parameters couldn't be made: it fails alone, and the stream never saw a byte of it.
                Finish(message, ex);
                continue;
            }

            unflushed.Add(message);
            try
            {
                // Never cancel a frame halfway: a header without its body corrupts the stream, and the peer drops the
                // connection. Cancellation applies while a request is queued only.
                if (Framing == JsonRpcFraming.NewlineDelimited)
                {
                    // Utf8JsonWriter escapes control characters in strings, so the body holds no newline of its own.
                    body.Write("\n"u8);
                }
                else
                {
                    await _writer.WriteAsync(FramedMessageWriter.Header(body.WrittenCount), CancellationToken.None).ConfigureAwait(false);
                }

                await _writer.WriteAsync(body.WrittenMemory, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Any failure of the stream ends the writes visibly, whatever its type (a remote window's path rewrite
                // throws InvalidDataException or FormatException): part of a frame may be out, and a loop that stopped
                // quietly would leave this message, the queued ones and every later one waiting forever.
                await EndWritesAsync(unflushed, ex).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task<bool> FlushAsync(List<Outgoing> written)
    {
        try
        {
            await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await EndWritesAsync(written, ex).ConfigureAwait(false); // as a failed write, whatever its type
            return false;
        }

        foreach (var message in written)
        {
            Finish(message, null);
        }

        written.Clear();
        return true;
    }

    /// <summary>
    /// The peer is gone (e.g. a crashed host's stdin pipe closed): what was written, what is queued and what comes later
    /// fail with one exception type, which says why when the supervisor knows (M49).
    /// </summary>
    private async Task EndWritesAsync(List<Outgoing> written, Exception failure)
    {
        var lost = await LostAsync(failure).ConfigureAwait(false);
        List<Outgoing> queued;
        lock (_queueGate)
        {
            _writesEnded = true;
            queued = TakeQueued();
        }

        foreach (var message in written.Concat(queued))
        {
            Finish(message, lost);
        }

        written.Clear();
    }

    /// <summary>Empties the queue (under <see cref="_queueGate"/>).</summary>
    private List<Outgoing> TakeQueued()
    {
        var queued = new List<Outgoing>(_queue);
        foreach (var message in queued)
        {
            message.Node = null;
            message.State = OutgoingState.Removed;
        }

        _queue.Clear();
        return queued;
    }

    private void Finish(Outgoing message, Exception? failure)
    {
        message.Registration.Dispose();
        _writing.TryRemove(message.Write, out _);
        if (_reportedWrites.TryRemove(message.Write, out _))
        {
            Slow?.Invoke(this, $"Writing {message.What} finished.");
        }

        if (failure is null)
        {
            message.Written.TrySetResult();
        }
        else
        {
            message.Written.TrySetException(failure);
        }
    }

    // ------------------------------------------------------------------ read and dispatch loops

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            var framed = Framing == JsonRpcFraming.ContentLength ? new FramedMessageReader(_reader) : null;
            var lines = framed is null ? new LineMessageReader(_reader) : null;
            while (!_shutdown.IsCancellationRequested)
            {
                var payload = framed is not null
                    ? await framed.ReadMessageAsync(_shutdown.Token).ConfigureAwait(false)
                    : await lines!.ReadMessageAsync(_shutdown.Token).ConfigureAwait(false);
                if (payload is null)
                {
                    break;
                }

                Volatile.Write(ref _received, 1);

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
        var lost = await LostAsync(failure).ConfigureAwait(false);
        Volatile.Write(ref _readEnded, 1);
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(lost);
        }

        foreach (var incoming in _incoming.Values)
        {
            _ = incoming.CancelAsync();
        }

        Disconnected?.Invoke(this, failure);

        // Nothing is left to watch, and the timer, ticking for the rest of the process, held the connection, its handlers
        // and all they reach (a supervisor keeps no lost connection, but a full run of the shell tests kept every shell
        // whose build host had exited).
        Interlocked.Exchange(ref _watchdog, null)?.Dispose();
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
                var details = error.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var method = _outgoing.TryGetValue(idElement.GetInt64(), out var outgoing) ? outgoing.Method : null;
                completion.TrySetException(new JsonRpcRemoteException(code, message ?? string.Empty) { Details = details, Method = method });
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
        else if (method == "$/cancelRequest" || method == CancelNotification?.Method)
        {
            var idProperty = method == CancelNotification?.Method ? CancelNotification.IdProperty : "id";
            if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(idProperty, out var cancelId)
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
                await WriteErrorAsync(id, ex.Code, ex.Message, ex.Details).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HandlerFailed?.Invoke(this, ex);
                await WriteErrorAsync(id, InternalError, ex.Message).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(writer =>
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
            }).ConfigureAwait(false);
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

    private Task WriteErrorAsync(JsonElement id, int code, string message, string? data = null) =>
        WriteResponseAsync(writer =>
        {
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            if (data is not null)
            {
                writer.WriteString("data", data);
            }

            writer.WriteEndObject();
        });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _watchdog, null) is { } watchdog)
        {
            await watchdog.DisposeAsync().ConfigureAwait(false);
        }

        // What is still queued fails, and nothing more is queued; the message being written ends with the stream.
        List<Outgoing> queued;
        lock (_queueGate)
        {
            _writesEnded = true;
            queued = TakeQueued();
        }

        _wake.Writer.TryComplete();
        if (queued.Count > 0)
        {
            var lost = await LostAsync(null).ConfigureAwait(false);
            foreach (var message in queued)
            {
                Finish(message, lost);
            }
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
    }

    private enum OutgoingKind
    {
        Request,
        Notification,
        Response,
        Cancellation,
    }

    private enum OutgoingState
    {
        Queued,
        Taken,
        Removed,
    }

    /// <summary>
    /// A message for the writer loop: its body (written on the loop; false sends nothing), the key a newer notification
    /// replaces it by, and for a request the token that takes it out of the queue. State and node change under the queue's lock.
    /// </summary>
    private sealed class Outgoing(OutgoingKind kind, string what, Func<Utf8JsonWriter, bool> writeBody, string? replaces, CancellationToken token)
    {
        public OutgoingKind Kind { get; } = kind;

        public string What { get; } = what;

        public Func<Utf8JsonWriter, bool> WriteBody { get; } = writeBody;

        public string? Replaces { get; } = replaces;

        public CancellationToken Token { get; } = token;

        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Write { get; set; }

        public OutgoingState State { get; set; }

        public LinkedListNode<Outgoing>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }
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

/// <summary>The notification that cancels a request, and the name of its parameter that holds the request's id.</summary>
public sealed record JsonRpcCancelNotification(string Method, string IdProperty)
{
    /// <summary>LSP's <c>$/cancelRequest</c> with <c>id</c>.</summary>
    public static JsonRpcCancelNotification Lsp { get; } = new("$/cancelRequest", "id");

    /// <summary>The Agent Client Protocol's <c>$/cancel_request</c> with <c>requestId</c> (M48).</summary>
    public static JsonRpcCancelNotification Acp { get; } = new("$/cancel_request", "requestId");
}

/// <summary>The peer answered a request with a JSON-RPC error.</summary>
public sealed class JsonRpcRemoteException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    /// <summary>The error's <c>data</c> when it is a string: a host sends the full exception there for a bug.</summary>
    public string? Details { get; init; }

    /// <summary>
    /// The request's method: the exception is set, not thrown, so it has no stack, and a failure nobody awaited is named
    /// only by it (the command sweep's unobserved "Request cancelled", Linux CI run 40819420873).
    /// </summary>
    public string? Method { get; init; }

    public override string ToString() => Details is null ? base.ToString() : base.ToString() + Environment.NewLine + "--- Remote exception ---" + Environment.NewLine + Details;
}

/// <summary>Thrown by an incoming request handler to answer with a specific JSON-RPC error code.</summary>
public sealed class JsonRpcHandlerException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    /// <summary>Sent as the error's <c>data</c> (a string), such as the full exception behind an unexpected failure.</summary>
    public string? Details { get; init; }
}

/// <summary>
/// The peer went away: pending and later requests fail with this. Subclasses say why when it's known (M49:
/// <see cref="HostExitedException"/>, <see cref="HostRuntimeMissingException"/>).
/// </summary>
public class JsonRpcConnectionLostException(string message, Exception? inner) : Exception(message, inner);
