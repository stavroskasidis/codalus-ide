using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mono.Debugger.Soft;
using MonoBreakpointEvent = Mono.Debugger.Soft.BreakpointEvent;
using MonoFrame = Mono.Debugger.Soft.StackFrame;
using MonoValue = Mono.Debugger.Soft.Value;

namespace Codalus.Debugging.Host.Mono;

/// <summary>
/// The Mono debugger adapter (M20b, ADR 0065): DAP on one side, Mono's soft debugger protocol on the other (the vendored
/// Mono.Debugger.Soft). It connects to a runtime whose agent listens (Android through adb's forward, a desktop test
/// program) or waits for one to connect (iOS simulators, Mac Catalyst), then serves breakpoints (bound when their types
/// load), stepping, threads and stacks, variables (fields, auto-properties, arrays), simple evaluation (names and member
/// paths) and exceptions. Handles (frames, variable references) live until the program resumes.
/// </summary>
internal sealed class MonoDebugSession(DapServer dap, TextWriter log)
{
    private const int MaxChildren = 100;
    private static readonly StringComparer s_paths = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    private static readonly JsonElement s_empty = JsonElement.Parse("{}");

    private readonly object _gate = new();
    private readonly Dictionary<string, List<BreakpointState>> _breakpoints = new(s_paths);
    private readonly Dictionary<long, int> _threadIds = [];
    private readonly Dictionary<int, ThreadMirror> _threads = [];
    private readonly Dictionary<int, MonoFrame> _frames = [];
    private readonly Dictionary<int, Func<IReadOnlyList<Variable>>> _children = [];

    /// <summary>An array's elements by page (a reference's start and count, M56): the shell shows a long array in ranges.</summary>
    private readonly Dictionary<int, Func<int, int, IReadOnlyList<Variable>>> _pages = [];
    private readonly Dictionary<int, ObjectMirror> _exceptions = [];
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private VirtualMachine? _vm;
    private TypeLoadEventRequest? _typeLoad;
    private StepEventRequest? _step;
    private readonly List<ExceptionEventRequest> _exceptionRequests = [];
    private bool _justMyCode = true;
    private bool _configured;
    private bool _startSuspended;
    private int _nextHandle = 1000;
    private int _nextBreakpoint = 1;

    private sealed class BreakpointState(int id, int line)
    {
        public int Id { get; } = id;

        public int Line { get; } = line;

        public int? BoundLine { get; set; }

        public List<BreakpointEventRequest> Requests { get; } = [];

        /// <summary>Where it's bound: a method and an IL offset, once each.</summary>
        public HashSet<(long Method, int Offset)> Bound { get; } = [];
    }

    /// <summary>Completes when the program has ended or the client disconnected.</summary>
    public Task Ended => _ended.Task;

    private VirtualMachine Vm => _vm ?? throw new DapRequestException("The debugger isn't connected to the program.");

    public async Task<JsonElement?> HandleAsync(string command, JsonElement arguments, CancellationToken cancellationToken)
    {
        var json = DapJsonContext.Default;
        switch (command)
        {
            case "initialize":
                return JsonSerializer.SerializeToElement(new Capabilities
                {
                    SupportsConfigurationDoneRequest = true,
                    SupportsExceptionInfoRequest = true,
                    SupportTerminateDebuggee = true,
                    SupportsExceptionFilterOptions = false,
                    ExceptionBreakpointFilters =
                    [
                        new("all", "All exceptions"),
                        new("user-unhandled", "Unhandled exceptions", Default: true),
                    ],
                }, json.Capabilities);
            case "launch" or "attach":
                await ConnectAsync(Read(arguments, json.MonoDebugArguments) ?? new MonoDebugArguments(), cancellationToken).ConfigureAwait(false);
                return null;
            case "setBreakpoints":
                return JsonSerializer.SerializeToElement(SetBreakpoints(Read(arguments, json.SetBreakpointsArguments)!), json.SetBreakpointsResponse);
            case "setFunctionBreakpoints":
                var functions = Read(arguments, json.SetFunctionBreakpointsArguments)?.Breakpoints ?? [];
                return JsonSerializer.SerializeToElement(new SetBreakpointsResponse([.. functions.Select(_ => new Breakpoint(Verified: false, Message: "Function breakpoints aren't supported for Mono programs."))]), json.SetBreakpointsResponse);
            case "setExceptionBreakpoints":
                SetExceptionBreakpoints(Read(arguments, json.SetExceptionBreakpointsArguments)?.Filters ?? []);
                return null;
            case "configurationDone":
                ConfigurationDone();
                return null;
            case "threads":
                return JsonSerializer.SerializeToElement(new ThreadsResponse(Threads()), json.ThreadsResponse);
            case "stackTrace":
                var stack = Read(arguments, json.StackTraceArguments)!;
                return JsonSerializer.SerializeToElement(StackTrace(stack.ThreadId, stack.StartFrame ?? 0, stack.Levels ?? 0), json.StackTraceResponse);
            case "scopes":
                return JsonSerializer.SerializeToElement(new ScopesResponse(Scopes(Read(arguments, json.ScopesArguments)!.FrameId)), json.ScopesResponse);
            case "variables":
                var variables = Read(arguments, json.VariablesArguments)!;
                return JsonSerializer.SerializeToElement(new VariablesResponse([.. Variables(variables.VariablesReference, variables.Start, variables.Count)]), json.VariablesResponse);
            case "evaluate":
                var evaluate = Read(arguments, json.EvaluateArguments)!;
                return JsonSerializer.SerializeToElement(Evaluate(evaluate.Expression, evaluate.FrameId), json.EvaluateResponse);
            case "continue":
                Resume();
                return JsonSerializer.SerializeToElement(new ContinueResponse(AllThreadsContinued: true), json.ContinueResponse);
            case "next":
                Step(Read(arguments, json.ThreadArguments)!.ThreadId, StepDepth.Over);
                return null;
            case "stepIn":
                Step(Read(arguments, json.ThreadArguments)!.ThreadId, StepDepth.Into);
                return null;
            case "stepOut":
                Step(Read(arguments, json.ThreadArguments)!.ThreadId, StepDepth.Out);
                return null;
            case "pause":
                await PauseAsync().ConfigureAwait(false);
                return null;
            case "exceptionInfo":
                return JsonSerializer.SerializeToElement(ExceptionInfo(Read(arguments, json.ThreadArguments)!.ThreadId), json.ExceptionInfoResponse);
            case "disconnect" or "terminate":
                Disconnect(Read(arguments, json.DisconnectArguments)?.TerminateDebuggee ?? true);
                return null;
            case "setVariable" or "setExpression":
                throw new DapRequestException("Changing values isn't supported for Mono programs yet.");
            default:
                return null;
        }
    }

    private static T? Read<T>(JsonElement arguments, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        arguments.ValueKind == JsonValueKind.Object ? arguments.Deserialize(info) : arguments.ValueKind == JsonValueKind.Undefined ? s_empty.Deserialize(info) : default;

    // ------------------------------------------------------------------ connecting

    private async Task ConnectAsync(MonoDebugArguments arguments, CancellationToken cancellationToken)
    {
        _justMyCode = arguments.JustMyCode;
        var endpoint = new IPEndPoint(IPAddress.Parse(arguments.Address), arguments.Port);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, arguments.TimeoutSeconds));
        if (arguments.Mode == MonoDebugArguments.Listen)
        {
            log.WriteLine($"Waiting for the Mono runtime to connect to {endpoint}…");
            try
            {
                _vm = await Task.Run(() => VirtualMachineManager.Listen(endpoint), cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new DapRequestException($"No Mono runtime connected to {endpoint} within {timeout.TotalSeconds:0} seconds.");
            }
        }
        else
        {
            log.WriteLine($"Connecting to the Mono runtime at {endpoint}…");
            var deadline = DateTime.UtcNow + timeout;
            Exception? last = null;
            while (_vm is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // adb's forward accepts the connection before the app listens: the handshake decides.
                    _vm = await Task.Run(() => VirtualMachineManager.Connect(endpoint), cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException or AggregateException or TimeoutException or VMDisconnectedException or CommandException)
                {
                    last = ex;
                    if (DateTime.UtcNow > deadline)
                    {
                        throw new DapRequestException($"No Mono runtime answered at {endpoint} within {timeout.TotalSeconds:0} seconds ({(last is AggregateException a ? a.InnerException?.Message : last.Message)}).");
                    }

                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var vm = _vm!;
        log.WriteLine($"Connected: Mono soft debugger protocol {vm.Version.MajorVersion}.{vm.Version.MinorVersion} ({vm.Version.VMVersion}).");
        vm.EnableEvents([EventType.ThreadStart, EventType.ThreadDeath], SuspendPolicy.None);
        _ = Task.Factory.StartNew(EventLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await dap.SendEventAsync(DapEvents.Process, new ProcessEvent("Mono", StartMethod: "attach"), DapJsonContext.Default.ProcessEvent).ConfigureAwait(false);
        await dap.SendEventAsync(DapEvents.Initialized).ConfigureAwait(false);
    }

    private void ConfigurationDone()
    {
        bool resume;
        lock (_gate)
        {
            _configured = true;
            resume = _startSuspended;
            _startSuspended = false;
        }

        if (resume)
        {
            Resume();
        }
    }

    // ------------------------------------------------------------------ events

    private void EventLoop()
    {
        var vm = _vm!;
        while (true)
        {
            EventSet set;
            try
            {
                set = vm.GetNextEventSet();
            }
            catch (Exception ex) when (ex is VMDisconnectedException or IOException or ObjectDisposedException or SocketException)
            {
                End(null);
                return;
            }

            var stopped = false;
            foreach (var e in set.Events)
            {
                try
                {
                    stopped |= Handle(e);
                }
                catch (Exception ex) when (ex is CommandException or InvalidOperationException or ObjectCollectedException or AbsentInformationException or InvalidStackFrameException)
                {
                    log.WriteLine($"{e.EventType}: {ex.Message}");
                }

                if (e.EventType is EventType.VMDeath or EventType.VMDisconnect)
                {
                    End(e.EventType == EventType.VMDeath ? 0 : null);
                    return;
                }
            }

            if (!stopped && set.SuspendPolicy != SuspendPolicy.None)
            {
                if (set.Events.Any(e => e.EventType == EventType.VMStart))
                {
                    lock (_gate)
                    {
                        if (!_configured)
                        {
                            _startSuspended = true; // resumed by configurationDone
                            continue;
                        }
                    }
                }

                TryResume(vm);
            }
        }
    }

    /// <summary>Handles one event; true when the program stays stopped for the user.</summary>
    private bool Handle(Event e)
    {
        switch (e)
        {
            case TypeLoadEvent typeLoad:
                BindPending(typeLoad.Type);
                return false;
            case ThreadStartEvent:
                _ = dap.SendEventAsync(DapEvents.Thread, new ThreadEvent("started", ThreadId(e.Thread)), DapJsonContext.Default.ThreadEvent);
                return false;
            case ThreadDeathEvent:
                _ = dap.SendEventAsync(DapEvents.Thread, new ThreadEvent("exited", ThreadId(e.Thread)), DapJsonContext.Default.ThreadEvent);
                return false;
            case MonoBreakpointEvent:
                int[] hit;
                lock (_gate)
                {
                    hit = [.. _breakpoints.Values.SelectMany(b => b).Where(b => e.Request is BreakpointEventRequest r && b.Requests.Contains(r)).Select(b => b.Id)];
                }

                Stop("breakpoint", e.Thread, hitBreakpoints: hit);
                return true;
            case StepEvent:
                ClearStep();
                if (_justMyCode && !HasSource(e.Thread))
                {
                    // Stepped into code without source (the framework): out again, to the user's code.
                    StartStep(e.Thread, StepDepth.Out);
                    return true;
                }

                Stop("step", e.Thread);
                return true;
            case ExceptionEvent exception:
                lock (_gate)
                {
                    _exceptions[ThreadId(e.Thread)] = exception.Exception;
                }

                Stop("exception", e.Thread, text: exception.Exception.Type.FullName);
                return true;
            case UserBreakEvent:
                Stop("pause", e.Thread);
                return true;
            default:
                return false;
        }
    }

    private bool HasSource(ThreadMirror thread)
    {
        var frame = thread.GetFrames().FirstOrDefault();
        return frame?.FileName is { Length: > 0 } file && File.Exists(file);
    }

    private void Stop(string reason, ThreadMirror thread, int[]? hitBreakpoints = null, string? text = null)
    {
        lock (_gate)
        {
            ClearHandles();
        }

        _ = dap.SendEventAsync(DapEvents.Stopped, new StoppedEvent(reason, ThreadId: ThreadId(thread), AllThreadsStopped: true, Text: text, HitBreakpointIds: hitBreakpoints is { Length: > 0 } ? hitBreakpoints : null), DapJsonContext.Default.StoppedEvent);
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
        }
    }

    private void TryResume(VirtualMachine vm)
    {
        try
        {
            vm.Resume();
        }
        catch (Exception ex) when (ex is InvalidOperationException or VMDisconnectedException or CommandException)
        {
            // Not suspended (another request resumed it), or gone.
        }
    }

    // ------------------------------------------------------------------ breakpoints

    private SetBreakpointsResponse SetBreakpoints(SetBreakpointsArguments arguments)
    {
        var path = arguments.Source.Path is { } p ? Path.GetFullPath(p) : throw new DapRequestException("Breakpoints need a source file path.");
        List<BreakpointState> states;
        lock (_gate)
        {
            if (_breakpoints.TryGetValue(path, out var old))
            {
                foreach (var request in old.SelectMany(b => b.Requests))
                {
                    TryDisable(request);
                }
            }

            states = [.. arguments.Breakpoints.Select(b => new BreakpointState(_nextBreakpoint++, b.Line))];
            if (states.Count == 0)
            {
                _breakpoints.Remove(path);
            }
            else
            {
                _breakpoints[path] = states;
            }
        }

        if (_vm is { } vm)
        {
            foreach (var type in vm.GetTypesForSourceFile(Path.GetFileName(path), ignoreCase: !OperatingSystem.IsLinux()))
            {
                Bind(path, states, type);
            }

            UpdateTypeLoadRequest(vm);
        }

        return new SetBreakpointsResponse([.. states.Select(s => new Breakpoint(s.Id, s.BoundLine is not null, s.BoundLine is null ? "The breakpoint will be bound when its code loads." : null, new Source(Path.GetFileName(path), path), s.BoundLine ?? s.Line))]);
    }

    /// <summary>Binds <paramref name="states"/> of <paramref name="path"/> in <paramref name="type"/>'s methods: each line, or the next line with code in the same method.</summary>
    private void Bind(string path, List<BreakpointState> states, TypeMirror type)
    {
        var locations = new List<Location>();
        foreach (var method in type.GetMethods())
        {
            try
            {
                locations.AddRange(method.Locations.Where(l => l.LineNumber > 0 && l.SourceFile is { } file && SameFile(file, path)));
            }
            catch (Exception ex) when (ex is AbsentInformationException or CommandException)
            {
            }
        }

        if (locations.Count == 0)
        {
            return;
        }

        foreach (var state in states)
        {
            var exact = locations.Where(l => l.LineNumber == state.Line).ToList();
            var candidates = exact.Count > 0 ? exact
                : locations.Where(l => l.LineNumber > state.Line && l.Method.Locations.Any(m => m.LineNumber > 0 && m.LineNumber <= state.Line))
                    .GroupBy(l => l.LineNumber).OrderBy(g => g.Key).FirstOrDefault()?.ToList() ?? [];

            // One request per method: its first IL offset on that line.
            foreach (var location in candidates.GroupBy(l => l.Method).Select(g => g.OrderBy(l => l.ILOffset).First()))
            {
                lock (_gate)
                {
                    if (!state.Bound.Add((location.Method.GetId(), location.ILOffset)))
                    {
                        continue;
                    }
                }

                var request = Vm.CreateBreakpointRequest(location);
                request.Enable();
                lock (_gate)
                {
                    state.Requests.Add(request);
                    var newlyBound = state.BoundLine is null;
                    state.BoundLine ??= location.LineNumber;
                    if (newlyBound && _configured)
                    {
                        _ = dap.SendEventAsync(DapEvents.Breakpoint, new Codalus.Debugging.BreakpointEvent("changed", new Breakpoint(state.Id, true, null, new Source(Path.GetFileName(path), path), state.BoundLine)), DapJsonContext.Default.BreakpointEvent);
                    }
                }
            }
        }
    }

    private void BindPending(TypeMirror type)
    {
        List<(string Path, List<BreakpointState> States)> pending;
        lock (_gate)
        {
            pending = [.. _breakpoints.Select(p => (p.Key, p.Value))];
        }

        foreach (var (path, states) in pending)
        {
            Bind(path, states, type);
        }
    }

    private static bool SameFile(string pdbPath, string path) =>
        s_paths.Equals(pdbPath, path) || s_paths.Equals(Normalize(pdbPath), Normalize(path));

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>The type-load request that binds breakpoints in types not loaded yet, filtered by the files with breakpoints.</summary>
    private void UpdateTypeLoadRequest(VirtualMachine vm)
    {
        string[] files;
        lock (_gate)
        {
            files = [.. _breakpoints.Keys.Select(Path.GetFileName).OfType<string>().Distinct(s_paths)];
        }

        if (_typeLoad is { } old)
        {
            TryDisable(old);
            _typeLoad = null;
        }

        if (files.Length > 0)
        {
            _typeLoad = vm.CreateTypeLoadRequest();
            _typeLoad.SourceFileFilter = files;
            _typeLoad.Enable();
        }
    }

    private void SetExceptionBreakpoints(string[] filters)
    {
        foreach (var request in _exceptionRequests)
        {
            TryDisable(request);
        }

        _exceptionRequests.Clear();
        if (_vm is not { } vm)
        {
            return;
        }

        var all = filters.Contains("all");
        var unhandled = all || filters.Contains("user-unhandled");
        if (all || unhandled)
        {
            var request = vm.CreateExceptionRequest(null, caught: all, uncaught: unhandled);
            request.Enable();
            _exceptionRequests.Add(request);
        }
    }

    private static void TryDisable(EventRequest request)
    {
        try
        {
            request.Disable();
        }
        catch (Exception ex) when (ex is InvalidOperationException or VMDisconnectedException or CommandException)
        {
        }
    }

    // ------------------------------------------------------------------ running

    private void Resume()
    {
        lock (_gate)
        {
            ClearHandles();
        }

        TryResume(Vm);
    }

    private void Step(int threadId, StepDepth depth)
    {
        var thread = Thread(threadId);
        lock (_gate)
        {
            ClearHandles();
        }

        StartStep(thread, depth);
    }

    private void StartStep(ThreadMirror thread, StepDepth depth)
    {
        ClearStep();
        var step = Vm.CreateStepRequest(thread);
        step.Depth = depth;
        step.Size = StepSize.Line;
        step.Filter = StepFilter.StaticCtor | StepFilter.DebuggerHidden | StepFilter.DebuggerStepThrough;
        step.Enable();
        _step = step;
        TryResume(Vm);
    }

    private void ClearStep()
    {
        if (_step is { } step)
        {
            TryDisable(step);
            _step = null;
        }
    }

    private async Task PauseAsync()
    {
        Vm.Suspend();
        var thread = Vm.GetThreads().FirstOrDefault(t => t.GetFrames().Any(f => f.FileName is { Length: > 0 })) ?? Vm.GetThreads().FirstOrDefault();
        lock (_gate)
        {
            ClearHandles();
        }

        if (thread is not null)
        {
            await dap.SendEventAsync(DapEvents.Stopped, new StoppedEvent("pause", ThreadId: ThreadId(thread), AllThreadsStopped: true), DapJsonContext.Default.StoppedEvent).ConfigureAwait(false);
        }
    }

    private void Disconnect(bool terminate)
    {
        if (_vm is { } vm)
        {
            try
            {
                if (terminate)
                {
                    vm.Exit(0);
                }
                else
                {
                    foreach (var request in _exceptionRequests.Cast<EventRequest>().Concat(_breakpoints.Values.SelectMany(b => b).SelectMany(b => b.Requests)))
                    {
                        TryDisable(request);
                    }

                    vm.Detach();
                }
            }
            catch (Exception ex) when (ex is VMDisconnectedException or IOException or ObjectDisposedException or InvalidOperationException or CommandException or SocketException)
            {
            }
        }

        End(null);
    }

    // ------------------------------------------------------------------ threads and stacks

    private int ThreadId(ThreadMirror thread)
    {
        lock (_gate)
        {
            if (!_threadIds.TryGetValue(thread.Id, out var id))
            {
                id = _threadIds.Count + 1;
                _threadIds[thread.Id] = id;
            }

            _threads[id] = thread;
            return id;
        }
    }

    private ThreadMirror Thread(int id)
    {
        lock (_gate)
        {
            if (_threads.TryGetValue(id, out var thread))
            {
                return thread;
            }
        }

        return Vm.GetThreads().FirstOrDefault(t => ThreadId(t) == id) ?? throw new DapRequestException($"There's no thread {id}.");
    }

    private DapThread[] Threads() =>
        [.. Vm.GetThreads().Select(t => new DapThread(ThreadId(t), string.IsNullOrEmpty(t.Name) ? $"Thread {t.ThreadId.ToString(CultureInfo.InvariantCulture)}" : t.Name))];

    private StackTraceResponse StackTrace(int threadId, int start, int levels)
    {
        var frames = Thread(threadId).GetFrames();
        var result = new List<StackFrame>();
        foreach (var frame in frames.Skip(start).Take(levels > 0 ? levels : int.MaxValue))
        {
            int id;
            lock (_gate)
            {
                id = _nextHandle++;
                _frames[id] = frame;
            }

            var method = frame.Method;
            var file = frame.FileName;
            var hasSource = file is { Length: > 0 } && File.Exists(file);
            result.Add(new StackFrame(
                id,
                $"{TypeName(method.DeclaringType)}.{method.Name}",
                hasSource ? new Source(Path.GetFileName(file), file) : null,
                hasSource ? frame.LineNumber : 0,
                hasSource && frame.ColumnNumber > 0 ? frame.ColumnNumber : 1,
                PresentationHint: hasSource ? null : "subtle"));
        }

        return new StackTraceResponse([.. result], frames.Length);
    }

    private MonoFrame Frame(int frameId)
    {
        lock (_gate)
        {
            return _frames.TryGetValue(frameId, out var frame) ? frame : throw new DapRequestException("The frame is no longer valid: the program has run since.");
        }
    }

    // ------------------------------------------------------------------ variables

    private Scope[] Scopes(int frameId)
    {
        var frame = Frame(frameId);
        var reference = Handle(() => Locals(frame));
        return [new Scope("Locals", reference, PresentationHint: "locals")];
    }

    private List<Variable> Locals(MonoFrame frame)
    {
        var result = new List<Variable>();
        if (!frame.Method.IsStatic && frame.GetThis() is { } self)
        {
            result.Add(VariableOf("this", self, "this"));
        }

        foreach (var parameter in frame.Method.GetParameters())
        {
            result.Add(VariableOf(parameter.Name ?? $"arg{parameter.Position}", Safe(() => frame.GetValue(parameter)), parameter.Name));
        }

        try
        {
            foreach (var local in frame.GetVisibleVariables().Where(l => !string.IsNullOrEmpty(l.Name) && !l.Name.StartsWith("CS$", StringComparison.Ordinal)))
            {
                result.Add(VariableOf(local.Name, Safe(() => frame.GetValue(local)), local.Name));
            }
        }
        catch (AbsentInformationException)
        {
            // No debug information for this method's locals.
        }

        return result;
    }

    /// <summary>
    /// A reference's children; an array's from <paramref name="start"/>, <paramref name="count"/> of them, when asked
    /// (without a count, the first 100 and how many more).
    /// </summary>
    private IReadOnlyList<Variable> Variables(int reference, int? start = null, int? count = null)
    {
        Func<IReadOnlyList<Variable>>? children;
        Func<int, int, IReadOnlyList<Variable>>? page = null;
        lock (_gate)
        {
            _children.TryGetValue(reference, out children);
            if (count is > 0)
            {
                _pages.TryGetValue(reference, out page);
            }
        }

        if (page is not null)
        {
            return page(Math.Max(0, start ?? 0), count!.Value);
        }

        return children is null ? throw new DapRequestException("The variable is no longer valid: the program has run since.") : children();
    }

    private int Handle(Func<IReadOnlyList<Variable>> children)
    {
        lock (_gate)
        {
            var id = _nextHandle++;
            _children[id] = children;
            return id;
        }
    }

    private static MonoValue? Safe(Func<MonoValue> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is AbsentInformationException or CommandException or InvalidStackFrameException or ObjectCollectedException or ArgumentException)
        {
            return null;
        }
    }

    private Variable VariableOf(string name, MonoValue? value, string? evaluateName) =>
        new(name, Format(value), TypeOf(value), ChildrenOf(value), evaluateName, IndexedVariables: Indexed(value));

    /// <summary>An array's length, as DAP's indexed variables (M56); null for any other value.</summary>
    private static int? Indexed(MonoValue? value) => value is ArrayMirror { Length: > 0 } array ? array.Length : null;

    private static string? TypeOf(MonoValue? value) => value switch
    {
        PrimitiveValue { Value: { } v } => v.GetType().Name,
        StructMirror s => TypeName(s.Type),
        ObjectMirror o => TypeName(o.Type),
        _ => null,
    };

    private int ChildrenOf(MonoValue? value) => value switch
    {
        StringMirror or EnumMirror => 0,
        ArrayMirror array => array.Length == 0 ? 0 : Paged(array),
        ObjectMirror o => Handle(() => Members(o, o.Type)),
        StructMirror s when s.Type.GetFields().Any(f => !f.IsStatic) => Handle(() => StructMembers(s)),
        _ => 0,
    };

    /// <summary>An array's handle: its first 100 elements unasked, any page of them asked by start and count (M56).</summary>
    private int Paged(ArrayMirror array)
    {
        var handle = Handle(() => Elements(array));
        lock (_gate)
        {
            _pages[handle] = (start, count) => Elements(array, start, count);
        }

        return handle;
    }

    /// <summary>The first 100 elements, and how many more: what a request without a count gets.</summary>
    private List<Variable> Elements(ArrayMirror array)
    {
        var count = Math.Min(array.Length, MaxChildren);
        var result = Elements(array, 0, count);
        if (array.Length > count)
        {
            result.Add(new Variable("…", $"{(array.Length - count).ToString(CultureInfo.InvariantCulture)} more"));
        }

        return result;
    }

    /// <summary><paramref name="count"/> elements from <paramref name="start"/> (fewer past the end): a page of a range (M56).</summary>
    private List<Variable> Elements(ArrayMirror array, int start, int count)
    {
        var end = (int)Math.Min(array.Length, (long)start + count);
        if (end <= start)
        {
            return [];
        }

        return [.. array.GetValues(start, end - start).Select((v, i) => VariableOf($"[{(start + i).ToString(CultureInfo.InvariantCulture)}]", v, null))];
    }

    /// <summary>An object's instance fields (an auto-property's backing field under the property's name) and its other properties, read through their getters.</summary>
    private IReadOnlyList<Variable> Members(ObjectMirror target, TypeMirror type)
    {
        var result = new List<Variable>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current.FullName != "System.Object"; current = current.BaseType)
        {
            foreach (var field in current.GetFields().Where(f => !f.IsStatic))
            {
                var name = DisplayName(field.Name);
                if (name is null || !seen.Add(name))
                {
                    continue;
                }

                result.Add(VariableOf(name, Safe(() => target.GetValue(field)), null));
            }
        }

        var thread = CurrentThread();
        if (thread is not null)
        {
            for (var current = type; current is not null && current.FullName != "System.Object" && result.Count < MaxChildren; current = current.BaseType)
            {
                foreach (var property in current.GetProperties().Where(p => !seen.Contains(p.Name)))
                {
                    if (property.GetGetMethod(true) is not { IsStatic: false } getter || property.GetIndexParameters().Length > 0 || !seen.Add(property.Name))
                    {
                        continue;
                    }

                    var value = Invoke(() => target.InvokeMethod(thread, getter, [], InvokeOptions.DisableBreakpoints | InvokeOptions.SingleThreaded));
                    result.Add(value.Error is null ? VariableOf(property.Name, value.Value, null) : new Variable(property.Name, value.Error, TypeName(property.PropertyType)));
                }
            }
        }

        return [.. result.OrderBy(v => v.Name, StringComparer.Ordinal).Take(MaxChildren)];
    }

    private List<Variable> StructMembers(StructMirror value)
    {
        var fields = value.Type.GetFields().Where(f => !f.IsStatic).ToList();
        var values = value.Fields;
        var result = new List<Variable>();
        for (var i = 0; i < fields.Count && i < values.Length; i++)
        {
            if (DisplayName(fields[i].Name) is { } name)
            {
                result.Add(VariableOf(name, values[i], null));
            }
        }

        return result;
    }

    /// <summary>A field's name as shown: an auto-property's backing field (<c>&lt;Name&gt;k__BackingField</c>) is <c>Name</c>; other compiler fields are hidden.</summary>
    private static string? DisplayName(string field)
    {
        if (field.StartsWith('<'))
        {
            var close = field.IndexOf('>', StringComparison.Ordinal);
            return close > 1 && field.EndsWith("k__BackingField", StringComparison.Ordinal) ? field[1..close] : null;
        }

        return field;
    }

    private (MonoValue? Value, string? Error) Invoke(Func<MonoValue> call)
    {
        try
        {
            return (call(), null);
        }
        catch (InvocationException ex)
        {
            return (null, ex.Exception is { } exception ? $"{{{TypeName(exception.Type)}}}" : "(the getter threw)");
        }
        catch (Exception ex) when (ex is CommandException or InvalidOperationException or ObjectCollectedException or NotSupportedException or ArgumentException)
        {
            return (null, "(" + ex.Message + ")");
        }
    }

    private ThreadMirror? CurrentThread()
    {
        lock (_gate)
        {
            return _frames.Values.FirstOrDefault()?.Thread;
        }
    }

    private static string Format(MonoValue? value) => value switch
    {
        null => "null",
        PrimitiveValue { Value: null } => "null",
        PrimitiveValue { Value: bool b } => b ? "true" : "false",
        PrimitiveValue { Value: char c } => $"'{c}'",
        PrimitiveValue { Value: IFormattable f } => f.ToString(null, CultureInfo.InvariantCulture),
        PrimitiveValue p => p.Value?.ToString() ?? "null",
        StringMirror s => Quote(s.Value),
        EnumMirror e => e.StringValue ?? Convert.ToString(e.Value, CultureInfo.InvariantCulture) ?? string.Empty,
        ArrayMirror a => $"{TypeName(a.Type.GetElementType())}[{a.Length.ToString(CultureInfo.InvariantCulture)}]",
        StructMirror s => "{" + TypeName(s.Type) + "}",
        ObjectMirror o => "{" + TypeName(o.Type) + "}",
        _ => value.ToString() ?? string.Empty,
    };

    private static string Quote(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text.Length > 1000 ? text[..1000] + "…" : text)
        {
            builder.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
        }

        return builder.Append('"').ToString();
    }

    private static string TypeName(TypeMirror? type)
    {
        if (type is null)
        {
            return "?";
        }

        try
        {
            return type.CSharpName;
        }
        catch (Exception ex) when (ex is CommandException or NotSupportedException)
        {
            return type.FullName;
        }
    }

    // ------------------------------------------------------------------ evaluation

    /// <summary>Evaluates a name or a member path (<c>item.Title</c>, <c>this.items[0]</c>) in a frame.</summary>
    private EvaluateResponse Evaluate(string expression, int? frameId)
    {
        if (frameId is not { } id)
        {
            throw new DapRequestException("Evaluation needs a stopped frame.");
        }

        var frame = Frame(id);
        var parts = MemberPath(expression.Trim());
        if (parts.Count == 0)
        {
            throw new DapRequestException("Only names and member paths can be evaluated (for example item.Title or items[0]).");
        }

        var value = Root(frame, parts[0]);
        foreach (var part in parts.Skip(1))
        {
            value = Member(frame, value, part);
        }

        return new EvaluateResponse(Format(value), TypeOf(value), ChildrenOf(value), IndexedVariables: Indexed(value));
    }

    /// <summary>A member path's parts: names and <c>[index]</c>es; empty when it's something else.</summary>
    private static List<string> MemberPath(string expression)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < expression.Length)
        {
            if (expression[i] == '.' && parts.Count > 0)
            {
                i++;
            }

            if (i < expression.Length && expression[i] == '[')
            {
                var close = expression.IndexOf(']', i);
                if (close < 0 || !int.TryParse(expression.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    return [];
                }

                parts.Add(expression[i..(close + 1)]);
                i = close + 1;
                continue;
            }

            var start = i;
            while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
            {
                i++;
            }

            if (i == start)
            {
                return [];
            }

            parts.Add(expression[start..i]);
        }

        return parts;
    }

    private MonoValue? Root(MonoFrame frame, string name)
    {
        if (name == "this")
        {
            return frame.Method.IsStatic ? throw new DapRequestException("'this' isn't available in a static method.") : frame.GetThis();
        }

        try
        {
            if (frame.GetVisibleVariableByName(name) is { } local)
            {
                return frame.GetValue(local);
            }
        }
        catch (AbsentInformationException)
        {
        }

        if (frame.Method.GetParameters().FirstOrDefault(p => p.Name == name) is { } parameter)
        {
            return frame.GetValue(parameter);
        }

        if (!frame.Method.IsStatic && frame.GetThis() is ObjectMirror self && TryMember(frame, self, name, out var member))
        {
            return member;
        }

        if (TryStatic(frame, frame.Method.DeclaringType, name, out var staticMember))
        {
            return staticMember;
        }

        throw new DapRequestException($"The name '{name}' doesn't exist in the current context.");
    }

    private MonoValue? Member(MonoFrame frame, MonoValue? value, string part)
    {
        if (part.StartsWith('['))
        {
            var index = int.Parse(part.AsSpan(1, part.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture);
            return value is ArrayMirror array
                ? index < array.Length ? array[index] : throw new DapRequestException($"The index {index} is outside the array (its length is {array.Length}).")
                : throw new DapRequestException("Only arrays can be indexed here.");
        }

        return value switch
        {
            null or PrimitiveValue { Value: null } => throw new DapRequestException("The value is null."),
            ObjectMirror o when TryMember(frame, o, part, out var member) => member,
            StructMirror s when s.Type.GetFields().Where(f => !f.IsStatic).ToList() is var fields
                && fields.FindIndex(f => f.Name == part || DisplayName(f.Name) == part) is var at and >= 0 => s.Fields[at],
            _ => throw new DapRequestException($"'{part}' isn't a member of {TypeOf(value) ?? "the value"}."),
        };
    }

    private bool TryMember(MonoFrame frame, ObjectMirror target, string name, out MonoValue? value)
    {
        for (var type = target.Type; type is not null; type = type.BaseType)
        {
            if (type.GetFields().FirstOrDefault(f => !f.IsStatic && (f.Name == name || DisplayName(f.Name) == name)) is { } field)
            {
                value = target.GetValue(field);
                return true;
            }

            if (type.GetProperty(name) is { } property && property.GetGetMethod(true) is { IsStatic: false } getter)
            {
                var result = Invoke(() => target.InvokeMethod(frame.Thread, getter, [], InvokeOptions.DisableBreakpoints | InvokeOptions.SingleThreaded));
                value = result.Error is null ? result.Value : throw new DapRequestException($"Reading {name} failed: {result.Error}");
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryStatic(MonoFrame frame, TypeMirror type, string name, out MonoValue? value)
    {
        if (type.GetFields().FirstOrDefault(f => f.IsStatic && f.Name == name) is { } field)
        {
            value = type.GetValue(field, frame.Thread);
            return true;
        }

        value = null;
        return false;
    }

    // ------------------------------------------------------------------ exceptions

    private ExceptionInfoResponse ExceptionInfo(int threadId)
    {
        ObjectMirror? exception;
        lock (_gate)
        {
            _exceptions.TryGetValue(threadId, out exception);
        }

        if (exception is null)
        {
            throw new DapRequestException("The thread didn't stop on an exception.");
        }

        var message = FindField(exception, "_message") is { } field ? exception.GetValue(field) as StringMirror : null;
        var typeName = exception.Type.FullName;
        return new ExceptionInfoResponse(typeName, message?.Value, "always", new ExceptionDetails(message?.Value, exception.Type.Name, typeName));
    }

    private static FieldInfoMirror? FindField(ObjectMirror target, string name)
    {
        for (var type = target.Type; type is not null; type = type.BaseType)
        {
            if (type.GetField(name) is { } field)
            {
                return field;
            }
        }

        return null;
    }

    private void ClearHandles()
    {
        _frames.Clear();
        _children.Clear();
        _pages.Clear();
    }
}
