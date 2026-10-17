using Codalus.Debugging.Host.Mono;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Codalus.Debugging.Host.NetFx;

/// <summary>Starts or attaches to a desktop-CLR process (ICorDebug on Windows; fakes in the tests).</summary>
internal interface IClrDebugger
{
    /// <summary>Starts <paramref name="arguments"/>' program under the debugger; its console output goes to <paramref name="output"/> (category, text).</summary>
    IClrProcess Launch(NetFxDebugArguments arguments, Action<string, string> output);

    IClrProcess Attach(int processId);
}

/// <summary>
/// The desktop-CLR debugger adapter (M43b, ADR 0076): DAP on one side, a debugged .NET Framework process on the other
/// (<see cref="IClrProcess"/>). Breakpoints (with conditions) bound in modules as they load through their portable
/// PDBs, function breakpoints, stepping by source line with Just My Code and step filtering, threads and stacks,
/// locals and arguments (async state machines' hoisted variables too), members through fields and property getters,
/// evaluation of C# expressions (<see cref="ClrEvaluator"/>), exceptions (all with a type filter, or unhandled), and
/// changing primitive values. Handles (frames, variable references) live until the program runs again.
/// </summary>
internal sealed class NetFxDebugSession(DapServer dap, TextWriter log, IClrDebugger debugger)
{
    private const int MaxChildren = 100;
    private static readonly JsonElement s_empty = JsonElement.Parse("{}");
    private static readonly StringComparer s_paths = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly Lock _gate = new();
    private readonly List<(IClrModule Module, ClrModuleInfo Info)> _modules = [];
    private readonly Dictionary<string, List<BreakpointState>> _breakpoints = new(s_paths);
    private readonly List<BreakpointState> _functionBreakpoints = [];
    private readonly Dictionary<IClrBreakpoint, BreakpointState> _byBreakpoint = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, IClrFrame> _frames = [];
    private readonly Dictionary<int, Func<IReadOnlyList<Variable>>> _children = [];
    private readonly Dictionary<int, Lazy<Dictionary<string, IClrValue>>> _childValues = [];
    private readonly Dictionary<int, IClrValue> _exceptions = [];
    private readonly HashSet<int> _stoppedWhenThrown = [];
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _configured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IClrProcess? _process;
    private ClrTypeSystem? _types;
    private StepState? _step;
    private bool _userStopped;
    private bool _attached;
    private bool _justMyCode = true;
    private bool _stepFiltering = true;
    private bool _breakOnThrown;
    private bool _breakOnUnhandled = true;
    private string[] _thrownTypes = [];
    private bool _thrownTypesNegated;
    private ulong _lastException;
    private int _nextHandle = 1000;
    private int _nextBreakpoint = 1;

    /// <summary>How long a func-eval (a property getter, a method call) may run.</summary>
    public TimeSpan EvalTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Completes when the program has ended or the client disconnected.</summary>
    public Task Ended => _ended.Task;

    private sealed class BreakpointState(int id, string? path, int line, string? condition, string? function = null)
    {
        public int Id { get; } = id;

        public string? Path { get; } = path;

        public int Line { get; } = line;

        public string? Condition { get; } = condition;

        public string? Function { get; } = function;

        public int? BoundLine { get; set; }

        public List<(IClrModule Module, int Method, int Offset, IClrBreakpoint Breakpoint)> Bound { get; } = [];
    }

    /// <summary>A step in progress: what was asked, where from, and how many times it went on by itself.</summary>
    private sealed record StepState(ClrStepKind Kind, int ThreadId, IClrModule? Module, int Method)
    {
        public int Repeats { get; set; }

        /// <summary>Stepping out of a filtered property or operator: then on into the rest of the line.</summary>
        public bool ResumeInto { get; set; }
    }

    private IClrProcess Process => _process ?? throw new DapRequestException("The debugger isn't attached to a program.");

    private ClrTypeSystem Types => _types ??= new ClrTypeSystem(() =>
    {
        lock (_gate)
        {
            return [.. _modules];
        }
    });

    public async Task<JsonElement?> HandleAsync(string command, JsonElement arguments, CancellationToken cancellationToken)
    {
        var json = DapJsonContext.Default;
        switch (command)
        {
            case "initialize":
                return JsonSerializer.SerializeToElement(new Capabilities
                {
                    SupportsConfigurationDoneRequest = true,
                    SupportsFunctionBreakpoints = true,
                    SupportsConditionalBreakpoints = true,
                    SupportsExceptionInfoRequest = true,
                    SupportsExceptionFilterOptions = true,
                    SupportsSetVariable = true,
                    SupportTerminateDebuggee = true,
                    ExceptionBreakpointFilters =
                    [
                        new("all", "All exceptions", Description: "Breaks when an exception is thrown (in your code with Just My Code); its condition lists the exception types."),
                        new("user-unhandled", "Unhandled exceptions", Default: true),
                    ],
                }, json.Capabilities);
            case "launch":
                Start(Read(arguments, json.NetFxDebugArguments) ?? new NetFxDebugArguments(), attach: false);
                await dap.SendEventAsync(DapEvents.Initialized).ConfigureAwait(false);
                return null;
            case "attach":
                Start(Read(arguments, json.NetFxDebugArguments) ?? new NetFxDebugArguments(), attach: true);
                await dap.SendEventAsync(DapEvents.Initialized).ConfigureAwait(false);
                return null;
            case "setBreakpoints":
                return JsonSerializer.SerializeToElement(SetBreakpoints(Read(arguments, json.SetBreakpointsArguments)!), json.SetBreakpointsResponse);
            case "setFunctionBreakpoints":
                return JsonSerializer.SerializeToElement(SetFunctionBreakpoints(Read(arguments, json.SetFunctionBreakpointsArguments)?.Breakpoints ?? []), json.SetBreakpointsResponse);
            case "setExceptionBreakpoints":
                var exceptions = Read(arguments, json.SetExceptionBreakpointsArguments);
                SetExceptionBreakpoints(exceptions?.Filters ?? [], exceptions?.FilterOptions ?? []);
                return null;
            case "configurationDone":
                _configured.TrySetResult();
                return null;
            case "threads":
                return JsonSerializer.SerializeToElement(new ThreadsResponse(Threads()), json.ThreadsResponse);
            case "stackTrace":
                var stack = Read(arguments, json.StackTraceArguments)!;
                return JsonSerializer.SerializeToElement(StackTrace(stack.ThreadId, stack.StartFrame ?? 0, stack.Levels ?? 0), json.StackTraceResponse);
            case "scopes":
                return JsonSerializer.SerializeToElement(new ScopesResponse(Scopes(Read(arguments, json.ScopesArguments)!.FrameId)), json.ScopesResponse);
            case "variables":
                return JsonSerializer.SerializeToElement(new VariablesResponse([.. Variables(Read(arguments, json.VariablesArguments)!.VariablesReference)]), json.VariablesResponse);
            case "evaluate":
                var evaluate = Read(arguments, json.EvaluateArguments)!;
                return JsonSerializer.SerializeToElement(Evaluate(evaluate.Expression, evaluate.FrameId), json.EvaluateResponse);
            case "setVariable":
                return JsonSerializer.SerializeToElement(SetVariable(Read(arguments, json.SetVariableArguments)!), json.SetVariableResponse);
            case "setExpression":
                throw new DapRequestException("Assigning to expressions isn't supported for .NET Framework programs: change a variable in the Locals window.");
            case "continue":
                Resume();
                return JsonSerializer.SerializeToElement(new ContinueResponse(AllThreadsContinued: true), json.ContinueResponse);
            case "next":
                Step(Read(arguments, json.ThreadArguments)!.ThreadId, ClrStepKind.Over);
                return null;
            case "stepIn":
                Step(Read(arguments, json.ThreadArguments)!.ThreadId, ClrStepKind.Into);
                return null;
            case "stepOut":
                Step(Read(arguments, json.ThreadArguments)!.ThreadId, ClrStepKind.Out);
                return null;
            case "pause":
                await PauseAsync().ConfigureAwait(false);
                return null;
            case "exceptionInfo":
                return JsonSerializer.SerializeToElement(ExceptionInfo(Read(arguments, json.ThreadArguments)!.ThreadId), json.ExceptionInfoResponse);
            case "disconnect" or "terminate":
                Disconnect(Read(arguments, json.DisconnectArguments)?.TerminateDebuggee ?? !_attached);
                return null;
            default:
                return null;
        }
    }

    private static T? Read<T>(JsonElement arguments, JsonTypeInfo<T> info) =>
        arguments.ValueKind == JsonValueKind.Object ? arguments.Deserialize(info) : arguments.ValueKind == JsonValueKind.Undefined ? s_empty.Deserialize(info) : default;

    // ------------------------------------------------------------------ starting

    private void Start(NetFxDebugArguments arguments, bool attach)
    {
        if (_process is not null)
        {
            throw new DapRequestException("The debugger is already attached to a program.");
        }

        _justMyCode = arguments.JustMyCode;
        _stepFiltering = arguments.EnableStepFiltering;
        _attached = attach;
        if (attach)
        {
            var id = arguments.ProcessId ?? throw new DapRequestException("Attach needs a process id.");
            log.WriteLine($"Attaching to process {id.ToString(CultureInfo.InvariantCulture)} on the desktop CLR…");
            _process = debugger.Attach(id);
        }
        else
        {
            if (string.IsNullOrEmpty(arguments.Program))
            {
                throw new DapRequestException("Launch needs a program.");
            }

            log.WriteLine($"Starting {arguments.Program} on the desktop CLR…");
            _process = debugger.Launch(arguments, (category, text) => _ = dap.SendEventAsync(DapEvents.Output, new OutputEvent(text, category), DapJsonContext.Default.OutputEvent));
        }

        log.WriteLine($"Debugging process {_process.Id.ToString(CultureInfo.InvariantCulture)} (.NET Framework, CLR {_process.RuntimeVersion}).");
        _ = dap.SendEventAsync(DapEvents.Process, new ProcessEvent(Path.GetFileName(arguments.Program ?? "process"), _process.Id, IsLocalProcess: true, StartMethod: attach ? "attach" : "launch"), DapJsonContext.Default.ProcessEvent);
        _ = Task.Factory.StartNew(EventLoopAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    // ------------------------------------------------------------------ events

    private async Task EventLoopAsync()
    {
        var process = _process!;

        // Modules load while the client sets its breakpoints: events wait until it's done (configurationDone).
        await _configured.Task.ConfigureAwait(false);
        try
        {
            await foreach (var e in process.Events.ReadAllAsync().ConfigureAwait(false))
            {
                if (e is ClrExited exited)
                {
                    End(exited.ExitCode);
                    return;
                }

                bool stopped;
                lock (_gate)
                {
                    try
                    {
                        // Paused already (the user's pause raced the event): it's handled, but doesn't stop again.
                        stopped = (!_userStopped || e is not (ClrBreakpointHit or ClrStepCompleted or ClrUserBreak or ClrExceptionThrown)) && Handle(e);
                    }
                    catch (InvalidOperationException ex)
                    {
                        log.WriteLine($"{e.GetType().Name}: {ex.Message}");
                        stopped = false;
                    }
                }

                if (!stopped && !e.Continued)
                {
                    TryContinue(process);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }

        End(null);
    }

    private void TryContinue(IClrProcess process)
    {
        try
        {
            process.Continue();
        }
        catch (InvalidOperationException ex)
        {
            log.WriteLine("Continue: " + ex.Message);
        }
    }

    /// <summary>Handles one event (under the lock); true when the program stays stopped for the user.</summary>
    private bool Handle(ClrEvent e)
    {
        switch (e)
        {
            case ClrModuleLoaded loaded:
                LoadModule(loaded.Module);
                return false;
            case ClrModuleUnloaded unloaded:
                UnloadModule(unloaded.Module);
                return false;
            case ClrThreadStarted started:
                _ = dap.SendEventAsync(DapEvents.Thread, new ThreadEvent("started", started.Thread.Id), DapJsonContext.Default.ThreadEvent);
                return false;
            case ClrThreadExited exited:
                _ = dap.SendEventAsync(DapEvents.Thread, new ThreadEvent("exited", exited.Thread.Id), DapJsonContext.Default.ThreadEvent);
                return false;
            case ClrBreakpointHit hit:
                return BreakpointHit(hit);
            case ClrStepCompleted step:
                return StepCompleted(step.Thread);
            case ClrUserBreak userBreak:
                _process!.CancelStep();
                _step = null;
                Stop("pause", userBreak.Thread, description: "Debugger.Break()");
                return true;
            case ClrExceptionThrown thrown:
                return ExceptionThrown(thrown);
            case ClrLogMessage message:
                _ = dap.SendEventAsync(DapEvents.Output, new OutputEvent(message.Message, "console"), DapJsonContext.Default.OutputEvent);
                return false;
            case ClrNotice notice:
                _ = dap.SendEventAsync(DapEvents.Output, new OutputEvent(notice.Message + "\n", "console"), DapJsonContext.Default.OutputEvent);
                return false;
            default:
                return false;
        }
    }

    private void Stop(string reason, IClrThread thread, int[]? hitBreakpoints = null, string? text = null, string? description = null)
    {
        _userStopped = true;
        ClearHandles();
        _ = dap.SendEventAsync(DapEvents.Stopped, new StoppedEvent(reason, description, thread.Id, AllThreadsStopped: true, text, hitBreakpoints is { Length: > 0 } ? hitBreakpoints : null), DapJsonContext.Default.StoppedEvent);
    }

    private void End(int? exitCode)
    {
        if (_ended.TrySetResult())
        {
            if (exitCode is { } code)
            {
                _ = dap.SendEventAsync(DapEvents.Exited, new ExitedEvent(code), DapJsonContext.Default.ExitedEvent);
            }

            _ = dap.SendEventAsync(DapEvents.Terminated);
            lock (_gate)
            {
                foreach (var (_, info) in _modules)
                {
                    info.Dispose();
                }

                _modules.Clear();
            }
        }
    }

    // ------------------------------------------------------------------ modules

    private void LoadModule(IClrModule module)
    {
        ClrModuleInfo? info = null;
        string? problem = null;
        if (!module.IsDynamic && module.Path.Length > 0)
        {
            try
            {
                info = ClrModuleInfo.Open(module.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                problem = ex.Message;
            }
        }

        // Just My Code: the user's code is what has symbols and isn't optimized.
        var userCode = info is { HasSymbols: true, IsOptimized: false };
        if (info is not null)
        {
            _modules.Add((module, info));
            try
            {
                _process!.SetUserCode(module, userCode);
            }
            catch (InvalidOperationException ex)
            {
                log.WriteLine($"Just My Code for {info.AssemblyName}: {ex.Message}");
            }

            foreach (var (path, states) in _breakpoints)
            {
                Bind(path, states, module, info);
            }

            foreach (var state in _functionBreakpoints)
            {
                BindFunction(state, module, info);
            }
        }

        var name = module.IsDynamic || module.Path.Length == 0 ? "(dynamic module)" : Path.GetFileName(module.Path);
        _ = dap.SendEventAsync(DapEvents.Module, new ModuleEvent("new", new DapModule(
            JsonSerializer.SerializeToElement(ModuleId(module), DapJsonContext.Default.JsonElement),
            name,
            module.Path.Length == 0 ? null : module.Path,
            info?.SymbolStatus ?? problem ?? "No symbols.",
            info?.PdbPath,
            IsOptimized: info?.IsOptimized,
            IsUserCode: userCode)), DapJsonContext.Default.ModuleEvent);
    }

    private static JsonElement ModuleId(IClrModule module) =>
        JsonSerializer.SerializeToElement(RuntimeHelpersId(module).ToString(CultureInfo.InvariantCulture), DapJsonContext.Default.String);

    private static int RuntimeHelpersId(IClrModule module) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(module);

    private void UnloadModule(IClrModule module)
    {
        var index = _modules.FindIndex(m => ReferenceEquals(m.Module, module));
        if (index >= 0)
        {
            var info = _modules[index].Info;
            _modules.RemoveAt(index);
            foreach (var state in _breakpoints.Values.SelectMany(b => b).Concat(_functionBreakpoints))
            {
                state.Bound.RemoveAll(b => ReferenceEquals(b.Module, module));
            }

            info.Dispose();
        }

        _ = dap.SendEventAsync(DapEvents.Module, new ModuleEvent("removed", new DapModule(ModuleId(module), Path.GetFileName(module.Path))), DapJsonContext.Default.ModuleEvent);
    }

    // ------------------------------------------------------------------ breakpoints

    private SetBreakpointsResponse SetBreakpoints(SetBreakpointsArguments arguments)
    {
        var path = arguments.Source.Path is { } p ? Path.GetFullPath(p) : throw new DapRequestException("Breakpoints need a source file path.");
        List<BreakpointState> states = [];
        lock (_gate)
        {
            WhileStopped(() =>
            {
                if (_breakpoints.TryGetValue(path, out var old))
                {
                    Remove(old);
                }

                states = [.. arguments.Breakpoints.Select(b => new BreakpointState(_nextBreakpoint++, path, b.Line, string.IsNullOrWhiteSpace(b.Condition) ? null : b.Condition.Trim()))];
                if (states.Count == 0)
                {
                    _breakpoints.Remove(path);
                }
                else
                {
                    _breakpoints[path] = states;
                }

                foreach (var (module, info) in _modules)
                {
                    Bind(path, states, module, info);
                }
            });
        }

        return new SetBreakpointsResponse([.. states.Select(s => Describe(s))]);
    }

    /// <summary>
    /// Runs <paramref name="change"/> with the process stopped: ICorDebug changes breakpoints only in a stopped process,
    /// so one that runs (breakpoints set while it runs) is stopped for it and goes on after.
    /// </summary>
    private void WhileStopped(Action change)
    {
        var pause = _process is { } process && _configured.Task.IsCompleted && !_userStopped && !_ended.Task.IsCompleted;
        if (pause)
        {
            try
            {
                _process!.Pause();
            }
            catch (InvalidOperationException ex)
            {
                log.WriteLine("Stopping to change breakpoints: " + ex.Message);
                pause = false;
            }
        }

        try
        {
            change();
        }
        finally
        {
            if (pause)
            {
                TryContinue(_process!);
            }
        }
    }

    private static Breakpoint Describe(BreakpointState state) =>
        new(state.Id, state.BoundLine is not null, state.BoundLine is null ? (state.Function is null ? "The breakpoint will be bound when its code loads." : "The function breakpoint will be bound when its method loads.") : null,
            state.Path is { } path ? new Source(Path.GetFileName(path), path) : null, state.BoundLine ?? (state.Path is null ? null : state.Line));

    private void Remove(List<BreakpointState> states)
    {
        foreach (var bound in states.SelectMany(s => s.Bound))
        {
            _byBreakpoint.Remove(bound.Breakpoint);
            try
            {
                bound.Breakpoint.Activate(false);
            }
            catch (InvalidOperationException)
            {
                // The module unloaded, or the process ended.
            }
        }
    }

    /// <summary>Binds <paramref name="states"/> of <paramref name="path"/> in one module, where its PDB has the file.</summary>
    private void Bind(string path, List<BreakpointState> states, IClrModule module, ClrModuleInfo info)
    {
        if (info.MatchDocument(path) is not { } document)
        {
            return;
        }

        foreach (var state in states)
        {
            foreach (var (method, offset, line) in info.Locations(document, state.Line))
            {
                BindAt(state, module, method, offset, line);
            }
        }
    }

    private void BindAt(BreakpointState state, IClrModule module, int method, int offset, int line)
    {
        if (state.Bound.Any(b => ReferenceEquals(b.Module, module) && b.Method == method && b.Offset == offset))
        {
            return;
        }

        IClrBreakpoint breakpoint;
        try
        {
            breakpoint = Process.CreateBreakpoint(module, method, offset);
        }
        catch (InvalidOperationException ex)
        {
            log.WriteLine($"Breakpoint {state.Id.ToString(CultureInfo.InvariantCulture)} couldn't be set: {ex.Message}");
            return;
        }

        state.Bound.Add((module, method, offset, breakpoint));
        _byBreakpoint[breakpoint] = state;
        var newlyBound = state.BoundLine is null;
        state.BoundLine ??= line;
        if (newlyBound && _configured.Task.IsCompleted)
        {
            _ = dap.SendEventAsync(DapEvents.Breakpoint, new BreakpointEvent("changed", Describe(state)), DapJsonContext.Default.BreakpointEvent);
        }
    }

    private SetBreakpointsResponse SetFunctionBreakpoints(FunctionBreakpoint[] breakpoints)
    {
        lock (_gate)
        {
            WhileStopped(() =>
            {
                Remove(_functionBreakpoints);
                _functionBreakpoints.Clear();
                _functionBreakpoints.AddRange(breakpoints.Select(b => new BreakpointState(_nextBreakpoint++, null, 0, string.IsNullOrWhiteSpace(b.Condition) ? null : b.Condition.Trim(), b.Name.Trim())));
                foreach (var (module, info) in _modules)
                {
                    foreach (var state in _functionBreakpoints)
                    {
                        BindFunction(state, module, info);
                    }
                }
            });

            return new SetBreakpointsResponse([.. _functionBreakpoints.Select(Describe)]);
        }
    }

    /// <summary>A function breakpoint (<c>Method</c>, <c>Type.Method</c> or <c>Ns.Type.Method</c>, no parameters) at each matching method's first line.</summary>
    private void BindFunction(BreakpointState state, IClrModule module, ClrModuleInfo info)
    {
        var name = state.Function!;
        if (name.IndexOf('(', StringComparison.Ordinal) is var paren and > 0)
        {
            name = name[..paren].Trim();
        }

        foreach (var method in info.FindMethods(name))
        {
            var first = info.SequencePoints(method).Where(p => !p.IsHidden).OrderBy(p => p.Offset).Cast<ClrSequencePoint?>().FirstOrDefault();
            BindAt(state, module, method, first?.Offset ?? 0, first?.StartLine ?? 0);
        }
    }

    private bool BreakpointHit(ClrBreakpointHit hit)
    {
        if (!_byBreakpoint.TryGetValue(hit.Breakpoint, out var state))
        {
            return false; // removed meanwhile
        }

        if (state.Condition is { } condition)
        {
            try
            {
                if (TopFrame(hit.Thread) is not { } frame || !Evaluator(frame).IsTrue(condition))
                {
                    return false;
                }
            }
            catch (InvalidOperationException ex)
            {
                _ = dap.SendEventAsync(DapEvents.Output, new OutputEvent($"The breakpoint's condition '{condition}' couldn't be evaluated: {ex.Message}\n", "console"), DapJsonContext.Default.OutputEvent);
            }
        }

        Process.CancelStep();
        _step = null;
        Stop(state.Function is null ? "breakpoint" : "function breakpoint", hit.Thread, [state.Id]);
        return true;
    }

    private void SetExceptionBreakpoints(string[] filters, ExceptionFilterOptions[] options)
    {
        lock (_gate)
        {
            var all = options.FirstOrDefault(o => o.FilterId == "all");
            _breakOnThrown = filters.Contains("all") || all is not null;
            _breakOnUnhandled = filters.Contains("user-unhandled") || filters.Contains("all");
            var condition = all?.Condition?.Trim() ?? string.Empty;
            _thrownTypesNegated = condition.StartsWith('!');
            _thrownTypes = [.. condition.TrimStart('!').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
    }

    /// <summary>Whether an exception of this type breaks when thrown (netcoredbg's condition: type names, a leading ! negating them).</summary>
    internal static bool BreaksWhenThrown(string typeName, string[] types, bool negated) =>
        types.Length == 0 || (types.Contains(typeName, StringComparer.Ordinal) != negated);

    /// <summary>
    /// An exception notification. Thrown: with Just My Code, where it's thrown in the user's code (a first chance there,
    /// or the runtime's user first chance when it reaches the user's code), once per exception; without, every first
    /// chance. Unhandled: always, when asked.
    /// </summary>
    private bool ExceptionThrown(ClrExceptionThrown thrown)
    {
        // The runtime's user first chance of an exception it stopped at already when it was thrown in the user's code
        // (on the same thread, right after): not again. Its address may have changed since (a func-eval may collect).
        var stoppedWhenThrown = _stoppedWhenThrown.Remove(thrown.Thread.Id);
        if (thrown.Stage == ClrExceptionStage.UserFirstChance && stoppedWhenThrown)
        {
            return false;
        }

        var value = SafeValue(Live(thrown.Thread.CurrentException));
        var typeName = value is null ? "System.Exception" : Types.FullName(value.Type);
        var thrownHere = _breakOnThrown && BreaksWhenThrown(typeName, _thrownTypes, _thrownTypesNegated);
        var breaks = thrown.Stage switch
        {
            ClrExceptionStage.Unhandled => _breakOnUnhandled,
            ClrExceptionStage.FirstChance => thrownHere && (!_justMyCode || IsUserCode(TopFrame(thrown.Thread))),
            ClrExceptionStage.UserFirstChance => thrownHere && _justMyCode && (value is null || value.Address == 0 || value.Address != _lastException),
            _ => false,
        };
        if (!breaks)
        {
            return false;
        }

        _lastException = value?.Address ?? 0;
        if (thrown.Stage == ClrExceptionStage.FirstChance)
        {
            _stoppedWhenThrown.Add(thrown.Thread.Id);
        }

        if (value is not null)
        {
            _exceptions[thrown.Thread.Id] = value;
        }

        Process.CancelStep();
        _step = null;
        var unhandled = thrown.Stage == ClrExceptionStage.Unhandled;
        Stop("exception", thrown.Thread, text: typeName, description: unhandled ? $"Unhandled exception: {typeName}" : $"Exception thrown: {typeName}");
        return true;
    }

    // ------------------------------------------------------------------ running

    private void RequireStopped()
    {
        if (!_userStopped)
        {
            throw new DapRequestException("The program is running.");
        }
    }

    private void Resume()
    {
        lock (_gate)
        {
            RequireStopped();
            _userStopped = false;
            ClearHandles();
            _exceptions.Clear();
        }

        TryContinue(Process);
    }

    private void Step(int threadId, ClrStepKind kind)
    {
        lock (_gate)
        {
            RequireStopped();
            var thread = Thread(threadId);
            _userStopped = false;
            ClearHandles();
            _exceptions.Clear();
            var top = TopFrame(thread);
            _step = new StepState(kind, threadId, top?.Module, top?.MethodToken ?? 0);
            StartStep(thread, kind, top);
        }
    }

    /// <summary>Steps from <paramref name="frame"/>: over or into the rest of its source line (its sequence point's range), or out.</summary>
    private void StartStep(IClrThread thread, ClrStepKind kind, IClrFrame? frame)
    {
        var ranges = new List<(int, int)>();
        if (kind != ClrStepKind.Out && frame?.Module is { } module && Types.Info(module) is { } info && info.RangeAt(frame.MethodToken, frame.IlOffset) is { } range)
        {
            ranges.Add(range);
        }

        Process.Step(thread, new ClrStep(kind, ranges, _justMyCode));
    }

    /// <summary>A step ended: stops on a line start in the user's code, or goes on (a hidden line, mid-line, code that isn't the user's, a filtered property).</summary>
    private bool StepCompleted(IClrThread thread)
    {
        if (_step is not { } step || step.ThreadId != thread.Id)
        {
            Stop("step", thread);
            return true;
        }

        var frame = TopFrame(thread);
        var info = frame?.Module is { } module ? Types.Info(module) : null;
        if (frame is null || step.Repeats++ > 50)
        {
            _step = null;
            Stop("step", thread);
            return true;
        }

        var userCode = info is { HasSymbols: true } && !info.IsOptimized && !info.IsNonUserCode(frame.MethodToken);
        if (_justMyCode && !userCode)
        {
            // Code without symbols (or [DebuggerNonUserCode]): out again, to the user's code.
            StartStep(thread, ClrStepKind.Out, frame);
            return true;
        }

        if (step.ResumeInto)
        {
            step.ResumeInto = false;
            StartStep(thread, ClrStepKind.Into, frame);
            return true;
        }

        var sameMethod = ReferenceEquals(frame.Module, step.Module) && frame.MethodToken == step.Method;
        if (_stepFiltering && step.Kind == ClrStepKind.Into && !sameMethod && info is not null && IsFiltered(info.Method(frame.MethodToken)))
        {
            // Step filtering: a property accessor or an operator is stepped over, then on into the rest of the line.
            step.ResumeInto = true;
            StartStep(thread, ClrStepKind.Out, frame);
            return true;
        }

        if (info is not null && info.SequencePointAt(frame.MethodToken, frame.IlOffset) is { } point && (point.IsHidden || (point.Offset != frame.IlOffset && step.Kind != ClrStepKind.Out)))
        {
            // A hidden sequence point (compiler code) or the middle of a line: on to the next line start.
            StartStep(thread, ClrStepKind.Over, frame);
            return true;
        }

        _step = null;
        Stop("step", thread);
        return true;
    }

    private bool IsUserCode(IClrFrame? frame) =>
        frame?.Module is { } module && Types.Info(module) is { HasSymbols: true, IsOptimized: false } info && !info.IsNonUserCode(frame.MethodToken);

    private static bool IsFiltered(ClrMethodInfo method) =>
        method.IsSpecialName && (method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name.StartsWith("set_", StringComparison.Ordinal) || method.Name.StartsWith("op_", StringComparison.Ordinal));

    private async Task PauseAsync()
    {
        IClrThread? thread;
        lock (_gate)
        {
            if (_userStopped)
            {
                return;
            }

            Process.Pause();
            _userStopped = true;
            Process.CancelStep();
            _step = null;
            ClearHandles();
            var threads = Process.Threads();
            thread = threads.FirstOrDefault(t => t.Frames().Any(f => f.Module is { } m && Types.Info(m) is { HasSymbols: true })) ?? (threads.Count > 0 ? threads[0] : null);
        }

        if (thread is not null)
        {
            await dap.SendEventAsync(DapEvents.Stopped, new StoppedEvent("pause", ThreadId: thread.Id, AllThreadsStopped: true), DapJsonContext.Default.StoppedEvent).ConfigureAwait(false);
        }
    }

    private void Disconnect(bool terminate)
    {
        if (_process is { } process)
        {
            try
            {
                if (terminate)
                {
                    process.Terminate();
                }
                else
                {
                    lock (_gate)
                    {
                        Remove([.. _breakpoints.Values.SelectMany(b => b), .. _functionBreakpoints]);
                        _breakOnThrown = _breakOnUnhandled = false;
                    }

                    process.Detach();
                }
            }
            catch (InvalidOperationException ex)
            {
                log.WriteLine((terminate ? "Terminate: " : "Detach: ") + ex.Message);
            }
        }

        End(null);
    }

    // ------------------------------------------------------------------ threads and stacks

    private IClrThread Thread(int id) =>
        Process.Threads().FirstOrDefault(t => t.Id == id) ?? throw new DapRequestException($"There's no thread {id.ToString(CultureInfo.InvariantCulture)}.");

    private DapThread[] Threads()
    {
        lock (_gate)
        {
            return [.. Process.Threads().Select(t => new DapThread(t.Id, ThreadName(t)))];
        }
    }

    private string ThreadName(IClrThread thread)
    {
        var name = SafeValue(thread.ThreadObject) is { Kind: ClrValueKind.Object } value ? SafeValue(() => Types.Field(value, "m_Name")) : null;
        return name is { Kind: ClrValueKind.String, Text: { Length: > 0 } text } ? text : "Thread " + thread.Id.ToString(CultureInfo.InvariantCulture);
    }

    private static IClrValue? SafeValue(Func<IClrValue?> read)
    {
        try
        {
            return read();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The innermost IL frame (with a module) of a thread.</summary>
    private static IClrFrame? TopFrame(IClrThread thread) => thread.Frames().FirstOrDefault(f => f.Module is not null);

    private StackTraceResponse StackTrace(int threadId, int start, int levels)
    {
        lock (_gate)
        {
            RequireStopped();
            var frames = Thread(threadId).Frames();
            var result = new List<StackFrame>();
            foreach (var frame in frames.Skip(start).Take(levels > 0 ? levels : int.MaxValue))
            {
                var id = _nextHandle++;
                _frames[id] = frame;
                if (frame.Module is not { } module)
                {
                    result.Add(new StackFrame(id, frame.Description ?? "[External Code]", null, 0, 0, PresentationHint: "label"));
                    continue;
                }

                var info = Types.Info(module);
                var name = info?.MethodDisplayName(frame.MethodToken) ?? $"[{Path.GetFileName(module.Path)}]";
                var point = info?.LineAt(frame.MethodToken, frame.IlOffset);
                var file = point?.Document;
                var hasSource = file is { Length: > 0 } && File.Exists(file);
                var userCode = info is { HasSymbols: true, IsOptimized: false } && !info.IsNonUserCode(frame.MethodToken);
                result.Add(new StackFrame(
                    id,
                    name,
                    hasSource ? new Source(Path.GetFileName(file), file) : null,
                    hasSource ? point!.Value.StartLine : 0,
                    hasSource ? Math.Max(1, point!.Value.StartColumn) : 0,
                    hasSource ? point!.Value.EndLine : null,
                    hasSource ? point!.Value.EndColumn : null,
                    PresentationHint: hasSource && (userCode || !_justMyCode) ? null : "subtle",
                    ModuleId: ModuleId(module)));
            }

            return new StackTraceResponse([.. result], frames.Count);
        }
    }

    private IClrFrame Frame(int frameId) =>
        _frames.TryGetValue(frameId, out var frame) ? frame : throw new DapRequestException("The frame is no longer valid: the program has run since.");

    // ------------------------------------------------------------------ variables

    private Scope[] Scopes(int frameId)
    {
        lock (_gate)
        {
            var frame = Frame(frameId);
            var context = Context(frame);
            var reference = Handle(() => [.. Locals(context)], () => Values(context));
            return [new Scope("Locals", reference, PresentationHint: "locals")];
        }
    }

    /// <summary>
    /// A frame's names: <c>this</c>, the arguments and the locals in scope (by the PDB). In an async method or an
    /// iterator (a state machine's <c>MoveNext</c>), its hoisted variables and parameters, and the original <c>this</c>.
    /// </summary>
    private ClrFrameContext Context(IClrFrame frame)
    {
        var variables = new List<(string Name, Func<IClrValue?> Read)>();
        Func<IClrValue?>? self = null;
        (IClrModule, int)? declaring = null;
        string? ns = null;
        if (frame.Module is { } module && Types.Info(module) is { } info)
        {
            var method = info.Method(frame.MethodToken);
            var type = info.DeclaringType(frame.MethodToken);
            declaring = (module, type);
            var typeName = info.TypeFullName(type);
            ns = typeName.LastIndexOf('.') is var dot and > 0 ? typeName[..dot] : null;
            if (!method.IsStatic)
            {
                self = Live(() => frame.Argument(0));
            }

            for (var i = 0; i < method.ParameterNames.Count; i++)
            {
                var index = i + (method.IsStatic ? 0 : 1);
                variables.Add((method.ParameterNames[i], Live(() => frame.Argument(index))));
            }

            foreach (var (slot, name) in info.Locals(frame.MethodToken, frame.IlOffset))
            {
                variables.Add((name, Live(() => frame.Local(slot))));
            }

            // A state machine (async, iterator): its fields are the method's variables.
            if (!method.IsStatic && method.Name == "MoveNext" && ClrModuleInfo.DisplayName(typeName).Split('.')[^1].StartsWith('<'))
            {
                var machine = self!;
                self = null;
                foreach (var field in info.Fields(type).Where(f => !f.IsStatic))
                {
                    var fieldToken = field.Token;
                    Func<IClrValue?> read = () => machine() is { Kind: ClrValueKind.Object } m ? m.Field(module, type, fieldToken) : null;
                    if (field.Name == "<>4__this")
                    {
                        self = read;
                    }
                    else if (HoistedName(field.Name) is { } hoisted && variables.All(v => v.Name != hoisted))
                    {
                        variables.Add((hoisted, read));
                    }
                }

                // Its namespace and type are the original method's (the outer type).
                if (typeName.LastIndexOf('+') is var plus and > 0 && info.FindType(typeName[..plus]) is var outer and not 0)
                {
                    declaring = (module, outer);
                }
            }
        }

        return new ClrFrameContext(frame, variables, self, declaring, ns);
    }

    /// <summary>
    /// Reads a value as a <see cref="ClrLiveValue"/>: one kept for later (a variable to expand, a stop's exception) is
    /// read again after a func-eval, which neuters the values ICorDebug handed out before it.
    /// </summary>
    private Func<IClrValue?> Live(Func<IClrValue?> read) => () => ClrLiveValue.Read(Process, read);

    /// <summary>A state machine field's variable name: <c>&lt;total&gt;5__2</c> is <c>total</c>, a parameter keeps its name; compiler state is hidden (null).</summary>
    internal static string? HoistedName(string field)
    {
        if (!field.StartsWith('<'))
        {
            return field;
        }

        var close = field.IndexOf('>', StringComparison.Ordinal);
        return close > 1 && field.AsSpan(close + 1).StartsWith("5__", StringComparison.Ordinal) ? field[1..close] : null;
    }

    private List<Variable> Locals(ClrFrameContext context)
    {
        var result = new List<Variable>();
        if (context.This is { } self)
        {
            result.Add(VariableOf("this", SafeValue(self), "this"));
        }

        foreach (var (name, read) in context.Variables)
        {
            result.Add(VariableOf(name, SafeValue(read), name));
        }

        return result;
    }

    private Dictionary<string, IClrValue> Values(ClrFrameContext context)
    {
        var values = new Dictionary<string, IClrValue>(StringComparer.Ordinal);
        foreach (var (name, read) in context.Variables)
        {
            if (SafeValue(read) is { } value)
            {
                values.TryAdd(name, value);
            }
        }

        return values;
    }

    private IReadOnlyList<Variable> Variables(int reference)
    {
        lock (_gate)
        {
            return _children.TryGetValue(reference, out var children) ? children() : throw new DapRequestException("The variable is no longer valid: the program has run since.");
        }
    }

    private int Handle(Func<IReadOnlyList<Variable>> children, Func<Dictionary<string, IClrValue>>? values = null)
    {
        var id = _nextHandle++;
        _children[id] = children;
        if (values is not null)
        {
            // Read only when setVariable needs them.
            _childValues[id] = new Lazy<Dictionary<string, IClrValue>>(values);
        }

        return id;
    }

    private Variable VariableOf(string name, IClrValue? value, string? evaluateName)
    {
        try
        {
            return new(name, Types.Format(value), value is null ? null : TypeName(value), ChildrenOf(value, evaluateName), evaluateName);
        }
        catch (InvalidOperationException ex)
        {
            return new(name, "(" + ex.Message + ")", null, 0, evaluateName); // a value that couldn't be read again
        }
    }

    private string TypeName(IClrValue value) => value.Kind switch
    {
        ClrValueKind.Null => Types.Name(value.Type) is "?" ? "object" : Types.Name(value.Type),
        ClrValueKind.String => "string",
        _ => Types.Name(value.Type),
    };

    private int ChildrenOf(IClrValue? value, string? evaluateName)
    {
        if (value is null)
        {
            return 0;
        }

        switch (value.Kind)
        {
            case ClrValueKind.Array when value.Length > 0:
                return Handle(() => Elements(value, evaluateName));
            case ClrValueKind.Object:
                var full = Types.FullName(value.Type);
                if (full is "System.Decimal" or "System.DateTime" or "System.TimeSpan" || Types.Unboxed(value) is not null || (Types.Definition(value.Type) is { } d && Types.Info(d.Module) is { } info && info.IsEnum(d.Token)))
                {
                    return 0;
                }

                return Handle(() => Members(value, evaluateName), () => MemberValues(value));
            default:
                return 0;
        }
    }

    private List<Variable> Elements(IClrValue array, string? evaluateName)
    {
        var count = Math.Min(array.Length, MaxChildren);
        var result = new List<Variable>();
        var dimensions = array.Dimensions;
        for (var i = 0; i < count; i++)
        {
            var index = dimensions.Count > 1 ? string.Join(", ", Indices(i, dimensions).Select(n => n.ToString(CultureInfo.InvariantCulture))) : i.ToString(CultureInfo.InvariantCulture);
            result.Add(VariableOf("[" + index + "]", SafeValue(() => array.Element(i)), evaluateName is null ? null : $"{evaluateName}[{index}]"));
        }

        if (array.Length > count)
        {
            result.Add(new Variable("…", $"{(array.Length - count).ToString(CultureInfo.InvariantCulture)} more"));
        }

        return result;
    }

    private static int[] Indices(int flat, IReadOnlyList<int> dimensions)
    {
        var indices = new int[dimensions.Count];
        for (var d = dimensions.Count - 1; d >= 0; d--)
        {
            indices[d] = flat % dimensions[d];
            flat /= dimensions[d];
        }

        return indices;
    }

    /// <summary>An object's instance fields (auto-properties' backing fields under the property's name) and its other properties, read through their getters.</summary>
    private List<Variable> Members(IClrValue target, string? evaluateName)
    {
        var result = new List<Variable>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chain = Types.Chain(target.Type).Where(c => c.Info.TypeFullName(c.Token) is not ("System.Object" or "System.ValueType")).ToList();
        foreach (var (module, token, info) in chain)
        {
            foreach (var field in info.Fields(token).Where(f => !f.IsStatic))
            {
                if (ClrTypeSystem.DisplayFieldName(field.Name) is { } name && seen.Add(name))
                {
                    var fieldToken = field.Token;
                    result.Add(VariableOf(name, SafeValue(() => target.Field(module, token, fieldToken)), evaluateName is null ? null : evaluateName + "." + name));
                }
            }
        }

        var thread = CurrentThread();
        var timedOut = false;
        foreach (var (module, token, info) in chain)
        {
            foreach (var property in info.Properties(token).Where(p => !p.IsStatic && p.IndexParameters == 0 && p.GetterToken != 0))
            {
                if (result.Count >= MaxChildren || !seen.Add(property.Name))
                {
                    continue;
                }

                var path = evaluateName is null ? null : evaluateName + "." + property.Name;
                if (thread is null || timedOut)
                {
                    result.Add(new Variable(property.Name, timedOut ? "(not evaluated: a getter timed out)" : "(not evaluated)", null, 0, path));
                    continue;
                }

                try
                {
                    var value = Process.Call(thread, module, property.GetterToken, target.Type.TypeArguments, [target], EvalTimeout);
                    result.Add(VariableOf(property.Name, ClrLiveValue.Of(Process, value, () => value), path));
                }
                catch (ClrEvalException ex)
                {
                    result.Add(new Variable(property.Name, "{" + Types.FullName(ex.Exception.Type) + "}", null, 0, path));
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                    result.Add(new Variable(property.Name, "(the getter timed out)", null, 0, path));
                }
                catch (InvalidOperationException ex)
                {
                    result.Add(new Variable(property.Name, "(" + ex.Message + ")", null, 0, path));
                }
            }
        }

        return [.. result.OrderBy(v => v.Name, StringComparer.Ordinal).Take(MaxChildren)];
    }

    private Dictionary<string, IClrValue> MemberValues(IClrValue target)
    {
        var values = new Dictionary<string, IClrValue>(StringComparer.Ordinal);
        foreach (var (module, token, info) in Types.Chain(target.Type))
        {
            foreach (var field in info.Fields(token).Where(f => !f.IsStatic))
            {
                if (ClrTypeSystem.DisplayFieldName(field.Name) is { } name && !values.ContainsKey(name) && SafeValue(() => target.Field(module, token, field.Token)) is { } value)
                {
                    values[name] = value;
                }
            }
        }

        return values;
    }

    /// <summary>The thread of the frames handed out since the stop (func-evals run there).</summary>
    private IClrThread? CurrentThread() => _frames.Values.FirstOrDefault()?.Thread;

    // ------------------------------------------------------------------ evaluation

    private ClrEvaluator Evaluator(IClrFrame frame) => new(Types, Process, Context(frame), EvalTimeout);

    private EvaluateResponse Evaluate(string expression, int? frameId)
    {
        lock (_gate)
        {
            RequireStopped();
            if (frameId is not { } id)
            {
                throw new DapRequestException("Evaluation needs a stopped frame.");
            }

            var frame = Frame(id);
            if (frame.Module is null)
            {
                throw new DapRequestException("Expressions can't be evaluated in this frame (it isn't managed code).");
            }

            var result = Evaluator(frame).Evaluate(expression);
            return result switch
            {
                LocalValue local => new EvaluateResponse(ClrTypeSystem.FormatPrimitive(local.Value), local.Value is null ? null : LocalTypeName(local.Value)),
                RemoteValue remote => new EvaluateResponse(Types.Format(remote.Value), TypeName(remote.Value), ChildrenOf(remote.Value, expression.Trim())),
                TypeValue type => throw new DapRequestException($"'{expression.Trim()}' is a type ({Types.Name(ClrTypeSystem.TypeOf(type.Module, type.Token))}), not a value."),
                _ => throw new DapRequestException($"'{expression.Trim()}' is a namespace, not a value."),
            };
        }
    }

    private static string LocalTypeName(object value) => value switch
    {
        bool => "bool",
        char => "char",
        int => "int",
        uint => "uint",
        long => "long",
        ulong => "ulong",
        short => "short",
        ushort => "ushort",
        byte => "byte",
        sbyte => "sbyte",
        float => "float",
        double => "double",
        decimal => "decimal",
        string => "string",
        _ => value.GetType().Name,
    };

    /// <summary>Changes a primitive variable (a number, a bool, a char) to an expression's value, converted to its type.</summary>
    private SetVariableResponse SetVariable(SetVariableArguments arguments)
    {
        lock (_gate)
        {
            RequireStopped();
            if (!_childValues.TryGetValue(arguments.VariablesReference, out var values) || values.Value.GetValueOrDefault(arguments.Name) is not { } target)
            {
                throw new DapRequestException($"'{arguments.Name}' can't be changed here.");
            }

            if (target.Kind != ClrValueKind.Primitive || target.Primitive is not { } current)
            {
                throw new DapRequestException("Only numbers, bools and characters can be changed here.");
            }

            var frame = _frames.Values.FirstOrDefault(f => f.Module is not null) ?? throw new DapRequestException("No frame to evaluate the new value in.");
            var value = Evaluator(frame).Evaluate(arguments.Value) switch
            {
                LocalValue { Value: { } local } => local,
                RemoteValue { Value: { Kind: ClrValueKind.Primitive, Primitive: { } remote } } => remote,
                _ => throw new DapRequestException($"'{arguments.Value}' isn't a number, a bool or a character."),
            };

            var keyword = LocalTypeName(current);
            var converted = ClrEvaluator.ConvertTo(value, keyword);
            target.SetPrimitive(converted);
            return new SetVariableResponse(ClrTypeSystem.FormatPrimitive(converted), keyword);
        }
    }

    // ------------------------------------------------------------------ exceptions

    private ExceptionInfoResponse ExceptionInfo(int threadId)
    {
        lock (_gate)
        {
            if (!_exceptions.TryGetValue(threadId, out var exception))
            {
                exception = SafeValue(Live(Thread(threadId).CurrentException)) ?? throw new DapRequestException("The thread didn't stop on an exception.");
            }

            var details = Details(exception, CurrentStackThread(threadId), depth: 0);
            return new ExceptionInfoResponse(details.FullTypeName ?? "System.Exception", details.Message, "always", details);
        }
    }

    private IClrThread? CurrentStackThread(int threadId) => Process.Threads().FirstOrDefault(t => t.Id == threadId);

    private ExceptionDetails Details(IClrValue exception, IClrThread? thread, int depth)
    {
        var typeName = Types.FullName(exception.Type);
        var message = SafeValue(() => Types.Field(exception, "_message")) is { Kind: ClrValueKind.String } m ? m.Text : null;
        string? stackTrace = null;
        if (thread is not null && depth == 0)
        {
            // Exception.StackTrace, through its getter (func-eval).
            try
            {
                if (Types.Chain(exception.Type).Select(c => (c.Module, c.Token, Getter: c.Info.Properties(c.Token).FirstOrDefault(p => p.Name == "StackTrace" && !p.IsStatic)?.GetterToken ?? 0)).FirstOrDefault(c => c.Getter != 0) is { Getter: not 0 } getter)
                {
                    stackTrace = Process.Call(thread, getter.Module, getter.Getter, [], [exception], EvalTimeout).Text;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                log.WriteLine("Exception.StackTrace: " + ex.Message);
            }

            if (string.IsNullOrEmpty(stackTrace))
            {
                // Not filled in yet (a first chance, before the runtime has walked the stack) or not readable: the thread
                // stopped where it's thrown, before anything unwound, so its stack is the exception's.
                stackTrace = StackTraceOf(thread);
            }
        }

        var inner = depth < 5 && SafeValue(() => Types.Field(exception, "_innerException")) is { Kind: ClrValueKind.Object } innerValue ? Details(innerValue, thread, depth + 1) : null;
        var shortName = typeName.LastIndexOf('.') is var dot and >= 0 ? typeName[(dot + 1)..] : typeName;
        return new ExceptionDetails(message, shortName, typeName, stackTrace, "$exception", inner is null ? null : [inner]);
    }

    /// <summary>A thread's managed frames as <c>Exception.StackTrace</c> lists them; null when there are none.</summary>
    private string? StackTraceOf(IClrThread thread)
    {
        var lines = new List<string>();
        foreach (var frame in SafeFrames(thread))
        {
            if (frame.Module is { } module && Types.Info(module) is { } info)
            {
                var line = "   at " + info.MethodDisplayName(frame.MethodToken);
                if (info.LineAt(frame.MethodToken, frame.IlOffset) is { Document.Length: > 0 } point)
                {
                    line += $" in {point.Document}:line {point.StartLine.ToString(CultureInfo.InvariantCulture)}";
                }

                lines.Add(line);
            }
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<IClrFrame> SafeFrames(IClrThread thread)
    {
        try
        {
            return thread.Frames();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private void ClearHandles()
    {
        _frames.Clear();
        _children.Clear();
        _childValues.Clear();
    }
}
