using DaedalusStudio.Interactive.Protocol;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Workbench.Interactive;

/// <summary>Locates the C# Interactive host.</summary>
public static class InteractiveHostLaunch
{
    public const string EnvironmentVariable = "DAEDALUS_REPLHOST";

    /// <summary>Resolution order: <c>DAEDALUS_REPLHOST</c> (an apphost or a .dll), then <c>replhost/</c> next to the shell.</summary>
    public static ProcessLaunchInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "daedalus-replhost.exe" : "daedalus-replhost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "replhost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "replhost", "daedalus-replhost.dll"));
        return candidates.FirstOrDefault(File.Exists) is { } path ? ProcessLaunchInfo.FromPath(path) : null;
    }
}

/// <summary>What "Initialize Interactive with Project" gives the REPL (ADR 0015).</summary>
public static class InteractiveProjects
{
    /// <summary>
    /// The configuration for a built project: its output assembly and the other assemblies in its output folder (which
    /// hold package runtime assets for applications), then its compile references that aren't reference assemblies
    /// (targeting packs and <c>ref/</c> folders can't be loaded to run code; the host has the framework already).
    /// The working directory is the project folder.
    /// </summary>
    public static ResetParams Configuration(string projectPath, string? outputPath, IReadOnlyList<string> compilerArguments)
    {
        var references = new List<string>();
        if (outputPath is not null && File.Exists(outputPath))
        {
            references.Add(outputPath);
            references.AddRange(Directory.EnumerateFiles(Path.GetDirectoryName(outputPath)!, "*.dll")
                .Where(f => !string.Equals(f, outputPath, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase));
        }

        var loaded = references.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in CompileReferences(compilerArguments))
        {
            var segments = reference.Split(['/', '\\']);
            if (segments.Contains("packs", StringComparer.OrdinalIgnoreCase) || segments.Contains("ref", StringComparer.OrdinalIgnoreCase)
                || !loaded.Add(Path.GetFileName(reference)))
            {
                continue;
            }

            references.Add(reference);
        }

        return new ResetParams(Path.GetDirectoryName(projectPath)!, [.. references], [], []);
    }

    /// <summary>The paths of <c>/reference:</c> arguments (aliases dropped).</summary>
    public static IEnumerable<string> CompileReferences(IReadOnlyList<string> compilerArguments)
    {
        foreach (var argument in compilerArguments)
        {
            var value = argument switch
            {
                _ when argument.StartsWith("/reference:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("-reference:", StringComparison.OrdinalIgnoreCase) => argument[11..],
                _ when argument.StartsWith("/r:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("-r:", StringComparison.OrdinalIgnoreCase) => argument[3..],
                _ => null,
            };
            if (value is null)
            {
                continue;
            }

            value = value.Trim('"');
            var equals = value.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && value[..equals].IndexOfAny(['/', '\\', ':']) < 0)
            {
                value = value[(equals + 1)..].Trim('"'); // alias=path
            }

            if (value.Length > 0)
            {
                yield return value;
            }
        }
    }
}

/// <summary>
/// The C# Interactive window's connection to <c>daedalus-replhost</c> (ADR 0015). User code only ever runs in that
/// supervised process: when a script crashes it or calls <c>Environment.Exit</c>, the host restarts with the same
/// references and usings and an empty state, and <see cref="HostRestarted"/> says so. <see cref="ResetAsync"/> always
/// starts a fresh process, which is also how a runaway submission is interrupted (loaded assemblies can't be unloaded).
/// </summary>
public sealed class InteractiveService : IAsyncDisposable
{
    private static readonly SupervisionPolicy Policy = new() { ShutdownRequest = null, MaxRestarts = 5 };

    private readonly Func<ProcessLaunchInfo?> _launch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SupervisedProcess? _process;
    private int _submission;

    public InteractiveService(Func<ProcessLaunchInfo?> launch, string workingDirectory)
    {
        _launch = launch;
        Configuration = new ResetParams(workingDirectory, [], [], []);
    }

    /// <summary>What the next state starts with: working directory, references, usings and <c>#load</c> files.</summary>
    public ResetParams Configuration { get; private set; }

    public bool IsAvailable => _launch() is not null;

    public bool IsRunning => _process?.IsRunning == true;

    public int? ProcessId => _process?.ProcessId;

    /// <summary>Text the running submission wrote; arrives on a background thread.</summary>
    public event EventHandler<OutputParams>? Output;

    /// <summary>The host exited unexpectedly and was restarted with an empty state.</summary>
    public event EventHandler<ProcessCrashedEventArgs>? HostRestarted;

    /// <summary>The result of the latest reset (runtime and reference errors), raised after every (re)start.</summary>
    public event EventHandler<ResetResult>? Reset;

    public event EventHandler<string>? Log;

    /// <summary>Starts the host if it isn't running.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Discards the state (stopping any running submission) and starts a new one, optionally with a new configuration.</summary>
    public async Task ResetAsync(ResetParams? configuration, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (configuration is not null)
            {
                Configuration = configuration;
            }

            if (Interlocked.Exchange(ref _process, null) is { } old)
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }

            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs one submission. A host that dies while running it yields a failed result, not an exception.</summary>
    public async Task<ExecuteResult> ExecuteAsync(string code, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        var json = InteractiveJsonContext.Default;
        try
        {
            return await connection.InvokeAsync(
                InteractiveMethods.Execute, new ExecuteParams(Interlocked.Increment(ref _submission), code), json.ExecuteParams, json.ExecuteResult, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcConnectionLostException)
        {
            return new ExecuteResult(false, null, "The interactive host exited. The state was reset.", [], 0);
        }
    }

    public async Task<bool> IsCompleteAsync(string code, CancellationToken cancellationToken)
    {
        var json = InteractiveJsonContext.Default;
        var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.InvokeAsync(InteractiveMethods.IsComplete, new CodeParams(code), json.CodeParams, json.IsCompleteResult, cancellationToken).ConfigureAwait(false)).Complete;
    }

    public async Task<ReplCompletionResult> CompletionAsync(string code, int position, CancellationToken cancellationToken)
    {
        var json = InteractiveJsonContext.Default;
        var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.InvokeAsync(InteractiveMethods.Completion, new CodePositionParams(code, position), json.CodePositionParams, json.ReplCompletionResult, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SignatureHelp?> SignatureHelpAsync(string code, int position, CancellationToken cancellationToken)
    {
        var json = InteractiveJsonContext.Default;
        var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.InvokeAsync(InteractiveMethods.SignatureHelp, new CodePositionParams(code, position), json.CodePositionParams, json.SignatureHelp, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>LSP-encoded semantic tokens of the pending input (<see cref="TokenTypes"/> legend).</summary>
    public async Task<int[]> TokensAsync(string code, CancellationToken cancellationToken)
    {
        var json = InteractiveJsonContext.Default;
        var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.InvokeAsync(InteractiveMethods.Tokens, new CodeParams(code), json.CodeParams, json.TokensResult, cancellationToken).ConfigureAwait(false)).Data;
    }

    private async Task<JsonRpcConnection> ConnectionAsync(CancellationToken cancellationToken)
    {
        if (_process is { IsRunning: true } running)
        {
            return running.Connection;
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
        return _process?.Connection ?? throw new JsonRpcConnectionLostException("The interactive host is not running.", null);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            var launch = _launch() ?? throw new InvalidOperationException("The C# Interactive host (daedalus-replhost) was not found.");
            var process = new SupervisedProcess("interactive host", launch, Policy)
            {
                ConfigureConnection = Configure,
                Initialize = InitializeAsync,
            };
            process.Crashed += (_, e) =>
            {
                if (ReferenceEquals(_process, process))
                {
                    HostRestarted?.Invoke(this, e);
                }
            };
            process.Log += (_, line) => Log?.Invoke(this, line);
            _process = process;
        }

        await _process.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Configure(JsonRpcConnection connection) =>
        connection.OnNotification(InteractiveMethods.Output, InteractiveJsonContext.Default.OutputParams, p => Output?.Invoke(this, p));

    private async Task InitializeAsync(JsonRpcConnection connection, bool isRestart, CancellationToken cancellationToken)
    {
        var json = InteractiveJsonContext.Default;
        var result = await connection.InvokeAsync(InteractiveMethods.Reset, Configuration, json.ResetParams, json.ResetResult, cancellationToken).ConfigureAwait(false);
        Reset?.Invoke(this, result);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _process, null) is { } process)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
