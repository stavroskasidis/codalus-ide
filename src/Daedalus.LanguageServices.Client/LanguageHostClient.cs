using Daedalus.LanguageServices.Protocol;
using Daedalus.Rpc;

namespace Daedalus.LanguageServices.Client;

/// <summary>Locates the language service host.</summary>
public static class HostLaunchInfo
{
    public const string EnvironmentVariable = "DAEDALUS_LSHOST";

    /// <summary>
    /// Resolution order: <c>DAEDALUS_LSHOST</c> (an apphost or a .dll), then <c>lshost/</c> next to the shell.
    /// </summary>
    public static ProcessLaunchInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "daedalus-lshost.exe" : "daedalus-lshost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "lshost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "lshost", "daedalus-lshost.dll"));
        return candidates.FirstOrDefault(File.Exists) is { } path ? ProcessLaunchInfo.FromPath(path) : null;
    }

    public static ProcessLaunchInfo FromPath(string path) => ProcessLaunchInfo.FromPath(path);
}

/// <summary>
/// Typed, supervised connection to <c>daedalus-lshost</c>. When the host restarts, the solution is reopened and
/// open documents are replayed from <see cref="DocumentTextProvider"/>.
/// </summary>
public sealed class LanguageHostClient : IAsyncDisposable
{
    private readonly SupervisedProcess _process;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _openDocuments = new(StringComparer.Ordinal);
    private string? _solutionPath;

    public LanguageHostClient(ProcessLaunchInfo launch)
    {
        _process = new SupervisedProcess("daedalus-lshost", launch) { Initialize = InitializeAsync };
        _process.Crashed += (_, e) => HostCrashed?.Invoke(this, e);
        _process.Restarted += (_, _) => HostRestarted?.Invoke(this, EventArgs.Empty);
        _process.Log += (_, line) => HostLog?.Invoke(this, line);
    }

    /// <summary>Returns the current text and version of an open document, used to replay state after a restart.</summary>
    public Func<string, (string Text, int Version)?>? DocumentTextProvider { get; set; }

    public SemanticTokensLegend? Legend { get; private set; }

    public int? HostProcessId => _process.ProcessId;

    public event EventHandler<ProcessCrashedEventArgs>? HostCrashed;

    /// <summary>Raised after a restart once the solution and documents have been replayed.</summary>
    public event EventHandler? HostRestarted;

    public event EventHandler<string>? HostLog;

    public Task StartAsync(CancellationToken cancellationToken) => _process.StartAsync(cancellationToken);

    private async Task InitializeAsync(JsonRpcConnection connection, bool isRestart, CancellationToken cancellationToken)
    {
        var result = await connection.InvokeAsync(
            Methods.Initialize,
            new InitializeParams(Environment.ProcessId, "daedalus"),
            ProtocolJsonContext.Default.InitializeParams,
            ProtocolJsonContext.Default.InitializeResult,
            cancellationToken).ConfigureAwait(false);
        Legend = result.Legend;
        if (!isRestart)
        {
            return;
        }

        if (_solutionPath is { } solution)
        {
            await connection.InvokeAsync(
                Methods.OpenSolution, new OpenSolutionParams(solution),
                ProtocolJsonContext.Default.OpenSolutionParams, ProtocolJsonContext.Default.OpenSolutionResult,
                cancellationToken).ConfigureAwait(false);
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
                await connection.NotifyAsync(Methods.DidOpen, new DidOpenParams(path, document.Text, document.Version), ProtocolJsonContext.Default.DidOpenParams, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<OpenSolutionResult> OpenSolutionAsync(string path, CancellationToken cancellationToken)
    {
        _solutionPath = path;
        return await _process.Connection.InvokeAsync(
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

        return _process.Connection.NotifyAsync(Methods.DidOpen, new DidOpenParams(path, text, version), ProtocolJsonContext.Default.DidOpenParams, cancellationToken);
    }

    public Task DidChangeAsync(string path, int version, TextEdit[] changes, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _openDocuments[path] = version;
        }

        return _process.Connection.NotifyAsync(Methods.DidChange, new DidChangeParams(path, version, changes), ProtocolJsonContext.Default.DidChangeParams, cancellationToken);
    }

    public Task DidCloseAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _openDocuments.Remove(path);
        }

        return _process.Connection.NotifyAsync(Methods.DidClose, new DidCloseParams(path), ProtocolJsonContext.Default.DidCloseParams, cancellationToken);
    }

    public Task<SemanticTokens> GetSemanticTokensAsync(string path, CancellationToken cancellationToken) =>
        _process.Connection.InvokeAsync(
            Methods.SemanticTokensFull, new SemanticTokensParams(path),
            ProtocolJsonContext.Default.SemanticTokensParams, ProtocolJsonContext.Default.SemanticTokens, cancellationToken);

    public Task<SemanticTokens> GetSemanticTokensRangeAsync(string path, int startLine, int endLine, CancellationToken cancellationToken) =>
        _process.Connection.InvokeAsync(
            Methods.SemanticTokensRange, new SemanticTokensRangeParams(path, startLine, endLine),
            ProtocolJsonContext.Default.SemanticTokensRangeParams, ProtocolJsonContext.Default.SemanticTokens, cancellationToken);

    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
