using System.Buffers.Binary;
using System.Threading.Channels;

namespace DaedalusStudio.Rpc.Multiplexing;

/// <summary>
/// A duplex byte stream that survives its link (M21b, ADR 0054): the multiplexer runs on it, and the physical
/// connection under it (ssh's stdio, a socket) can drop and be replaced without losing or repeating a byte.
/// <list type="bullet">
/// <item>Every byte written has an offset. Written bytes are kept until the other side acknowledges them.</item>
/// <item>A link starts with a hello from each side: <c>'R'</c> and the offset it has received up to. Each side then
/// resends what the other hasn't received, and goes on.</item>
/// <item>On a link, frames are <c>'D'</c> + length (<c>uint32</c>) + bytes, and <c>'A'</c> + received offset
/// (<c>uint64</c>), sent every <see cref="AckEvery"/> bytes received and on <see cref="AckInterval"/>.</item>
/// </list>
/// While no link is attached, writes are kept (up to <see cref="MaxUnacknowledged"/>) and reads wait. Reads end only
/// when the stream is closed (<see cref="Close"/>, or too much kept).
/// </summary>
public sealed class ResumableStream : Stream
{
    public const int AckEvery = 64 * 1024;

    public static readonly TimeSpan AckInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>What may wait unacknowledged (a long disconnection with a busy writer): beyond it, the stream closes.</summary>
    public const long MaxUnacknowledged = 256L * 1024 * 1024;

    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<byte[]> _unacknowledged = [];
    private readonly Lock _gate = new();
    private ReadOnlyMemory<byte> _leftover;
    private long _unacknowledgedBase;   // the offset of the first byte in _unacknowledged
    private long _written;              // every byte written so far
    private long _received;             // every byte received so far
    private long _lastAcknowledged;     // what this side last told the other
    private Link? _link;
    private int _closed;

    private sealed class Link(Stream input, Stream output)
    {
        public Stream Input { get; } = input;

        public Stream Output { get; } = output;

        public CancellationTokenSource Stop { get; } = new();
    }

    /// <summary>A link dropped (the reader of the stream doesn't notice: attach another to go on).</summary>
    public event EventHandler<Exception?>? LinkLost;

    public bool IsLinked => Volatile.Read(ref _link) is not null;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Bytes written and not yet acknowledged by the other side.</summary>
    public long Unacknowledged
    {
        get
        {
            lock (_gate)
            {
                return _written - _unacknowledgedBase;
            }
        }
    }

    /// <summary>
    /// Attaches a link: hellos are exchanged, what the other side missed is resent, and reading the link starts. A link
    /// still attached is dropped first.
    /// </summary>
    public async Task AttachAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        Detach(null);
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hello = new byte[9];
            hello[0] = (byte)'R';
            BinaryPrimitives.WriteInt64BigEndian(hello.AsSpan(1), Interlocked.Read(ref _received));
            await output.WriteAsync(hello, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            var theirs = new byte[9];
            await input.ReadExactlyAsync(theirs, cancellationToken).ConfigureAwait(false);
            if (theirs[0] != (byte)'R')
            {
                throw new InvalidDataException("The other side didn't start with a resumable hello.");
            }

            var peerReceived = BinaryPrimitives.ReadInt64BigEndian(theirs.AsSpan(1));
            byte[] resend;
            lock (_gate)
            {
                Acknowledge(peerReceived);
                if (peerReceived < _unacknowledgedBase || peerReceived > _written)
                {
                    throw new InvalidDataException($"The other side has received up to {peerReceived}, which this side can't resume from ({_unacknowledgedBase}–{_written}).");
                }

                resend = Collect(peerReceived);
            }

            for (var at = 0; at < resend.Length; at += ChannelMultiplexer.MaxPayload)
            {
                await WriteDataAsync(output, resend.AsMemory(at, Math.Min(ChannelMultiplexer.MaxPayload, resend.Length - at)), cancellationToken).ConfigureAwait(false);
            }

            var link = new Link(input, output);
            Volatile.Write(ref _link, link);
            _ = Task.Run(() => ReadLinkAsync(link), CancellationToken.None);
            _ = Task.Run(() => AcknowledgeLoopAsync(link), CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>Drops the current link (its streams are disposed); the stream waits for another.</summary>
    public void Detach(Exception? error)
    {
        if (Interlocked.Exchange(ref _link, null) is not { } link)
        {
            return;
        }

        link.Stop.Cancel();
        try
        {
            link.Input.Dispose();
            link.Output.Dispose();
        }
        catch (IOException)
        {
        }

        LinkLost?.Invoke(this, error);
    }

    /// <summary>Ends the stream: reads return 0, writes fail, the link goes.</summary>
    public override void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            var link = Interlocked.Exchange(ref _link, null);
            link?.Stop.Cancel();
            try
            {
                link?.Input.Dispose();
                link?.Output.Dispose();
            }
            catch (IOException)
            {
            }

            _incoming.Writer.TryComplete();
        }

        base.Close();
    }

    // ------------------------------------------------------------------ writing

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (buffer.Length == 0)
        {
            return;
        }

        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _unacknowledged.Add(buffer.ToArray());
                _written += buffer.Length;
                if (_written - _unacknowledgedBase > MaxUnacknowledged)
                {
                    throw new IOException("Too much waited for the other side while disconnected.");
                }
            }

            if (Volatile.Read(ref _link) is { } link)
            {
                try
                {
                    for (var at = 0; at < buffer.Length; at += ChannelMultiplexer.MaxPayload)
                    {
                        await WriteDataAsync(link.Output, buffer.Slice(at, Math.Min(ChannelMultiplexer.MaxPayload, buffer.Length - at)), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Kept: the next link resends it.
                    Detach(ex);
                }
            }
        }
        catch (IOException) when (!IsClosed)
        {
            Close();
            throw;
        }
        finally
        {
            _send.Release();
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Flush()
    {
    }

    private static async Task WriteDataAsync(Stream output, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var frame = new byte[5 + data.Length];
        frame[0] = (byte)'D';
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), data.Length);
        data.Span.CopyTo(frame.AsSpan(5));
        await output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops what the other side has received.</summary>
    private void Acknowledge(long offset)
    {
        while (_unacknowledged.Count > 0 && _unacknowledgedBase + _unacknowledged[0].Length <= offset)
        {
            _unacknowledgedBase += _unacknowledged[0].Length;
            _unacknowledged.RemoveAt(0);
        }

        if (_unacknowledged.Count > 0 && offset > _unacknowledgedBase)
        {
            var skip = (int)(offset - _unacknowledgedBase);
            _unacknowledged[0] = _unacknowledged[0][skip..];
            _unacknowledgedBase = offset;
        }
    }

    /// <summary>The kept bytes from <paramref name="offset"/> on.</summary>
    private byte[] Collect(long offset)
    {
        var result = new byte[_written - offset];
        var at = 0;
        var position = _unacknowledgedBase;
        foreach (var chunk in _unacknowledged)
        {
            var start = (int)Math.Max(0, offset - position);
            if (start < chunk.Length)
            {
                chunk.AsSpan(start).CopyTo(result.AsSpan(at));
                at += chunk.Length - start;
            }

            position += chunk.Length;
        }

        return result;
    }

    // ------------------------------------------------------------------ reading

    private async Task ReadLinkAsync(Link link)
    {
        Exception? error = null;
        try
        {
            var header = new byte[9];
            while (!link.Stop.IsCancellationRequested)
            {
                await link.Input.ReadExactlyAsync(header.AsMemory(0, 1), link.Stop.Token).ConfigureAwait(false);
                switch (header[0])
                {
                    case (byte)'D':
                        await link.Input.ReadExactlyAsync(header.AsMemory(1, 4), link.Stop.Token).ConfigureAwait(false);
                        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                        if (length is < 0 or > 16 * 1024 * 1024)
                        {
                            throw new InvalidDataException($"A data frame of {length} bytes.");
                        }

                        var data = new byte[length];
                        await link.Input.ReadExactlyAsync(data, link.Stop.Token).ConfigureAwait(false);
                        Interlocked.Add(ref _received, length);
                        await _incoming.Writer.WriteAsync(data, CancellationToken.None).ConfigureAwait(false);
                        if (Interlocked.Read(ref _received) - Interlocked.Read(ref _lastAcknowledged) >= AckEvery)
                        {
                            await SendAcknowledgementAsync(link).ConfigureAwait(false);
                        }

                        break;
                    case (byte)'A':
                        await link.Input.ReadExactlyAsync(header.AsMemory(1, 8), link.Stop.Token).ConfigureAwait(false);
                        var acknowledged = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(1));
                        lock (_gate)
                        {
                            Acknowledge(acknowledged);
                        }

                        break;
                    default:
                        throw new InvalidDataException($"An unknown frame ({header[0]}).");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
        {
            error = ex is OperationCanceledException ? null : ex;
        }

        if (Volatile.Read(ref _link) == link)
        {
            Detach(error);
        }
    }

    private async Task AcknowledgeLoopAsync(Link link)
    {
        try
        {
            while (!link.Stop.IsCancellationRequested)
            {
                await Task.Delay(AckInterval, link.Stop.Token).ConfigureAwait(false);
                if (Interlocked.Read(ref _received) != Interlocked.Read(ref _lastAcknowledged))
                {
                    await SendAcknowledgementAsync(link).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAcknowledgementAsync(Link link)
    {
        await _send.WaitAsync(link.Stop.Token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _link) != link)
            {
                return;
            }

            var received = Interlocked.Read(ref _received);
            var frame = new byte[9];
            frame[0] = (byte)'A';
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1), received);
            await link.Output.WriteAsync(frame, link.Stop.Token).ConfigureAwait(false);
            await link.Output.FlushAsync(link.Stop.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastAcknowledged, received);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Detach(ex);
        }
        finally
        {
            _send.Release();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_leftover.IsEmpty)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            _leftover = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        var count = Math.Min(buffer.Length, _leftover.Length);
        _leftover[..count].CopyTo(buffer);
        _leftover = _leftover[count..];
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    // ------------------------------------------------------------------ Stream

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
