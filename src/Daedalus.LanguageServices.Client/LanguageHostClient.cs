using System.Diagnostics;
using Daedalus.LanguageServices.Protocol;

namespace Daedalus.LanguageServices.Client;

/// <summary>How to start the language service host process.</summary>
public sealed record HostLaunchInfo(string FileName, IReadOnlyList<string> Arguments)
{
    public const string EnvironmentVariable = "DAEDALUS_LSHOST";

    /// <summary>
    /// Resolution order: <c>DAEDALUS_LSHOST</c> (an apphost or a .dll), then <c>lshost/</c> next to the shell.
    /// </summary>
    public static HostLaunchInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "daedalus-lshost.exe" : "daedalus-lshost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "lshost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "lshost", "daedalus-lshost.dll"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return FromPath(candidate);
            }
        }

        return null;
    }

    public static HostLaunchInfo FromPath(string path) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new HostLaunchInfo("dotnet", [path])
            : new HostLaunchInfo(path, []);
}

public sealed class HostCrashedEventArgs(int? exitCode, bool willRestart, Exception? error) : EventArgs
{
    public int? ExitCode { get; } = exitCode;

    public bool WillRestart { get; } = willRestart;

    public Exception? Error { get; } = error;
}

/// <summary>
/// Typed, supervised connection to <c>daedalus-lshost</c>. If the host dies it is restarted (with backoff and a
/// crash-loop limit), the solution is reopened and open documents are replayed from <see cref="DocumentTextProvider"/>.
/// A host failure never propagates beyond failed in-flight requests (<see cref="JsonRpcConnectionLostException"/>).
/// </summary>
public sealed class LanguageHostClient : IAsyncDisposable
{
    private const int MaxRestartsPerWindow = 5;
    private static readonly TimeSpan s_restartWindow = TimeSpan.FromMinutes(1);

    private readonly HostLaunchInfo _launch;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _openDocuments = new(StringComparer.Ordinal);
    private readonly Queue<DateTime> _restarts = new();
    private Process? _process;
    private JsonRpcConnection? _connection;
    private string? _solutionPath;
    private bool _disposed;

    public LanguageHostClient(HostLaunchInfo launch) => _launch = launch;

    /// <summary>Returns the current text and version of an open document, used to replay state after a restart.</summary>
    public Func<string, (string Text, int Version)?>? DocumentTextProvider { get; set; }

    public SemanticTokensLegend? Legend { get; private set; }

    public int? HostProcessId => _process?.Id;

    public event EventHandler<HostCrashedEventArgs>? HostCrashed;

    public event EventHandler? HostRestarted;

    public event EventHandler<string>? HostLog;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
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

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {_launch.FileName}.");
        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                HostLog?.Invoke(this, e.Data);
            }
        };
        process.BeginErrorReadLine();

        var connection = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        connection.Disconnected += OnDisconnected;
        lock (_gate)
        {
            _process = process;
            _connection = connection;
        }

        var result = await connection.InvokeAsync(
            Methods.Initialize,
            new InitializeParams(Environment.ProcessId, "daedalus"),
            ProtocolJsonContext.Default.InitializeParams,
            ProtocolJsonContext.Default.InitializeResult,
            cancellationToken).ConfigureAwait(false);
        Legend = result.Legend;
    }

    public async Task<OpenSolutionResult> OpenSolutionAsync(string path, CancellationToken cancellationToken)
    {
        _solutionPath = path;
        return await Connection.InvokeAsync(
            Methods.OpenSolution, new OpenSolutionParams(path),
            ProtocolJsonContext.Default.OpenSolutionParams, ProtocolJsonContext.Default.OpenSolutionResult,
            cancellationToken).ConfigureAwait(false);
    }

    public Task DidOpenAsync(string path, string text, int version, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _openDocuments[path] = version;
        }

        return Connection.NotifyAsync(Methods.DidOpen, new DidOpenParams(path, text, version), ProtocolJsonContext.Default.DidOpenParams, cancellationToken);
    }

    public Task DidChangeAsync(string path, int version, TextEdit[] changes, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _openDocuments[path] = version;
        }

        return Connection.NotifyAsync(Methods.DidChange, new DidChangeParams(path, version, changes), ProtocolJsonContext.Default.DidChangeParams, cancellationToken);
    }

    public Task DidCloseAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _openDocuments.Remove(path);
        }

        return Connection.NotifyAsync(Methods.DidClose, new DidCloseParams(path), ProtocolJsonContext.Default.DidCloseParams, cancellationToken);
    }

    public Task<SemanticTokens> GetSemanticTokensAsync(string path, CancellationToken cancellationToken) =>
        Connection.InvokeAsync(
            Methods.SemanticTokensFull, new SemanticTokensParams(path),
            ProtocolJsonContext.Default.SemanticTokensParams, ProtocolJsonContext.Default.SemanticTokens, cancellationToken);

    public Task<SemanticTokens> GetSemanticTokensRangeAsync(string path, int startLine, int endLine, CancellationToken cancellationToken) =>
        Connection.InvokeAsync(
            Methods.SemanticTokensRange, new SemanticTokensRangeParams(path, startLine, endLine),
            ProtocolJsonContext.Default.SemanticTokensRangeParams, ProtocolJsonContext.Default.SemanticTokens, cancellationToken);

    private JsonRpcConnection Connection =>
        _connection ?? throw new JsonRpcConnectionLostException("The language service host is not running.", null);

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
            while (_restarts.Count > 0 && now - _restarts.Peek() > s_restartWindow)
            {
                _restarts.Dequeue();
            }

            restart = _restarts.Count < MaxRestartsPerWindow;
            if (restart)
            {
                _restarts.Enqueue(now);
            }
        }

        HostCrashed?.Invoke(this, new HostCrashedEventArgs(exitCode, restart, error));
        if (restart)
        {
            _ = RestartAsync(_restarts.Count);
        }
    }

    private async Task RestartAsync(int attempt)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << Math.Min(attempt - 1, 4)))).ConfigureAwait(false);
            await StartAsync(CancellationToken.None).ConfigureAwait(false);
            if (_solutionPath is { } solution)
            {
                await OpenSolutionAsync(solution, CancellationToken.None).ConfigureAwait(false);
            }

            string[] paths;
            lock (_gate)
            {
                paths = [.. _openDocuments.Keys];
            }

            foreach (var path in paths)
            {
                if (DocumentTextProvider?.Invoke(path) is { } document)
                {
                    await DidOpenAsync(path, document.Text, document.Version, CancellationToken.None).ConfigureAwait(false);
                }
            }

            HostRestarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is JsonRpcConnectionLostException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // The disconnect handler of the new connection decides about further restarts.
            HostLog?.Invoke(this, $"Language service host restart failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        JsonRpcConnection? connection;
        Process? process;
        lock (_gate)
        {
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
                await connection.InvokeAsync(Methods.Shutdown, timeout.Token).ConfigureAwait(false);
                await connection.NotifyAsync(Methods.Exit, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or JsonRpcConnectionLostException or IOException or JsonRpcRemoteException)
            {
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (process is not null)
        {
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }
    }
}
