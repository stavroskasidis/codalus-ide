using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Workbench.Debugging;

/// <summary>A delta as the browser's Hot Reload agent takes it (byte arrays in base64).</summary>
public sealed record BrowserDelta(Guid ModuleId, byte[] MetadataDelta, byte[] IlDelta, byte[] PdbDelta, int[] UpdatedTypes);

/// <summary><c>ApplyManagedCodeUpdates</c>: the deltas for every page, and the secret that shows them it is the IDE.</summary>
public sealed record BrowserApplyMessage(string Type, string? SharedSecret, int UpdateId, BrowserDelta[] Deltas, int ResponseLoggingLevel);

/// <summary>A message without data: <c>RefreshBrowser</c>, <c>GetApplyUpdateCapabilities</c>.</summary>
public sealed record BrowserMessage(string Type);

public sealed record BrowserLogEntry(string Message, int Severity);

/// <summary>A page's answer to <see cref="BrowserApplyMessage"/>.</summary>
public sealed record BrowserApplyAnswer(bool Success, BrowserLogEntry[]? Log);

/// <summary>
/// What the pages made of an edit: how many were open, the errors of those that couldn't apply it, and how many didn't
/// answer in time (a page paused at a breakpoint, or slow to apply, answers later: <see cref="BrowserRefreshServer.LateAnswer"/>).
/// </summary>
public sealed record BrowserApplyResult(int Pages, IReadOnlyList<string> Errors, int Unanswered = 0)
{
    public static readonly BrowserApplyResult NoPage = new(0, []);
}

/// <summary>A page's answer to an edit that came after <see cref="BrowserRefreshServer.ApplyAsync"/> stopped waiting: null <see cref="Error"/> is applied.</summary>
public sealed record BrowserLateAnswer(int UpdateId, string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserApplyMessage))]
[JsonSerializable(typeof(BrowserMessage))]
[JsonSerializable(typeof(BrowserApplyAnswer))]
internal sealed partial class BrowserRefreshJsonContext : JsonSerializerContext;

/// <summary>
/// The IDE as a Blazor Web App's browser refresh server (ADR 0090), the part of <c>dotnet watch</c> that the server's
/// <c>Microsoft.AspNetCore.Watch.BrowserRefresh</c> middleware (the user's SDK's, loaded as a hosting startup) points
/// each page at: the middleware injects <c>aspnetcore-browser-refresh.js</c>, which turns on the WebAssembly runtime's
/// Hot Reload agent (.NET 10) and connects here (<c>ASPNETCORE_AUTO_RELOAD_WS_ENDPOINT</c>). A page proves it got the
/// script from the server by a secret it encrypts with this server's public key (<c>ASPNETCORE_AUTO_RELOAD_WS_KEY</c>),
/// which the server sends back with each update, as <c>dotnet watch</c> does. <see cref="ApplyAsync"/> sends every
/// page an edit's deltas (<c>ApplyManagedCodeUpdates</c>) and waits for each answer; the script also posts them to the
/// middleware, so a page opened or reloaded later gets them. Plain HTTP on the loopback interface.
/// </summary>
public sealed class BrowserRefreshServer : IAsyncDisposable
{
    public const string EndpointVariable = "ASPNETCORE_AUTO_RELOAD_WS_ENDPOINT";
    public const string KeyVariable = "ASPNETCORE_AUTO_RELOAD_WS_KEY";

    private static readonly TimeSpan s_teardownLimit = TimeSpan.FromSeconds(15);

    /// <summary>How many free ports <see cref="Start"/> tries when another listener took the one it chose first.</summary>
    private const int PortAttempts = 5;

    private readonly RSA _key = RSA.Create(2048);
    private readonly ConcurrentDictionary<WebSocket, Page> _pages = new();
    private readonly CancellationTokenSource _lifetime = new();
    private HttpListener _listener = new();
    private Task _loop = Task.CompletedTask;
    private int _nextUpdate;
    private int _connections;
    private int _disposed;

    private BrowserRefreshServer()
    {
        Port = Containers.ContainerLaunch.FreePort();
        _listener.Prefixes.Add(Prefix(Port));
    }

    private static string Prefix(int port) => string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}/");

    public int Port { get; private set; }

    /// <summary>The pages connected now.</summary>
    public int Pages => _pages.Count;

    /// <summary>How many times a page connected: each load and reload of a page connects again.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>What happened (pages that connected or were refused), for the Debug Console.</summary>
    public event Action<string>? Log;

    /// <summary>A page answered an edit after the wait for it ended (on any thread).</summary>
    public event Action<BrowserLateAnswer>? LateAnswer;

    /// <summary>Listens on a free loopback port.</summary>
    public static BrowserRefreshServer Start()
    {
        var server = new BrowserRefreshServer();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                server._listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < PortAttempts)
            {
                // Free when chosen, but anything can take it before the listener binds it.
                CloseListener(server._listener);
                server.Port = Containers.ContainerLaunch.FreePort();
                server._listener = new HttpListener();
                server._listener.Prefixes.Add(Prefix(server.Port));
            }
        }

        server._loop = Task.Run(() => server.AcceptAsync(server._lifetime.Token));
        return server;
    }

    /// <summary>The variables that point the server's middleware, and through it its pages, here.</summary>
    public IReadOnlyDictionary<string, string> Environment() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [EndpointVariable] = string.Create(CultureInfo.InvariantCulture, $"ws://localhost:{Port}"),
        [KeyVariable] = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
    };

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => ConnectAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ConnectAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            // The script offers its encrypted secret as the WebSocket's subprotocol, which the answer has to repeat.
            var protocol = context.Request.Headers["Sec-WebSocket-Protocol"];
            if (!context.Request.IsWebSocketRequest || Secret(protocol) is not { } secret)
            {
                Log?.Invoke("Hot Reload: refused a browser connection without the server's secret.");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }

            var socket = (await context.AcceptWebSocketAsync(protocol).ConfigureAwait(false)).WebSocket;
            var page = new Page(socket, context, secret);
            _pages[socket] = page;
            var connection = Interlocked.Increment(ref _connections);
            Log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"Hot Reload: a page connected (connection {connection}, {_pages.Count} open)."));
            try
            {
                await page.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pages.TryRemove(socket, out _);
                Log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"Hot Reload: a page left ({_pages.Count} open)."));
                page.Close();
                page.Dispose();
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or WebSocketException or InvalidOperationException or OperationCanceledException)
        {
        }
    }

    /// <summary>The page's secret, decrypted with the private key, as the script compares it (base64); null when it isn't one.</summary>
    private string? Secret(string? protocol)
    {
        if (string.IsNullOrEmpty(protocol))
        {
            return null;
        }

        try
        {
            return Convert.ToBase64String(_key.Decrypt(Convert.FromBase64String(Uri.UnescapeDataString(protocol)), RSAEncryptionPadding.OaepSHA256));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends every open page an edit's deltas and waits up to <paramref name="timeout"/> for each to apply them. A page
    /// whose Blazor hasn't started yet applies them as it starts (the script posted them to the middleware) and says so
    /// as a success. A page that doesn't answer in time is counted, not taken for a failure, and its answer comes as a
    /// <see cref="LateAnswer"/>: under load, applying took a page more than ten seconds (Windows, a CPU-bound run).
    /// </summary>
    public async Task<BrowserApplyResult> ApplyAsync(IReadOnlyList<HotReloadModuleUpdate> updates, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pages = _pages.Values.ToArray();
        if (pages.Length == 0)
        {
            return BrowserApplyResult.NoPage;
        }

        var id = Interlocked.Increment(ref _nextUpdate);
        var deltas = updates.Select(u => new BrowserDelta(u.ModuleId, u.MetadataDelta, u.IlDelta, u.PdbDelta, u.UpdatedTypes)).ToArray();
        var answers = await Task.WhenAll(pages.Select(async Task<(bool Answered, string? Error)> (page) =>
        {
            var message = new BrowserApplyMessage("ApplyManagedCodeUpdates", page.Secret, id, deltas, ResponseLoggingLevel: 0);
            var answer = await page.RequestAsync(JsonSerializer.SerializeToUtf8Bytes(message, BrowserRefreshJsonContext.Default.BrowserApplyMessage), cancellationToken).ConfigureAwait(false);
            try
            {
                return await answer.WaitAsync(timeout, cancellationToken).ConfigureAwait(false) is { } text ? (true, Error(text)) : (false, null);
            }
            catch (TimeoutException)
            {
                _ = answer.ContinueWith(late =>
                {
                    if (late.Result is { } text)
                    {
                        LateAnswer?.Invoke(new BrowserLateAnswer(id, Error(text)));
                    }
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                return (false, null);
            }
        })).ConfigureAwait(false);
        return new BrowserApplyResult(pages.Length, [.. answers.Select(a => a.Error).OfType<string>()], answers.Count(a => !a.Answered));
    }

    /// <summary>A page's answer to an edit: null when it applied it, else why not.</summary>
    private static string? Error(string answer)
    {
        try
        {
            var applied = JsonSerializer.Deserialize(answer, BrowserRefreshJsonContext.Default.BrowserApplyAnswer);
            return applied is { Success: true } ? null : string.Join(" ", applied?.Log?.Select(l => l.Message) ?? ["The page couldn't apply the edit."]);
        }
        catch (JsonException)
        {
            return "The page's answer wasn't understood: " + answer;
        }
    }

    /// <summary>
    /// <c>RefreshBrowser</c>: each page that Blazor runs refreshes what the server rendered statically (an enhanced
    /// navigation to the same page), which the server's handlers have just made run the new code.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var message = JsonSerializer.SerializeToUtf8Bytes(new BrowserMessage("RefreshBrowser"), BrowserRefreshJsonContext.Default.BrowserMessage);
        foreach (var page in _pages.Values.ToArray())
        {
            await page.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What the first open page's WebAssembly runtime can apply (<c>GetApplyUpdateCapabilities</c>), or null.</summary>
    public async Task<string?> CapabilitiesAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_pages.Values.FirstOrDefault() is not { } page)
        {
            return null;
        }

        var message = JsonSerializer.SerializeToUtf8Bytes(new BrowserMessage("GetApplyUpdateCapabilities"), BrowserRefreshJsonContext.Default.BrowserMessage);
        var answer = await page.RequestAsync(message, cancellationToken).ConfigureAwait(false);
        try
        {
            return await answer.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Closes a listener without throwing; only <see cref="HttpListener.Close"/> (see <c>IdeExecutionServer</c>).</summary>
    private static void CloseListener(HttpListener listener)
    {
        try
        {
            listener.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Bounded and on the thread pool, as IdeExecutionServer's: an HttpListener WebSocket's abort can block on Windows.
        var teardown = Task.Run(async () =>
        {
            try
            {
                await _lifetime.CancelAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            CloseListener(_listener);
            foreach (var page in _pages.Values)
            {
                page.Close();
            }

            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        });
        try
        {
            await teardown.WaitAsync(s_teardownLimit).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return; // left to close in the background, with the lifetime it still uses
        }

        _lifetime.Dispose();
        _key.Dispose();
    }

    /// <summary>One connected page: messages out, its answers back in order (each request has one).</summary>
    private sealed class Page(WebSocket socket, HttpListenerContext context, string secret) : IDisposable
    {
        private readonly SemaphoreSlim _sending = new(1, 1);
        private readonly ConcurrentQueue<TaskCompletionSource<string?>> _waiting = new();

        public string Secret { get; } = secret;

        public async Task ReadAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                if (_waiting.TryDequeue(out var answer))
                {
                    answer.TrySetResult(text);
                }
            }

            while (_waiting.TryDequeue(out var answer))
            {
                answer.TrySetResult(null);
            }
        }

        /// <summary>Sends <paramref name="message"/>; the task it returns completes with the page's answer (null when the page closed first).</summary>
        public async Task<Task<string?>> RequestAsync(byte[] message, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _waiting.Enqueue(answer);
                await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                answer.TrySetResult(null);
            }
            finally
            {
                _sending.Release();
            }

            return answer.Task;
        }

        public async Task SendAsync(byte[] message, CancellationToken cancellationToken)
        {
            await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                _sending.Release();
            }
        }

        /// <summary>Aborts the socket and its connection: on Windows http.sys keeps an aborted WebSocket's connection otherwise.</summary>
        public void Close()
        {
            try
            {
                socket.Abort();
                socket.Dispose();
                context.Response.Abort();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException)
            {
            }
        }

        public void Dispose() => _sending.Dispose();
    }
}
