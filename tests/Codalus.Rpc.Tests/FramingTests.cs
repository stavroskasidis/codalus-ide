using System.Text;

namespace Codalus.Rpc.Tests;

/// <summary>
/// <c>Content-Length</c> framing. A message larger than the read buffer is read into its own array: growing the buffer for
/// it made garbage of twice its size and kept the buffer at the largest message's size for the connection's life (8 MB in
/// the shell after a 100k-line file's document symbols).
/// </summary>
public sealed class FramingTests
{
    private static byte[] Frame(byte[] body) => [.. FramedMessageWriter.Header(body.Length), .. body];

    [Fact]
    public async Task A_message_larger_than_the_buffer_is_read_whole_between_small_ones_without_growing_the_buffer()
    {
        var small = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"a"}""");
        var large = new byte[1_000_003];
        for (var i = 0; i < large.Length; i++)
        {
            large[i] = (byte)('a' + (i % 26));
        }

        using var input = new MemoryStream([.. Frame(small), .. Frame(large), .. Frame(small), .. Frame(large)]);
        var reader = new FramedMessageReader(new TrickleStream(input, 7_000));
        var capacity = reader.BufferCapacity;
        foreach (var expected in new[] { small, large, small, large })
        {
            Assert.Equal(expected, await reader.ReadMessageAsync(CancellationToken.None));
        }

        Assert.Null(await reader.ReadMessageAsync(CancellationToken.None));
        Assert.Equal(capacity, reader.BufferCapacity);
    }

    [Fact]
    public async Task A_large_message_cut_short_is_an_error()
    {
        var body = new byte[200_000];
        using var input = new MemoryStream(Frame(body)[..^10]);
        var reader = new FramedMessageReader(new TrickleStream(input, 50_000));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadMessageAsync(CancellationToken.None));
    }

    /// <summary>A stream that returns at most <paramref name="chunk"/> bytes per read, as a pipe does.</summary>
    private sealed class TrickleStream(Stream inner, int chunk) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
