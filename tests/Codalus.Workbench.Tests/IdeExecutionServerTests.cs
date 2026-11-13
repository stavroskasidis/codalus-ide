using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Codalus.Workbench.Aspire;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The IDE execution server (ADR 0043) without an AppHost: a client plays the orchestrator, listening on the
/// notification socket while the server is disposed.
/// </summary>
public sealed class IdeExecutionServerTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long disposal may take, and the orchestrator's read then, when the server doesn't wait for the socket: under the
    /// 15 s after which the server leaves a teardown that doesn't finish behind, so a disposal that waited for the peer's
    /// close (or a socket it never closed) fails, rather than passing on that fallback. The two don't agree on purpose;
    /// a healthy teardown takes milliseconds, and the tests also fail when one of its steps runs out of its own time.
    /// </summary>
    private static readonly TimeSpan s_noWait = TimeSpan.FromSeconds(10);

    private sealed class FakeSession : IIdeRunSession
    {
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> Started { get; } = Task.FromResult(4242);

        public Task<int?> Exited => _exited.Task;

        public int Terminated { get; private set; }

        public void Terminate()
        {
            Terminated++;
            _exited.TrySetResult(137);
        }
    }

    /// <summary>
    /// The state a failure describes says when disposal reached each step of its teardown: a disposal that took over 10 s
    /// on Windows CI said nothing of which step (PR #300's run 41849511374).
    /// </summary>
    [Fact]
    public async Task The_state_lists_when_disposal_reached_each_step_of_its_teardown()
    {
        var server = new IdeExecutionServer((_, _) => Task.FromResult<IIdeRunSession>(new FakeSession()));
        server.Start();
        Assert.DoesNotContain("teardown", server.DescribeState(), StringComparison.Ordinal);

        await server.DisposeAsync().AsTask().WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        var state = server.DescribeState();
        Assert.Matches(@"teardown [0-9.]+ s in: sessions stopped at [0-9.]+ s, teardown started at [0-9.]+ s, cancelled at [0-9.]+ s, sockets aborted at [0-9.]+ s, connections aborted at [0-9.]+ s, listener closed at [0-9.]+ s, accept loop ended at [0-9.]+ s$", state);
    }

    [Fact]
    public async Task Disposal_ends_the_sessions_and_closes_a_listening_notification_socket_without_waiting_for_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = new FakeSession();
        var log = new List<string>();
        var server = new IdeExecutionServer((_, _) => Task.FromResult<IIdeRunSession>(session));
        server.Log += line =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };
        server.Start();
        string Log()
        {
            lock (log)
            {
                return string.Join('\n', log);
            }
        }

        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + server.Token);
            await socket.ConnectAsync(new Uri($"ws://localhost:{server.Port}/run_session/notify"), ct);

            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
            using var body = new StringContent("""{"launch_configurations":[{"type":"project","project_path":"/app/Api/Api.csproj","mode":"NoDebug"}]}""", Encoding.UTF8, "application/json");
            using var created = await http.PutAsync("run_session", body, ct);
            Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);

            // The orchestrator hears that the program runs, then keeps a read pending, as DCP does. Its connection completes
            // before the server registers the socket, and the session starts at once: the processRestarted it gets may have
            // waited for the socket.
            var buffer = new byte[4096];
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(s_timeout);
            var received = await socket.ReceiveAsync(buffer, wait.Token);
            Assert.Contains("\"processRestarted\"", Encoding.UTF8.GetString(buffer, 0, received.Count), StringComparison.Ordinal);
            var pending = socket.ReceiveAsync(buffer, CancellationToken.None);

            // Disposing ends the session (its sessionTerminated races the teardown) and doesn't wait on the socket.
            await server.DisposeAsync().AsTask().WaitAsync(s_noWait, ct);
            Assert.Equal(1, session.Terminated);
            Assert.False(server.DescribeState().Contains("ran out", StringComparison.Ordinal), $"Server: {server.DescribeState()}\n{Log()}");

            // The orchestrator's read ends: the server is gone.
            try
            {
                while (true)
                {
                    var result = await pending.WaitAsync(s_noWait, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    pending = socket.ReceiveAsync(buffer, CancellationToken.None); // a sessionTerminated sent before the teardown
                }
            }
            catch (WebSocketException)
            {
            }

            // Disposing again does nothing.
            await server.DisposeAsync().AsTask().WaitAsync(s_timeout, ct);
            Assert.Equal(1, session.Terminated);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.Fail($"{ex}\nServer: {server.DescribeState()}\n{Log()}");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Disposal while sessions end and orchestrators listen, over and over: each round races the sessions' last
    /// notifications against the teardown, which is where disposal blocked on Windows CI (runs 41849511374 and
    /// 43184717554). Before the sockets stopped taking the lifetime token this failed in its 23rd round on Windows (run
    /// 43192618601), with the cancellation and a notification's send waiting for each other. No step of the teardown
    /// may run out of its time, and a disposal that is still running after a few seconds fails with the stacks of every
    /// thread, taken while it is blocked.
    /// </summary>
    [Fact]
    public async Task Disposal_never_blocks_while_sessions_end_and_orchestrators_listen()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var round = 1; round <= 300 && clock.Elapsed < TimeSpan.FromSeconds(20); round++)
        {
            var log = new List<string>();
            var server = new IdeExecutionServer((_, _) => Task.FromResult<IIdeRunSession>(new FakeSession()));
            server.Log += line =>
            {
                lock (log)
                {
                    log.Add(line);
                }
            };
            server.Start();
            string Describe()
            {
                lock (log)
                {
                    return $"Round {round}. Server: {server.DescribeState()}\n{string.Join('\n', log)}";
                }
            }

            var sockets = new List<ClientWebSocket>();
            try
            {
                // Each orchestrator keeps a read pending, as DCP does, until the server is gone.
                var reads = new List<Task>();
                for (var i = 0; i < 3; i++)
                {
                    var socket = new ClientWebSocket();
                    sockets.Add(socket);
                    socket.Options.SetRequestHeader("Authorization", "Bearer " + server.Token);
                    await socket.ConnectAsync(new Uri($"ws://localhost:{server.Port}/run_session/notify"), ct);
                    reads.Add(Task.Run(
                        async () =>
                        {
                            var buffer = new byte[4096];
                            try
                            {
                                while ((await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)).MessageType != WebSocketMessageType.Close)
                                {
                                }
                            }
                            catch (WebSocketException)
                            {
                            }
                        },
                        ct));
                }

                using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.Port}/") };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
                for (var i = 0; i < 3; i++)
                {
                    using var body = new StringContent("""{"launch_configurations":[{"type":"project","project_path":"/app/Api/Api.csproj","mode":"NoDebug"}]}""", Encoding.UTF8, "application/json");
                    using var created = await http.PutAsync("run_session", body, ct);
                    Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
                }

                // Disposing ends the sessions, whose sessionTerminated notifications race the teardown.
                var disposal = server.DisposeAsync().AsTask();
                if (await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(3), ct)) != disposal)
                {
                    Assert.Fail($"Disposal still runs after 3 s. {Describe()}\n{Codalus.Tests.TestWatchdog.ManagedStacks()}");
                }

                await disposal;
                Assert.False(server.DescribeState().Contains("ran out", StringComparison.Ordinal), Describe());
                try
                {
                    await Task.WhenAll(reads).WaitAsync(s_noWait, ct);
                }
                catch (TimeoutException)
                {
                    Assert.Fail($"An orchestrator's read didn't end. {Describe()}\n{Codalus.Tests.TestWatchdog.ManagedStacks()}");
                }
            }
            finally
            {
                await server.DisposeAsync();
                foreach (var socket in sockets)
                {
                    socket.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// A session's end is logged with its exit code and its last lines, which otherwise went only to the dashboard: the
    /// Linux smoke test's api ended as it started, and the IDE's log said nothing of why (PR #338's run 43302285600).
    /// </summary>
    [Fact]
    public async Task A_sessions_end_is_logged_with_its_exit_code_and_its_last_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessions = new List<FakeSession>();
        var log = new List<string>();
        var server = new IdeExecutionServer((_, _) =>
        {
            var session = new FakeSession();
            lock (sessions)
            {
                sessions.Add(session);
            }

            return Task.FromResult<IIdeRunSession>(session);
        });
        server.Log += line =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
            async Task StartAsync()
            {
                using var body = new StringContent("""{"launch_configurations":[{"type":"project","project_path":"/app/Api/Api.csproj","mode":"NoDebug"}]}""", Encoding.UTF8, "application/json");
                using var created = await http.PutAsync("run_session", body, ct);
                Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
            }

            string? Ended(string id)
            {
                lock (log)
                {
                    return log.LastOrDefault(l => l.StartsWith($"Run session {id} ended", StringComparison.Ordinal));
                }
            }

            // One that printed more than the tail keeps: its last lines, in order.
            await StartAsync();
            for (var i = 1; i <= IdeExecutionServer.TailLines + 5; i++)
            {
                await server.LogAsync("1", $"line {i}", isError: false);
            }

            sessions[0].Terminate();
            var deadline = DateTime.UtcNow + s_timeout;
            while (Ended("1") is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, ct);
            }

            var ended = Ended("1");
            Assert.NotNull(ended);
            var lines = ended!.Split('\n');
            Assert.Equal("Run session 1 ended (exit code 137). Its last output:", lines[0]);
            Assert.Equal([.. Enumerable.Range(6, IdeExecutionServer.TailLines).Select(i => $"  line {i}")], lines[1..]);

            // One that printed nothing says so.
            await StartAsync();
            sessions[1].Terminate();
            deadline = DateTime.UtcNow + s_timeout;
            while (Ended("2") is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, ct);
            }

            Assert.Equal("Run session 2 ended (exit code 137) without output.", Ended("2"));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_notification_sent_before_the_orchestrator_listens_reaches_it_once_it_does()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new IdeExecutionServer((_, _) => throw new InvalidOperationException("No runs here."));
        server.Start();
        try
        {
            await server.LogAsync("1", "first line", isError: false);
            Assert.Contains("1 notification(s) waiting", server.DescribeState(), StringComparison.Ordinal);

            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + server.Token);
            await socket.ConnectAsync(new Uri($"ws://localhost:{server.Port}/run_session/notify"), ct);
            var buffer = new byte[4096];
            var received = await socket.ReceiveAsync(buffer, ct).WaitAsync(s_timeout, ct);
            var text = Encoding.UTF8.GetString(buffer, 0, received.Count);
            Assert.Contains("\"serviceLogs\"", text, StringComparison.Ordinal);
            Assert.Contains("first line", text, StringComparison.Ordinal);
            Assert.Contains("0 notification(s) waiting", server.DescribeState(), StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_server_whose_port_is_taken_fails_to_start_and_disposes_without_throwing()
    {
        // A port given to the server is the one it uses (only a port it chose is replaced when taken), and the shell
        // disposes a server that couldn't start: that disposal doesn't throw either. The port is taken by a listener of
        // the same prefix: a socket on 127.0.0.1 alone doesn't always collide with the managed listener's localhost (it
        // can bind ::1 as well; CI's Linux runner let the server start), but the same prefix collides on every OS.
        var port = FreePort();
        using var taken = new System.Net.HttpListener();
        taken.Prefixes.Add($"http://localhost:{port}/");
        taken.Start();
        var server = new IdeExecutionServer((_, _) => throw new InvalidOperationException("No runs here."), port);
        Assert.Throws<System.Net.HttpListenerException>(server.Start);

        await server.DisposeAsync().AsTask().WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.Equal(port, server.Port);
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        return ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
    }
}
