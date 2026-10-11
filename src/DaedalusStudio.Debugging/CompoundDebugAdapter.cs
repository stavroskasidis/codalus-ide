using System.Text.Json;

namespace DaedalusStudio.Debugging;

/// <summary>
/// Several adapters as one debug session (ADR 0027): an ASP.NET Core server under netcoredbg and its page in the
/// browser under vscode-js-debug. The first adapter is the primary: it is launched and configured with the session;
/// the others join later (<see cref="AddAsync"/>, once the server listens) and get the session's breakpoints and
/// exception settings replayed.
/// <list type="bullet">
/// <item>Thread and frame ids and variable references are the adapters' own times four plus the adapter's index, so a
/// request goes to the adapter its id came from. Breakpoint ids are this class's own; a breakpoint is bound when any
/// adapter bound it.</item>
/// <item>One stop shows at a time. A stop in another adapter while one shows waits, and shows once the shown one
/// resumes. Pause pauses every adapter, and Continue from a pause continues every paused one.</item>
/// <item>The session ends when any adapter ends: closing the browser stops the server, as stopping the server closes
/// the browser.</item>
/// </list>
/// With <c>independent</c> (M14c, multiple startup projects), each adapter debugs a program of its own: the session
/// ends when the last one ends, every thread is named after its program, and <see cref="PartChanged"/> reports each
/// program's process and exit.
/// </summary>
public sealed class CompoundDebugAdapter : IDebugAdapter
{
    /// <summary>The most adapters one session holds (ids keep the index in their low bits).</summary>
    public const int MaxParts = 8;

    private readonly Lock _gate = new();
    private readonly List<Part> _parts = [];
    private readonly Dictionary<string, (SourceBreakpoint[] Specs, int[] Ids)> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Dictionary<int, Breakpoint>> _states = [];
    private readonly Queue<(Part Part, StoppedEvent Stop)> _waiting = new();
    private readonly SemaphoreSlim _raising = new(1, 1);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FunctionBreakpoint[]? _functions;
    private (string[] Filters, ExceptionFilterOptions[]? Options)? _exceptions;
    private Part? _shown;
    private string? _shownReason;
    private int _nextId;
    private int _ending;

    public CompoundDebugAdapter(IDebugAdapter primary, string name, bool independent = false)
    {
        Primary = primary;
        Independent = independent;
        var part = new Part(primary, name, 0);
        Attach(part);
        _ = independent
            ? primary.Closed.ContinueWith(_ => EndAsync(part), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap()
            : primary.Closed.ContinueWith(_ => _closed.TrySetResult(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Each adapter debugs a program of its own, and the session lasts until the last one ends (multiple startup projects).</summary>
    public bool Independent { get; }

    /// <summary>A program of an independent session started (its process id) or ended (its exit code).</summary>
    public event EventHandler<CompoundPartEvent>? PartChanged;

    /// <summary>The adapters' names, in the order they joined.</summary>
    public IReadOnlyList<string> PartNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _parts.Select(p => p.Name)];
            }
        }
    }

    private sealed class Part(IDebugAdapter adapter, string name, int index)
    {
        public IDebugAdapter Adapter { get; } = adapter;

        public string Name { get; } = name;

        public int Index { get; } = index;

        /// <summary>This adapter's breakpoint ids → the session's.</summary>
        public Dictionary<int, int> Ids { get; } = [];

        public bool Stopped { get; set; }

        /// <summary>The thread of this adapter's last stop (its own id).</summary>
        public int StoppedThread { get; set; }

        /// <summary>It couldn't join the session (its launch failed), or its program ended: requests and events skip it.</summary>
        public bool Ended { get; set; }

        /// <summary>Its program's exit code (independent sessions).</summary>
        public int? ExitCode { get; set; }
    }

    /// <summary>The adapter the session was launched with.</summary>
    public IDebugAdapter Primary { get; }

    public Capabilities Capabilities => Primary.Capabilities;

    public Task Initialized => Primary.Initialized;

    public Task Closed => _closed.Task;

    public event Func<DapEvent, Task>? EventReceived;

    public event EventHandler<string>? Log;

    /// <summary>The adapters in the session.</summary>
    public int PartCount => Parts().Count;

    private static DapJsonContext Json => DapJsonContext.Default;

    private void Attach(Part part)
    {
        part.Adapter.EventReceived += e => OnEventAsync(part, e);
        part.Adapter.Log += (_, line) => Log?.Invoke(this, part.Index == 0 ? line : $"[{part.Name}] {line}");
        lock (_gate)
        {
            _parts.Add(part);
        }
    }

    /// <summary>
    /// Adds <paramref name="adapter"/> to the running session: <paramref name="launch"/> starts its debuggee while the
    /// session's breakpoints and exception settings are replayed to it, then it is configured.
    /// </summary>
    public async Task AddAsync(IDebugAdapter adapter, string name, Func<IDebugAdapter, Task> launch, CancellationToken cancellationToken)
    {
        Part part;
        lock (_gate)
        {
            if (_parts.Count >= MaxParts)
            {
                throw new InvalidOperationException($"A debug session holds at most {MaxParts} adapters.");
            }

            part = new Part(adapter, name, _parts.Count);
        }

        Attach(part);
        _ = adapter.Closed.ContinueWith(_ => EndAsync(part), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        try
        {
            var launching = launch(adapter);
            await Task.WhenAny(launching, adapter.Initialized.WaitAsync(cancellationToken)).ConfigureAwait(false);
            if (launching.IsFaulted || launching.IsCanceled)
            {
                await launching.ConfigureAwait(false);
            }

            await adapter.Initialized.WaitAsync(cancellationToken).ConfigureAwait(false);
            await ReplayAsync(part, cancellationToken).ConfigureAwait(false);
            await adapter.ConfigurationDoneAsync(cancellationToken).ConfigureAwait(false);
            await launching.ConfigureAwait(false);
        }
        catch
        {
            // The rest of the session goes on without it (an independent session ends if it was the last one).
            if (Independent)
            {
                await EndAsync(part).ConfigureAwait(false);
            }
            else
            {
                lock (_gate)
                {
                    part.Ended = true;
                    Unqueue(part);
                }
            }

            await adapter.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReplayAsync(Part part, CancellationToken cancellationToken)
    {
        List<(string Path, SourceBreakpoint[] Specs, int[] Ids)> files;
        FunctionBreakpoint[]? functions;
        (string[] Filters, ExceptionFilterOptions[]? Options)? exceptions;
        lock (_gate)
        {
            files = [.. _breakpoints.Select(b => (b.Key, b.Value.Specs, b.Value.Ids))];
            functions = _functions;
            exceptions = _exceptions;
        }

        foreach (var (path, specs, ids) in files)
        {
            var states = await SendBreakpointsAsync(part, path, specs, ids, cancellationToken).ConfigureAwait(false);
            foreach (var state in states)
            {
                await RaiseAsync(DapEvents.Breakpoint, JsonSerializer.SerializeToElement(new BreakpointEvent("changed", state), Json.BreakpointEvent)).ConfigureAwait(false);
            }
        }

        if (functions is { Length: > 0 })
        {
            await part.Adapter.SetFunctionBreakpointsAsync(functions, cancellationToken).ConfigureAwait(false);
        }

        if (exceptions is { } settings)
        {
            await part.Adapter.SetExceptionBreakpointsAsync(settings.Filters, cancellationToken, settings.Options).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ ids

    private static int Id(int value, Part part) => value * MaxParts + part.Index;

    /// <summary>A variables reference: 0 (none) stays 0.</summary>
    private static int Reference(int value, Part part) => value == 0 ? 0 : Id(value, part);

    private (Part Part, int Value) Owner(int id, string command)
    {
        lock (_gate)
        {
            var index = id % MaxParts;
            return index < _parts.Count ? (_parts[index], id / MaxParts) : throw new DapException(command, "That debug target has ended.");
        }
    }

    /// <summary>The adapters still in the session.</summary>
    private List<Part> Parts()
    {
        lock (_gate)
        {
            return [.. _parts.Where(p => !p.Ended)];
        }
    }

    // ------------------------------------------------------------------ events

    private async Task OnEventAsync(Part part, DapEvent e)
    {
        if (part.Ended)
        {
            return;
        }

        switch (e.Name)
        {
            case DapEvents.Initialized:
                return;
            case DapEvents.Stopped when DebugAdapter.Read(e.Body, Json.StoppedEvent) is { } stopped:
                StoppedEvent? show = null;
                lock (_gate)
                {
                    part.Stopped = true;
                    part.StoppedThread = stopped.ThreadId ?? 0;
                    var mapped = stopped with
                    {
                        ThreadId = stopped.ThreadId is { } thread ? Id(thread, part) : null,
                        HitBreakpointIds = stopped.HitBreakpointIds?.Select(id => part.Ids.GetValueOrDefault(id)).Where(id => id != 0).ToArray(),
                    };
                    if (_shown is null || _shown == part)
                    {
                        _shown = part;
                        _shownReason = stopped.Reason;
                        show = mapped;
                    }
                    else
                    {
                        _waiting.Enqueue((part, mapped));
                    }
                }

                if (show is not null)
                {
                    await RaiseAsync(e.Name, JsonSerializer.SerializeToElement(show, Json.StoppedEvent)).ConfigureAwait(false);
                }

                return;
            case DapEvents.Continued when DebugAdapter.Read(e.Body, Json.ContinuedEvent) is { } continued:
                bool forward;
                lock (_gate)
                {
                    part.Stopped = false;
                    Unqueue(part);
                    forward = _shown == part;
                    if (forward)
                    {
                        _shown = null;
                    }
                }

                if (forward)
                {
                    await RaiseAsync(e.Name, JsonSerializer.SerializeToElement(continued with { ThreadId = Id(continued.ThreadId, part) }, Json.ContinuedEvent)).ConfigureAwait(false);
                    ShowNext();
                }

                return;
            case DapEvents.Thread when DebugAdapter.Read(e.Body, Json.ThreadEvent) is { } thread:
                await RaiseAsync(e.Name, JsonSerializer.SerializeToElement(thread with { ThreadId = Id(thread.ThreadId, part) }, Json.ThreadEvent)).ConfigureAwait(false);
                return;
            case DapEvents.Breakpoint when DebugAdapter.Read(e.Body, Json.BreakpointEvent) is { Breakpoint.Id: { } partId } changed:
                Breakpoint? merged;
                lock (_gate)
                {
                    if (!part.Ids.TryGetValue(partId, out var ours))
                    {
                        return; // one the adapter set itself
                    }

                    merged = Merge(ours, part, changed.Breakpoint);
                }

                await RaiseAsync(e.Name, JsonSerializer.SerializeToElement(changed with { Breakpoint = merged }, Json.BreakpointEvent)).ConfigureAwait(false);
                return;
            case DapEvents.Terminated:
                await EndAsync(part).ConfigureAwait(false);
                return;
            case DapEvents.Process when Independent && DebugAdapter.Read(e.Body, Json.ProcessEvent) is { SystemProcessId: { } pid }:
                PartChanged?.Invoke(this, new CompoundPartEvent(part.Name, part.Index, pid, Ended: false, ExitCode: null));
                if (part.Index == 0)
                {
                    await RaiseAsync(e.Name, e.Body).ConfigureAwait(false);
                }

                return;
            case DapEvents.Exited when Independent && DebugAdapter.Read(e.Body, Json.ExitedEvent) is { } exited:
                part.ExitCode = exited.ExitCode;
                if (part.Index == 0)
                {
                    await RaiseAsync(e.Name, e.Body).ConfigureAwait(false);
                }
                else
                {
                    var text = $"[{part.Name}] The program has exited with code {exited.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)} (0x{exited.ExitCode:x}).\n";
                    await RaiseAsync(DapEvents.Output, JsonSerializer.SerializeToElement(new OutputEvent(text, "console"), Json.OutputEvent)).ConfigureAwait(false);
                }

                return;
            case DapEvents.Exited or DapEvents.Process when part.Index != 0:
                // The browser's own process: the session's debuggee is the primary's.
                return;
            default:
                await RaiseAsync(e.Name, e.Body).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>The session's state of breakpoint <paramref name="id"/> after <paramref name="part"/> reported <paramref name="state"/>: bound when any adapter bound it.</summary>
    private Breakpoint Merge(int id, Part part, Breakpoint state)
    {
        if (!_states.TryGetValue(id, out var states))
        {
            _states[id] = states = [];
        }

        states[part.Index] = state;
        var best = states.Values.FirstOrDefault(s => s.Verified) ?? states.GetValueOrDefault(0) ?? state;
        return best with { Id = id };
    }

    private void Unqueue(Part part)
    {
        var keep = _waiting.Where(w => w.Part != part).ToList();
        _waiting.Clear();
        foreach (var item in keep)
        {
            _waiting.Enqueue(item);
        }
    }

    /// <summary>Shows the next waiting stop, if none shows (off the caller's event, which may still hold the event lock).</summary>
    private void ShowNext()
    {
        StoppedEvent? next = null;
        lock (_gate)
        {
            while (_shown is null && _waiting.TryDequeue(out var waiting))
            {
                if (waiting.Part.Stopped)
                {
                    _shown = waiting.Part;
                    _shownReason = waiting.Stop.Reason;
                    next = waiting.Stop;
                }
            }
        }

        if (next is not null)
        {
            _ = Task.Run(() => RaiseAsync(DapEvents.Stopped, JsonSerializer.SerializeToElement(next, Json.StoppedEvent)));
        }
    }

    /// <summary>
    /// One adapter ended: the others are disconnected (terminating their debuggees), then the session ends once. In an
    /// independent session the others go on, and the session ends with the last one.
    /// </summary>
    private async Task EndAsync(Part part)
    {
        if (Independent)
        {
            bool last;
            lock (_gate)
            {
                if (part.Ended)
                {
                    return;
                }

                part.Ended = true;
                part.Stopped = false;
                Unqueue(part);
                if (_shown == part)
                {
                    _shown = null;
                }

                last = _parts.All(p => p.Ended);
            }

            PartChanged?.Invoke(this, new CompoundPartEvent(part.Name, part.Index, null, Ended: true, part.ExitCode));
            if (!last)
            {
                ShowNext();
                return;
            }

            if (Interlocked.Exchange(ref _ending, 1) == 0)
            {
                await RaiseAsync(DapEvents.Terminated, default).ConfigureAwait(false);
            }

            _closed.TrySetResult();
            return;
        }

        if (part.Ended || Interlocked.Exchange(ref _ending, 1) == 1)
        {
            return;
        }

        foreach (var other in Parts().Where(p => p != part).Reverse())
        {
            try
            {
                await other.Adapter.DisconnectAsync(terminateDebuggee: true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DapException or DapConnectionLostException or TimeoutException or IOException or ObjectDisposedException)
            {
            }
        }

        await RaiseAsync(DapEvents.Terminated, default).ConfigureAwait(false);
        _closed.TrySetResult();
    }

    private async Task RaiseAsync(string name, JsonElement body)
    {
        if (EventReceived is not { } handler)
        {
            return;
        }

        // Events of several adapters reach the session one at a time, in each adapter's order.
        await _raising.WaitAsync().ConfigureAwait(false);
        try
        {
            await handler(new DapEvent(name, body)).ConfigureAwait(false);
        }
        finally
        {
            _raising.Release();
        }
    }

    // ------------------------------------------------------------------ requests

    public Task ConfigurationDoneAsync(CancellationToken cancellationToken) => Primary.ConfigurationDoneAsync(cancellationToken);

    public async Task<Breakpoint[]> SetBreakpointsAsync(string path, SourceBreakpoint[] breakpoints, CancellationToken cancellationToken)
    {
        int[] ids;
        List<Part> parts;
        lock (_gate)
        {
            if (_breakpoints.TryGetValue(path, out var previous))
            {
                // The file's previous breakpoints are replaced: forget their ids.
                foreach (var stale in previous.Ids)
                {
                    _states.Remove(stale);
                }

                foreach (var part in _parts)
                {
                    foreach (var key in part.Ids.Where(p => previous.Ids.Contains(p.Value)).Select(p => p.Key).ToList())
                    {
                        part.Ids.Remove(key);
                    }
                }
            }

            ids = [.. breakpoints.Select(_ => ++_nextId)];
            _breakpoints[path] = (breakpoints, ids);
            parts = [.. _parts.Where(p => !p.Ended)];
        }

        Breakpoint[] result = [];
        foreach (var part in parts)
        {
            result = await SendBreakpointsAsync(part, path, breakpoints, ids, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Sends a file's breakpoints to one adapter; returns the session's merged states.</summary>
    private async Task<Breakpoint[]> SendBreakpointsAsync(Part part, string path, SourceBreakpoint[] specs, int[] ids, CancellationToken cancellationToken)
    {
        Breakpoint[] response;
        try
        {
            response = await part.Adapter.SetBreakpointsAsync(path, specs, cancellationToken).ConfigureAwait(false);
        }
        catch (DapException ex) when (part.Index != 0)
        {
            response = [.. specs.Select(_ => new Breakpoint(Verified: false, Message: ex.Message))];
        }

        var result = new Breakpoint[ids.Length];
        lock (_gate)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var state = i < response.Length ? response[i] : new Breakpoint(Verified: false);
                if (state.Id is { } partId)
                {
                    part.Ids[partId] = ids[i];
                }

                result[i] = Merge(ids[i], part, state);
            }
        }

        return result;
    }

    public async Task<Breakpoint[]> SetFunctionBreakpointsAsync(FunctionBreakpoint[] breakpoints, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _functions = breakpoints;
        }

        Breakpoint[]? result = null;
        foreach (var part in Parts())
        {
            var states = await part.Adapter.SetFunctionBreakpointsAsync(breakpoints, cancellationToken).ConfigureAwait(false);
            result = result is null ? states : [.. result.Select((b, i) => !b.Verified && i < states.Length && states[i].Verified ? states[i] : b)];
        }

        return result ?? [];
    }

    public async Task SetExceptionBreakpointsAsync(string[] filters, CancellationToken cancellationToken, ExceptionFilterOptions[]? options = null)
    {
        lock (_gate)
        {
            _exceptions = (filters, options);
        }

        foreach (var part in Parts())
        {
            await part.Adapter.SetExceptionBreakpointsAsync(filters, cancellationToken, options).ConfigureAwait(false);
        }
    }

    public Task ContinueAsync(int threadId, CancellationToken cancellationToken) =>
        ResumeAsync(threadId, "continue", (adapter, thread) => adapter.ContinueAsync(thread, cancellationToken), cancellationToken);

    public Task NextAsync(int threadId, CancellationToken cancellationToken) =>
        ResumeAsync(threadId, "next", (adapter, thread) => adapter.NextAsync(thread, cancellationToken), cancellationToken);

    public Task StepInAsync(int threadId, CancellationToken cancellationToken) =>
        ResumeAsync(threadId, "stepIn", (adapter, thread) => adapter.StepInAsync(thread, cancellationToken), cancellationToken);

    public Task StepOutAsync(int threadId, CancellationToken cancellationToken) =>
        ResumeAsync(threadId, "stepOut", (adapter, thread) => adapter.StepOutAsync(thread, cancellationToken), cancellationToken);

    private async Task ResumeAsync(int threadId, string command, Func<IDebugAdapter, int, Task> resume, CancellationToken cancellationToken)
    {
        var (part, thread) = Owner(threadId, command);
        List<Part> paused = [];
        lock (_gate)
        {
            if (command == "continue" && _shown == part && _shownReason == "pause")
            {
                // Continue after Pause: every adapter the pause stopped goes on.
                paused = [.. _waiting.Where(w => w.Stop.Reason == "pause").Select(w => w.Part).Distinct()];
                foreach (var other in paused)
                {
                    Unqueue(other);
                    other.Stopped = false;
                }
            }

            part.Stopped = false;
            if (_shown == part)
            {
                _shown = null;
            }
        }

        await resume(part.Adapter, thread).ConfigureAwait(false);
        foreach (var other in paused)
        {
            await other.Adapter.ContinueAsync(other.StoppedThread, cancellationToken).ConfigureAwait(false);
        }

        ShowNext();
    }

    /// <summary>Pauses <paramref name="threadId"/>'s adapter, or with 0 every running adapter.</summary>
    public async Task PauseAsync(int threadId, CancellationToken cancellationToken)
    {
        if (threadId != 0)
        {
            var (part, thread) = Owner(threadId, "pause");
            await part.Adapter.PauseAsync(thread, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var part in Parts().Where(p => !p.Stopped))
        {
            try
            {
                await part.Adapter.PauseAsync(0, cancellationToken).ConfigureAwait(false);
            }
            catch (DapException ex)
            {
                Log?.Invoke(this, $"[{part.Name}] Pause failed: {ex.Message}");
            }
        }
    }

    public async Task<DapThread[]> ThreadsAsync(CancellationToken cancellationToken)
    {
        var result = new List<DapThread>();
        foreach (var part in Parts())
        {
            try
            {
                foreach (var thread in await part.Adapter.ThreadsAsync(cancellationToken).ConfigureAwait(false))
                {
                    result.Add(new DapThread(Id(thread.Id, part), part.Index == 0 && !Independent ? thread.Name : $"{part.Name}: {thread.Name}"));
                }
            }
            catch (DapException) when (part.Index != 0 || Independent)
            {
                // Not attached yet, or its program is ending.
            }
            catch (DapConnectionLostException) when (Independent)
            {
                // Its program ended meanwhile.
            }
        }

        return [.. result];
    }

    public async Task<StackTraceResponse> StackTraceAsync(int threadId, int levels, CancellationToken cancellationToken)
    {
        var (part, thread) = Owner(threadId, "stackTrace");
        var response = await part.Adapter.StackTraceAsync(thread, levels, cancellationToken).ConfigureAwait(false);
        return response with { StackFrames = [.. response.StackFrames.Select(f => f with { Id = Id(f.Id, part) })] };
    }

    public async Task<Scope[]> ScopesAsync(int frameId, CancellationToken cancellationToken)
    {
        var (part, frame) = Owner(frameId, "scopes");
        return [.. (await part.Adapter.ScopesAsync(frame, cancellationToken).ConfigureAwait(false)).Select(s => s with { VariablesReference = Reference(s.VariablesReference, part) })];
    }

    public async Task<Variable[]> VariablesAsync(int variablesReference, CancellationToken cancellationToken)
    {
        var (part, reference) = Owner(variablesReference, "variables");
        return [.. (await part.Adapter.VariablesAsync(reference, cancellationToken).ConfigureAwait(false)).Select(v => v with { VariablesReference = Reference(v.VariablesReference, part) })];
    }

    public async Task<EvaluateResponse> EvaluateAsync(string expression, int? frameId, string context, CancellationToken cancellationToken)
    {
        Part part;
        int? frame = null;
        if (frameId is { } id)
        {
            (part, var value) = Owner(id, "evaluate");
            frame = value;
        }
        else
        {
            lock (_gate)
            {
                part = _shown ?? _parts[0];
            }
        }

        var response = await part.Adapter.EvaluateAsync(expression, frame, context, cancellationToken).ConfigureAwait(false);
        return response with { VariablesReference = Reference(response.VariablesReference, part) };
    }

    public async Task<SetVariableResponse> SetVariableAsync(int variablesReference, string name, string value, CancellationToken cancellationToken)
    {
        var (part, reference) = Owner(variablesReference, "setVariable");
        var response = await part.Adapter.SetVariableAsync(reference, name, value, cancellationToken).ConfigureAwait(false);
        return response with { VariablesReference = response.VariablesReference is { } r ? Reference(r, part) : null };
    }

    public async Task<SetExpressionResponse> SetExpressionAsync(string expression, string value, int? frameId, CancellationToken cancellationToken)
    {
        var part = Parts()[0];
        int? frame = null;
        if (frameId is { } id)
        {
            (part, var partFrame) = Owner(id, "setExpression");
            frame = partFrame;
        }

        var response = await part.Adapter.SetExpressionAsync(expression, value, frame, cancellationToken).ConfigureAwait(false);
        return response with { VariablesReference = response.VariablesReference is { } r ? Reference(r, part) : null };
    }

    public Task<ExceptionInfoResponse> ExceptionInfoAsync(int threadId, CancellationToken cancellationToken)
    {
        var (part, thread) = Owner(threadId, "exceptionInfo");
        return part.Adapter.ExceptionInfoAsync(thread, cancellationToken);
    }

    /// <summary>Ends every adapter: the joined ones first (the browser closes), then the primary.</summary>
    public async Task DisconnectAsync(bool terminateDebuggee, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _ending, 1);
        foreach (var part in Parts().AsEnumerable().Reverse())
        {
            try
            {
                await part.Adapter.DisconnectAsync(terminateDebuggee, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DapException or DapConnectionLostException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var part in Parts().AsEnumerable().Reverse())
        {
            await part.Adapter.DisposeAsync().ConfigureAwait(false);
        }

        _closed.TrySetResult();
        _raising.Dispose();
    }
}

/// <summary>A program of an independent compound session (M14c): started with <see cref="ProcessId"/>, or ended with <see cref="ExitCode"/>.</summary>
public sealed record CompoundPartEvent(string Name, int Index, int? ProcessId, bool Ended, int? ExitCode);
