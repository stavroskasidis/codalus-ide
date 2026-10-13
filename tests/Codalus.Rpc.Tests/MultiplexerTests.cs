using System.Diagnostics;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Codalus.Rpc.Multiplexing;

namespace Codalus.Rpc.Tests;

/// <summary>
/// The multiplexer of the remote server's stdio (M21, ADR 0051): channels interleave, each has its own flow control,
/// half-closes end reads, the end of the stream ends every channel, and JSON-RPC runs on a channel.
/// </summary>
public sealed class MultiplexerTests
{
    /// <summary>Two multiplexers joined by in-memory pipes (a client and a server).</summary>
    private static (ChannelMultiplexer Client, ChannelMultiplexer Server, MuxChannel ClientControl, MuxChannel ServerControl) Pair()
    {
        var up = new Pipe();
        var down = new Pipe();
        var client = new ChannelMultiplexer(down.Reader.AsStream(), up.Writer.AsStream());
        var server = new ChannelMultiplexer(up.Reader.AsStream(), down.Writer.AsStream());
        return (client, server, client.Start(), server.Start());
    }

    private static CancellationToken Timeout(int seconds = 20) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        var data = new MemoryStream();
        await stream.CopyToAsync(data, cancellationToken);
        return data.ToArray();
    }

    [Fact]
    public async Task Channels_carry_bytes_both_ways_and_a_half_close_ends_the_reader()
    {
        var (client, server, _, _) = Pair();
        await using var _c = client;
        await using var _s = server;
        var a = client.Open(7);
        var b = server.Open(7);

        await a.Stream.WriteAsync("hello"u8.ToArray(), Timeout());
        var buffer = new byte[16];
        var read = await b.Stream.ReadAsync(buffer, Timeout());
        Assert.Equal("hello", Encoding.ASCII.GetString(buffer, 0, read));

        await b.Stream.WriteAsync("world"u8.ToArray(), Timeout());
        await b.CloseOutputAsync();
        Assert.Equal("world", Encoding.ASCII.GetString(await ReadAllAsync(a.Stream, Timeout())));

        // The other half still works after one side closed its own.
        await a.Stream.WriteAsync("again"u8.ToArray(), Timeout());
        await a.CloseOutputAsync();
        Assert.Equal("again", Encoding.ASCII.GetString(await ReadAllAsync(b.Stream, Timeout())));
        await b.RemoteClosed.WaitAsync(Timeout());
    }

    [Fact]
    public async Task Megabytes_go_through_flow_control_intact_on_many_channels_at_once()
    {
        var (client, server, _, _) = Pair();
        await using var _c = client;
        await using var _s = server;
        var tasks = Enumerable.Range(1, 6).Select(async i =>
        {
            var id = (uint)i;
            var sender = client.Open(id);
            var receiver = server.Open(id);
            var payload = RandomNumberGenerator.GetBytes(3 * ChannelMultiplexer.Window + 12345 * i); // over the window: credit must flow
            var write = Task.Run(async () =>
            {
                await sender.Stream.WriteAsync(payload, Timeout(60));
                await sender.CloseOutputAsync();
            });
            var received = await ReadAllAsync(receiver.Stream, Timeout(60));
            await write;
            Assert.Equal(SHA256.HashData(payload), SHA256.HashData(received));
        });
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task A_channel_nobody_reads_doesnt_stall_the_others()
    {
        var (client, server, _, _) = Pair();
        await using var _c = client;
        await using var _s = server;
        var flooded = client.Open(1);
        var unread = server.Open(1); // never read
        var flood = flooded.Stream.WriteAsync(new byte[ChannelMultiplexer.Window * 3], Timeout(60)).AsTask();

        // The flood fills its window and waits for credit; the other channel answers at once.
        await Task.Delay(300, Timeout());
        Assert.False(flood.IsCompleted);
        var echo = client.Open(2);
        var other = server.Open(2);
        var watch = Stopwatch.StartNew();
        await echo.Stream.WriteAsync("ping"u8.ToArray(), Timeout());
        var buffer = new byte[4];
        await other.Stream.ReadExactlyAsync(buffer, Timeout());
        Assert.Equal("ping", Encoding.ASCII.GetString(buffer));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The second channel took {watch.Elapsed}.");

        // Closing the unread channel drops what it holds and returns the credit: the flood completes.
        await unread.CloseAsync();
        await flood.WaitAsync(Timeout());
    }

    [Fact]
    public async Task Data_for_a_closed_channel_is_dropped_and_its_writer_isnt_left_waiting()
    {
        var (client, server, _, _) = Pair();
        await using var _c = client;
        await using var _s = server;
        var sender = client.Open(9);
        var receiver = server.Open(9);
        await receiver.CloseAsync();

        // More than a window: it only completes because the dropped bytes' credit comes back.
        await sender.Stream.WriteAsync(new byte[ChannelMultiplexer.Window * 2], Timeout());
        await sender.RemoteClosed.WaitAsync(Timeout());
    }

    [Fact]
    public async Task The_end_of_the_stream_ends_every_channel()
    {
        var up = new Pipe();
        var down = new Pipe();
        var client = new ChannelMultiplexer(down.Reader.AsStream(), up.Writer.AsStream());
        await using var _c = client;
        client.Start();
        var channel = client.Open(4);
        var disconnected = new TaskCompletionSource();
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        await down.Writer.CompleteAsync(); // the server's stdout ended
        await disconnected.Task.WaitAsync(Timeout());
        Assert.Equal(0, await channel.Stream.ReadAsync(new byte[8], Timeout()));
        await Assert.ThrowsAsync<IOException>(async () => await channel.Stream.WriteAsync(new byte[1], Timeout()));
        Assert.False(client.IsConnected);
        Assert.Throws<IOException>(() => client.Open(5));
    }

    [Fact]
    public async Task A_ping_measures_the_round_trip_including_simulated_latency()
    {
        var up = new Pipe();
        var down = new Pipe();
        var client = new ChannelMultiplexer(new LatencyStream(down.Reader.AsStream(), TimeSpan.FromMilliseconds(80)), up.Writer.AsStream());
        var server = new ChannelMultiplexer(up.Reader.AsStream(), down.Writer.AsStream());
        await using var _c = client;
        await using var _s = server;
        client.Start();
        server.Start();
        var rtt = await client.PingAsync(Timeout());
        Assert.InRange(rtt, TimeSpan.FromMilliseconds(70), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Json_rpc_runs_on_a_channel()
    {
        var (client, server, clientControl, serverControl) = Pair();
        await using var _c = client;
        await using var _s = server;
        await using var caller = new JsonRpcConnection(clientControl.Stream, clientControl.Stream);
        await using var callee = new JsonRpcConnection(serverControl.Stream, serverControl.Stream);
        callee.OnRequest("echo", async (element, _) =>
        {
            await Task.Yield();
            return element;
        });
        callee.Start();
        caller.Start();
        using var document = System.Text.Json.JsonDocument.Parse("{\"text\":\"over the control channel\"}");
        var answer = await caller.InvokeRawAsync("echo", document.RootElement, Timeout());
        Assert.Equal("over the control channel", answer.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_supervised_process_starts_through_a_custom_launcher_and_restarts_when_its_channel_ends()
    {
        var launcher = new ChannelLauncher();
        await using var process = new SupervisedProcess("test host", new ProcessLaunchInfo("anything", []) { Launcher = launcher }, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(20), ShutdownRequest = null, ExitNotification = null });
        var restarted = new TaskCompletionSource();
        process.Restarted += (_, _) => restarted.TrySetResult();
        await process.StartAsync(Timeout());
        Assert.Equal(1, launcher.Starts);
        Assert.Equal(42, process.ProcessId);

        await launcher.Last!.EndAsync(); // the "remote" host exits
        await restarted.Task.WaitAsync(Timeout());
        Assert.Equal(2, launcher.Starts);
    }

    /// <summary>Hosts whose stdio is a multiplexer channel, as the remote launcher's are.</summary>
#pragma warning disable CA1001 // the processes are the supervisor's to dispose
    private sealed class ChannelLauncher : IProcessLauncher
#pragma warning restore CA1001
    {
        public int Starts;

        public FakeProcess? Last;

        public Task<IHostProcess> StartAsync(ProcessLaunchInfo launch, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            Last = new FakeProcess();
            return Task.FromResult<IHostProcess>(Last);
        }
    }

    private sealed class FakeProcess : IHostProcess
    {
        private readonly Pipe _toHost = new();
        private readonly Pipe _fromHost = new();
        private readonly TaskCompletionSource<int?> _exited = new();

        public Stream Output => _fromHost.Reader.AsStream();

        public Stream Input => _toHost.Writer.AsStream();

        public int? Id => 42;

        public Task<int?> Exited => _exited.Task;

        public event Action<string>? ErrorLine;

        public void BeginErrorRead() => ErrorLine?.Invoke("started");

        public void Kill() => _exited.TrySetResult(-1);

        public async Task EndAsync()
        {
            _exited.TrySetResult(0);
            await _fromHost.Writer.CompleteAsync();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
