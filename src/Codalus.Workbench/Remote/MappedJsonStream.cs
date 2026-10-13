using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Codalus.Rpc;

namespace Codalus.Workbench.Remote;

/// <summary>
/// A <c>Content-Length</c>-framed JSON stream (JSON-RPC, LSP, DAP) whose paths are rewritten on the way: messages
/// read from <paramref name="inner"/> get local paths, messages written get remote ones (<see cref="RemotePathMap"/>).
/// The hosts on the remote machine see their own paths; the shell sees its own.
/// </summary>
public sealed class MappedJsonStream : Stream
{
    private readonly Stream _inner;
    private readonly RemotePathMap _map;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly MemoryStream _outgoing = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private byte[]? _current;
    private int _offset;

    public MappedJsonStream(Stream inner, RemotePathMap map)
    {
        _inner = inner;
        _map = map;
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        var reader = new FramedMessageReader(_inner);
        try
        {
            while (await reader.ReadMessageAsync(_shutdown.Token).ConfigureAwait(false) is { } message)
            {
                var body = _map.MessageToLocal(message);
                await _incoming.Writer.WriteAsync([.. FramedMessageWriter.Header(body.Length), .. body], _shutdown.Token).ConfigureAwait(false);
            }

            _incoming.Writer.TryComplete();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _incoming.Writer.TryComplete();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_current is null)
        {
            try
            {
                if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) || !_incoming.Reader.TryRead(out _current))
                {
                    return 0;
                }
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        if (_offset == _current.Length)
        {
            _current = null;
        }

        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _outgoing.Write(buffer.Span);
            while (TakeMessage() is { } body)
            {
                var mapped = _map.MessageToRemote(body);
                await _inner.WriteAsync(FramedMessageWriter.Header(mapped.Length), cancellationToken).ConfigureAwait(false);
                await _inner.WriteAsync(mapped, cancellationToken).ConfigureAwait(false);
                await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>The next complete message's body from what was written, or null.</summary>
    private byte[]? TakeMessage()
    {
        var data = _outgoing.GetBuffer().AsSpan(0, (int)_outgoing.Length);
        var headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
        {
            return null;
        }

        var length = -1;
        foreach (var line in Encoding.ASCII.GetString(data[..headerEnd]).Split("\r\n"))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line.AsSpan(colon + 1).Trim(), CultureInfo.InvariantCulture);
            }
        }

        if (length < 0)
        {
            throw new InvalidDataException("A message has no Content-Length.");
        }

        var start = headerEnd + 4;
        if (data.Length - start < length)
        {
            return null;
        }

        var body = data.Slice(start, length).ToArray();
        var rest = data[(start + length)..].ToArray();
        _outgoing.SetLength(0);
        _outgoing.Write(rest);
        return body;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
            _inner.Dispose();
            _writeLock.Dispose();
            _outgoing.Dispose();
            _shutdown.Dispose();
        }

        base.Dispose(disposing);
    }
}
