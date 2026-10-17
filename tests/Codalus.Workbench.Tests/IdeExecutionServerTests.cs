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

            // The orchestrator hears that the program runs, then keeps a read pending, as DCP does.
            var buffer = new byte[4096];
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(s_timeout);
            var received = await socket.ReceiveAsync(buffer, wait.Token);
            Assert.Contains("\"processRestarted\"", Encoding.UTF8.GetString(buffer, 0, received.Count), StringComparison.Ordinal);
            var pending = socket.ReceiveAsync(buffer, CancellationToken.None);

            // Disposing ends the session (its sessionTerminated races the teardown) and doesn't wait on the socket.
            await server.DisposeAsync().AsTask().WaitAsync(s_timeout, ct);
            Assert.Equal(1, session.Terminated);

            // The orchestrator's read ends: the server is gone.
            try
            {
                while (true)
                {
                    var result = await pending.WaitAsync(s_timeout, ct);
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
            Assert.Fail(ex + "\n" + Log());
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
