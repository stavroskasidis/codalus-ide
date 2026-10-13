using System.Diagnostics;
using System.Threading.Channels;

namespace Codalus.Rpc.Multiplexing;

/// <summary>
/// Simulated network latency for budgets and tests (M21): bytes read from <paramref name="inner"/> become readable
/// <paramref name="delay"/> after they arrived, so a request's answer comes back one round trip later. Writes pass
/// through. Never used for a real connection.
/// </summary>
public sealed class LatencyStream(Stream inner, TimeSpan delay) : Stream
{
    private readonly Channel<(long Arrived, byte[] Data)> _queue = Channel.CreateUnbounded<(long, byte[])>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private Task? _pump;
    private byte[]? _current;
    private int _offset;

    /// <summary>The simulated round trip; changes apply to bytes read from now on.</summary>
    public TimeSpan Delay { get; set; } = delay;

    private async Task PumpAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await inner.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await _queue.Writer.WriteAsync((Stopwatch.GetTimestamp(), buffer.AsSpan(0, read).ToArray())).ConfigureAwait(false);
            }

            _queue.Writer.TryComplete();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _queue.Writer.TryComplete(ex is IOException ? ex : null);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _pump ??= Task.Run(PumpAsync, CancellationToken.None);
        if (_current is null)
        {
            if (!await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) || !_queue.Reader.TryRead(out var next))
            {
                return 0;
            }

            var wait = Delay - Stopwatch.GetElapsedTime(next.Arrived);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            (_current, _offset) = (next.Data, 0);
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

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
