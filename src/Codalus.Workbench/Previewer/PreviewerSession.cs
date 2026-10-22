using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Codalus.Workbench.Previewer;

/// <summary>How to start a designer host for an app: Avalonia's (ADR 0048) or the WPF previewer's (M40b, ADR 0072).</summary>
/// <param name="HostApp">Avalonia's <c>Avalonia.Designer.HostApp.dll</c> (the app's <c>AvaloniaPreviewerNetCoreToolPath</c>), or <c>codalus-wpfpreviewer.dll</c>.</param>
/// <param name="AppAssembly">The executable project's output (<c>TargetPath</c>); its runtimeconfig and deps.json start the host.</param>
/// <param name="Agent">The previewer agent (a startup hook), or null to preview without selection and theme switching.</param>
public sealed record PreviewerLaunch(string HostApp, string AppAssembly, string? Agent)
{
    /// <summary>
    /// The designer host serves the agent's commands itself (the WPF previewer): it gets the agent's port and token,
    /// and no startup hook.
    /// </summary>
    public bool AgentInHost { get; init; }

    /// <summary>The <c>dotnet</c> that runs it (default: <c>DOTNET_HOST_PATH</c>, else <c>dotnet</c> on the PATH).</summary>
    public string? Dotnet { get; init; }

    public string RuntimeConfig => Path.ChangeExtension(AppAssembly, ".runtimeconfig.json");

    public string DepsFile => Path.ChangeExtension(AppAssembly, ".deps.json");

    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>A rendered preview: pixels in <see cref="Format"/>, and the scale it was rendered at.</summary>
public sealed record PreviewFrame(long SequenceId, PreviewPixelFormat Format, byte[] Data, int Width, int Height, int Stride, double Scale);

/// <summary>An update's outcome: <see cref="Error"/> (with a 1-based position when the XAML loader gave one) or the frame that shows it.</summary>
public sealed record PreviewUpdateResult(string? Error, int? Line, int? Column, PreviewFrame? Frame, TimeSpan Elapsed)
{
    public bool Succeeded => Error is null;
}

/// <summary>A point in the preview hit an element: its bounds (device-independent pixels) and its path from the previewed window.</summary>
public sealed record PreviewHit(PreviewRect Bounds, IReadOnlyList<PreviewPathStep> Path);

public readonly record struct PreviewRect(double X, double Y, double Width, double Height);

/// <summary>One step of an element's logical path: its type's name, its index among its logical siblings of that type, its name.</summary>
public sealed record PreviewPathStep(string Type, int Ordinal, string? Name = null);

/// <summary>
/// One designer host process (ADR 0048): the user's app started out of process under Avalonia's
/// <c>Avalonia.Designer.HostApp</c> (or the WPF previewer's host, which speaks the same protocol; ADR 0072), connected back over the remote protocol on a local TCP port (XAML in, frames out),
/// and the previewer agent on a second port (hit testing, bounds, theme). Every wait has a timeout, and the process
/// ends with the session. It runs the user's code: callers start it for trusted folders only.
/// </summary>
public sealed class PreviewerSession : IAsyncDisposable
{
    public const string AgentPortVariable = "CODALUS_PREVIEW_AGENT_PORT";
    public const string AgentTokenVariable = "CODALUS_PREVIEW_AGENT_TOKEN";
    public const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";

    // An update's answer comes in well under a second once the designer runs (a cold first load takes a few).
    private static readonly TimeSpan s_resendAfter = TimeSpan.FromSeconds(10);

    private readonly Process _process;
    private readonly TcpListener _listener;
    private readonly TcpListener _agentListener;
    private readonly string _sessionId;
    private readonly string _agentToken;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _output = [];
    private Stream? _stream;
    private TaskCompletionSource<UpdateXamlResultMessage>? _pendingResult;
    private TaskCompletionSource<PreviewFrame>? _pendingFrame;
    private bool _resultSeen;
    private long _updatesSent;
    private long _resultsReceived;
    private PreviewAgentConnection? _agent;
    private readonly TaskCompletionSource<PreviewAgentConnection> _agentConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private double _scale = 1;
    private int _disposed;

    private PreviewerSession(Process process, TcpListener listener, TcpListener agentListener, string sessionId, string agentToken)
    {
        _sessionId = sessionId;
        _agentToken = agentToken;
        _process = process;
        _listener = listener;
        _agentListener = agentListener;
    }

    /// <summary>The designer host's process id.</summary>
    public int ProcessId => _process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>The newest frame, or null before the first.</summary>
    public PreviewFrame? LastFrame { get; private set; }

    /// <summary>The render scale (1 = 96 DPI).</summary>
    public double Scale => _scale;

    /// <summary>A frame arrived (any thread).</summary>
    public event EventHandler<PreviewFrame>? FrameReceived;

    /// <summary>The designer host ended or its connection broke (any thread).</summary>
    public event EventHandler? Exited;

    /// <summary>A line of the host's output or of this session's own log (any thread).</summary>
    public event EventHandler<string>? Log;

    /// <summary>The host's recent output (its last 200 lines), for error messages.</summary>
    public string RecentOutput
    {
        get
        {
            lock (_output)
            {
                return string.Join('\n', _output);
            }
        }
    }

    /// <summary>
    /// Starts the designer host and waits (at most <paramref name="timeout"/>) until it has connected and the session is
    /// set up at <paramref name="scale"/>.
    /// </summary>
    public static async Task<PreviewerSession> StartAsync(PreviewerLaunch launch, double scale, TimeSpan timeout, CancellationToken cancellationToken)
    {
        foreach (var required in new[] { launch.HostApp, launch.AppAssembly, launch.RuntimeConfig, launch.DepsFile })
        {
            if (!File.Exists(required))
            {
                throw new PreviewerException($"The previewer needs {required}, which doesn't exist (build the project).");
            }
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        var agentListener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        agentListener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var agentPort = ((IPEndPoint)agentListener.LocalEndpoint).Port;

        var start = new ProcessStartInfo(launch.Dotnet ?? DefaultDotnet())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(launch.AppAssembly)!,
        };
        var sessionId = Guid.NewGuid().ToString("N");

        // TODO(issue): start on a BuildOutputShadowCopy of the app's output, as the Windows Forms designer does (#66): on the
        // output's own deps file the app's assemblies load from bin/, so on Windows a build while the previewer runs can't replace them.
        foreach (var argument in new[]
                 {
                     "exec", "--runtimeconfig", launch.RuntimeConfig, "--depsfile", launch.DepsFile, launch.HostApp,
                     "--transport", $"tcp-bson://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/",
                     "--session-id", sessionId,
                     "--method", "avalonia-remote",
                     launch.AppAssembly,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        if (launch.Environment is { } environment)
        {
            foreach (var (name, value) in environment)
            {
                start.Environment[name] = value;
            }
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        var agentToken = Guid.NewGuid().ToString("N");
        ApplyAgentEnvironment(start.Environment, launch, agentPort, agentToken);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new PreviewerException("The previewer didn't start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            listener.Stop();
            agentListener.Stop();
            throw new PreviewerException($"The previewer didn't start: {ex.Message}", ex);
        }

        var session = new PreviewerSession(process, listener, agentListener, sessionId, agentToken) { _scale = scale };
        session.Begin();
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var exited = process.WaitForExitAsync(timeoutSource.Token);
            var connected = await Task.WhenAny(session._connected.Task, exited).ConfigureAwait(false);
            if (connected != session._connected.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new PreviewerException(session.HasExited
                    ? $"The previewer exited (code {SafeExitCode(process)}) before it connected.\n{session.RecentOutput}"
                    : $"The previewer didn't connect within {timeout.TotalSeconds:0} s.\n{session.RecentOutput}");
            }

            await session.SendAsync(new ClientSupportedPixelFormatsMessage([PreviewPixelFormat.Rgba8888]), cancellationToken).ConfigureAwait(false);
            await session.SendAsync(new ClientRenderInfoMessage(96 * scale, 96 * scale), cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Where the agent connects: its port and token, with the agent as a startup hook (after any the environment
    /// has), or without one when the designer host serves the agent itself; nothing without an agent.
    /// </summary>
    internal static void ApplyAgentEnvironment(IDictionary<string, string?> environment, PreviewerLaunch launch, int agentPort, string agentToken)
    {
        if (!launch.AgentInHost)
        {
            if (launch.Agent is not { } agent || !File.Exists(agent))
            {
                return;
            }

            var existing = environment.TryGetValue(StartupHooksVariable, out var hooks) ? hooks : null;
            environment[StartupHooksVariable] = string.IsNullOrEmpty(existing) ? agent : agent + Path.PathSeparator + existing;
        }

        environment[AgentPortVariable] = agentPort.ToString(CultureInfo.InvariantCulture);
        environment[AgentTokenVariable] = agentToken;
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string DefaultDotnet() =>
        System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    private void Begin()
    {
        _process.OutputDataReceived += (_, e) => Output(e.Data);
        _process.ErrorDataReceived += (_, e) => Output(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => OnExited();
        _ = AcceptAsync();
        _ = AcceptAgentAsync();
    }

    private void Output(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_output)
        {
            _output.Add(line);
            if (_output.Count > 200)
            {
                _output.RemoveAt(0);
            }
        }

        Log?.Invoke(this, line);
    }

    // ------------------------------------------------------------------ the remote protocol

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var stream = client.GetStream();

                // The first message names the session: anything else on the port isn't our designer host.
                using var first = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(first.Token, _stop.Token);
                PreviewMessage? hello;
                try
                {
                    hello = await RemoteProtocol.ReadAsync(stream, linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
                {
                    hello = null;
                }

                if (hello is not StartDesignerSessionMessage { SessionId: var id } || id != _sessionId)
                {
                    Output($"[previewer] refused a connection that isn't the designer host ({hello?.GetType().Name ?? "nothing"})");
                    client.Dispose();
                    continue;
                }

                _stream = stream;
                _listener.Stop();
                _connected.TrySetResult();
                await ReadLoopAsync(stream).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    private async Task ReadLoopAsync(Stream stream)
    {
        try
        {
            while (await RemoteProtocol.ReadAsync(stream, _stop.Token).ConfigureAwait(false) is { } message)
            {
                switch (message)
                {
                    case UpdateXamlResultMessage result:
                        lock (_gate)
                        {
                            // The designer answers every update in order, and the update's answer is the one to the
                            // last copy sent: an earlier copy's (sent again because its answer was late) comes first,
                            // and the designer loads the copy after it, so returning on it would leave a reload running
                            // under the next request (a hit test on CI found no window).
                            if (++_resultsReceived < _updatesSent)
                            {
                                break;
                            }

                            // Frames before the answer still show the previous XAML.
                            _resultSeen = true;
                            _pendingResult?.TrySetResult(result);
                        }

                        break;
                    case FrameMessage frame:
                        var preview = new PreviewFrame(frame.SequenceId, frame.Format, frame.Data, frame.Width, frame.Height, frame.Stride, frame.DpiX > 0 ? frame.DpiX / 96 : _scale);
                        LastFrame = preview;
                        await SendAsync(new FrameReceivedMessage(frame.SequenceId), _stop.Token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            if (_resultSeen)
                            {
                                _pendingFrame?.TrySetResult(preview);
                            }
                        }

                        FrameReceived?.Invoke(this, preview);
                        break;
                    case RequestViewportResizeMessage or MeasureViewportMessage:
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or InvalidDataException or SocketException)
        {
            if (ex is InvalidDataException)
            {
                Output("[previewer] " + ex.Message);
            }
        }

        OnExited();
    }

    private async Task SendAsync(PreviewMessage message, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new PreviewerException("The previewer isn't connected.");
        var bytes = RemoteProtocol.Serialize(message);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            throw new PreviewerException("The previewer's connection broke.", ex);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Sends <paramref name="xaml"/> and waits for the loader's answer, then (when it loaded) for the frame that shows it.
    /// <paramref name="assemblyPath"/> is the assembly the file belongs to (a library's when previewing its XAML through
    /// an app). Updates run one at a time.
    /// </summary>
    public async Task<PreviewUpdateResult> UpdateXamlAsync(string xaml, string assemblyPath, string? projectPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new TaskCompletionSource<UpdateXamlResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var frame = new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _pendingResult = result;
                _pendingFrame = frame;
                _resultSeen = false;
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            timeoutSource.CancelAfter(timeout);
            var update = new UpdateXamlMessage(xaml, assemblyPath, projectPath);
            UpdateXamlResultMessage answer;
            try
            {
                while (true)
                {
                    lock (_gate)
                    {
                        _updatesSent++;
                    }

                    await SendAsync(update, timeoutSource.Token).ConfigureAwait(false);

                    // Avalonia's designer queues what it sends for a worker that can miss its wake-up
                    // (TransportConnectionWrapper), so an answer can wait there until the designer sends anything else,
                    // and after an error nothing else comes. Sending the update again sends its answer, and the answer
                    // to the copy is the update's (the designer has loaded it by then).
                    using var resend = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token);
                    resend.CancelAfter(s_resendAfter);
                    try
                    {
                        answer = await result.Task.WaitAsync(resend.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (OperationCanceledException) when (!timeoutSource.IsCancellationRequested)
                    {
                        Output("[previewer] no answer to the update yet, sent again");
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PreviewerException(HasExited
                    ? $"The previewer exited while loading the XAML.\n{RecentOutput}"
                    : $"The previewer didn't answer within {timeout.TotalSeconds:0} s.");
            }

            if (answer.Error is { Length: > 0 } || answer.Exception is { Message: { Length: > 0 } })
            {
                var message = answer.Exception?.Message is { Length: > 0 } detail ? detail : answer.Error!;
                if (answer.Exception?.ExceptionType is { Length: > 0 } exceptionType && !message.Contains(exceptionType, StringComparison.Ordinal))
                {
                    message = $"{exceptionType}: {message}";
                }

                return new PreviewUpdateResult(message, answer.Exception?.LineNumber, answer.Exception?.LinePosition, null, watch.Elapsed);
            }

            try
            {
                // Loaded: the frame follows at once (a render), so a long wait means nothing changed on screen.
                using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token);
                frameTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var shown = await frame.Task.WaitAsync(frameTimeout.Token).ConfigureAwait(false);
                return new PreviewUpdateResult(null, null, null, shown, watch.Elapsed);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Loaded, but nothing changed on screen (the same XAML twice): the last frame still shows it.
                return new PreviewUpdateResult(null, null, null, LastFrame, watch.Elapsed);
            }
        }
        finally
        {
            lock (_gate)
            {
                _pendingResult = null;
                _pendingFrame = null;
            }

            _updateGate.Release();
        }
    }

    /// <summary>Renders at <paramref name="scale"/> (1 = 96 DPI) from the next update on.</summary>
    public async Task SetScaleAsync(double scale, CancellationToken cancellationToken)
    {
        _scale = scale;
        await SendAsync(new ClientRenderInfoMessage(96 * scale, 96 * scale), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The space the preview has in the tool window (device-independent pixels).</summary>
    public Task SetViewportAsync(double width, double height, CancellationToken cancellationToken) =>
        SendAsync(new ClientViewportAllocatedMessage(width, height, 96 * _scale, 96 * _scale), cancellationToken);

    /// <summary>Forwards the pointer's position (device-independent pixels), for hover effects.</summary>
    public Task PointerMovedAsync(double x, double y, CancellationToken cancellationToken) =>
        SendAsync(new PointerMovedEventMessage(x, y), cancellationToken);

    // ------------------------------------------------------------------ the agent

    private async Task AcceptAgentAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _agentListener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var connection = new PreviewAgentConnection(client);
                using var first = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(first.Token, _stop.Token);
                if (!await connection.HandshakeAsync(_agentToken, linked.Token).ConfigureAwait(false))
                {
                    Output("[previewer] refused an agent connection with the wrong token");
                    connection.Dispose();
                    continue;
                }

                _agent = connection;
                _agentListener.Stop();
                _agentConnected.TrySetResult(connection);
                return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    /// <summary>The agent, once it has connected (at most <paramref name="timeout"/>), or null without one.</summary>
    private async Task<PreviewAgentConnection?> AgentAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_agent is { } agent)
        {
            return agent;
        }

        try
        {
            return await _agentConnected.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The element at a point of the preview (device-independent pixels), or null.</summary>
    public async Task<PreviewHit?> HitTestAsync(double x, double y, CancellationToken cancellationToken)
    {
        if (await AgentAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false) is not { } agent)
        {
            return null;
        }

        var reply = await agent.RequestAsync(["hit", Format(x), Format(y)], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return ParseHit(reply);
    }

    /// <summary>The bounds of the element at <paramref name="path"/> (from the XAML's root), or null.</summary>
    public async Task<PreviewRect?> BoundsAsync(IReadOnlyList<PreviewPathStep> path, CancellationToken cancellationToken)
    {
        if (path.Count == 0 || await AgentAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false) is not { } agent)
        {
            return null;
        }

        var reply = await agent.RequestAsync(["bounds", string.Join('/', path.Select(s => $"{s.Type}:{s.Ordinal.ToString(CultureInfo.InvariantCulture)}"))], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return ParseRect(reply);
    }

    /// <summary>Sets the application's theme variant (<c>Default</c>, <c>Light</c> or <c>Dark</c>); false without an agent.</summary>
    public async Task<bool> SetThemeAsync(string variant, CancellationToken cancellationToken)
    {
        if (await AgentAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false) is not { } agent)
        {
            return false;
        }

        await agent.RequestAsync(["theme", variant], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    internal static PreviewHit? ParseHit(string reply)
    {
        var parts = reply.Split('\t');
        if (parts.Length < 2 || parts[1].Length == 0 || ParseRect(parts[0]) is not { } bounds)
        {
            return null;
        }

        var steps = parts[1].Split('/').Select(s => s.Split(':')).Select(p => new PreviewPathStep(
            p[0],
            p.Length > 1 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal) ? ordinal : 0,
            p.Length > 2 && p[2].Length > 0 ? p[2] : null)).ToList();
        return new PreviewHit(bounds, steps);
    }

    internal static PreviewRect? ParseRect(string text)
    {
        var values = text.Split(',');
        if (values.Length != 4)
        {
            return null;
        }

        var numbers = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return new PreviewRect(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    // ------------------------------------------------------------------ lifetime

    private void OnExited()
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 0)
        {
            lock (_gate)
            {
                _pendingResult?.TrySetException(new PreviewerException($"The previewer exited.\n{RecentOutput}"));
                _pendingFrame?.TrySetCanceled();
            }

            Exited?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _agentListener.Stop();
        _agent?.Dispose();
        _stream?.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
        }

        _process.Dispose();
        _stop.Dispose();
    }
}

/// <summary>The previewer agent's connection: tab-separated request and reply lines with request ids.</summary>
internal sealed class PreviewAgentConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextId;

    public PreviewAgentConnection(TcpClient client)
    {
        _client = client;
        var stream = client.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    public async Task<bool> HandshakeAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return line == "hello\t" + token;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Sends a request and returns its reply's payload; throws <see cref="PreviewerException"/> for an error reply or a timeout.</summary>
    public async Task<string> RequestAsync(IReadOnlyList<string> request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await _gate.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        try
        {
            var id = (++_nextId).ToString(CultureInfo.InvariantCulture);
            await _writer.WriteLineAsync((id + "\t" + string.Join('\t', request)).AsMemory(), timeoutSource.Token).ConfigureAwait(false);
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false)
                    ?? throw new PreviewerException("The previewer agent disconnected.");
                var parts = line.Split('\t', 3);
                if (parts.Length < 2 || parts[0] != id)
                {
                    continue; // a late reply to a request that timed out
                }

                return parts[1] == "ok" ? (parts.Length > 2 ? parts[2] : string.Empty) : throw new PreviewerException("The previewer agent: " + (parts.Length > 2 ? parts[2] : "an error"));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PreviewerException($"The previewer agent didn't answer '{request[0]}' within {timeout.TotalSeconds:0} s.");
        }
        catch (IOException ex)
        {
            throw new PreviewerException("The previewer agent's connection broke.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }
}

public sealed class PreviewerException : Exception
{
    public PreviewerException(string message)
        : base(message)
    {
    }

    public PreviewerException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public PreviewerException()
    {
    }
}
