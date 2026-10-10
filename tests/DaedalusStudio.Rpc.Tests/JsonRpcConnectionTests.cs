using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaedalusStudio.Rpc.Tests;

public sealed record Ping(string Text, int Delay = 0);

public sealed record Pong(string Text);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Ping))]
[JsonSerializable(typeof(Pong))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

public sealed class JsonRpcConnectionTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);
    private readonly Pipe _aToB = new();
    private readonly Pipe _bToA = new();
    private JsonRpcConnection _a = null!;
    private JsonRpcConnection _b = null!;

    public ValueTask InitializeAsync()
    {
        _a = new JsonRpcConnection(_bToA.Reader.AsStream(), _aToB.Writer.AsStream());
        _b = new JsonRpcConnection(_aToB.Reader.AsStream(), _bToA.Writer.AsStream());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(s_timeout).Token;

    [Fact]
    public async Task Requests_work_in_both_directions()
    {
        _a.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (p, _) => Task.FromResult(new Pong("a:" + p.Text)));
        _b.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (p, _) => Task.FromResult(new Pong("b:" + p.Text)));
        _a.Start();
        _b.Start();

        var fromB = await _a.InvokeAsync("echo", new Ping("hi"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());
        var fromA = await _b.InvokeAsync("echo", new Ping("yo"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());

        Assert.Equal("b:hi", fromB.Text);
        Assert.Equal("a:yo", fromA.Text);
    }

    [Fact]
    public async Task Notifications_are_dispatched_in_order()
    {
        var received = new List<string>();
        var done = new TaskCompletionSource();
        _b.OnNotification("note", TestJsonContext.Default.Ping, async p =>
        {
            await Task.Delay(p.Delay, TestContext.Current.CancellationToken);
            received.Add(p.Text);
            if (received.Count == 3)
            {
                done.SetResult();
            }
        });
        _a.Start();
        _b.Start();

        await _a.NotifyAsync("note", new Ping("1", Delay: 50), TestJsonContext.Default.Ping, Timeout());
        await _a.NotifyAsync("note", new Ping("2", Delay: 0), TestJsonContext.Default.Ping, Timeout());
        await _a.NotifyAsync("note", new Ping("3", Delay: 10), TestJsonContext.Default.Ping, Timeout());
        await done.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        Assert.Equal(["1", "2", "3"], received);
    }

    [Fact]
    public async Task Unknown_methods_and_failing_handlers_return_errors()
    {
        _b.OnRequest("fail", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (_, _) => throw new InvalidOperationException("boom"));
        _b.OnRequest("coded", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (_, _) => throw new JsonRpcHandlerException(-33000, "plugin not found"));
        _a.Start();
        _b.Start();

        var missing = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _a.InvokeAsync("nope", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout()));
        var failed = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _a.InvokeAsync("fail", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout()));
        var coded = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _a.InvokeAsync("coded", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout()));

        Assert.Equal(JsonRpcConnection.MethodNotFound, missing.Code);
        Assert.Equal(JsonRpcConnection.InternalError, failed.Code);
        Assert.Equal("boom", failed.Message);
        Assert.Equal(-33000, coded.Code);
    }

    [Fact]
    public async Task Cancelling_a_request_cancels_the_remote_handler()
    {
        var handlerCancelled = new TaskCompletionSource();
        _b.OnRequest("slow", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, async (_, ct) =>
        {
            try
            {
                await Task.Delay(s_timeout, ct);
            }
            catch (OperationCanceledException)
            {
                handlerCancelled.SetResult();
                throw;
            }

            return new Pong("late");
        });
        _a.Start();
        _b.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _a.InvokeAsync("slow", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, cts.Token));
        await handlerCancelled.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_slow_request_does_not_block_later_messages()
    {
        var release = new TaskCompletionSource();
        _b.OnRequest("slow", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, async (_, _) =>
        {
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return new Pong("slow");
        });
        _b.OnRequest("fast", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (_, _) => Task.FromResult(new Pong("fast")));
        _a.Start();
        _b.Start();

        var slow = _a.InvokeAsync("slow", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());
        var fast = await _a.InvokeAsync("fast", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());
        Assert.Equal("fast", fast.Text);
        Assert.False(slow.IsCompleted);
        release.SetResult();
        Assert.Equal("slow", (await slow.WaitAsync(TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public async Task Pending_requests_fail_when_the_peer_disconnects()
    {
        var disconnected = new TaskCompletionSource();
        _a.Disconnected += (_, _) => disconnected.TrySetResult();
        _b.OnRequest("hang", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, async (_, ct) =>
        {
            await Task.Delay(s_timeout, ct);
            return new Pong("never");
        });
        _a.Start();
        _b.Start();

        var pending = _a.InvokeAsync("hang", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await _bToA.Writer.CompleteAsync(); // the peer's outgoing stream ends

        await Assert.ThrowsAsync<JsonRpcConnectionLostException>(() => pending);
        await disconnected.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Raw_requests_round_trip_json()
    {
        _b.OnRequest("raw", (element, _) => Task.FromResult<JsonElement?>(element));
        _a.Start();
        _b.Start();

        using var doc = JsonDocument.Parse("""{"a":[1,2,3],"b":"x"}""");
        var result = await _a.InvokeRawAsync("raw", doc.RootElement, Timeout());

        Assert.Equal(3, result.GetProperty("a").GetArrayLength());
        Assert.Equal("x", result.GetProperty("b").GetString());
    }

    [Fact]
    public async Task A_request_cancelled_while_its_frame_is_written_leaves_the_stream_intact()
    {
        // The token is cancelled right after the header goes out: the body must follow, or the peer reads garbage.
        using var cts = new CancellationTokenSource();
        var cancelling = new CancelOnFirstWriteStream(_aToB.Writer.AsStream(), cts);
        await using var a = new JsonRpcConnection(_bToA.Reader.AsStream(), cancelling);
        _b.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (p, _) => Task.FromResult(new Pong(p.Text)));
        a.Start();
        _b.Start();

        try
        {
            // Cancelled, or answered before the cancellation was seen: either way the frame went out whole.
            await a.InvokeAsync("echo", new Ping("first"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        var next = await a.InvokeAsync("echo", new Ping("second"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());

        Assert.Equal("second", next.Text);
    }

    private sealed class CancelOnFirstWriteStream(Stream inner, CancellationTokenSource cancel) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await inner.WriteAsync(buffer, cancellationToken);
            await cancel.CancelAsync();
        }
    }
}
