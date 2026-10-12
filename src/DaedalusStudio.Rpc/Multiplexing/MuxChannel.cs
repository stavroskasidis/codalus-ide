namespace DaedalusStudio.Rpc.Multiplexing;

/// <summary>
/// One channel of a <see cref="ChannelMultiplexer"/>: a duplex byte stream. Reading returns 0 once the other side
/// closed its half (or the connection ended); <see cref="CloseOutputAsync"/> closes this side's half; disposing the
/// <see cref="Stream"/> does both.
/// </summary>
#pragma warning disable CA1001 // The write lock never uses its wait handle; the channel's lifetime is its stream's.
public sealed class MuxChannel
#pragma warning restore CA1001
{
    private readonly ChannelMultiplexer _owner;
    private readonly Lock _gate = new();
    private readonly Queue<byte[]> _incoming = new();
    private int _offset;
    private bool _remoteClosed;
    private bool _discarding;
    private Exception? _error;
    private TaskCompletionSource? _readable;
    private int _consumed;
    private long _credit = ChannelMultiplexer.Window;
    private TaskCompletionSource? _writable;
    private bool _localClosed;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource _remoteClosedTask = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal MuxChannel(ChannelMultiplexer owner, uint id)
    {
        _owner = owner;
        Id = id;
        Stream = new ChannelStream(this);
    }

    public uint Id { get; }

    public Stream Stream { get; }

    /// <summary>Completes when the other side closed its half or the connection ended.</summary>
    public Task RemoteClosed => _remoteClosedTask.Task;

    // ------------------------------------------------------------------ incoming

    internal void OnData(byte[] payload)
    {
        TaskCompletionSource? wake;
        lock (_gate)
        {
            if (_discarding)
            {
                _ = _owner.GrantAsync(Id, payload.Length);
                return;
            }

            _incoming.Enqueue(payload);
            wake = _readable;
            _readable = null;
        }

        wake?.TrySetResult();
    }

    internal void OnRemoteClosed()
    {
        TaskCompletionSource? wake;
        bool forget;
        lock (_gate)
        {
            _remoteClosed = true;
            wake = _readable;
            _readable = null;
            forget = _localClosed;
        }

        wake?.TrySetResult();
        _remoteClosedTask.TrySetResult();
        if (forget)
        {
            _owner.Forget(this);
        }
    }

    internal void OnCredit(uint bytes)
    {
        TaskCompletionSource? wake;
        lock (_gate)
        {
            _credit += bytes;
            wake = _writable;
            _writable = null;
        }

        wake?.TrySetResult();
    }

    internal void OnConnectionLost(Exception? error)
    {
        TaskCompletionSource? readable;
        TaskCompletionSource? writable;
        lock (_gate)
        {
            _remoteClosed = true;
            _error = error ?? new IOException("The connection is closed.");
            readable = _readable;
            writable = _writable;
            _readable = _writable = null;
        }

        readable?.TrySetResult();
        writable?.TrySetResult();
        _remoteClosedTask.TrySetResult();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_incoming.Count > 0)
                {
                    var head = _incoming.Peek();
                    var count = Math.Min(buffer.Length, head.Length - _offset);
                    head.AsMemory(_offset, count).CopyTo(buffer);
                    _offset += count;
                    if (_offset == head.Length)
                    {
                        _incoming.Dequeue();
                        _offset = 0;
                    }

                    _consumed += count;
                    if (_consumed >= ChannelMultiplexer.Window / 4)
                    {
                        _ = _owner.GrantAsync(Id, _consumed);
                        _consumed = 0;
                    }

                    return count;
                }

                if (_remoteClosed || _discarding)
                {
                    return 0;
                }

                _readable ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _readable.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ outgoing

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (data.Length > 0)
            {
                int count;
                Task? wait = null;
                lock (_gate)
                {
                    if (_localClosed)
                    {
                        throw new IOException($"Channel {Id} is closed for writing.");
                    }

                    if (_error is { } error)
                    {
                        throw new IOException("The connection is closed.", error);
                    }

                    count = (int)Math.Min(Math.Min(_credit, ChannelMultiplexer.MaxPayload), data.Length);
                    if (count <= 0)
                    {
                        _writable ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        wait = _writable.Task;
                    }
                    else
                    {
                        _credit -= count;
                    }
                }

                if (wait is not null)
                {
                    await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await _owner.WriteFrameAsync(Id, FrameType.Data, data[..count], cancellationToken).ConfigureAwait(false);
                data = data[count..];
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Tells the other side this side writes nothing more (its reads end). Idempotent.</summary>
    public async Task CloseOutputAsync()
    {
        bool forget;
        lock (_gate)
        {
            if (_localClosed)
            {
                return;
            }

            _localClosed = true;
            forget = _remoteClosed;
        }

        try
        {
            await _owner.WriteFrameAsync(Id, FrameType.Close, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        if (forget)
        {
            _owner.Forget(this);
        }
    }

    /// <summary>Closes this side's half and drops what arrives from now on (its credit goes back to the writer).</summary>
    public async Task CloseAsync()
    {
        int pending;
        TaskCompletionSource? wake;
        lock (_gate)
        {
            _discarding = true;
            pending = _incoming.Sum(b => b.Length) - _offset + _consumed;
            _incoming.Clear();
            _offset = 0;
            _consumed = 0;
            wake = _readable;
            _readable = null;
        }

        wake?.TrySetResult();
        if (pending > 0)
        {
            await _owner.GrantAsync(Id, pending).ConfigureAwait(false);
        }

        await CloseOutputAsync().ConfigureAwait(false);
    }

    /// <summary>The channel as a <see cref="System.IO.Stream"/> (JSON-RPC connections, DAP, terminals, tunnels).</summary>
    private sealed class ChannelStream(MuxChannel channel) : Stream
    {
        private int _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            channel.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            channel.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            channel.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            channel.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(channel.WriteAsync(buffer, cancellationToken));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            channel.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ = channel.CloseAsync();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await channel.CloseAsync().ConfigureAwait(false);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
