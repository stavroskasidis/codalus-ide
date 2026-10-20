using System.IO.Pipelines;
using System.Text;

namespace Codalus.Rpc.Tests;

/// <summary>
/// Newline-delimited framing (M48: the Agent Client Protocol's stdio transport): one message per line, other lines
/// skipped, and the protocol's own cancel notification.
/// </summary>
public sealed class LineFramingTests
{
    [Fact]
    public async Task Lines_are_messages_and_blank_and_log_lines_are_skipped()
    {
        var big = "{\"method\":\"big\",\"params\":{\"text\":\"" + new string('x', 200_000) + "\"}}";
        var text = "{\"a\":1}\r\n\n  \nnot a message\n" + big + "\n{\"b\":2}";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var reader = new LineMessageReader(new SlowStream(input, 3_000));
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString((await reader.ReadMessageAsync(CancellationToken.None))!));
        Assert.Equal(big, Encoding.UTF8.GetString((await reader.ReadMessageAsync(CancellationToken.None))!));
        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString((await reader.ReadMessageAsync(CancellationToken.None))!));
        Assert.Null(await reader.ReadMessageAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_connection_writes_one_line_per_message_and_cancels_with_its_own_notification()
    {
        var toB = new Pipe();
        var toA = new Pipe();
        await using var a = new JsonRpcConnection(toA.Reader.AsStream(), toB.Writer.AsStream(), JsonRpcFraming.NewlineDelimited) { CancelNotification = JsonRpcCancelNotification.Acp };
        var cancelled = new TaskCompletionSource();
        a.Start();

        // The peer, by hand: reads lines, answers "echo" at once and records the cancel of "wait".
        var ct = TestContext.Current.CancellationToken;
        var peer = Task.Run(async () =>
        {
            using var reader = new StreamReader(toB.Reader.AsStream(), Encoding.UTF8);
            var writer = toA.Writer.AsStream();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                using var message = System.Text.Json.JsonDocument.Parse(line);
                var root = message.RootElement;
                var method = root.GetProperty("method").GetString();
                if (method == "echo")
                {
                    var id = root.GetProperty("id").GetInt64();
                    var raw = root.GetProperty("params").GetProperty("text").GetRawText();
                    var answer = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"text\":" + raw + "}}\n";
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(answer), ct);
                    await writer.FlushAsync(ct);
                }
                else if (method == "$/cancel_request" && root.GetProperty("params").TryGetProperty("requestId", out _))
                {
                    cancelled.TrySetResult();
                }
            }
        }, ct);

        var pong = await a.InvokeAsync("echo", new Ping("two\nlines"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, ct);
        Assert.Equal("two\nlines", pong.Text);

        using var stop = new CancellationTokenSource();
        var waiting = a.InvokeAsync("wait", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await toB.Writer.CompleteAsync();
        await peer.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private sealed class SlowStream(Stream inner, int chunk) : Stream
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
