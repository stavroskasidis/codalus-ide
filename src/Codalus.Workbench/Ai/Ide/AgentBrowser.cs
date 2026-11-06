using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Workbench.Ai.Ide;

/// <summary>
/// The browser an agent drives (ADR 0092): a Chromium of <see cref="Debugging.Browsers"/> started by the IDE with its own
/// profile in the state folder and the Chrome DevTools Protocol on a local port, at the address of the web app the IDE
/// runs or debugs. It reads the page (text or a selector's HTML), takes screenshots, runs JavaScript, navigates, and keeps
/// the page's console messages and uncaught errors. The port listens on the loopback interface only, and the browser
/// stops with the chat session, the workspace or the IDE. One page: the first one the browser opens.
/// <see cref="AttachAsync"/> drives instead a page of a browser someone else started (the page the IDE debugs, whose
/// browser has the protocol on a port too), and leaves that browser running when it lets go.
/// </summary>
public sealed class AgentBrowser : IAsyncDisposable
{
    private const int MaxConsole = 500;
    private readonly Process? _process;
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly List<string> _console = [];
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly object _gate = new();
    private int _nextId;
    private Task? _receive;

    private AgentBrowser(Process? process)
    {
        _process = process;
    }

    /// <summary>Whether the browser still runs.</summary>
    public bool IsRunning => _process is not { HasExited: true } && _socket.State == WebSocketState.Open && !_stop.IsCancellationRequested;

    /// <summary>Whether this drives a browser someone else started (<see cref="AttachAsync"/>).</summary>
    public bool IsAttached => _process is null;

    /// <summary>
    /// Drives a page of the browser whose profile is <paramref name="profileFolder"/>, started by someone else with
    /// <c>--remote-debugging-port</c> (the page the IDE debugs): its port is the first line of the profile's
    /// <c>DevToolsActivePort</c>. The page is the first one whose address starts with <paramref name="origin"/>, else the
    /// first page. Disposing it lets go of the page; the browser keeps running.
    /// </summary>
    public static async Task<AgentBrowser> AttachAsync(string profileFolder, string? origin, TimeSpan wait, CancellationToken cancellationToken)
    {
        var port = await PortAsync(Path.Combine(profileFolder, "DevToolsActivePort"), null, wait, cancellationToken).ConfigureAwait(false);
        var browser = new AgentBrowser(null);
        try
        {
            await browser.ConnectAsync(port, origin, cancellationToken).ConfigureAwait(false);
            return browser;
        }
        catch
        {
            await browser.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts <paramref name="executable"/> with a fresh profile in <paramref name="profileFolder"/> at <paramref name="url"/>,
    /// and connects to its page. Headless when <paramref name="headless"/> (tests and CI).
    /// </summary>
    public static async Task<AgentBrowser> StartAsync(string executable, string profileFolder, string url, bool headless, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(profileFolder);
        var portFile = Path.Combine(profileFolder, "DevToolsActivePort");
        File.Delete(portFile);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[]
        {
            "--remote-debugging-address=127.0.0.1",
            "--remote-debugging-port=0",
            "--user-data-dir=" + profileFolder,
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-features=Translate",
        })
        {
            start.ArgumentList.Add(argument);
        }

        if (headless)
        {
            start.ArgumentList.Add("--headless=new");
        }

        if (OperatingSystem.IsLinux() && Environment.UserName == "root")
        {
            start.ArgumentList.Add("--no-sandbox"); // Chromium refuses to start as root otherwise (containers, CI)
        }

        start.ArgumentList.Add("about:blank");
        var process = Process.Start(start) ?? throw new IdeToolException("The browser didn't start.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var browser = new AgentBrowser(process);
        try
        {
            var port = await PortAsync(portFile, process, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            await browser.ConnectAsync(port, null, cancellationToken).ConfigureAwait(false);
            await browser.NavigateAsync(url, cancellationToken).ConfigureAwait(false);
            return browser;
        }
        catch
        {
            await browser.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The port a browser chose: the first line of its <c>DevToolsActivePort</c> once it listens.</summary>
    private static async Task<int> PortAsync(string portFile, Process? process, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process is { HasExited: true })
            {
                throw new IdeToolException(string.Create(CultureInfo.InvariantCulture, $"The browser exited at once (exit code {process.ExitCode})."));
            }

            try
            {
                if (File.Exists(portFile) && (await File.ReadAllLinesAsync(portFile, cancellationToken).ConfigureAwait(false)) is [var first, ..] && int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port > 0)
                {
                    return port;
                }
            }
            catch (IOException)
            {
                // The browser is writing it.
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new IdeToolException(string.Create(CultureInfo.InvariantCulture, $"The browser didn't open its DevTools port within {wait.TotalSeconds:0} seconds."));
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync(int port, string? origin, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        string? socketUrl = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (socketUrl is null)
        {
            var list = await http.GetStringAsync(new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/json/list")), cancellationToken).ConfigureAwait(false);
            using var targets = JsonDocument.Parse(list);
            var pages = targets.RootElement.EnumerateArray()
                .Where(t => t.TryGetProperty("type", out var type) && type.GetString() == "page" && t.TryGetProperty("webSocketDebuggerUrl", out _))
                .Select(t => (Url: t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "", Socket: t.GetProperty("webSocketDebuggerUrl").GetString()))
                .Where(t => !t.Url.StartsWith("devtools://", StringComparison.Ordinal))
                .ToList();
            socketUrl = (origin is not null ? pages.FirstOrDefault(p => p.Url.StartsWith(origin, StringComparison.OrdinalIgnoreCase)).Socket : null)
                ?? pages.FirstOrDefault().Socket;
            if (socketUrl is null)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new IdeToolException("The browser has no page to drive.");
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        await _socket.ConnectAsync(new Uri(socketUrl), cancellationToken).ConfigureAwait(false);
        _receive = Task.Run(ReceiveAsync, CancellationToken.None);
        await SendAsync("Runtime.enable", null, cancellationToken).ConfigureAwait(false);
        await SendAsync("Log.enable", null, cancellationToken).ConfigureAwait(false);
        await SendAsync("Page.enable", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens <paramref name="url"/> and waits until the document has loaded (at most 30 seconds). A server that doesn't
    /// answer yet (connection refused or reset: a web app still starting) is tried again until
    /// <paramref name="retryFor"/> has passed; any other failure, or that one after the wait, is an
    /// <see cref="IdeToolException"/> that says what the browser said.
    /// </summary>
    public async Task NavigateAsync(string url, CancellationToken cancellationToken, TimeSpan retryFor = default)
    {
        var retryUntil = DateTime.UtcNow + retryFor;
        while (true)
        {
            var result = await SendAsync("Page.navigate", w => w.WriteString("url", url), cancellationToken).ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 } text)
            {
                if (IsNotListeningYet(text) && DateTime.UtcNow < retryUntil)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new IdeToolException($"The browser couldn't open {url}: {text}" + (IsNotListeningYet(text) ? " (nothing answers at that address: is the web app running?)." : "."));
            }

            break;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await EvaluateAsync("document.readyState", cancellationToken).ConfigureAwait(false) is "\"complete\"")
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Chromium's errors for an address where nothing listens yet.</summary>
    internal static bool IsNotListeningYet(string errorText) =>
        errorText.Contains("ERR_CONNECTION_REFUSED", StringComparison.Ordinal)
        || errorText.Contains("ERR_CONNECTION_RESET", StringComparison.Ordinal)
        || errorText.Contains("ERR_CONNECTION_CLOSED", StringComparison.Ordinal)
        || errorText.Contains("ERR_EMPTY_RESPONSE", StringComparison.Ordinal);

    /// <summary>The page's address, title and visible text, or the outer HTML of what <paramref name="selector"/> matches.</summary>
    public async Task<string> GetPageAsync(string? selector, CancellationToken cancellationToken)
    {
        var script = selector is { Length: > 0 }
            ? "(() => { const all = [...document.querySelectorAll(" + JsonSerializer.Serialize(selector, IdeJsonContext.Default.String) + ")]; return all.length === 0 ? 'Nothing matches the selector.' : all.slice(0, 50).map(e => e.outerHTML).join('\\n'); })()"
            : "location.href + '\\n' + document.title + '\\n\\n' + (document.body ? document.body.innerText : '')";
        var value = await EvaluateAsync(script, cancellationToken).ConfigureAwait(false);
        return value.Length > 1 && value[0] == '"' ? JsonSerializer.Deserialize(value, IdeJsonContext.Default.String) ?? "" : value;
    }

    /// <summary>A PNG screenshot of the page's viewport, as base64.</summary>
    public async Task<string> ScreenshotAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync("Page.captureScreenshot", w => w.WriteString("format", "png"), cancellationToken).ConfigureAwait(false);
        return result.GetProperty("data").GetString() ?? "";
    }

    /// <summary>
    /// Runs <paramref name="script"/> (an expression; a promise is awaited) and returns its value as JSON, or what it threw.
    /// </summary>
    public async Task<string> EvaluateAsync(string script, CancellationToken cancellationToken)
    {
        var result = await SendAsync("Runtime.evaluate", w =>
        {
            w.WriteString("expression", script);
            w.WriteBoolean("awaitPromise", true);
            w.WriteBoolean("returnByValue", true);
            w.WriteBoolean("userGesture", true);
        }, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("exceptionDetails", out var exception))
        {
            var text = exception.TryGetProperty("exception", out var e) && e.TryGetProperty("description", out var d) ? d.GetString() : exception.GetProperty("text").GetString();
            throw new IdeToolException("The script threw: " + text);
        }

        var value = result.GetProperty("result");
        return value.TryGetProperty("value", out var v) ? v.GetRawText()
            : value.TryGetProperty("description", out var description) ? description.GetString() ?? ""
            : value.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "";
    }

    /// <summary>The last <paramref name="count"/> console messages and uncaught errors.</summary>
    public IReadOnlyList<string> Console(int count)
    {
        lock (_gate)
        {
            return [.. _console.Skip(Math.Max(0, _console.Count - count))];
        }
    }

    private async Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? parameters, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_gate)
        {
            id = ++_nextId;
            _pending[id] = tcs;
        }

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            w.WriteStartObject("params");
            parameters?.Invoke(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(buffer.ToArray(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IdeToolException($"The browser didn't answer {method}.");
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(id);
            }
        }
    }

    private async Task ReceiveAsync()
    {
        var chunk = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!_stop.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var received = await _socket.ReceiveAsync(chunk, _stop.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(chunk, 0, received.Count);
                if (!received.EndOfMessage)
                {
                    continue;
                }

                Handle(message.ToArray());
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        lock (_gate)
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new IdeToolException("The browser closed."));
            }
        }
    }

    private void Handle(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
        {
            TaskCompletionSource<JsonElement>? tcs;
            lock (_gate)
            {
                _pending.TryGetValue(id, out tcs);
            }

            if (root.TryGetProperty("error", out var error))
            {
                tcs?.TrySetException(new IdeToolException(error.TryGetProperty("message", out var m) ? m.GetString() ?? "The browser refused." : "The browser refused."));
            }
            else
            {
                tcs?.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }

            return;
        }

        var method = root.TryGetProperty("method", out var me) ? me.GetString() : null;
        var p = root.TryGetProperty("params", out var pe) ? pe : default;
        string? line = method switch
        {
            "Runtime.consoleAPICalled" => p.GetProperty("type").GetString() + ": " + string.Join(' ', p.GetProperty("args").EnumerateArray().Select(Describe)),
            "Runtime.exceptionThrown" => "uncaught: " + (p.GetProperty("exceptionDetails").TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d) ? d.GetString() : p.GetProperty("exceptionDetails").GetProperty("text").GetString()),
            "Log.entryAdded" => p.GetProperty("entry").GetProperty("level").GetString() + ": " + p.GetProperty("entry").GetProperty("text").GetString() + (p.GetProperty("entry").TryGetProperty("url", out var url) ? " (" + url.GetString() + ")" : ""),
            _ => null,
        };
        if (line is not null)
        {
            lock (_gate)
            {
                _console.Add(line);
                if (_console.Count > MaxConsole)
                {
                    _console.RemoveAt(0);
                }
            }
        }

        static string Describe(JsonElement arg) =>
            arg.TryGetProperty("value", out var value) ? (value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText())
            : arg.TryGetProperty("description", out var description) ? description.GetString() ?? ""
            : arg.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "";
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, close.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        _socket.Dispose();
        if (_process is { HasExited: false })
        {
            ProcessTree.Kill(_process); // an attached browser is someone else's and keeps running
        }

        if (_receive is not null)
        {
            await _receive.ConfigureAwait(false);
        }

        _process?.Dispose();
        _stop.Dispose();
        _send.Dispose();
    }
}

/// <summary>Source-generated JSON for the IDE tools (Native AOT).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class IdeJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
