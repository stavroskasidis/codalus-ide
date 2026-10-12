using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DaedalusStudio.Rpc.Multiplexing;

/// <summary>The kinds of frame on a multiplexed stream (ADR 0050).</summary>
public enum FrameType : byte
{
    /// <summary>Bytes for a channel; counts against the receiver's window.</summary>
    Data = 1,

    /// <summary>The sender writes nothing more on the channel (a half-close: end of stream for the receiver).</summary>
    Close = 2,

    /// <summary>The receiver consumed bytes: the sender may write that many more (a big-endian <c>uint32</c>).</summary>
    Credit = 3,

    /// <summary>A round-trip probe (8 bytes, echoed in a <see cref="Pong"/>).</summary>
    Ping = 4,

    Pong = 5,
}

/// <summary>
/// Many byte channels over one duplex stream (M21, ADR 0050): the remote server's stdio carries the control channel
/// (0) and a channel per host, terminal and tunnel. A frame is a 9-byte header — channel (<c>uint32</c>, big-endian),
/// <see cref="FrameType"/>, payload length (<c>uint32</c>) — and at most <see cref="MaxPayload"/> bytes. Each channel
/// has a <see cref="Window"/> of credit per direction, so a terminal flooding output or a reader that stopped reading
/// never stalls the other channels: only its own writer waits. Frames for a channel this side doesn't know (closed,
/// or never opened) are dropped and their credit returned. Only one side opens channels (the client), by id, before
/// telling the other through the control channel; the other side opens the same id when it takes the request.
/// </summary>
public sealed class ChannelMultiplexer : IAsyncDisposable
{
    public const int HeaderSize = 9;

    public const int MaxPayload = 32 * 1024;

    /// <summary>Bytes a writer may send before the reader grants more.</summary>
    public const int Window = 1024 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, MuxChannel> _channels = new();
    private readonly ConcurrentDictionary<ulong, (long Started, TaskCompletionSource<TimeSpan> Done)> _pings = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task _readLoop = Task.CompletedTask;
    private long _nextChannel;
    private long _nextPing;
    private int _failed;

    public ChannelMultiplexer(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>The underlying stream ended or failed: every channel ended with it.</summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Completes when the read loop ends.</summary>
    public Task Completion => _readLoop;

    public bool IsConnected => Volatile.Read(ref _failed) == 0;

    /// <summary>Bytes written and read, frames included (the status bar's traffic, tests).</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    private long _bytesWritten;
    private long _bytesRead;

    /// <summary>Opens channel 0 (the control channel) and starts reading.</summary>
    public MuxChannel Start()
    {
        var control = Open(0);
        _readLoop = Task.Run(ReadLoopAsync);
        return control;
    }

    /// <summary>A new channel id (the opening side only).</summary>
    public uint NextId() => (uint)Interlocked.Increment(ref _nextChannel);

    /// <summary>Opens <paramref name="id"/> on this side; data that arrives for it from now on is kept for its reader.</summary>
    public MuxChannel Open(uint id)
    {
        if (Volatile.Read(ref _failed) != 0)
        {
            throw new IOException("The connection is closed.");
        }

        var channel = new MuxChannel(this, id);
        if (!_channels.TryAdd(id, channel))
        {
            throw new InvalidOperationException($"Channel {id} is already open.");
        }

        return channel;
    }

    internal void Forget(MuxChannel channel) => _channels.TryRemove(new KeyValuePair<uint, MuxChannel>(channel.Id, channel));

    public int OpenChannels => _channels.Count;

    /// <summary>The round trip of a ping frame (the transport's latency, as the status bar shows it).</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken)
    {
        var id = (ulong)Interlocked.Increment(ref _nextPing);
        var done = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pings[id] = (Stopwatch.GetTimestamp(), done);
        try
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(payload, id);
            await WriteFrameAsync(0, FrameType.Ping, payload, cancellationToken).ConfigureAwait(false);
            return await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pings.TryRemove(id, out _);
        }
    }

    internal async Task WriteFrameAsync(uint channel, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _failed) != 0)
        {
            throw new IOException("The connection is closed.");
        }

        var length = HeaderSize + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, channel);
            buffer[4] = (byte)type;
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(5), (uint)payload.Length);
            payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _output.WriteAsync(buffer.AsMemory(0, length), CancellationToken.None).ConfigureAwait(false);
                await _output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                Interlocked.Add(ref _bytesWritten, length);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or NotSupportedException)
        {
            throw new IOException("The connection is closed.", ex);
        }
        catch (IOException ex)
        {
            Fail(ex);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[HeaderSize];
        Exception? error = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                if (!await ReadExactlyAsync(header).ConfigureAwait(false))
                {
                    break;
                }

                var channel = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = (FrameType)header[4];
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5));
                if (length > MaxPayload)
                {
                    throw new InvalidDataException($"A frame of {length} bytes is larger than {MaxPayload}.");
                }

                var payload = length == 0 ? [] : new byte[length];
                if (length > 0 && !await ReadExactlyAsync(payload).ConfigureAwait(false))
                {
                    throw new EndOfStreamException("The stream ended in the middle of a frame.");
                }

                Interlocked.Add(ref _bytesRead, HeaderSize + length);
                Dispatch(channel, type, payload);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
            error = ex is OperationCanceledException ? null : ex;
        }

        Fail(error);
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _input.ReadAsync(buffer.AsMemory(read), _shutdown.Token).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The stream ended in the middle of a frame.");
            }

            read += n;
        }

        return true;
    }

    private void Dispatch(uint id, FrameType type, byte[] payload)
    {
        switch (type)
        {
            case FrameType.Ping when payload.Length == 8:
                _ = WritePongAsync(payload);
                return;
            case FrameType.Pong when payload.Length == 8:
                if (_pings.TryGetValue(BinaryPrimitives.ReadUInt64BigEndian(payload), out var ping))
                {
                    ping.Done.TrySetResult(Stopwatch.GetElapsedTime(ping.Started));
                }

                return;
        }

        if (!_channels.TryGetValue(id, out var channel))
        {
            if (type == FrameType.Data && payload.Length > 0)
            {
                // Nobody reads it: give the credit back so the writer isn't left waiting for it. Never awaited here:
                // the read loop must not wait on writes, or two peers writing to each other could both stall.
                _ = GrantAsync(id, payload.Length);
            }

            return;
        }

        switch (type)
        {
            case FrameType.Data:
                channel.OnData(payload);
                break;
            case FrameType.Close:
                channel.OnRemoteClosed();
                break;
            case FrameType.Credit when payload.Length == 4:
                channel.OnCredit(BinaryPrimitives.ReadUInt32BigEndian(payload));
                break;
        }
    }

    private async Task WritePongAsync(byte[] payload)
    {
        try
        {
            await WriteFrameAsync(0, FrameType.Pong, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    internal async Task GrantAsync(uint channel, int bytes)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)bytes);
        try
        {
            await WriteFrameAsync(channel, FrameType.Credit, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    private void Fail(Exception? error)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        foreach (var channel in _channels.Values)
        {
            channel.OnConnectionLost(error);
        }

        _channels.Clear();
        foreach (var ping in _pings.Values)
        {
            ping.Done.TrySetException(new IOException("The connection is closed.", error));
        }

        Disconnected?.Invoke(this, error);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Fail(null);
        try
        {
            await _output.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        try
        {
            await _input.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}
