using System.Diagnostics;

namespace Daedalus.Rpc;

/// <summary>How to start a service process. A <c>.dll</c> is launched with <c>dotnet</c>.</summary>
public sealed record ProcessLaunchInfo(string FileName, IReadOnlyList<string> Arguments)
{
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

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

    /// <summary>Graceful shutdown: request, then notification. Null skips the step.</summary>
    public string? ShutdownRequest { get; init; } = "shutdown";

    public string? ExitNotification { get; init; } = "exit";
}

public sealed class ProcessCrashedEventArgs(int? exitCode, bool willRestart, Exception? error) : EventArgs
{
    public int? ExitCode { get; } = exitCode;

    public bool WillRestart { get; } = willRestart;

    public Exception? Error { get; } = error;
}

/// <summary>
/// Runs a service process that speaks JSON-RPC over stdio and keeps it alive: when the connection drops, the
/// process is restarted with exponential backoff, up to <see cref="SupervisionPolicy.MaxRestarts"/> times per
/// <see cref="SupervisionPolicy.Window"/>. A service failure never propagates beyond failed in-flight requests
/// (<see cref="JsonRpcConnectionLostException"/>) and the <see cref="Crashed"/> event.
/// </summary>
public sealed class SupervisedProcess : IAsyncDisposable
{
    private readonly ProcessLaunchInfo _launch;
    private readonly SupervisionPolicy _policy;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Queue<DateTime> _restarts = new();
    private Process? _process;
    private JsonRpcConnection? _connection;
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

    /// <summary>True once the crash-loop limit was hit; the process is not restarted again until <see cref="StartAsync"/>.</summary>
    public bool GaveUp => _gaveUp;

    public int? ProcessId
    {
        get
        {
            try
            {
                return _process?.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public JsonRpcConnection Connection =>
        _connection ?? throw new JsonRpcConnectionLostException($"{Name} is not running.", null);

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
        var psi = new ProcessStartInfo(_launch.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in _launch.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in _launch.Environment ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {_launch.FileName}.");
        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Log?.Invoke(this, e.Data);
            }
        };
        process.BeginErrorReadLine();

        var connection = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        ConfigureConnection?.Invoke(connection);
        connection.Disconnected += OnDisconnected;
        connection.HandlerFailed += (_, ex) => Log?.Invoke(this, $"[{Name}] handler failed: {ex.Message}");
        lock (_gate)
        {
            _process = process;
            _connection = connection;
        }

        connection.Start();
        if (Initialize is { } initialize)
        {
            await initialize(connection, isRestart, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnDisconnected(object? sender, Exception? error)
    {
        int? exitCode = null;
        bool restart;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _connection))
            {
                return;
            }

            if (_process is { } process && process.WaitForExit(2000))
            {
                exitCode = process.ExitCode;
            }

            _connection = null;
            var now = DateTime.UtcNow;
            while (_restarts.Count > 0 && now - _restarts.Peek() > _policy.Window)
            {
                _restarts.Dequeue();
            }

            restart = _restarts.Count < _policy.MaxRestarts;
            if (restart)
            {
                _restarts.Enqueue(now);
            }
            else
            {
                _gaveUp = true;
            }
        }

        Crashed?.Invoke(this, new ProcessCrashedEventArgs(exitCode, restart, error));
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

    /// <summary>Graceful shutdown (shutdown request + exit notification), then kill after a grace period.</summary>
    public async ValueTask DisposeAsync()
    {
        JsonRpcConnection? connection;
        Process? process;
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
            try
            {
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }

        _startLock.Dispose();
    }
}
