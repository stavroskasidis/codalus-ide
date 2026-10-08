using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Daedalus.LanguageServices.Client;

/// <summary>
/// Minimal JSON-RPC 2.0 client using LSP <c>Content-Length</c> framing. All serialization goes
/// through source-generated <see cref="JsonTypeInfo{T}"/> so it is safe under Native AOT and trimming.
/// Cancelling a request sends LSP's <c>$/cancelRequest</c> notification.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private static readonly byte[] s_headerTerminator = "\r\n\r\n"u8.ToArray();

    private readonly Stream _reader;
    private readonly Stream _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readLoop;
    private long _nextId;

    public JsonRpcConnection(Stream reader, Stream writer)
    {
        _reader = reader;
        _writer = writer;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Raised once when the connection is lost (stream closed or host died).</summary>
    public event EventHandler<Exception?>? Disconnected;

    public Task Completion => _readLoop;

    public async Task<TResult> InvokeAsync<TParams, TResult>(
        string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
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
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters, paramsInfo);
            }, cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(() => _ = CancelRemoteAsync(id));
            var element = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return element.Deserialize(resultInfo)!;
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

    /// <summary>Sends a request whose parameters are omitted (e.g. <c>shutdown</c>) and waits for completion.</summary>
    public async Task InvokeAsync(string method, CancellationToken cancellationToken)
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
            }, cancellationToken).ConfigureAwait(false);
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

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
            var buffer = new MessageReader(_reader);
            while (!_shutdown.IsCancellationRequested)
            {
                var payload = await buffer.ReadMessageAsync(_shutdown.Token).ConfigureAwait(false);
                if (payload is null)
                {
                    break;
                }

                Dispatch(payload);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException or InvalidDataException)
        {
            failure = ex;
        }

        var lost = new JsonRpcConnectionLostException("The language service host connection was lost.", failure);
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(lost);
        }

        Disconnected?.Invoke(this, failure);
    }

    private void Dispatch(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number || root.TryGetProperty("method", out _))
        {
            // Server-to-client requests and notifications are not used by the M0 protocol.
            return;
        }

        if (!_pending.TryGetValue(idElement.GetInt64(), out var completion))
        {
            return;
        }

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

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        await _reader.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
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

public sealed class JsonRpcRemoteException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed class JsonRpcConnectionLostException(string message, Exception? inner) : Exception(message, inner);
