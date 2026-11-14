namespace Codalus.Rpc;

/// <summary>
/// A host's standard input or output whose first asynchronous read or write and whose disposal don't overlap. On Unix
/// a redirected process stream is a pipe stream, which makes the socket it reads and writes through on its first
/// read or write. Disposed at that moment, the stream closes the new socket while the creation still holds the pipe's
/// handle, and the socket's close waits for that handle's release forever (.NET's
/// <c>SafePipeHandle.CreatePipeSocket</c>, on 11.0's release candidate and its release alike): the read never returns
/// and its thread spins. A host that exited as its supervisor began reading left the connection's read loop like that
/// (Linux CI, PR #321's run 42964724644; docs/ci-flakes.md). Once the socket exists, a dispose during a read is the
/// ordinary one, so only the first asynchronous operation takes the lock; it holds it only for the operation's
/// synchronous part, where the socket is made. Synchronous reads and writes pass through: a blocking first read
/// would keep the lock until data came. The host's <see cref="System.Diagnostics.Process"/> is disposed under the same
/// lock (<see cref="LocalHostProcess"/>), since that closes these streams too.
/// </summary>
internal sealed class PipeStartGuard(Stream inner, object gate) : Stream
{
    private volatile bool _started;

    public override bool CanRead => inner.CanRead;

    public override bool CanWrite => inner.CanWrite;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _started ? inner.ReadAsync(buffer, cancellationToken) : First(() => inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _started ? inner.WriteAsync(buffer, cancellationToken) : First(() => inner.WriteAsync(buffer, cancellationToken));

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (gate)
            {
                inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private T First<T>(Func<T> operation)
    {
        lock (gate)
        {
            var started = operation(); // its synchronous part makes the pipe's socket
            _started = true;
            return started;
        }
    }
}
