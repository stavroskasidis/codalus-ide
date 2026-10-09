using System.Globalization;
using System.Text;
using DaedalusStudio.Debugging;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Debugging;

public enum DebugState
{
    /// <summary>No debug session.</summary>
    Idle,

    /// <summary>Starting the adapter and the program.</summary>
    Starting,

    Running,

    /// <summary>Stopped at a breakpoint, a step, an exception or a pause.</summary>
    Stopped,
}

/// <summary>Why the program stopped. <see cref="Exception"/> is set for exception stops.</summary>
public sealed record StopInfo(string Reason, int ThreadId, string? Description, ExceptionInfoResponse? Exception);

/// <summary>A line of debugger output: <see cref="Category"/> is <c>stdout</c>, <c>stderr</c>, <c>console</c> (the debugger's own) or <c>important</c>.</summary>
public sealed record DebugOutput(string Category, string Text);

/// <summary>
/// Debug sessions on netcoredbg (ADR 0007): launch or attach, breakpoints (with client-side hit counts and logpoints),
/// stepping, threads, frames, variables and evaluation. One session at a time. Events may be raised on any thread.
/// </summary>
public sealed class DebugService : IAsyncDisposable
{
    public const string ExceptionsAll = "all";
    public const string ExceptionsUserUnhandled = "user-unhandled";

    private readonly Func<string?> _resolveAdapter;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, BreakpointSpec> _adapterBreakpoints = [];
    private readonly Dictionary<BreakpointSpec, int> _hits = [];
    private DebugAdapter? _adapter;
    private bool _attached;
    private int _exitCode;
    private CancellationTokenSource _session = new();
    private TaskCompletionSource<int> _process = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DebugService(Func<string?> resolveAdapter)
    {
        _resolveAdapter = resolveAdapter;
        Breakpoints.Changed += (_, path) => _ = SyncBreakpointsAsync(path);
    }

    public BreakpointStore Breakpoints { get; } = new();

    /// <summary>The exception filters to break on (<see cref="ExceptionsAll"/>, <see cref="ExceptionsUserUnhandled"/>).</summary>
    public IReadOnlyList<string> ExceptionFilters { get; private set; } = [ExceptionsUserUnhandled];

    public bool JustMyCode { get; set; } = true;

    public DebugState State { get; private set; }

    public bool IsActive => State != DebugState.Idle;

    /// <summary>Whether the session attached to a running process (Stop detaches instead of terminating).</summary>
    public bool IsAttached => _attached;

    public StopInfo? CurrentStop { get; private set; }

    /// <summary>The debuggee's process id, once the adapter reported it (DAP <c>process</c> event) or after an attach.</summary>
    public int? ProcessId { get; private set; }

    public IReadOnlyList<DapThread> Threads { get; private set; } = [];

    public int? SelectedThreadId { get; private set; }

    /// <summary>The selected thread's frames while stopped.</summary>
    public IReadOnlyList<StackFrame> Frames { get; private set; } = [];

    public StackFrame? SelectedFrame { get; private set; }

    public event EventHandler? StateChanged;

    /// <summary>The program stopped (after <see cref="Frames"/> and <see cref="Threads"/> were loaded).</summary>
    public event EventHandler<StopInfo>? Stopped;

    /// <summary>A different frame or thread was selected.</summary>
    public event EventHandler? FrameChanged;

    public event EventHandler<DebugOutput>? Output;

    /// <summary>The session ended (the exit code when the program exited).</summary>
    public event EventHandler<int?>? Ended;

    // ------------------------------------------------------------------ start and stop

    /// <summary>Starts <paramref name="plan"/> under the debugger. Throws when no adapter is installed or the launch fails.</summary>
    public Task LaunchAsync(LaunchPlan plan, CancellationToken cancellationToken) =>
        StartAsync(adapter => adapter.LaunchAsync(
            new LaunchArguments(plan.FileName, [.. CommandLine.Split(plan.Arguments)], plan.WorkingDirectory, new Dictionary<string, string>(plan.Environment), JustMyCode: JustMyCode, EnableStepFiltering: JustMyCode),
            cancellationToken), attached: false, cancellationToken);

    /// <summary>Starts <paramref name="program"/> with arguments that are already split (no command-line parsing).</summary>
    public Task LaunchAsync(string program, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken) =>
        StartAsync(adapter => adapter.LaunchAsync(
            new LaunchArguments(program, [.. arguments], workingDirectory, new Dictionary<string, string>(environment), JustMyCode: JustMyCode, EnableStepFiltering: JustMyCode),
            cancellationToken), attached: false, cancellationToken);

    public async Task AttachAsync(int processId, CancellationToken cancellationToken)
    {
        await StartAsync(adapter => adapter.AttachAsync(new AttachArguments(processId, JustMyCode), cancellationToken), attached: true, cancellationToken).ConfigureAwait(false);
        OnProcess(processId);
    }

    /// <summary>The debuggee's process id: completes when the adapter reports it; cancelled when the session ends first.</summary>
    public Task<int> WaitForProcessIdAsync(CancellationToken cancellationToken) => _process.Task.WaitAsync(cancellationToken);

    private void OnProcess(int processId)
    {
        ProcessId = processId;
        _process.TrySetResult(processId);
    }

    private async Task StartAsync(Func<DebugAdapter, Task> start, bool attached, CancellationToken cancellationToken)
    {
        if (IsActive)
        {
            throw new InvalidOperationException("A debug session is already running.");
        }

        var path = _resolveAdapter() ?? throw new InvalidOperationException(
            "The debugger (netcoredbg) was not found. Install it next to Daedalus Studio (debugger/), set DAEDALUS_NETCOREDBG, or run eng/get-netcoredbg.sh in a source checkout.");
        SetState(DebugState.Starting);
        _session = new CancellationTokenSource();
        _attached = attached;
        _exitCode = 0;
        ProcessId = null;
        _process = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _hits.Clear();
            _adapterBreakpoints.Clear();
        }

        DebugAdapter? adapter = null;
        try
        {
            adapter = await DebugAdapter.StartAsync(path, cancellationToken).ConfigureAwait(false);
            adapter.EventReceived += OnEventAsync;
            adapter.Log += (_, line) => Output?.Invoke(this, new DebugOutput("console", line + "\n"));
            _adapter = adapter;
            _ = adapter.Closed.ContinueWith(_ => EndAsync(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

            await start(adapter).ConfigureAwait(false);
            await adapter.Initialized.WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var file in Breakpoints.Files)
            {
                await SendBreakpointsAsync(adapter, file, cancellationToken).ConfigureAwait(false);
            }

            await adapter.SetExceptionBreakpointsAsync([.. ExceptionFilters], cancellationToken).ConfigureAwait(false);
            if (State == DebugState.Starting)
            {
                SetState(DebugState.Running);
            }

            await adapter.ConfigurationDoneAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
        {
            if (adapter is not null)
            {
                await adapter.DisconnectAsync(terminateDebuggee: !attached, CancellationToken.None).ConfigureAwait(false);
            }

            await EndAsync().ConfigureAwait(false);
            throw ex is OperationCanceledException ? ex : new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>Ends the session: terminates a launched program, detaches from an attached one.</summary>
    public async Task StopAsync()
    {
        if (_adapter is not { } adapter)
        {
            return;
        }

        await adapter.DisconnectAsync(terminateDebuggee: !_attached, CancellationToken.None).ConfigureAwait(false);
        await EndAsync().ConfigureAwait(false);
    }

    private async Task EndAsync()
    {
        DebugAdapter? adapter;
        lock (_gate)
        {
            adapter = _adapter;
            _adapter = null;
        }

        if (adapter is null)
        {
            return;
        }

        await _session.CancelAsync().ConfigureAwait(false);
        await adapter.DisposeAsync().ConfigureAwait(false);
        foreach (var breakpoint in Breakpoints.All.Where(b => b.Enabled && Breakpoints.StateOf(b) is { Verified: false }))
        {
            var message = Breakpoints.StateOf(breakpoint)?.Message;
            Output?.Invoke(this, new DebugOutput("console", $"The breakpoint at {breakpoint.Path}, line {breakpoint.Line.ToString(CultureInfo.InvariantCulture)} was never bound{(message is null ? "." : ": " + message)}\n"));
        }

        CurrentStop = null;
        Threads = [];
        Frames = [];
        SelectedFrame = null;
        SelectedThreadId = null;
        _process.TrySetCanceled();
        Breakpoints.ClearStates();
        SetState(DebugState.Idle);
        Ended?.Invoke(this, _exitCode);
    }

    // ------------------------------------------------------------------ execution control

    private int CurrentThread => SelectedThreadId ?? CurrentStop?.ThreadId ?? (Threads.Count > 0 ? Threads[0].Id : (int?)null) ?? 0;

    public Task ContinueAsync() => ResumeAsync(a => a.ContinueAsync(CurrentThread, _session.Token));

    public Task StepOverAsync() => ResumeAsync(a => a.NextAsync(CurrentThread, _session.Token));

    public Task StepIntoAsync() => ResumeAsync(a => a.StepInAsync(CurrentThread, _session.Token));

    public Task StepOutAsync() => ResumeAsync(a => a.StepOutAsync(CurrentThread, _session.Token));

    public async Task PauseAsync()
    {
        if (_adapter is { } adapter && State == DebugState.Running)
        {
            await adapter.PauseAsync(0, _session.Token).ConfigureAwait(false);
        }
    }

    private async Task ResumeAsync(Func<DebugAdapter, Task> resume)
    {
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            return;
        }

        MarkRunning();
        await resume(adapter).ConfigureAwait(false);
    }

    private void MarkRunning()
    {
        CurrentStop = null;
        Frames = [];
        SelectedFrame = null;
        SetState(DebugState.Running);
    }

    // ------------------------------------------------------------------ inspection

    /// <summary>Shows another thread's frames (while stopped).</summary>
    public async Task SelectThreadAsync(int threadId)
    {
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            return;
        }

        var frames = await adapter.StackTraceAsync(threadId, 200, _session.Token).ConfigureAwait(false);
        SelectedThreadId = threadId;
        Frames = frames.StackFrames;
        SelectedFrame = Frames.FirstOrDefault(f => f.Source?.Path is not null) ?? (Frames.Count > 0 ? Frames[0] : null);
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectFrame(StackFrame frame)
    {
        SelectedFrame = frame;
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<Scope>> ScopesAsync(StackFrame frame) =>
        _adapter is { } adapter ? await adapter.ScopesAsync(frame.Id, _session.Token).ConfigureAwait(false) : [];

    public async Task<IReadOnlyList<Variable>> VariablesAsync(int variablesReference) =>
        _adapter is { } adapter && variablesReference > 0 ? await adapter.VariablesAsync(variablesReference, _session.Token).ConfigureAwait(false) : [];

    /// <summary>Evaluates in the selected frame. <paramref name="context"/>: <c>watch</c>, <c>repl</c> or <c>hover</c>.</summary>
    public async Task<EvaluateResponse> EvaluateAsync(string expression, string context, CancellationToken cancellationToken = default)
    {
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            throw new InvalidOperationException("Expressions can be evaluated only while the program is stopped.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
        return await adapter.EvaluateAsync(expression, SelectedFrame?.Id, context, linked.Token).ConfigureAwait(false);
    }

    public async Task<SetVariableResponse> SetVariableAsync(int variablesReference, string name, string value)
    {
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            throw new InvalidOperationException("Variables can be changed only while the program is stopped.");
        }

        return await adapter.SetVariableAsync(variablesReference, name, value, _session.Token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ settings

    public async Task SetExceptionFiltersAsync(IReadOnlyList<string> filters)
    {
        ExceptionFilters = [.. filters];
        if (_adapter is { } adapter)
        {
            await adapter.SetExceptionBreakpointsAsync([.. filters], _session.Token).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ breakpoints

    private async Task SyncBreakpointsAsync(string? path)
    {
        if (_adapter is not { } adapter || State == DebugState.Starting)
        {
            return;
        }

        try
        {
            foreach (var file in path is null ? Breakpoints.Files : [path])
            {
                await SendBreakpointsAsync(adapter, file, _session.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException)
        {
            Output?.Invoke(this, new DebugOutput("console", $"Breakpoints could not be set: {ex.Message}\n"));
        }
    }

    private async Task SendBreakpointsAsync(DebugAdapter adapter, string path, CancellationToken cancellationToken)
    {
        var specs = Breakpoints.For(path).Where(b => b.Enabled).ToList();
        var result = await adapter.SetBreakpointsAsync(path, [.. specs.Select(b => new SourceBreakpoint(b.Line, Condition: b.Condition))], cancellationToken).ConfigureAwait(false);
        var states = new List<(int, BreakpointState)>();
        lock (_gate)
        {
            foreach (var stale in _adapterBreakpoints.Where(p => SolutionService.PathComparer.Equals(p.Value.Path, path)).Select(p => p.Key).ToList())
            {
                _adapterBreakpoints.Remove(stale);
            }

            for (var i = 0; i < specs.Count && i < result.Length; i++)
            {
                if (result[i].Id is { } id)
                {
                    _adapterBreakpoints[id] = specs[i];
                }

                states.Add((specs[i].Line, new BreakpointState(result[i].Verified, result[i].Message)));
            }
        }

        Breakpoints.SetStates(path, states);
    }

    // ------------------------------------------------------------------ events

    private async Task OnEventAsync(DapEvent e)
    {
        switch (e.Name)
        {
            case DapEvents.Stopped when DebugAdapter.Read(e.Body, DapJsonContext.Default.StoppedEvent) is { } stopped:
                await OnStoppedAsync(stopped).ConfigureAwait(false);
                break;
            case DapEvents.Continued:
                if (State == DebugState.Stopped)
                {
                    MarkRunning();
                }

                break;
            case DapEvents.Output when DebugAdapter.Read(e.Body, DapJsonContext.Default.OutputEvent) is { } output:
                Output?.Invoke(this, new DebugOutput(output.Category ?? "console", output.Output));
                break;
            case DapEvents.Exited when DebugAdapter.Read(e.Body, DapJsonContext.Default.ExitedEvent) is { } exited:
                _exitCode = exited.ExitCode;
                Output?.Invoke(this, new DebugOutput("console", $"The program has exited with code {exited.ExitCode.ToString(CultureInfo.InvariantCulture)} (0x{exited.ExitCode:x}).\n"));
                break;
            case DapEvents.Terminated:
                _ = Task.Run(EndAsync);
                break;
            case DapEvents.Process when DebugAdapter.Read(e.Body, DapJsonContext.Default.ProcessEvent) is { SystemProcessId: { } pid }:
                OnProcess(pid);
                break;
            case DapEvents.Breakpoint when DebugAdapter.Read(e.Body, DapJsonContext.Default.BreakpointEvent) is { Breakpoint.Id: { } id } changed:
                BreakpointSpec? spec;
                lock (_gate)
                {
                    spec = _adapterBreakpoints.GetValueOrDefault(id);
                }

                if (spec is not null)
                {
                    Breakpoints.SetStates(spec.Path, [(spec.Line, new BreakpointState(changed.Breakpoint.Verified, changed.Breakpoint.Message))]);
                }

                break;
        }
    }

    private async Task OnStoppedAsync(StoppedEvent stopped)
    {
        if (_adapter is not { } adapter)
        {
            return;
        }

        var threadId = stopped.ThreadId ?? 0;
        try
        {
            var frames = (await adapter.StackTraceAsync(threadId, 200, _session.Token).ConfigureAwait(false)).StackFrames;
            if (stopped.Reason == "breakpoint" && FindBreakpoint(stopped, frames) is { NeedsClientHandling: true } breakpoint
                && !await ShouldBreakAsync(adapter, breakpoint, frames, threadId).ConfigureAwait(false))
            {
                await adapter.ContinueAsync(threadId, _session.Token).ConfigureAwait(false);
                return;
            }

            ExceptionInfoResponse? exception = null;
            if (stopped.Reason == "exception" && adapter.Capabilities.SupportsExceptionInfoRequest)
            {
                try
                {
                    exception = await adapter.ExceptionInfoAsync(threadId, _session.Token).ConfigureAwait(false);
                }
                catch (DapException)
                {
                }
            }

            Threads = await adapter.ThreadsAsync(_session.Token).ConfigureAwait(false);
            SelectedThreadId = threadId;
            Frames = frames;
            SelectedFrame = frames.FirstOrDefault(f => f.Source?.Path is not null) ?? (frames.Length > 0 ? frames[0] : null);
            var info = new StopInfo(stopped.Reason, threadId, stopped.Description ?? stopped.Text, exception);
            CurrentStop = info;
            SetState(DebugState.Stopped);
            Stopped?.Invoke(this, info);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException)
        {
            Output?.Invoke(this, new DebugOutput("console", $"Could not read the stopped program's state: {ex.Message}\n"));
        }
    }

    private BreakpointSpec? FindBreakpoint(StoppedEvent stopped, StackFrame[] frames)
    {
        lock (_gate)
        {
            foreach (var id in stopped.HitBreakpointIds ?? [])
            {
                if (_adapterBreakpoints.TryGetValue(id, out var spec))
                {
                    return spec;
                }
            }
        }

        return frames.Length > 0 && frames[0] is { Source.Path: { } path } top ? Breakpoints.At(path, top.Line) : null;
    }

    /// <summary>Client-side hit counts and logpoints: whether this hit shows as a stop.</summary>
    private async Task<bool> ShouldBreakAsync(DebugAdapter adapter, BreakpointSpec breakpoint, StackFrame[] frames, int threadId)
    {
        int hits;
        lock (_gate)
        {
            hits = _hits[breakpoint] = _hits.GetValueOrDefault(breakpoint) + 1;
        }

        if (breakpoint.HitCondition is { } condition && !BreakpointStore.HitConditionMet(condition, hits))
        {
            return false;
        }

        if (breakpoint.LogMessage is { } message)
        {
            var text = await FormatLogMessageAsync(adapter, message, frames.Length > 0 ? frames[0] : null, threadId).ConfigureAwait(false);
            Output?.Invoke(this, new DebugOutput("console", text + "\n"));
            return false;
        }

        return true;
    }

    /// <summary>Replaces <c>{expression}</c> with its value, <c>$FUNCTION</c> and <c>$TID</c> with the frame and thread.</summary>
    internal async Task<string> FormatLogMessageAsync(DebugAdapter adapter, string message, StackFrame? frame, int threadId)
    {
        var result = new StringBuilder();
        for (var i = 0; i < message.Length; i++)
        {
            if (message[i] == '{' && i + 1 < message.Length && message[i + 1] == '{')
            {
                result.Append('{');
                i++;
            }
            else if (message[i] == '}' && i + 1 < message.Length && message[i + 1] == '}')
            {
                result.Append('}');
                i++;
            }
            else if (message[i] == '{' && message.IndexOf('}', i) is var end and > 0)
            {
                var expression = message[(i + 1)..end];
                try
                {
                    result.Append((await adapter.EvaluateAsync(expression, frame?.Id, "watch", _session.Token).ConfigureAwait(false)).Result);
                }
                catch (DapException ex)
                {
                    result.Append(CultureInfo.InvariantCulture, $"<{ex.Message}>");
                }

                i = end;
            }
            else
            {
                result.Append(message[i]);
            }
        }

        return result.ToString()
            .Replace("$FUNCTION", frame?.Name ?? "?", StringComparison.Ordinal)
            .Replace("$TID", threadId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private void SetState(DebugState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _session.Dispose();
    }
}

/// <summary>Splits a command line into arguments the way the C runtime and .NET do (quotes, and backslashes before quotes).</summary>
public static class CommandLine
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArgument = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', backslashes);
                    i--;
                }

                hasArgument = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasArgument = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasArgument)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasArgument = false;
                }
            }
            else
            {
                current.Append(c);
                hasArgument = true;
            }
        }

        if (hasArgument)
        {
            args.Add(current.ToString());
        }

        return args;
    }
}
