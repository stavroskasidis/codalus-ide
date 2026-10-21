using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codalus.Debugging;

/// <summary>
/// vscode-js-debug's DAP server (<c>dapDebugServer.js</c>, on Node) as one debug adapter (ADR 0026). js-debug runs a
/// session per target: the launch session asks for a child session (<c>startDebugging</c>) for each page and worker it
/// attaches to. This class owns the server, the launch session and the children:
/// <list type="bullet">
/// <item>it replays the breakpoints and exception settings to each child before its <c>configurationDone</c>;</item>
/// <item>it routes requests to the child that stopped last, or the first;</item>
/// <item>it hands out its own breakpoint ids and maps the children's onto them, so its user sees one session.</item>
/// </list>
/// </summary>
public sealed partial class JsDebugAdapter : IDebugAdapter
{
    // The first start after a download (a fresh Node.js, scanned on Windows) took over 30 s on a loaded CI runner.
    private static readonly TimeSpan s_startTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _server;
    private readonly int _port;
    private readonly string _adapterId;
    private readonly Session _launch;
    private readonly Lock _gate = new();
    private readonly List<Session> _children = [];
    private readonly Dictionary<string, (SourceBreakpoint[] Specs, int[] Ids)> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private (string[] Filters, ExceptionFilterOptions[]? Options)? _exceptions;
    private Session? _active;
    private int _nextId;

    /// <summary>The page <see cref="LaunchAsync"/> left blank, until a child opens it.</summary>
    private string? _navigateTo;

    private JsDebugAdapter(Process server, int port, string adapterId, TcpClient client)
    {
        _server = server;
        _port = port;
        _adapterId = adapterId;
        _launch = new Session(client);
        _launch.Connection.EventReceived += OnLaunchEventAsync;
        _launch.Connection.ReverseRequestReceived += OnReverseRequestAsync;
        _launch.Connection.HandlerFailed += (_, ex) => Log?.Invoke(this, "Event handler failed: " + ex);
    }

    public Capabilities Capabilities { get; private set; } = new();

    public Task Initialized => _initialized.Task;

    public Task Closed => _launch.Connection.Closed;

    public event Func<DapEvent, Task>? EventReceived;

    public event EventHandler<string>? Log;

    /// <summary>The child sessions (pages and workers) attached so far.</summary>
    public int ChildCount
    {
        get
        {
            lock (_gate)
            {
                return _children.Count;
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="serverScript"/> (<c>js-debug/src/dapDebugServer.js</c>) on <paramref name="node"/>,
    /// connects to it and sends <c>initialize</c>. <paramref name="adapterId"/> is the launch type (<c>pwa-chrome</c>).
    /// </summary>
    public static async Task<JsDebugAdapter> StartAsync(string node, string serverScript, string adapterId, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(serverScript)!,
        };
        start.ArgumentList.Add(serverScript);
        start.ArgumentList.Add("0"); // any free port; the server prints the one it got
        start.ArgumentList.Add("127.0.0.1");
        var process = Process.Start(start) ?? throw new InvalidOperationException($"{node} did not start.");
        var listening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        void OnLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (log)
            {
                log.Add(line);
            }

            if (ListeningLine().Match(line) is { Success: true } match)
            {
                listening.TrySetResult(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        JsDebugAdapter? adapter = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(s_startTimeout);
            var port = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            adapter = new JsDebugAdapter(process, port, adapterId, client);
            process.OutputDataReceived += (_, e) => adapter.OnServerOutput(e.Data);
            process.ErrorDataReceived += (_, e) => adapter.OnServerOutput(e.Data);
            adapter._launch.Connection.Start();
            adapter.Capabilities = await adapter._launch.Connection.RequestAsync(
                "initialize", adapter.InitializeArguments(), Json.InitializeArguments, Json.Capabilities, timeout.Token).ConfigureAwait(false);
            return adapter;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or DapException or DapConnectionLostException)
        {
            if (adapter is not null)
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
            }
            else if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            string output;
            lock (log)
            {
                output = string.Join("\n", log);
            }

            throw new InvalidOperationException($"vscode-js-debug didn't start: {ex.Message}\n{output}".TrimEnd(), ex);
        }
    }

    private static DapJsonContext Json => DapJsonContext.Default;

    private InitializeArguments InitializeArguments() =>
        new("codalus", "Codalus IDE", _adapterId, SupportsStartDebuggingRequest: true);

    private void OnServerOutput(string? line)
    {
        if (line is { Length: > 0 } && !line.Contains("DeprecationWarning", StringComparison.Ordinal))
        {
            Log?.Invoke(this, line);
        }
    }

    /// <summary>
    /// Launches the browser (<c>launch</c> on the launch session). The page's child session follows. Behind the Blazor
    /// WebAssembly debugging proxy (an <c>inspectUri</c>), the page stays blank until that session has started, and
    /// <see cref="OpenPageAsync"/> opens it.
    /// </summary>
    public Task LaunchAsync(BrowserLaunchArguments arguments, CancellationToken cancellationToken)
    {
        if (arguments.InspectUri is not null)
        {
            // The proxy sets globalThis.dotnetDebugger for the page's next documents when the browser reports the page
            // attached, and the page's .NET runtime pauses for the proxy at its start only where it's set. Without that
            // pause the proxy never learns that the runtime is ready, and no C# breakpoint binds. js-debug opens the
            // page right after attaching, and on a loaded machine the app's document came before the proxy's handshake
            // had finished (BlazorWasmTests and the smoke step, under load). js-debug still gets the url: the inspectUri
            // and its path mapping use it.
            lock (_gate)
            {
                _navigateTo = arguments.Url;
            }

            arguments = arguments with { SkipNavigateForTest = true };
        }

        return _launch.Connection.RequestAsync("launch", arguments, Json.BrowserLaunchArguments, cancellationToken);
    }

    // ------------------------------------------------------------------ sessions

    private sealed class Session
    {
        public Session(TcpClient client)
        {
            Client = client;
            var stream = client.GetStream();
            Connection = new DapConnection(stream, stream);
            Connection.Accept("startDebugging");
        }

        public TcpClient Client { get; }

        public DapConnection Connection { get; }

        /// <summary>This session's breakpoint ids → the adapter's.</summary>
        public Dictionary<int, int> Ids { get; } = [];

        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>js-debug answered its start (<c>launch</c> or <c>attach</c>). Read and written under the adapter's gate.</summary>
        public bool Started { get; set; }
    }

    private async Task OnLaunchEventAsync(DapEvent e)
    {
        if (e.Name == DapEvents.Initialized)
        {
            _initialized.TrySetResult();
            return;
        }

        if (EventReceived is { } handler)
        {
            await handler(e).ConfigureAwait(false);
        }
    }

    private Task OnReverseRequestAsync(DapReverseRequest request)
    {
        if (request.Command == "startDebugging" && request.Arguments.ValueKind == JsonValueKind.Object
            && request.Arguments.Deserialize(Json.StartDebuggingArguments) is { } arguments)
        {
            // Off the event loop: the child's own events must keep flowing while it is configured.
            _ = Task.Run(() => AttachChildAsync(arguments));
        }

        return Task.CompletedTask;
    }

    /// <summary>A target js-debug attached to (a page, a worker): its own connection, configured like the session.</summary>
    private async Task AttachChildAsync(StartDebuggingArguments arguments)
    {
        Session? child = null;
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, _port).ConfigureAwait(false);
            child = new Session(client);
            var session = child;
            session.Connection.EventReceived += e => OnChildEventAsync(session, e);
            session.Connection.ReverseRequestReceived += OnReverseRequestAsync;
            session.Connection.HandlerFailed += (_, ex) => Log?.Invoke(this, "Event handler failed: " + ex);
            session.Connection.Start();
            await session.Connection.RequestAsync("initialize", InitializeArguments(), Json.InitializeArguments, Json.Capabilities).ConfigureAwait(false);
            var started = session.Connection.RequestAsync(arguments.Request, arguments.Configuration, Json.JsonElement);
            await ((Task)session.Initialized.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            lock (_gate)
            {
                _children.Add(session);
                _active ??= session;
            }

            await ReplayAsync(session).ConfigureAwait(false);
            await session.Connection.RequestAsync("configurationDone", new EmptyArguments(), Json.EmptyArguments).ConfigureAwait(false);
            await started.ConfigureAwait(false);
            lock (_gate)
            {
                session.Started = true;
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or DapException or DapConnectionLostException or ObjectDisposedException)
        {
            Log?.Invoke(this, "A browser target couldn't be attached: " + ex.Message);
            if (child is not null)
            {
                Remove(child);
                child = null;
            }
        }

        await OpenPageAsync(child).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the page <see cref="LaunchAsync"/> left blank, once, through <paramref name="started"/>, a child whose start
    /// js-debug has answered. js-debug answers it after the browser has answered the page session's <c>Debugger.enable</c>,
    /// so after the proxy's first handshake step, which went out before it; the proxy sends the second step (the flag for
    /// new documents) as soon as it reads that answer. So the flag is registered before this navigation reaches the
    /// browser, and the breakpoints sent while the page was blank carry over to its document. When the child's attach
    /// failed (null), another started child opens the page, or one still starting does once it has; with none, the Debug
    /// Console says the page couldn't be opened.
    /// </summary>
    private async Task OpenPageAsync(Session? started)
    {
        string? url;
        lock (_gate)
        {
            started ??= _children.FirstOrDefault(c => c.Started);
            if (started is null && _children.Count > 0)
            {
                return;
            }

            url = _navigateTo;
            _navigateTo = null;
        }

        if (url is null)
        {
            return;
        }

        if (started is null)
        {
            Log?.Invoke(this, $"The page {url} couldn't be opened: the browser's page couldn't be attached.");
            return;
        }

        try
        {
            // location.replace: about:blank stays out of the page's history. JsonEncodedText makes the url a string
            // literal without reflection (Native AOT).
            await started.Connection.RequestAsync(
                "evaluate", new EvaluateArguments($"location.replace(\"{JsonEncodedText.Encode(url)}\")", null, "repl"), Json.EvaluateArguments, Json.EvaluateResponse).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or DapException or DapConnectionLostException or ObjectDisposedException)
        {
            Log?.Invoke(this, $"The page {url} couldn't be opened: {ex.Message}");
        }
    }

    /// <summary>Sends the session's breakpoints and exception settings to a new child, and reports their states.</summary>
    private async Task ReplayAsync(Session child)
    {
        List<(string Path, SourceBreakpoint[] Specs, int[] Ids)> files;
        (string[] Filters, ExceptionFilterOptions[]? Options)? exceptions;
        lock (_gate)
        {
            files = [.. _breakpoints.Select(b => (b.Key, b.Value.Specs, b.Value.Ids))];
            exceptions = _exceptions;
        }

        foreach (var (path, specs, ids) in files)
        {
            foreach (var breakpoint in await SendBreakpointsAsync(child, path, specs, ids).ConfigureAwait(false))
            {
                await RaiseAsync(DapEvents.Breakpoint, JsonSerializer.SerializeToElement(new BreakpointEvent("changed", breakpoint), Json.BreakpointEvent)).ConfigureAwait(false);
            }
        }

        if (exceptions is { } settings)
        {
            await SendExceptionsAsync(child, settings.Filters, settings.Options).ConfigureAwait(false);
        }
    }

    private async Task<Breakpoint[]> SendBreakpointsAsync(Session child, string path, SourceBreakpoint[] specs, int[] ids)
    {
        var response = await child.Connection.RequestAsync(
            "setBreakpoints", new SetBreakpointsArguments(new Source(Path.GetFileName(path), path), specs), Json.SetBreakpointsArguments, Json.SetBreakpointsResponse).ConfigureAwait(false);
        var result = new Breakpoint[ids.Length];
        lock (_gate)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var breakpoint = i < response.Breakpoints.Length ? response.Breakpoints[i] : new Breakpoint(Verified: false);
                if (breakpoint.Id is { } childId)
                {
                    child.Ids[childId] = ids[i];
                }

                result[i] = breakpoint with { Id = ids[i] };
            }
        }

        return result;
    }

    private Task<JsonElement> SendExceptionsAsync(Session child, string[] filters, ExceptionFilterOptions[]? options)
    {
        // Only the filters js-debug offers (all, uncaught); the others are netcoredbg's.
        var supported = Capabilities.ExceptionBreakpointFilters?.Select(f => f.Filter).ToHashSet(StringComparer.Ordinal) ?? [];
        return child.Connection.RequestAsync(
            "setExceptionBreakpoints",
            new SetExceptionBreakpointsArguments([.. filters.Where(supported.Contains)], options?.Where(o => supported.Contains(o.FilterId)).ToArray()),
            Json.SetExceptionBreakpointsArguments);
    }

    private async Task OnChildEventAsync(Session child, DapEvent e)
    {
        switch (e.Name)
        {
            case DapEvents.Initialized:
                child.Initialized.TrySetResult();
                return;
            case DapEvents.Terminated or DapEvents.Exited:
                // A page or worker went away; the launch session reports the end of the whole session.
                Remove(child);
                return;
            case DapEvents.Breakpoint when DebugAdapter.Read(e.Body, Json.BreakpointEvent) is { Breakpoint.Id: { } childId } changed:
                int ours;
                lock (_gate)
                {
                    if (!child.Ids.TryGetValue(childId, out ours))
                    {
                        return; // one js-debug set itself
                    }
                }

                await RaiseAsync(e.Name, JsonSerializer.SerializeToElement(changed with { Breakpoint = changed.Breakpoint with { Id = ours } }, Json.BreakpointEvent)).ConfigureAwait(false);
                return;
            case DapEvents.Stopped when DebugAdapter.Read(e.Body, Json.StoppedEvent) is { } stopped:
                int[]? hits;
                lock (_gate)
                {
                    _active = child;
                    hits = stopped.HitBreakpointIds?.Select(id => child.Ids.GetValueOrDefault(id)).Where(id => id != 0).ToArray();
                }

                await RaiseAsync(e.Name, JsonSerializer.SerializeToElement(stopped with { HitBreakpointIds = hits }, Json.StoppedEvent)).ConfigureAwait(false);
                return;
            default:
                await RaiseAsync(e.Name, e.Body).ConfigureAwait(false);
                return;
        }
    }

    private Task RaiseAsync(string name, JsonElement body) =>
        EventReceived?.Invoke(new DapEvent(name, body)) ?? Task.CompletedTask;

    private void Remove(Session child)
    {
        lock (_gate)
        {
            _children.Remove(child);
            if (_active == child)
            {
                _active = _children.Count > 0 ? _children[0] : null;
            }
        }
    }

    private DapConnection Active(string command)
    {
        lock (_gate)
        {
            return (_active ?? throw new DapException(command, "The browser isn't attached yet.")).Connection;
        }
    }

    // ------------------------------------------------------------------ requests

    public Task ConfigurationDoneAsync(CancellationToken cancellationToken) =>
        _launch.Connection.RequestAsync("configurationDone", new EmptyArguments(), Json.EmptyArguments, cancellationToken);

    public async Task<Breakpoint[]> SetBreakpointsAsync(string path, SourceBreakpoint[] breakpoints, CancellationToken cancellationToken)
    {
        int[] ids;
        List<Session> children;
        lock (_gate)
        {
            ids = [.. breakpoints.Select(_ => ++_nextId)];
            _breakpoints[path] = (breakpoints, ids);
            children = [.. _children];
        }

        Breakpoint[]? result = null;
        foreach (var child in children)
        {
            var states = await SendBreakpointsAsync(child, path, breakpoints, ids).ConfigureAwait(false);
            result ??= states;
        }

        return result ?? [.. breakpoints.Select((b, i) => new Breakpoint(ids[i], Verified: false, Message: "Waiting for the browser.", Line: b.Line))];
    }

    /// <summary>js-debug has no function breakpoints (its capabilities say so): none bind.</summary>
    public Task<Breakpoint[]> SetFunctionBreakpointsAsync(FunctionBreakpoint[] breakpoints, CancellationToken cancellationToken) =>
        Task.FromResult<Breakpoint[]>([.. breakpoints.Select(_ => new Breakpoint(Verified: false, Message: "Function breakpoints aren't supported in the browser."))]);

    public async Task SetExceptionBreakpointsAsync(string[] filters, CancellationToken cancellationToken, ExceptionFilterOptions[]? options = null)
    {
        List<Session> children;
        lock (_gate)
        {
            _exceptions = (filters, options);
            children = [.. _children];
        }

        foreach (var child in children)
        {
            await SendExceptionsAsync(child, filters, options).ConfigureAwait(false);
        }
    }

    public Task ContinueAsync(int threadId, CancellationToken cancellationToken) =>
        Active("continue").RequestAsync("continue", new ThreadArguments(threadId), Json.ThreadArguments, cancellationToken);

    public Task NextAsync(int threadId, CancellationToken cancellationToken) =>
        Active("next").RequestAsync("next", new ThreadArguments(threadId), Json.ThreadArguments, cancellationToken);

    public Task StepInAsync(int threadId, CancellationToken cancellationToken) =>
        Active("stepIn").RequestAsync("stepIn", new ThreadArguments(threadId), Json.ThreadArguments, cancellationToken);

    public Task StepOutAsync(int threadId, CancellationToken cancellationToken) =>
        Active("stepOut").RequestAsync("stepOut", new ThreadArguments(threadId), Json.ThreadArguments, cancellationToken);

    public Task PauseAsync(int threadId, CancellationToken cancellationToken) =>
        Active("pause").RequestAsync("pause", new ThreadArguments(threadId), Json.ThreadArguments, cancellationToken);

    public async Task<DapThread[]> ThreadsAsync(CancellationToken cancellationToken)
    {
        DapConnection? active;
        lock (_gate)
        {
            active = _active?.Connection;
        }

        return active is null
            ? []
            : (await active.RequestAsync("threads", new EmptyArguments(), Json.EmptyArguments, Json.ThreadsResponse, cancellationToken).ConfigureAwait(false)).Threads;
    }

    public Task<StackTraceResponse> StackTraceAsync(int threadId, int levels, CancellationToken cancellationToken) =>
        Active("stackTrace").RequestAsync("stackTrace", new StackTraceArguments(threadId, 0, levels), Json.StackTraceArguments, Json.StackTraceResponse, cancellationToken);

    public async Task<Scope[]> ScopesAsync(int frameId, CancellationToken cancellationToken) =>
        (await Active("scopes").RequestAsync("scopes", new ScopesArguments(frameId), Json.ScopesArguments, Json.ScopesResponse, cancellationToken).ConfigureAwait(false)).Scopes;

    public async Task<Variable[]> VariablesAsync(int variablesReference, CancellationToken cancellationToken) =>
        (await Active("variables").RequestAsync("variables", new VariablesArguments(variablesReference), Json.VariablesArguments, Json.VariablesResponse, cancellationToken).ConfigureAwait(false)).Variables;

    public Task<EvaluateResponse> EvaluateAsync(string expression, int? frameId, string context, CancellationToken cancellationToken) =>
        Active("evaluate").RequestAsync("evaluate", new EvaluateArguments(expression, frameId, context), Json.EvaluateArguments, Json.EvaluateResponse, cancellationToken);

    public Task<SetVariableResponse> SetVariableAsync(int variablesReference, string name, string value, CancellationToken cancellationToken) =>
        Active("setVariable").RequestAsync("setVariable", new SetVariableArguments(variablesReference, name, value), Json.SetVariableArguments, Json.SetVariableResponse, cancellationToken);

    public Task<SetExpressionResponse> SetExpressionAsync(string expression, string value, int? frameId, CancellationToken cancellationToken) =>
        Active("setExpression").RequestAsync("setExpression", new SetExpressionArguments(expression, value, frameId), Json.SetExpressionArguments, Json.SetExpressionResponse, cancellationToken);

    public Task<ExceptionInfoResponse> ExceptionInfoAsync(int threadId, CancellationToken cancellationToken) =>
        Active("exceptionInfo").RequestAsync("exceptionInfo", new ThreadArguments(threadId), Json.ThreadArguments, Json.ExceptionInfoResponse, cancellationToken);

    /// <summary>Ends every session; terminating closes the browser js-debug launched.</summary>
    public async Task DisconnectAsync(bool terminateDebuggee, CancellationToken cancellationToken)
    {
        List<Session> children;
        lock (_gate)
        {
            children = [.. _children];
        }

        foreach (var connection in children.Select(c => c.Connection).Append(_launch.Connection))
        {
            try
            {
                await connection.RequestAsync("disconnect", new DisconnectArguments(terminateDebuggee), Json.DisconnectArguments, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DapConnectionLostException or DapException or TimeoutException)
            {
                // Already gone.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Session> children;
        lock (_gate)
        {
            children = [.. _children];
            _children.Clear();
            _active = null;
        }

        foreach (var session in children.Append(_launch))
        {
            await session.Connection.DisposeAsync().ConfigureAwait(false);
            session.Client.Dispose();
        }

        try
        {
            if (!_server.HasExited)
            {
                _server.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
        }

        _server.Dispose();
    }

    /// <summary>"Debug server listening at 127.0.0.1:59519".</summary>
    [GeneratedRegex(@"listening at [^\s:]+:(\d+)")]
    private static partial Regex ListeningLine();
}
