using System.Diagnostics;
using System.Text.Json;

namespace DaedalusStudio.Debugging;

/// <summary>
/// Finds netcoredbg (ADR 0007): <c>DAEDALUS_NETCOREDBG</c>, then <c>debugger/netcoredbg</c> next to the shell, then
/// <c>.work/netcoredbg</c> in a source checkout (where <c>eng/get-netcoredbg.sh</c> puts it).
/// </summary>
public static class DebugAdapterLocator
{
    public const string EnvironmentVariable = "DAEDALUS_NETCOREDBG";

    public static string? Resolve()
    {
        var exe = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(Directory.Exists(fromEnv) ? Path.Combine(fromEnv, exe) : fromEnv);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "debugger", exe));
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DaedalusStudio.slnx")))
            {
                candidates.Add(Path.Combine(directory.FullName, ".work", "netcoredbg", exe));
                break;
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// One debug adapter process (netcoredbg <c>--interpreter=vscode</c>) and its DAP connection, with typed requests.
/// The adapter's stderr goes to <see cref="Log"/>.
/// </summary>
public sealed class DebugAdapter : IDebugAdapter
{
    private readonly Process _process;
    private readonly DapConnection _connection;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DebugAdapter(Process process)
    {
        _process = process;
        _connection = new DapConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        _connection.EventReceived += e =>
        {
            if (e.Name == DapEvents.Initialized)
            {
                _initialized.TrySetResult();
            }

            return EventReceived?.Invoke(e) ?? Task.CompletedTask;
        };
        _connection.HandlerFailed += (_, ex) => Log?.Invoke(this, "Event handler failed: " + ex);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lock (_stderr)
                {
                    if (_stderr.Length < 4096)
                    {
                        _stderr.AppendLine(line);
                    }
                }

                Log?.Invoke(this, line);
            }
        };
    }

    /// <summary>The start of the adapter's stderr, to say why a pipe transport failed.</summary>
    private readonly System.Text.StringBuilder _stderr = new();

    public Capabilities Capabilities { get; private set; } = new();

    /// <summary>
    /// Completes on the adapter's <c>initialized</c> event: breakpoints can be sent, then <c>configurationDone</c>.
    /// (netcoredbg sends it right after <c>initialize</c>, before anyone could subscribe to events.)
    /// </summary>
    public Task Initialized => _initialized.Task;

    /// <summary>Adapter events, in order (see <see cref="DapConnection.EventReceived"/>).</summary>
    public event Func<DapEvent, Task>? EventReceived;

    public event EventHandler<string>? Log;

    /// <summary>Completes when the adapter exits or closes its output.</summary>
    public Task Closed => _connection.Closed;

    public DapConnection Connection => _connection;

    /// <summary>Starts the adapter at <paramref name="path"/> and sends <c>initialize</c>.</summary>
    public static Task<DebugAdapter> StartAsync(string path, CancellationToken cancellationToken) =>
        StartAsync(DebugAdapterStartInfo.Local(path), cancellationToken);

    /// <summary>
    /// Starts the adapter as <paramref name="info"/> says (netcoredbg here, or through a pipe transport on another
    /// machine, M14c) and sends <c>initialize</c>. With <paramref name="terminal"/>, the client offers DAP's
    /// <c>runInTerminal</c> and answers it through the terminal.
    /// </summary>
    public static async Task<DebugAdapter> StartAsync(DebugAdapterStartInfo info, CancellationToken cancellationToken, ITerminalLauncher? terminal = null)
    {
        var start = new ProcessStartInfo(info.Program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = info.WorkingDirectory ?? (Path.IsPathRooted(info.Program) ? Path.GetDirectoryName(info.Program)! : Environment.CurrentDirectory),
        };
        foreach (var argument in info.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in info.Environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"{info.Program} did not start.");
        var adapter = new DebugAdapter(process) { Transport = info.Transport };
        if (terminal is not null)
        {
            adapter._connection.Handle("runInTerminal", (arguments, ct) => RunInTerminalAsync(terminal, arguments, ct));
        }

        process.BeginErrorReadLine();
        adapter._connection.Start();
        try
        {
            adapter.Capabilities = await adapter._connection.RequestAsync(
                "initialize",
                new InitializeArguments("daedalus", "Daedalus Studio", "coreclr", SupportsRunInTerminalRequest: terminal is not null),
                DapJsonContext.Default.InitializeArguments,
                DapJsonContext.Default.Capabilities,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (info.Transport is not null && ex is DapConnectionLostException)
        {
            // A transport that ended before the adapter answered (ssh refused, the container is gone…): say what it printed.
            var stderr = adapter._stderr.ToString().Trim();
            await adapter.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The debugger didn't start through {info.Transport}{(stderr.Length == 0 ? "." : ": " + stderr)}", ex);
        }
        catch
        {
            await adapter.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return adapter;
    }

    /// <summary>The pipe transport's name (<c>ssh host</c>, a container, a WSL distribution), or null when local.</summary>
    public string? Transport { get; private init; }

    /// <summary>Answers <c>runInTerminal</c>: starts the program in a terminal and returns its process id.</summary>
    internal static async Task<JsonElement> RunInTerminalAsync(ITerminalLauncher terminal, JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = Read(arguments, DapJsonContext.Default.RunInTerminalRequestArguments)
                      ?? throw new InvalidOperationException("runInTerminal: no arguments.");
        if (request.Args.Length == 0)
        {
            throw new InvalidOperationException("runInTerminal: no program to run.");
        }

        var launch = new TerminalLaunch(
            request.Title ?? Path.GetFileNameWithoutExtension(request.Args[0]),
            request.Args[0],
            request.Args[1..],
            request.Cwd,
            new Dictionary<string, string?>(request.Env ?? [], StringComparer.Ordinal));
        var processId = await terminal.LaunchAsync(launch, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new RunInTerminalResponse(processId), DapJsonContext.Default.RunInTerminalResponse);
    }

    private static DapJsonContext Json => DapJsonContext.Default;

    public Task LaunchAsync(LaunchArguments arguments, CancellationToken cancellationToken) =>
        _connection.RequestAsync("launch", arguments, Json.LaunchArguments, cancellationToken);

    /// <summary>Launches with arguments of another shape (a remote target's program and paths, M14c).</summary>
    public Task LaunchAsync<T>(T arguments, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken cancellationToken) =>
        _connection.RequestAsync("launch", arguments, info, cancellationToken);

    public Task AttachAsync(AttachArguments arguments, CancellationToken cancellationToken) =>
        _connection.RequestAsync("attach", arguments, Json.AttachArguments, cancellationToken);

    public Task ConfigurationDoneAsync(CancellationToken cancellationToken) =>
        _connection.RequestAsync("configurationDone", new EmptyArguments(), Json.EmptyArguments, cancellationToken);

    public async Task<Breakpoint[]> SetBreakpointsAsync(string path, SourceBreakpoint[] breakpoints, CancellationToken cancellationToken) =>
        (await _connection.RequestAsync("setBreakpoints", new SetBreakpointsArguments(new Source(Path.GetFileName(path), path), breakpoints), Json.SetBreakpointsArguments, Json.SetBreakpointsResponse, cancellationToken).ConfigureAwait(false)).Breakpoints;

    public async Task<Breakpoint[]> SetFunctionBreakpointsAsync(FunctionBreakpoint[] breakpoints, CancellationToken cancellationToken) =>
        (await _connection.RequestAsync("setFunctionBreakpoints", new SetFunctionBreakpointsArguments(breakpoints), Json.SetFunctionBreakpointsArguments, Json.SetBreakpointsResponse, cancellationToken).ConfigureAwait(false)).Breakpoints;

    public Task SetExceptionBreakpointsAsync(string[] filters, CancellationToken cancellationToken, ExceptionFilterOptions[]? options = null) =>
        _connection.RequestAsync("setExceptionBreakpoints", new SetExceptionBreakpointsArguments(filters, options), Json.SetExceptionBreakpointsArguments, cancellationToken);

    public Task ContinueAsync(int threadId, CancellationToken cancellationToken) =>
        _connection.RequestAsync("continue", new ThreadArguments(threadId), Json.ThreadArguments, cancellationToken);

    public Task NextAsync(int threadId, CancellationToken cancellationToken) =>
        _connection.RequestAsync("next", new ThreadArguments(threadId), Json.ThreadArguments, cancellationToken);

    public Task StepInAsync(int threadId, CancellationToken cancellationToken) =>
        _connection.RequestAsync("stepIn", new ThreadArguments(threadId), Json.ThreadArguments, cancellationToken);

    public Task StepOutAsync(int threadId, CancellationToken cancellationToken) =>
        _connection.RequestAsync("stepOut", new ThreadArguments(threadId), Json.ThreadArguments, cancellationToken);

    public Task PauseAsync(int threadId, CancellationToken cancellationToken) =>
        _connection.RequestAsync("pause", new ThreadArguments(threadId), Json.ThreadArguments, cancellationToken);

    public async Task<DapThread[]> ThreadsAsync(CancellationToken cancellationToken) =>
        (await _connection.RequestAsync("threads", new EmptyArguments(), Json.EmptyArguments, Json.ThreadsResponse, cancellationToken).ConfigureAwait(false)).Threads;

    public Task<StackTraceResponse> StackTraceAsync(int threadId, int levels, CancellationToken cancellationToken) =>
        _connection.RequestAsync("stackTrace", new StackTraceArguments(threadId, 0, levels), Json.StackTraceArguments, Json.StackTraceResponse, cancellationToken);

    public async Task<Scope[]> ScopesAsync(int frameId, CancellationToken cancellationToken) =>
        (await _connection.RequestAsync("scopes", new ScopesArguments(frameId), Json.ScopesArguments, Json.ScopesResponse, cancellationToken).ConfigureAwait(false)).Scopes;

    public async Task<Variable[]> VariablesAsync(int variablesReference, CancellationToken cancellationToken) =>
        (await _connection.RequestAsync("variables", new VariablesArguments(variablesReference), Json.VariablesArguments, Json.VariablesResponse, cancellationToken).ConfigureAwait(false)).Variables;

    public Task<EvaluateResponse> EvaluateAsync(string expression, int? frameId, string context, CancellationToken cancellationToken) =>
        _connection.RequestAsync("evaluate", new EvaluateArguments(expression, frameId, context), Json.EvaluateArguments, Json.EvaluateResponse, cancellationToken);

    public Task<SetVariableResponse> SetVariableAsync(int variablesReference, string name, string value, CancellationToken cancellationToken) =>
        _connection.RequestAsync("setVariable", new SetVariableArguments(variablesReference, name, value), Json.SetVariableArguments, Json.SetVariableResponse, cancellationToken);

    /// <summary>Assigns <paramref name="value"/> to an expression (a local, a field, a property) in the frame.</summary>
    public Task<SetExpressionResponse> SetExpressionAsync(string expression, string value, int? frameId, CancellationToken cancellationToken) =>
        _connection.RequestAsync("setExpression", new SetExpressionArguments(expression, value, frameId), Json.SetExpressionArguments, Json.SetExpressionResponse, cancellationToken);

    public Task<ExceptionInfoResponse> ExceptionInfoAsync(int threadId, CancellationToken cancellationToken) =>
        _connection.RequestAsync("exceptionInfo", new ThreadArguments(threadId), Json.ThreadArguments, Json.ExceptionInfoResponse, cancellationToken);

    /// <summary>Ends the session: terminates a launched program, or detaches from an attached one.</summary>
    public async Task DisconnectAsync(bool terminateDebuggee, CancellationToken cancellationToken)
    {
        try
        {
            await _connection.RequestAsync("disconnect", new DisconnectArguments(terminateDebuggee), Json.DisconnectArguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapConnectionLostException or DapException)
        {
            // Already gone.
        }
    }

    /// <summary>Reads a body as <typeparamref name="T"/> (events).</summary>
    public static T? Read<T>(JsonElement body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        body.ValueKind == JsonValueKind.Object ? body.Deserialize(info) : default;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }

            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}
