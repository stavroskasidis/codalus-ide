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

    /// <summary>
    /// The start that ended was a restart (M49). An exit during the first start is reported to the caller of
    /// <see cref="SupervisedProcess.StartAsync"/>, which handles it; one during a restart has no caller but this event.
    /// </summary>
    public bool IsRestart { get; init; }
}

/// <summary>
/// Runs a service process that speaks JSON-RPC over stdio and keeps it alive: when the connection drops, the
/// process is restarted with exponential backoff, up to <see cref="SupervisionPolicy.MaxRestarts"/> times per
/// <see cref="SupervisionPolicy.Window"/>. A service failure never propagates beyond failed in-flight requests
/// (<see cref="JsonRpcConnectionLostException"/>) and the <see cref="Crashed"/> event.
/// <para>
/// M49: a process that ends says why. Its standard error (the first lines for a failed start, the last ones for a
/// crash) and its exit code become the error requests fail with (<see cref="HostExitedException"/>) and a
/// <see cref="Log"/> line; one that ends during startup isn't
/// restarted (<see cref="SupervisionPolicy.RestartAfterStartupExit"/>); and a framework-dependent host whose .NET
/// runtime is missing isn't started at all (<see cref="HostRuntimeMissingException"/>).
/// </para>
/// </summary>
public sealed class SupervisedProcess : IAsyncDisposable
{
    /// <summary>The first lines of standard error kept from each start: why a start failed.</summary>
    public const int ErrorLinesKept = 50;

    /// <summary>The last lines of standard error kept from each start: why a running host crashed.</summary>
    public const int ErrorTailKept = 20;

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

        var run = new Run(process, isRestart);
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
        try
        {
            if (Initialize is { } initialize)
            {
                await initialize(connection, isRestart, cancellationToken).ConfigureAwait(false);
            }

            LastFailure = null;
        }
        finally
        {
            // Startup ends here, however Initialize ended: a host that failed its handshake but kept running (a request
            // it refused, a cancelled start) is a running host whose later crash restarts as usual.
            run.StartupOver = true;
        }
    }

    /// <summary>Whether the process is still starting: before its handshake ended (or its first message, without one).</summary>
    private bool InStartup(Run run, JsonRpcConnection connection, int? exitCode) =>
        Initialize is not null
            ? !run.StartupOver
            : exitCode is not (null or 0) && run.HasErrorOutput && !connection.HasReceived && System.Diagnostics.Stopwatch.GetElapsedTime(run.Started) < _policy.StartupWindow;

    /// <summary>
    /// The error for a lost connection: when the process ended, its exit code and its standard error's lines. It waits
    /// a little for the process to end and its standard error to be read (it closed its output, so it is ending).
    /// </summary>
    /// <remarks>
    /// Whether the exit was during startup is decided here, once: this runs before the pending requests fail (the
    /// read loop waits for it, and so does a write that found the peer gone), so Initialize can't have ended yet when
    /// its own failure is the cause. <see cref="OnDisconnected"/> reuses the decision.
    /// </remarks>
    private async Task<JsonRpcConnectionLostException?> ExplainAsync(Run run, JsonRpcConnection connection)
    {
        var process = run.Process;
        await ((Task)process.Exited).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!process.Exited.IsCompletedSuccessfully || _disposed)
        {
            run.DecideStartup(InStartup(run, connection, null));
            return null;
        }

        await process.ErrorCompleted.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var code = process.Exited.Result;
        var startup = run.DecideStartup(InStartup(run, connection, code));

        // A failed start says why in its first lines (from its crash report's first line when it logged before it);
        // a running host that crashed, in its last ones: from its crash report's first line, not its stack frames.
        var lines = startup ? run.Head() : run.CrashOutput();
        if (lines.Count == 0 && code is 0 or null)
        {
            return null; // a clean exit says nothing more than "lost"
        }

        var quotedLines = startup ? lines.Skip(Math.Max(0, lines.ToList().FindIndex(ErrorOutputBuffer.BeginsReport))) : run.Report();
        var quoted = string.Join(" ", quotedLines.Take(ErrorLinesQuoted));
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
        bool isRestart;
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

            startup = run.StartupExit ?? InStartup(run, lost, exitCode);
            reason = run.Exit ?? error;
            isRestart = run.IsRestart;
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

        Crashed?.Invoke(this, new ProcessCrashedEventArgs(exitCode, restart, reason, startup) { IsRestart = isRestart });
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

    /// <summary>One start of the process: its standard error, whether its startup is over, and how it ended.</summary>
    private sealed class Run(IHostProcess process, bool isRestart)
    {
        private readonly ErrorOutputBuffer _errors = new(ErrorLinesKept, ErrorTailKept);
        private int _startupExit = -1; // -1 undecided, 0 no, 1 yes

        public IHostProcess Process { get; } = process;

        public bool IsRestart { get; } = isRestart;

        public long Started { get; } = System.Diagnostics.Stopwatch.GetTimestamp();

        /// <summary>Initialize ended, however it ended: later exits are crashes of a running host.</summary>
        public volatile bool StartupOver;

        public HostExitedException? Exit { get; set; }

        /// <summary>Whether the exit was during startup, once decided (<see cref="ExplainAsync"/>).</summary>
        public bool? StartupExit => Volatile.Read(ref _startupExit) is var value and >= 0 ? value == 1 : null;

        public bool HasErrorOutput => _errors.Count > 0;

        /// <summary>Records the decision the first time; returns the decision that stands.</summary>
        public bool DecideStartup(bool startup)
        {
            Interlocked.CompareExchange(ref _startupExit, startup ? 1 : 0, -1);
            return Volatile.Read(ref _startupExit) == 1;
        }

        public void Add(string line) => _errors.Add(line);

        public IReadOnlyList<string> Head() => _errors.Head();

        public IReadOnlyList<string> CrashOutput() => _errors.CrashOutput();

        public IReadOnlyList<string> Report() => _errors.Report();
    }
}

/// <summary>
/// A process's standard error as the supervisor keeps it (M49): the first lines, which say why a start failed, and a
/// ring of the last ones, which say why a running process crashed (a host that logs to standard error fills the first
/// ones with its startup). Blank lines aren't kept, and a line is cut at <see cref="MaxLineLength"/> characters.
/// </summary>
/// <remarks>
/// The last line that begins a .NET crash report (<c>Unhandled exception.</c>, <c>Fatal error.</c>, <c>Process
/// terminated.</c>) is kept with the lines after it even once the ring has moved past it: a crash's last lines are its
/// stack frames, and the error quotes what the exception was (<see cref="Report"/>).
/// </remarks>
internal sealed class ErrorOutputBuffer(int headLines, int tailLines)
{
    public const int MaxLineLength = 2000;

    /// <summary>The lines kept from a crash report's first line: as many as the error quotes.</summary>
    public const int ReportLinesKept = SupervisedProcess.ErrorLinesQuoted;

    private readonly List<string> _head = [];
    private readonly Queue<string> _tail = new();
    private readonly List<string> _report = [];
    private readonly Lock _gate = new();
    private int _count;
    private int _reportAt; // the 1-based number of the last report's first line, 0 for none

    /// <summary>Whether a line begins a crash report: the runtime's own words for an unhandled exception or a fatal error.</summary>
    public static bool BeginsReport(string line) =>
        line.StartsWith("Unhandled exception.", StringComparison.Ordinal)
        || line.StartsWith("Unhandled Exception:", StringComparison.Ordinal)
        || line.StartsWith("Fatal error.", StringComparison.Ordinal)
        || line.StartsWith("Process terminated.", StringComparison.Ordinal);

    /// <summary>A stack frame line (or the separator between an inner exception's frames and the outer ones).</summary>
    private static bool IsFrame(string line) => line.StartsWith("at ", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal);

    /// <summary>The lines seen, kept or not.</summary>
    public int Count => Volatile.Read(ref _count);

    public void Add(string line)
    {
        if (line.Trim() is not { Length: > 0 } text)
        {
            return;
        }

        if (text.Length > MaxLineLength)
        {
            text = text[..MaxLineLength] + "…";
        }

        lock (_gate)
        {
            _count++;
            if (_head.Count < headLines)
            {
                _head.Add(text);
            }

            _tail.Enqueue(text);
            if (_tail.Count > tailLines)
            {
                _tail.Dequeue();
            }

            if (BeginsReport(text))
            {
                _reportAt = _count;
                _report.Clear();
                _report.Add(text);
            }
            else if (_reportAt > 0 && _report.Count < ReportLinesKept)
            {
                _report.Add(text);
            }
        }
    }

    /// <summary>
    /// What a crash's error quotes: from the last crash report's first line (the exception, not the frames below it),
    /// else from the last line of the ring that isn't a stack frame, else the ring.
    /// </summary>
    public IReadOnlyList<string> Report()
    {
        lock (_gate)
        {
            var firstInTail = _count - _tail.Count + 1;
            if (_reportAt > 0)
            {
                return _reportAt >= firstInTail ? [.. _tail.Skip(_reportAt - firstInTail)] : [.. _report];
            }

            var tail = _tail.ToList();
            var last = tail.FindLastIndex(line => !IsFrame(line));
            return last < 0 ? tail : tail[last..];
        }
    }

    /// <summary>
    /// A crash's standard error: the ring, after the crash report's first lines when the ring has moved past them.
    /// </summary>
    public IReadOnlyList<string> CrashOutput()
    {
        lock (_gate)
        {
            return _reportAt > 0 && _reportAt < _count - _tail.Count + 1 ? [.. _report, "…", .. _tail] : [.. _tail];
        }
    }

    public IReadOnlyList<string> Head()
    {
        lock (_gate)
        {
            return [.. _head];
        }
    }

    public IReadOnlyList<string> Tail()
    {
        lock (_gate)
        {
            return [.. _tail];
        }
    }
}
