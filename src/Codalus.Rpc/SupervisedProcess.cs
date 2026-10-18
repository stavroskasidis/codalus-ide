namespace Codalus.Rpc;

/// <summary>How to start a service process. A <c>.dll</c> is launched with <c>dotnet</c>.</summary>
public sealed record ProcessLaunchInfo(string FileName, IReadOnlyList<string> Arguments)
{
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Where the process starts: null for this machine, or a remote server's launcher (M21, ADR 0050).</summary>
    public IProcessLauncher? Launcher { get; init; }

    /// <summary>
    /// Whether <see cref="LocalProcessLauncher"/> checks a framework-dependent program's .NET runtime before starting it
    /// (M49, <see cref="HostRuntimeCheck"/>): a missing one is a <see cref="HostRuntimeMissingException"/>.
    /// </summary>
    public bool CheckRuntime { get; init; } = true;

    public static ProcessLaunchInfo FromPath(string path, params string[] arguments) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new ProcessLaunchInfo("dotnet", [path, .. arguments])
            : new ProcessLaunchInfo(path, arguments);
}

/// <summary>Restart policy of a <see cref="SupervisedProcess"/>.</summary>
public sealed record SupervisionPolicy
{
    public static SupervisionPolicy Default { get; } = new();

    /// <summary>Restarts allowed within <see cref="Window"/> before the supervisor gives up.</summary>
    public int MaxRestarts { get; init; } = 5;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Exit codes that mean starting again won't help (the build host's 3: no usable SDK): no restart.</summary>
    public IReadOnlyCollection<int> FinalExitCodes { get; init; } = [];

    /// <summary>
    /// Whether a process that ends during startup is started again (M49: no, by default). Startup lasts until
    /// <see cref="SupervisedProcess.Initialize"/> finishes; without one, an exit with a failure code and standard error
    /// output, before the process first writes a message and within <see cref="StartupWindow"/>, counts (a process killed
    /// early says nothing, and is restarted). An exit that early is deterministic (a missing runtime, a missing file, a
    /// bad setting): starting again would fail the same way.
    /// </summary>
    public bool RestartAfterStartupExit { get; init; }

    /// <summary>Startup of a process without <see cref="SupervisedProcess.Initialize"/>: see <see cref="RestartAfterStartupExit"/>.</summary>
    public TimeSpan StartupWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Graceful shutdown: request, then notification. Null skips the step.</summary>
    public string? ShutdownRequest { get; init; } = "shutdown";

    public string? ExitNotification { get; init; } = "exit";
}

public sealed class ProcessCrashedEventArgs(int? exitCode, bool willRestart, Exception? error) : EventArgs
{
    public ProcessCrashedEventArgs(int? exitCode, bool willRestart, Exception? error, bool duringStartup)
        : this(exitCode, willRestart, error)
    {
        DuringStartup = duringStartup;
    }

    public int? ExitCode { get; } = exitCode;

    public bool WillRestart { get; } = willRestart;

    /// <summary>Why: a <see cref="HostExitedException"/> with the process's standard error when it ended, or the read failure.</summary>
    public Exception? Error { get; } = error;

    /// <summary>The process ended during startup (M49): it isn't restarted unless the policy says so.</summary>
    public bool DuringStartup { get; }
}

/// <summary>
/// Runs a service process that speaks JSON-RPC over stdio and keeps it alive: when the connection drops, the
/// process is restarted with exponential backoff, up to <see cref="SupervisionPolicy.MaxRestarts"/> times per
/// <see cref="SupervisionPolicy.Window"/>. A service failure never propagates beyond failed in-flight requests
/// (<see cref="JsonRpcConnectionLostException"/>) and the <see cref="Crashed"/> event.
/// <para>
/// M49: a process that ends says why. Its first lines of standard error and its exit code become the error requests
/// fail with (<see cref="HostExitedException"/>) and a <see cref="Log"/> line; one that ends during startup isn't
/// restarted (<see cref="SupervisionPolicy.RestartAfterStartupExit"/>); and a framework-dependent host whose .NET
/// runtime is missing isn't started at all (<see cref="HostRuntimeMissingException"/>).
/// </para>
/// </summary>
public sealed class SupervisedProcess : IAsyncDisposable
{
    /// <summary>The lines of standard error kept from each start for the error (the first ones say why it failed).</summary>
    public const int ErrorLinesKept = 50;

    /// <summary>The lines of standard error the error's message quotes.</summary>
    public const int ErrorLinesQuoted = 6;

    private readonly ProcessLaunchInfo _launch;
    private readonly SupervisionPolicy _policy;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Queue<DateTime> _restarts = new();
    private IHostProcess? _process;
    private JsonRpcConnection? _connection;
    private Run? _run;
    private bool _disposed;
    private bool _gaveUp;

    public SupervisedProcess(string name, ProcessLaunchInfo launch, SupervisionPolicy? policy = null)
    {
        Name = name;
        _launch = launch;
        _policy = policy ?? SupervisionPolicy.Default;
    }

    public string Name { get; }

    /// <summary>Registers incoming handlers on every new connection, before it starts reading.</summary>
    public Action<JsonRpcConnection>? ConfigureConnection { get; set; }

    /// <summary>Runs after every (re)start, e.g. the <c>initialize</c> handshake and state replay. The flag is true on restarts.</summary>
    public Func<JsonRpcConnection, bool, CancellationToken, Task>? Initialize { get; set; }

    public bool IsRunning => _connection is not null;

    /// <summary>True once the crash-loop limit was hit, or the process ended during startup; it is not restarted again until <see cref="StartAsync"/>.</summary>
    public bool GaveUp => _gaveUp;

    /// <summary>Why the process last stopped or didn't start (M49), when that is known; null while it runs well.</summary>
    public JsonRpcConnectionLostException? LastFailure { get; private set; }

    public int? ProcessId => _process?.Id;

    public JsonRpcConnection Connection =>
        _connection ?? throw (LastFailure is { } failure && _gaveUp ? Again(failure) : new JsonRpcConnectionLostException($"{Name} is not running.", null));

    public event EventHandler<ProcessCrashedEventArgs>? Crashed;

    public event EventHandler? Restarted;

    public event EventHandler<string>? Log;

    /// <summary>Starts the process if it is not running. Safe to call concurrently.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return;
            }

            _gaveUp = false;
            await StartCoreAsync(isRestart: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task StartCoreAsync(bool isRestart, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IHostProcess process;
        try
        {
            process = await (_launch.Launcher ?? LocalProcessLauncher.Instance).StartAsync(_launch, cancellationToken).ConfigureAwait(false);
        }
        catch (HostRuntimeMissingException ex)
        {
            // Not started: its .NET runtime isn't installed. Nothing to restart until the runtime is there.
            LastFailure = ex;
            _gaveUp = true;
            Log?.Invoke(this, $"[{Name}] {ex.Message}");
            throw;
        }

        var run = new Run(process);
        process.ErrorLine += line =>
        {
            run.Add(line);
            Log?.Invoke(this, line);
        };
        process.BeginErrorRead();

        var connection = new JsonRpcConnection(process.Output, process.Input);
        connection.ExplainLoss = _ => ExplainAsync(run, connection);
        ConfigureConnection?.Invoke(connection);
        connection.Disconnected += OnDisconnected;
        connection.HandlerFailed += (_, ex) => Log?.Invoke(this, $"[{Name}] handler failed: {ex.Message}");
        lock (_gate)
        {
            _process = process;
            _connection = connection;
            _run = run;
        }

        connection.Start();
        if (Initialize is { } initialize)
        {
            await initialize(connection, isRestart, cancellationToken).ConfigureAwait(false);
        }

        run.Ready = true;
        LastFailure = null;
    }

    /// <summary>Whether the process is still starting: before its handshake finished (or its first message, without one).</summary>
    private bool InStartup(Run run, JsonRpcConnection connection, int? exitCode) =>
        Initialize is not null
            ? !run.Ready
            : exitCode is not (null or 0) && run.Lines().Count > 0 && !connection.HasReceived && System.Diagnostics.Stopwatch.GetElapsedTime(run.Started) < _policy.StartupWindow;

    /// <summary>
    /// The error for a lost connection: when the process ended, its exit code and first lines of standard error. It waits
    /// a little for the process to end and its standard error to be read (it closed its output, so it is ending).
    /// </summary>
    private async Task<JsonRpcConnectionLostException?> ExplainAsync(Run run, JsonRpcConnection connection)
    {
        var process = run.Process;
        await ((Task)process.Exited).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!process.Exited.IsCompletedSuccessfully || _disposed)
        {
            return null;
        }

        await process.ErrorCompleted.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var code = process.Exited.Result;
        var lines = run.Lines();
        if (lines.Count == 0 && code is 0 or null)
        {
            return null; // a clean exit says nothing more than "lost"
        }

        var startup = InStartup(run, connection, code);
        var quoted = string.Join(" ", lines.Take(ErrorLinesQuoted));
        if (quoted.Length > 1500)
        {
            quoted = quoted[..1500] + "…";
        }

        var message = $"{Name} {(startup ? "exited during startup" : "exited")}{(code is { } c ? $" with exit code {c}" : string.Empty)}{(quoted.Length > 0 ? ": " + quoted : ".")}";
        var exited = new HostExitedException(message, code, lines, startup);
        run.Exit = exited;
        return exited;
    }

    private void OnDisconnected(object? sender, Exception? error)
    {
        int? exitCode = null;
        bool restart;
        bool startup;
        Exception? reason;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _connection) || _connection is not { } lost || _run is not { } run)
            {
                return;
            }

            if (_process is { } process && process.Exited.Wait(2000))
            {
                exitCode = process.Exited.Result;
            }

            startup = InStartup(run, lost, exitCode);
            reason = run.Exit ?? error;
            LastFailure = run.Exit ?? new JsonRpcConnectionLostException($"{Name} stopped{(exitCode is { } code ? $" with exit code {code}" : string.Empty)}.", error);
            _connection = null;
            var now = DateTime.UtcNow;
            while (_restarts.Count > 0 && now - _restarts.Peek() > _policy.Window)
            {
                _restarts.Dequeue();
            }

            restart = _restarts.Count < _policy.MaxRestarts
                && !(exitCode is { } final && _policy.FinalExitCodes.Contains(final))
                && !(startup && !_policy.RestartAfterStartupExit);
            if (restart)
            {
                _restarts.Enqueue(now);
            }
            else
            {
                _gaveUp = true;
            }
        }

        if (reason is HostExitedException exited)
        {
            Log?.Invoke(this, $"[{Name}] {exited.Message}{(startup && !restart ? " It isn't started again: it would end the same way." : string.Empty)}");
        }

        Crashed?.Invoke(this, new ProcessCrashedEventArgs(exitCode, restart, reason, startup));
        if (restart)
        {
            _ = RestartAsync(_restarts.Count);
        }
    }

    private async Task RestartAsync(int attempt)
    {
        var delay = TimeSpan.FromTicks(Math.Min(
            _policy.MaxBackoff.Ticks,
            _policy.InitialBackoff.Ticks * (1L << Math.Min(attempt - 1, 16))));
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
            await _startLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _connection is not null)
                {
                    return;
                }

                await StartCoreAsync(isRestart: true, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _startLock.Release();
            }

            Restarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is JsonRpcConnectionLostException or JsonRpcRemoteException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or IOException or ObjectDisposedException)
        {
            // The disconnect handler of the new connection decides about further restarts.
            Log?.Invoke(this, $"[{Name}] restart failed: {ex.Message}");
        }
    }

    /// <summary>A failure to throw again, as a new exception of its kind (each throw gets its own stack).</summary>
    private static JsonRpcConnectionLostException Again(JsonRpcConnectionLostException failure) => failure switch
    {
        HostRuntimeMissingException missing => new HostRuntimeMissingException(missing.Result),
        HostExitedException exited => new HostExitedException(exited.Message, exited.ExitCode, exited.ErrorOutput, exited.DuringStartup),
        _ => new JsonRpcConnectionLostException(failure.Message, failure.InnerException),
    };

    /// <summary>Graceful shutdown (shutdown request + exit notification), then kill after a grace period.</summary>
    public async ValueTask DisposeAsync()
    {
        JsonRpcConnection? connection;
        IHostProcess? process;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connection = _connection;
            process = _process;
            _connection = null;
        }

        if (connection is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (_policy.ShutdownRequest is { } shutdown)
                {
                    await connection.InvokeAsync(shutdown, timeout.Token).ConfigureAwait(false);
                }

                if (_policy.ExitNotification is { } exit)
                {
                    await connection.NotifyAsync(exit, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or JsonRpcConnectionLostException or IOException or JsonRpcRemoteException or ObjectDisposedException)
            {
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (process is not null)
        {
            await ((Task)process.Exited).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!process.Exited.IsCompleted)
            {
                process.Kill();
            }

            await process.DisposeAsync().ConfigureAwait(false);
        }

        _startLock.Dispose();
    }

    /// <summary>One start of the process: its standard error's first lines, and whether it got past startup.</summary>
    private sealed class Run(IHostProcess process)
    {
        private readonly List<string> _lines = [];

        public IHostProcess Process { get; } = process;

        public long Started { get; } = System.Diagnostics.Stopwatch.GetTimestamp();

        public volatile bool Ready;

        public HostExitedException? Exit { get; set; }

        public void Add(string line)
        {
            lock (_lines)
            {
                if (_lines.Count < ErrorLinesKept && line.Trim() is { Length: > 0 } text)
                {
                    _lines.Add(text);
                }
            }
        }

        public IReadOnlyList<string> Lines()
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }
}
