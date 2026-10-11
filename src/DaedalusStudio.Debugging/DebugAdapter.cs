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

    /// <summary>
    /// Whether <paramref name="program"/> runs as x64 on this Arm64 Mac: a Mach-O executable for x86-64 only (the x64
    /// <c>dotnet</c>, or the apphost of .NET 5 and earlier). Such a process needs the x64 debugger (M33, ADR 0030).
    /// </summary>
    public static bool RunsAsX64OnArm64Mac(string program)
    {
        if (!OperatingSystem.IsMacOS() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
        {
            return false;
        }

        try
        {
            Span<byte> header = stackalloc byte[8];
            using var file = File.OpenRead(program);
            return file.Read(header) == 8 && IsMachOX64(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A thin 64-bit Mach-O header (<c>MH_MAGIC_64</c>, little-endian) whose CPU type is x86-64.</summary>
    public static bool IsMachOX64(ReadOnlySpan<byte> header) =>
        header.Length >= 8
        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xFEEDFACF
        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == 0x01000007;

    /// <summary>Points at the x64 netcoredbg (a folder or the executable), on an Arm64 machine.</summary>
    public const string X64EnvironmentVariable = "DAEDALUS_NETCOREDBG_X64";

    /// <summary>
    /// The x64 netcoredbg for programs that run as x64 on an Arm64 machine (M33, ADR 0030: .NET Core 2.x to .NET 5 on
    /// Apple silicon): <c>DAEDALUS_NETCOREDBG_X64</c>, then <c>debugger/x64/</c> next to the shell, then
    /// <c>.work/netcoredbg-x64</c> in a source checkout (<c>eng/get-netcoredbg.sh --x64</c>). On an x64 machine it is the
    /// regular one.
    /// </summary>
    public static string? ResolveX64()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
        {
            return Resolve();
        }

        var exe = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(X64EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(Directory.Exists(fromEnv) ? Path.Combine(fromEnv, exe) : fromEnv);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "debugger", "x64", exe));
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DaedalusStudio.slnx")))
            {
                candidates.Add(Path.Combine(directory.FullName, ".work", "netcoredbg-x64", exe));
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
    /// <summary>How long <see cref="DisconnectAsync"/> waits for the adapter's answer.</summary>
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(10);

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
                Log?.Invoke(this, line);
            }
        };

        // The connection normally closes when the adapter's output ends with it. On Unix the debuggee inherits that
        // output, so an adapter that exits before its debuggee would leave the session running forever (M33).
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = AbandonUnlessClosedAsync();
    }

    private async Task AbandonUnlessClosedAsync()
    {
        await Task.WhenAny(_connection.Closed, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        if (!_connection.Closed.IsCompleted)
        {
            Log?.Invoke(this, $"The debug adapter exited (code {ExitCode()}), and a process it started still holds its output.");
            _connection.Abandon();
        }
    }

    private string ExitCode()
    {
        try
        {
            return _process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

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

    /// <summary>
    /// Starts the adapter at <paramref name="path"/>, with <paramref name="environment"/> added to its own, and sends
    /// <c>initialize</c>.
    /// </summary>
    public static async Task<DebugAdapter> StartAsync(string path, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        start.ArgumentList.Add("--interpreter=vscode");
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"{path} did not start.");
        var adapter = new DebugAdapter(process);
        process.BeginErrorReadLine();
        adapter._connection.Start();
        try
        {
            adapter.Capabilities = await adapter._connection.RequestAsync(
                "initialize",
                new InitializeArguments("daedalus", "Daedalus Studio", "coreclr"),
                DapJsonContext.Default.InitializeArguments,
                DapJsonContext.Default.Capabilities,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await adapter.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return adapter;
    }

    private static DapJsonContext Json => DapJsonContext.Default;

    public Task LaunchAsync(LaunchArguments arguments, CancellationToken cancellationToken) =>
        _connection.RequestAsync("launch", arguments, Json.LaunchArguments, cancellationToken);

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
        // An adapter that no longer answers (netcoredbg after its debuggee failed fast at start, M33) must not hold the
        // session open: after the timeout, disposing the adapter ends its process.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DisconnectTimeout);
        try
        {
            await _connection.RequestAsync("disconnect", new DisconnectArguments(terminateDebuggee), Json.DisconnectArguments, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapConnectionLostException or DapException)
        {
            // Already gone.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // No answer in time.
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
