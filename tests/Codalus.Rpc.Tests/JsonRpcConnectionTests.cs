using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Rpc.Tests;

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
    public async Task A_response_can_overtake_the_notifications_before_it_and_when_dispatched_waits_for_them()
    {
        var release = new TaskCompletionSource();
        var received = new List<string>();
        _a.OnNotification("note", TestJsonContext.Default.Ping, async p =>
        {
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            lock (received)
            {
                received.Add(p.Text);
            }
        });
        _b.OnRequest("build", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, async (p, ct) =>
        {
            await _b.NotifyAsync("note", new Ping("error 1"), TestJsonContext.Default.Ping, ct);
            await _b.NotifyAsync("note", new Ping("error 2"), TestJsonContext.Default.Ping, ct);
            return new Pong("failed");
        });
        _a.Start();
        _b.Start();

        // The response is read while the first notification's handler still runs: the call completes first.
        var result = await _a.InvokeAsync("build", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());
        Assert.Equal("failed", result.Text);
        var dispatched = _a.WhenDispatchedAsync();
        Assert.False(dispatched.IsCompleted);
        lock (received)
        {
            Assert.Empty(received);
        }

        release.SetResult();
        await dispatched.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        lock (received)
        {
            Assert.Equal(["error 1", "error 2"], received);
        }

        // Once the connection has ended, it completes at once.
        await _bToA.Writer.CompleteAsync();
        await _a.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await _a.WhenDispatchedAsync().WaitAsync(s_timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task When_dispatched_called_from_a_notification_handler_does_not_wait_for_itself()
    {
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _a.OnNotification("note", TestJsonContext.Default.Ping, async _ =>
        {
            // The dispatch loop waits for this handler: a marker queued behind it would never be reached.
            await Task.Yield();
            await _a.WhenDispatchedAsync().WaitAsync(s_timeout, TestContext.Current.CancellationToken);
            handled.TrySetResult();
        });
        _a.Start();
        _b.Start();

        await _b.NotifyAsync("note", new Ping("x"), TestJsonContext.Default.Ping, Timeout());
        await handled.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Outside the handler it waits for the dispatch loop again.
        await _a.WhenDispatchedAsync().WaitAsync(s_timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task When_dispatched_completes_when_the_connection_is_disposed_behind_a_handler_that_never_ends()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _a.OnNotification("note", TestJsonContext.Default.Ping, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        _a.Start();
        _b.Start();

        await _b.NotifyAsync("note", new Ping("x"), TestJsonContext.Default.Ping, Timeout());
        await entered.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        var dispatched = _a.WhenDispatchedAsync();
        Assert.False(dispatched.IsCompleted);

        // Disposing waits for the handler, but whoever waits for the dispatch doesn't: it completes, without failing.
        var disposing = _a.DisposeAsync().AsTask();
        await dispatched.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.True(dispatched.IsCompletedSuccessfully);
        await _a.WhenDispatchedAsync().WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        release.SetResult();
        await disposing.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await _a.WhenDispatchedAsync().WaitAsync(s_timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unknown_methods_and_failing_handlers_return_errors()
    {
        _b.OnRequest("fail", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (_, _) => throw new InvalidOperationException("boom"));
        _b.OnRequest("coded", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (_, _) => throw new JsonRpcHandlerException(-33000, "plugin not found"));
        _b.OnRequest("detailed", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (_, _) => throw new JsonRpcHandlerException(-33001, "Handle is not initialized.") { Details = "System.InvalidOperationException: Handle is not initialized.\n   at Somewhere()" });
        _a.Start();
        _b.Start();

        var missing = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _a.InvokeAsync("nope", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout()));
        var failed = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _a.InvokeAsync("fail", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout()));
        var coded = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _a.InvokeAsync("coded", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout()));

        Assert.Equal(JsonRpcConnection.MethodNotFound, missing.Code);
        Assert.Equal(JsonRpcConnection.InternalError, failed.Code);
        Assert.Equal("boom", failed.Message);
        Assert.Equal(-33000, coded.Code);
        Assert.Null(coded.Details);

        // Each names its request: the exception is set, not thrown, so a failure nobody awaited has only that.
        Assert.Equal("nope", missing.Method);
        Assert.Equal("fail", failed.Method);
        Assert.Equal("coded", coded.Method);

        // The error's data (a string): the full exception behind a bug, shown with the remote exception.
        var detailed = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _a.InvokeAsync("detailed", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout()));
        Assert.Equal("Handle is not initialized.", detailed.Message);
        Assert.Equal("System.InvalidOperationException: Handle is not initialized.\n   at Somewhere()", detailed.Details);
        Assert.Contains("at Somewhere()", detailed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_request_cancels_the_remote_handler()
    {
        var handlerCancelled = new TaskCompletionSource();
        var handlerStarted = new TaskCompletionSource();
        _b.OnRequest("slow", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, async (_, ct) =>
        {
            handlerStarted.TrySetResult();
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

        // Cancelled once the handler runs: a fixed delay could fire before the request is sent on a loaded machine.
        using var cts = new CancellationTokenSource();
        var call = _a.InvokeAsync("slow", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, cts.Token);
        await handlerStarted.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
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

    [Fact]
    public async Task A_cancel_that_races_with_the_end_of_its_request_leaves_the_connection_working()
    {
        // Each $/cancelRequest follows its request at once, so some arrive just as the request ends and disposes
        // its token source. That once threw in the dispatch loop, and the peer went silent for good.
        _b.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, async (p, _) =>
        {
            await Task.Yield();
            return new Pong(p.Text);
        });
        _a.Start();
        _b.Start();

        var input = _aToB.Writer.AsStream();
        for (var id = 1_000_000; id < 1_003_000; id++)
        {
            await WriteFrameAsync(input, $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"echo","params":{"text":"x"}}""");
            await WriteFrameAsync(input, $$$"""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":{{{id}}}}}""");
        }

        var next = await _a.InvokeAsync("echo", new Ping("after"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());

        Assert.Equal("after", next.Text);
    }

    [Fact]
    public async Task A_message_the_dispatcher_cannot_read_is_reported_and_later_messages_still_arrive()
    {
        var failures = new List<Exception>();
        _b.HandlerFailed += (_, ex) => failures.Add(ex);
        _b.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (p, _) => Task.FromResult(new Pong(p.Text)));
        _a.Start();
        _b.Start();

        await WriteFrameAsync(_aToB.Writer.AsStream(), """{"jsonrpc":"2.0","method":42}""");
        var next = await _a.InvokeAsync("echo", new Ping("after"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());

        Assert.Equal("after", next.Text);
        Assert.Single(failures);
    }

    [Fact]
    public async Task A_malformed_response_ends_the_connection_instead_of_leaving_requests_waiting()
    {
        var disconnected = new TaskCompletionSource<Exception?>();
        _a.Disconnected += (_, ex) => disconnected.TrySetResult(ex);
        _a.Start();

        var pending = _a.InvokeAsync("echo", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());
        await WriteFrameAsync(_bToA.Writer.AsStream(), """{"jsonrpc":"2.0","id":1,"error":{"code":"not a number","message":"x"}}""");

        await Assert.ThrowsAsync<JsonRpcConnectionLostException>(() => pending);
        Assert.NotNull(await disconnected.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken));
    }

    private static async Task WriteFrameAsync(Stream stream, string json)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(FramedMessageWriter.Header(body.Length), TestContext.Current.CancellationToken);
        await stream.WriteAsync(body, TestContext.Current.CancellationToken);
        await stream.FlushAsync(TestContext.Current.CancellationToken);
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
