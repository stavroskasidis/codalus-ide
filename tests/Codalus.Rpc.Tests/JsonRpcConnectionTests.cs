using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Rpc.Tests;

public sealed record Ping(string Text, int Delay = 0);

public sealed record Pong(string Text);

/// <summary>Parameters whose serialization waits for <see cref="Gate"/> (a big text being encoded), and says when it started.</summary>
[JsonConverter(typeof(GatedConverter))]
public sealed record Gated(string Text)
{
    public ManualResetEventSlim? Gate { get; init; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class GatedConverter : JsonConverter<Gated>
{
    public override Gated Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, Gated value, JsonSerializerOptions options)
    {
        value.Entered.TrySetResult();
        value.Gate?.Wait(TimeSpan.FromSeconds(30));
        writer.WriteStringValue(value.Text);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Ping))]
[JsonSerializable(typeof(Pong))]
[JsonSerializable(typeof(Gated))]
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

    // ------------------------------------------------------------------ the writer loop (M57)

    /// <summary>A gated message the writer loop is stuck encoding: everything queued after it waits.</summary>
    private static async Task<(Gated Gated, Task Sent, ManualResetEventSlim Gate)> HoldWriterAsync(JsonRpcConnection connection)
    {
        var gate = new ManualResetEventSlim();
        var gated = new Gated("gate") { Gate = gate };
        var sent = Task.Run(() => connection.NotifyAsync("gated", gated, TestJsonContext.Default.Gated, CancellationToken.None), TestContext.Current.CancellationToken);
        await gated.Entered.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        return (gated, sent, gate);
    }

    [Fact]
    public async Task A_notification_is_queued_before_its_parameters_are_serialized_and_a_request_after_it_follows_it()
    {
        // The didOpen of a large file: the caller (the UI thread) must not encode its text, and a request queued after it
        // still reaches the peer after it.
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _b.OnNotification("gated", TestJsonContext.Default.Gated, g => received.Enqueue("gated " + g.Text));
        _b.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (p, _) =>
        {
            received.Enqueue("echo " + p.Text);
            return Task.FromResult(new Pong(p.Text));
        });
        _a.Start();
        _b.Start();

        using var gate = new ManualResetEventSlim();
        var gated = new Gated("big") { Gate = gate };
        var queueing = Task.Run(() =>
        {
            var sent = _a.NotifyAsync("gated", gated, TestJsonContext.Default.Gated, Timeout());
            var echo = _a.InvokeAsync("echo", new Ping("after"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());
            return (sent, echo);
        }, TestContext.Current.CancellationToken);

        // Both calls return while the notification's parameters are still being serialized, elsewhere.
        var (sent, echo) = await queueing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await gated.Entered.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.False(sent.IsCompleted);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(echo.IsCompleted);

        gate.Set();
        await sent.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.Equal("after", (await echo.WaitAsync(s_timeout, TestContext.Current.CancellationToken)).Text);
        Assert.Equal(["gated big", "echo after"], received);
    }

    [Fact]
    public async Task A_lazy_notification_whose_parameters_are_null_sends_nothing()
    {
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _b.OnNotification("note", TestJsonContext.Default.Ping, p => received.Enqueue(p.Text));
        _b.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (p, _) => Task.FromResult(new Pong(p.Text)));
        _a.Start();
        _b.Start();

        await _a.NotifyAsync("note", () => (Ping?)null, TestJsonContext.Default.Ping, null, Timeout());
        await _a.NotifyAsync("note", () => new Ping("made"), TestJsonContext.Default.Ping, null, Timeout());
        await _a.InvokeAsync("echo", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());
        await _b.WhenDispatchedAsync();

        Assert.Equal(["made"], received);
    }

    [Fact]
    public async Task Messages_go_out_in_the_order_they_were_queued_from_many_threads()
    {
        const int Threads = 8;
        const int Each = 250;
        var received = new List<int>();
        var done = new TaskCompletionSource();
        _b.OnNotification("note", TestJsonContext.Default.Ping, p =>
        {
            received.Add(int.Parse(p.Text, System.Globalization.CultureInfo.InvariantCulture));
            if (received.Count == Threads * Each)
            {
                done.TrySetResult();
            }
        });
        _a.Start();
        _b.Start();

        // The order is the order of the calls: each number is taken and queued under one lock, from eight threads.
        var order = 0;
        var gate = new object();
        var sent = new System.Collections.Concurrent.ConcurrentBag<Task>();
        await Task.WhenAll(Enumerable.Range(0, Threads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < Each; i++)
            {
                lock (gate)
                {
                    var text = (order++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    sent.Add(i % 2 == 0
                        ? _a.NotifyAsync("note", new Ping(text), TestJsonContext.Default.Ping, Timeout())
                        : _a.NotifyAsync("note", () => new Ping(text), TestJsonContext.Default.Ping, null, Timeout()));
                }
            }
        }, TestContext.Current.CancellationToken)));
        await Task.WhenAll(sent).WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await done.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(0, Threads * Each), received);
    }

    [Fact]
    public async Task A_request_cancelled_while_queued_sends_nothing_not_even_its_cancellation()
    {
        var recording = new RecordingStream(_aToB.Writer.AsStream());
        await using var a = new JsonRpcConnection(_bToA.Reader.AsStream(), recording);
        var slowCalled = false;
        _b.OnRequest("slow", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (p, _) =>
        {
            slowCalled = true;
            return Task.FromResult(new Pong(p.Text));
        });
        _b.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (p, _) => Task.FromResult(new Pong(p.Text)));
        _b.OnNotification("gated", TestJsonContext.Default.Gated, _ => { });
        a.Start();
        _b.Start();

        var (_, held, gate) = await HoldWriterAsync(a);
        using var cts = new CancellationTokenSource();
        var slow = a.InvokeAsync("slow", new Ping("never"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, cts.Token);
        await cts.CancelAsync();

        // Cancelled at once, while the writer is still busy before it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        gate.Set();
        await held.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.Equal("after", (await a.InvokeAsync("echo", new Ping("after"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout())).Text);

        var written = recording.Text;
        Assert.DoesNotContain("\"slow\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("$/cancelRequest", written, StringComparison.Ordinal);
        Assert.False(slowCalled);
        gate.Dispose();
    }

    [Fact]
    public async Task A_queued_notification_is_never_dropped_even_when_its_caller_stops_waiting()
    {
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _b.OnNotification("note", TestJsonContext.Default.Ping, p => received.Enqueue(p.Text));
        _b.OnNotification("gated", TestJsonContext.Default.Gated, _ => { });
        _b.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (p, _) => Task.FromResult(new Pong(p.Text)));
        _a.Start();
        _b.Start();

        var (_, held, gate) = await HoldWriterAsync(_a);
        using var cts = new CancellationTokenSource();
        var kept = _a.NotifyAsync("note", new Ping("kept"), TestJsonContext.Default.Ping, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kept.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        gate.Set();
        await held.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await _a.InvokeAsync("echo", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());
        await _b.WhenDispatchedAsync();
        Assert.Equal(["kept"], received);
        gate.Dispose();
    }

    [Fact]
    public async Task A_newer_whole_text_replaces_one_still_queued_but_never_moves_past_a_request()
    {
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _b.OnNotification("note", TestJsonContext.Default.Ping, p => received.Enqueue(p.Text));
        _b.OnNotification("gated", TestJsonContext.Default.Gated, g => received.Enqueue(g.Text));
        _b.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, (p, _) =>
        {
            received.Enqueue("echo");
            return Task.FromResult(new Pong(p.Text));
        });
        _a.Start();
        _b.Start();

        var (_, held, gate) = await HoldWriterAsync(_a);
        Task Text(string text) => _a.NotifyAsync("note", () => new Ping(text), TestJsonContext.Default.Ping, "doc", Timeout());
        var first = Text("v1");
        var second = Text("v2");
        var third = Text("v3");
        var echo = _a.InvokeAsync("echo", new Ping("x"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());
        var fourth = Text("v4");
        var other = _a.NotifyAsync("note", () => new Ping("other"), TestJsonContext.Default.Ping, "another doc", Timeout());
        var fifth = Text("v5");

        // Replaced ones complete as written.
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        gate.Set();
        await Task.WhenAll(held, third, echo, fourth, other, fifth).WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await _b.WhenDispatchedAsync();

        // v3 stays before the request that followed it; past "another doc"'s text, v5 can't replace v4.
        Assert.Equal(["gate", "v3", "echo", "v4", "other", "v5"], received);
        gate.Dispose();
    }

    [Fact]
    public async Task Disposing_fails_every_queued_message_with_a_lost_connection()
    {
        _b.OnNotification("gated", TestJsonContext.Default.Gated, _ => { });
        _a.Start();
        _b.Start();

        var (_, held, gate) = await HoldWriterAsync(_a);
        var note = _a.NotifyAsync("note", new Ping("queued"), TestJsonContext.Default.Ping, CancellationToken.None);
        var lazy = _a.NotifyAsync("note", () => new Ping("queued"), TestJsonContext.Default.Ping, "doc", CancellationToken.None);
        var request = _a.InvokeAsync("echo", new Ping("queued"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout());

        // Disposing doesn't wait for the message stuck being written.
        await _a.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<JsonRpcConnectionLostException>(() => note);
        await Assert.ThrowsAsync<JsonRpcConnectionLostException>(() => lazy);
        await Assert.ThrowsAsync<JsonRpcConnectionLostException>(() => request);
        await Assert.ThrowsAsync<JsonRpcConnectionLostException>(() => _a.NotifyAsync("note", new Ping("later"), TestJsonContext.Default.Ping, CancellationToken.None));

        gate.Set();
        await Assert.ThrowsAnyAsync<Exception>(() => held.WaitAsync(s_timeout, TestContext.Current.CancellationToken)); // its stream is gone
        gate.Dispose();
    }

    [Fact]
    public async Task A_write_stuck_in_the_stream_and_the_messages_behind_it_are_reported_slow_and_then_finished()
    {
        using var stuck = new ManualResetEventSlim();
        var stream = new StuckStream(_aToB.Writer.AsStream(), stuck);
        await using var a = new JsonRpcConnection(_bToA.Reader.AsStream(), stream);
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        a.Slow += (_, message) => reports.Enqueue(message);
        _b.OnNotification("note", TestJsonContext.Default.Ping, _ => { });
        _b.Start();

        var first = a.NotifyAsync("note", new Ping("first"), TestJsonContext.Default.Ping, CancellationToken.None);
        await stream.Entered.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        var second = a.NotifyAsync("note", new Ping("second"), TestJsonContext.Default.Ping, CancellationToken.None);
        a.ReportSlow(TimeSpan.Zero);

        Assert.Contains(reports, r => r.StartsWith("Writing notification note has been in progress", StringComparison.Ordinal));
        Assert.Contains(reports, r => r.StartsWith("Writing notification note has waited", StringComparison.Ordinal) && r.Contains("2 write(s) pending", StringComparison.Ordinal));
        Assert.Contains("2 write(s) pending", a.Describe(), StringComparison.Ordinal);

        stuck.Set();
        await Task.WhenAll(first, second).WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.Equal(2, reports.Count(r => r == "Writing notification note finished."));
    }

    [Fact]
    public async Task Requests_cancelled_at_any_point_leave_the_stream_intact()
    {
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        _b.HandlerFailed += (_, ex) => failures.Enqueue(ex);
        var disconnected = false;
        _b.Disconnected += (_, _) => disconnected = true;
        _b.OnRequest("echo", TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, async (p, _) =>
        {
            await Task.Yield();
            return new Pong(p.Text);
        });
        _b.OnNotification("note", TestJsonContext.Default.Ping, _ => { });
        _a.Start();
        _b.Start();

        // Cancellations land while requests are queued, being written, or waiting for their answer; big notifications
        // between them keep the writer busy.
        var big = new string('<', 64 * 1024);
        var calls = new List<Task>();
        for (var i = 0; i < 300; i++)
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(i % 4));
            calls.Add(_a.InvokeAsync("echo", new Ping(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, cts.Token)
                .ContinueWith(_ => cts.Dispose(), TaskScheduler.Default));
            if (i % 10 == 0)
            {
                calls.Add(_a.NotifyAsync("note", new Ping(big), TestJsonContext.Default.Ping, CancellationToken.None));
            }
        }

        await Task.WhenAll(calls).WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.Equal("after", (await _a.InvokeAsync("echo", new Ping("after"), TestJsonContext.Default.Ping, TestJsonContext.Default.Pong, Timeout())).Text);
        Assert.Empty(failures);
        Assert.False(disconnected);
    }

    /// <summary>
    /// A stream that fails with something other than an I/O error (a remote window's path rewrite throws
    /// InvalidDataException or FormatException) ends the writes with a lost connection: the failed message, those queued
    /// behind it and later ones fail rather than wait forever (M57 review: the loop stopped, and nothing drained its queue).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stream_failing_with_any_exception_fails_the_messages_instead_of_leaving_them_waiting(bool inFlush)
    {
        var stream = new FailingStream(new InvalidDataException("A message has no Content-Length."), inFlush);
        await using var a = new JsonRpcConnection(_bToA.Reader.AsStream(), stream);
        a.Start();

        var first = a.InvokeAsync("first", CancellationToken.None);
        var note = a.NotifyAsync("note", new Ping("queued"), TestJsonContext.Default.Ping, CancellationToken.None);
        var lost = await Assert.ThrowsAsync<JsonRpcConnectionLostException>(() => first.WaitAsync(s_timeout, TestContext.Current.CancellationToken));
        Assert.IsType<InvalidDataException>(lost.InnerException);
        await Assert.ThrowsAsync<JsonRpcConnectionLostException>(() => note.WaitAsync(s_timeout, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<JsonRpcConnectionLostException>(() => a.InvokeAsync("second", CancellationToken.None).WaitAsync(s_timeout, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<JsonRpcConnectionLostException>(() => a.NotifyAsync("note", new Ping("later"), TestJsonContext.Default.Ping, CancellationToken.None).WaitAsync(s_timeout, TestContext.Current.CancellationToken));
    }

    /// <summary>Records what is written through it.</summary>
    private sealed class RecordingStream(Stream inner) : Stream
    {
        private readonly MemoryStream _written = new();

        public string Text
        {
            get
            {
                lock (_written)
                {
                    return System.Text.Encoding.UTF8.GetString(_written.ToArray());
                }
            }
        }

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

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_written)
            {
                _written.Write(buffer.Span);
            }

            return inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A stream whose first write blocks until <paramref name="stuck"/> is set (a peer that stopped reading).</summary>
    private sealed class StuckStream(Stream inner, ManualResetEventSlim stuck) : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Run(() => stuck.Wait(TimeSpan.FromSeconds(30)), CancellationToken.None);
            await inner.WriteAsync(buffer, cancellationToken);
        }
    }

    /// <summary>A stream whose writes (or flushes) throw <paramref name="failure"/>.</summary>
    private sealed class FailingStream(Exception failure, bool inFlush) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken) => inFlush ? Task.FromException(failure) : Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inFlush ? ValueTask.CompletedTask : ValueTask.FromException(failure);
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
