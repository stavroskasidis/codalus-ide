using System.IO.Pipes;
using System.Text;
using Codalus.Rpc.Multiplexing;

namespace Codalus.Rpc.Tests;

/// <summary>The resumable stream under a remote session's multiplexer (M21b): links drop and come back, bytes don't.</summary>
public sealed class ResumableStreamTests
{
    /// <summary>A link: two one-way pipes, one per direction.</summary>
    private sealed class Link : IDisposable
    {
        private readonly AnonymousPipeServerStream _aToB = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _bToA = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _bReads;
        private readonly AnonymousPipeClientStream _aReads;

        public Link()
        {
            _bReads = new AnonymousPipeClientStream(PipeDirection.In, _aToB.ClientSafePipeHandle);
            _aReads = new AnonymousPipeClientStream(PipeDirection.In, _bToA.ClientSafePipeHandle);
        }

        public Task AttachAsync(ResumableStream a, ResumableStream b, CancellationToken token) =>
            Task.WhenAll(a.AttachAsync(_aReads, _aToB, token), b.AttachAsync(_bReads, _bToA, token));

        public void Dispose()
        {
            _aToB.Dispose();
            _bToA.Dispose();
            _aReads.Dispose();
            _bReads.Dispose();
        }
    }

    private static async Task<string> ReadAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, token).AsTask().WaitAsync(TimeSpan.FromSeconds(20), token);
        return Encoding.UTF8.GetString(buffer);
    }

    [Fact]
    public async Task Bytes_written_while_the_link_is_down_arrive_once_after_another_link_is_attached()
    {
        var token = TestContext.Current.CancellationToken;
        using var a = new ResumableStream();
        using var b = new ResumableStream();
        var first = new Link();
        await first.AttachAsync(a, b, token);

        await a.WriteAsync("hello "u8.ToArray(), token);
        Assert.Equal("hello ", await ReadAsync(b, 6, token));
        await b.WriteAsync("back"u8.ToArray(), token);
        Assert.Equal("back", await ReadAsync(a, 4, token));

        // The link drops: both sides notice; writes on either side are kept.
        var lost = new TaskCompletionSource();
        a.LinkLost += (_, _) => lost.TrySetResult();
        first.Dispose();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        await a.WriteAsync("while "u8.ToArray(), token);
        await a.WriteAsync("down"u8.ToArray(), token);
        await b.WriteAsync("from b"u8.ToArray(), token);
        Assert.False(a.IsLinked);

        // Another link: each side resends what the other missed, nothing twice.
        using var second = new Link();
        await second.AttachAsync(a, b, token);
        Assert.Equal("while down", await ReadAsync(b, 10, token));
        Assert.Equal("from b", await ReadAsync(a, 6, token));
        await a.WriteAsync("!"u8.ToArray(), token);
        Assert.Equal("!", await ReadAsync(b, 1, token));

        // Acknowledgements free what was kept.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((a.Unacknowledged > 0 || b.Unacknowledged > 0) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, token);
        }

        Assert.Equal((0L, 0L), (a.Unacknowledged, b.Unacknowledged));
    }

    [Fact]
    public async Task A_large_stream_survives_links_dropping_in_the_middle()
    {
        var token = TestContext.Current.CancellationToken;
        using var a = new ResumableStream();
        using var b = new ResumableStream();
        var data = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(data);

        var link = new Link();
        await link.AttachAsync(a, b, token);
        var received = new MemoryStream();
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (received.Length < data.Length)
            {
                var read = await b.ReadAsync(buffer, token);
                received.Write(buffer, 0, read);
            }
        }, token);

        // Written in pieces, the link replaced twice along the way.
        for (var i = 0; i < 12; i++)
        {
            await a.WriteAsync(data.AsMemory(i * (data.Length / 12), data.Length / 12), token);
            if (i is 3 or 8)
            {
                link.Dispose();
                await Task.Delay(50, token);
                link = new Link();
                await link.AttachAsync(a, b, token);
            }
        }

        await reader.WaitAsync(TimeSpan.FromSeconds(60), token);
        link.Dispose();
        Assert.Equal(data, received.ToArray());
    }

    [Fact]
    public async Task Closing_ends_the_reader()
    {
        var token = TestContext.Current.CancellationToken;
        var a = new ResumableStream();
        var read = a.ReadAsync(new byte[10], token).AsTask();
        a.Close();
        Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(5), token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => a.WriteAsync("x"u8.ToArray(), token).AsTask());
    }
}
