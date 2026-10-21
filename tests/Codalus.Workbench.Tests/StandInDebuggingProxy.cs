using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.Workbench.Tests;

/// <summary>
/// A stand-in for the Blazor WebAssembly dev server's debugging proxy (BrowserDebugProxy), between vscode-js-debug and
/// the browser. It relays the Chrome DevTools Protocol both ways, and does what that proxy does when the browser reports
/// a page attached: it holds the event, sets <c>globalThis.dotnetDebugger</c> in the page's current document
/// (<c>Runtime.evaluate</c>), registers it for the page's next documents once that's answered
/// (<c>Page.addScriptToEvaluateOnNewDocument</c>), and only then passes the event on. The .NET runtime pauses for the
/// proxy at its start only in a document that has the flag. The browser's answer to the first step comes
/// <see cref="SlowAnswer"/> late, as on a loaded machine (197 ms in the linux-arm64 smoke step's failure), and the
/// messages after it wait for it, as they do behind a browser that answers in order.
/// </summary>
internal sealed class StandInDebuggingProxy : IAsyncDisposable
{
    /// <summary>The flag, as the proxy sets it.</summary>
    public const string Flag = "globalThis.dotnetDebugger = true";

    /// <summary>The proxy's own command ids, far above js-debug's, so their answers can be told apart.</summary>
    private const int FirstOwnId = 1_000_000_000;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _relays = [];
    private readonly Task _accepting;
    private int _nextId = FirstOwnId;
    private int _handshakes;

    public StandInDebuggingProxy(TimeSpan slowAnswer)
    {
        SlowAnswer = slowAnswer;
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        InspectUri = string.Create(CultureInfo.InvariantCulture, $"ws://127.0.0.1:{port}/_framework/debug/ws-proxy?browser={{browserInspectUri}}");
        _accepting = AcceptAsync();
    }

    public TimeSpan SlowAnswer { get; }

    /// <summary>The launch's <c>inspectUri</c>: js-debug connects here, with the browser's own address in the query.</summary>
    public string InspectUri { get; }

    /// <summary>The pages whose attach event js-debug got, after the handshake.</summary>
    public int Handshakes => Volatile.Read(ref _handshakes);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Task[] relays;
        lock (_relays)
        {
            relays = [.. _relays, _accepting];
        }

        await Task.WhenAll(relays).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }

            lock (_relays)
            {
                _relays.Add(RelayAsync(client));
            }
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream);
                var key = head.Split("\r\n").Select(l => l.Split(':', 2)).First(p => p.Length == 2 && p[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))[1].Trim();
                var target = head[..head.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ')[1];
                var browserUri = Uri.UnescapeDataString(target[(target.IndexOf("browser=", StringComparison.Ordinal) + "browser=".Length)..]);
#pragma warning disable CA5350 // RFC 6455 fixes SHA-1 for the handshake's accept key; nothing is secret here.
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
                using var debugger = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
                using var browser = new ClientWebSocket();
                await browser.ConnectAsync(new Uri(browserUri), _stop.Token);
                using var browserSend = new SemaphoreSlim(1, 1);
                await Task.WhenAny(ToBrowserAsync(debugger, browser, browserSend), ToDebuggerAsync(browser, debugger, browserSend));
            }
            catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>js-debug's commands go to the browser as they are.</summary>
    private async Task ToBrowserAsync(WebSocket debugger, WebSocket browser, SemaphoreSlim browserSend)
    {
        try
        {
            while (await ReceiveAsync(debugger, _stop.Token) is { } message)
            {
                await SendAsync(browser, message, browserSend, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>The browser's answers and events go to js-debug, except a page's attach event, which waits for the handshake.</summary>
    private async Task ToDebuggerAsync(WebSocket browser, WebSocket debugger, SemaphoreSlim browserSend)
    {
        // The proxy's commands in flight → the page's session, its held event, and whether the flag is registered yet.
        var steps = new Dictionary<int, (string Session, string Event, bool Registering)>();
        try
        {
            while (await ReceiveAsync(browser, _stop.Token) is { } message)
            {
                using var json = JsonDocument.Parse(message);
                var root = json.RootElement;
                if (root.TryGetProperty("id", out var id) && id.GetInt32() >= FirstOwnId)
                {
                    if (!steps.Remove(id.GetInt32(), out var step))
                    {
                        continue;
                    }

                    if (!step.Registering)
                    {
                        // The browser's late answer to the first step: nothing after it reaches js-debug before it. As in
                        // the proxy, the second step goes out as soon as the answer is read, before anything else is passed on.
                        await Task.Delay(SlowAnswer, _stop.Token);
                        var source = new JsonObject { ["source"] = Flag + ";" };
                        steps[await SendOwnAsync(browser, step.Session, "Page.addScriptToEvaluateOnNewDocument", source, browserSend)] = step with { Registering = true };
                    }
                    else
                    {
                        await SendAsync(debugger, step.Event, null, _stop.Token);
                        Interlocked.Increment(ref _handshakes);
                    }

                    continue;
                }

                if (root.TryGetProperty("method", out var method) && method.GetString() == "Target.attachedToTarget"
                    && root.GetProperty("params") is var attached && attached.GetProperty("targetInfo").GetProperty("type").GetString() == "page")
                {
                    var session = attached.GetProperty("sessionId").GetString()!;
                    var expression = new JsonObject { ["expression"] = Flag };
                    steps[await SendOwnAsync(browser, session, "Runtime.evaluate", expression, browserSend)] = (session, message, false);
                    continue;
                }

                await SendAsync(debugger, message, null, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
        }
    }

    private async Task<int> SendOwnAsync(WebSocket browser, string session, string method, JsonObject parameters, SemaphoreSlim browserSend)
    {
        var id = Interlocked.Increment(ref _nextId);
        var command = new JsonObject { ["id"] = id, ["sessionId"] = session, ["method"] = method, ["params"] = parameters };
        await SendAsync(browser, command.ToJsonString(), browserSend, _stop.Token);
        return id;
    }

    private static async Task SendAsync(WebSocket socket, string message, SemaphoreSlim? gate, CancellationToken cancellationToken)
    {
        if (gate is not null)
        {
            await gate.WaitAsync(cancellationToken);
        }

        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            gate?.Release();
        }
    }

    private static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.GetMemory(64 * 1024), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            buffer.Advance(result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(buffer.WrittenSpan);
            }
        }
    }

    /// <summary>The upgrade request's head, read a byte at a time so no frame after it is taken.</summary>
    private async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0)
            {
                throw new IOException("The connection closed before the upgrade request ended.");
            }

            head.Append((char)one[0]);
        }

        return head.ToString();
    }
}
