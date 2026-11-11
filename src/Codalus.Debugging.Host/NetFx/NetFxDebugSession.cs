using Codalus.Debugging.Host.Disassembly;
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

    /// <summary>The runtime it debugs, for messages: ".NET Framework" (the desktop CLR) or ".NET" (CoreCLR, ADR 0086).</summary>
    string Runtime => ".NET Framework";
}

/// <summary>
/// The desktop-CLR debugger adapter (M43b, ADR 0076), and CoreCLR's (ADR 0086): DAP on one side, a debugged .NET
/// Framework or .NET process on the other (<see cref="IClrProcess"/>, through <see cref="IClrDebugger"/>). Breakpoints (with conditions) bound in modules as they load through their portable
/// PDBs, function breakpoints, stepping by source line with Just My Code and step filtering, threads and stacks,
/// locals and arguments (async state machines' hoisted variables too), members through fields and property getters,
/// evaluation of C# expressions (<see cref="ClrEvaluator"/>), exceptions (all with a type filter, or unhandled), and
/// changing primitive values. Handles (frames, variable references) live until the program runs again.
/// </summary>
internal sealed class NetFxDebugSession(DapServer dap, TextWriter log, IClrDebugger debugger) : IDisposable
{
    /// <summary>Closes the assemblies read for the debugger attributes, and ends the symbol lookups.</summary>
    public void Dispose()
    {
        _attributes.Dispose();
        _symbolsCancel.Cancel();
        _symbols?.Dispose();
        _localSymbols?.Dispose();
        _http?.Dispose();
        _symbolsCancel.Dispose();
    }

    /// <summary>Where PDBs that aren't next to their modules are looked for (M58: symbol paths and servers); null: nowhere.</summary>
    private Codalus.Debugging.Symbols.SymbolLocator? _symbols;

    /// <summary>The same without the servers: the symbol paths and the cache are read as a module loads, before its breakpoints bind.</summary>
    private Codalus.Debugging.Symbols.SymbolLocator? _localSymbols;
    private HttpClient? _http;
    private readonly CancellationTokenSource _symbolsCancel = new();

    private const int MaxChildren = 100;
    private static readonly JsonElement s_empty = JsonElement.Parse("{}");
    private static readonly StringComparer s_paths = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly Lock _gate = new();
    private readonly List<(IClrModule Module, ClrModuleInfo Info)> _modules = [];
    private readonly HashSet<IClrModule> _loaded = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<BreakpointState>> _breakpoints = new(s_paths);
    private readonly List<BreakpointState> _functionBreakpoints = [];
    private readonly Dictionary<IClrBreakpoint, BreakpointState> _byBreakpoint = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, IClrFrame> _frames = [];
    private readonly Dictionary<Guid, List<ModuleDelta>> _pendingDeltas = [];
    private readonly Dictionary<int, Func<IReadOnlyList<Variable>>> _children = [];

    /// <summary>An array's elements by page (a reference's start and count, M56): the shell shows a long array in ranges.</summary>
    private readonly Dictionary<int, Func<int, int, IReadOnlyList<Variable>>> _pages = [];
    private readonly Dictionary<int, Lazy<Dictionary<string, IClrValue>>> _childValues = [];
    private readonly Dictionary<int, IClrValue> _exceptions = [];
    private readonly HashSet<int> _stoppedWhenThrown = [];
    private readonly HashSet<(string Path, int Line)> _hitLocations = [];
    private readonly Dictionary<int, (string Path, int Line)> _gotoTargets = [];

    /// <summary>The frames handed out since the stop that are their thread's innermost managed one (frames are read anew each time, so not by reference to another read).</summary>
    private readonly HashSet<IClrFrame> _innermost = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, (int FrameId, string Name, int Ordinal)> _stepInTargets = [];

    /// <summary>The client compiles the expressions the evaluator doesn't read (M58, <c>codalus/compileExpression</c>).</summary>
    private bool _compilesExpressions;
    private readonly Dictionary<string, CompiledExpression> _compiledExpressions = new(StringComparer.Ordinal);
    private readonly HashSet<IClrModule> _expressionModules = new(ReferenceEqualityComparer.Instance);

    /// <summary>A compiled expression in the program: its method, what to pass it, or why it didn't compile.</summary>
    private sealed record CompiledExpression(IClrModule? Module, int MethodToken, string[] Parameters, int TypeParameters, string? Error);

    /// <summary>The objects with an ID (M58: <c>$1</c>, <c>$2</c>…), each held until its ID is deleted or the session ends.</summary>
    private readonly Dictionary<string, (IClrValue Value, Action Release)> _objectIds = new(StringComparer.Ordinal);
    private readonly TypeAttributes _attributes = new();
    private HashSet<string> _clientViews = new(StringComparer.Ordinal);
    private int _nextObjectId;

    /// <summary>What the method a step just returned from returned (M58): shown first in Locals, and <c>$ReturnValue</c>.</summary>
    private (int ThreadId, string Method, IClrValue Value)? _returnValue;
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

        /// <summary>Breaks on some of its hits only (<see cref="HitConditions"/>, M58).</summary>
        public string? HitCondition { get; init; }

        /// <summary>A logpoint (M58): the message is written and the program goes on.</summary>
        public string? LogMessage { get; init; }

        /// <summary>The filter's text, and the filter (null when the text isn't one: then it doesn't filter).</summary>
        public string? FilterText { get; init; }

        public BreakpointFilter? Filter { get; init; }

        /// <summary>Breaks only once the breakpoint at this file and line was hit in the session.</summary>
        public (string Path, int Line)? DependsOn { get; init; }

        /// <summary>The times it was hit with its condition true, its filter matching and its dependency met.</summary>
        public int Hits { get; set; }

        /// <summary>The same breakpoint as <paramref name="other"/>, sent again: its hits are kept.</summary>
        public bool SameAs(BreakpointState other) =>
            Line == other.Line && Condition == other.Condition && HitCondition == other.HitCondition && LogMessage == other.LogMessage && FilterText == other.FilterText && DependsOn == other.DependsOn && Function == other.Function;

        public int? BoundLine { get; set; }

        public List<(IClrModule Module, int Method, int Offset, int Version, IClrBreakpoint Breakpoint)> Bound { get; } = [];
    }

    /// <summary>A step in progress: what was asked, where from, and how many times it went on by itself.</summary>
    private sealed record StepState(ClrStepKind Kind, int ThreadId, IClrModule? Module, int Method)
    {
        public int Repeats { get; set; }

        /// <summary>Step Into Specific (M58): the call to stop in, by its method's name, and which of the line's calls of that name.</summary>
        public (string Name, int Ordinal)? Target { get; init; }

        /// <summary>The calls of <see cref="Target"/>'s name the step went into so far.</summary>
        public int TargetCalls { get; set; }

        /// <summary>A step of one machine instruction (M58): it stops where it ends, mid-line or not.</summary>
        public bool Instruction { get; init; }

        /// <summary>The frames the thread had when the step started: fewer at its end means the method returned.</summary>
        public int Depth { get; set; }

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
                _compilesExpressions = Read(arguments, json.InitializeArguments)?.SupportsExpressionCompiler == true;
                if (debugger is CoreClr.DbgShimDebugger shim && Read(arguments, json.InitializeArguments)?.SupportsRunInTerminalRequest == true)
                {
                    // The client starts programs in its terminal for us (M54): the launcher runs there.
                    shim.RunInTerminal = request => dap.RequestAsync("runInTerminal", request, json.RunInTerminalRequestArguments, CancellationToken.None).GetAwaiter().GetResult() is { } body
                        ? body.Deserialize(json.RunInTerminalResponse)?.ProcessId
                        : null;
                }

                return JsonSerializer.SerializeToElement(new Capabilities
                {
                    SupportsConfigurationDoneRequest = true,
                    SupportsFunctionBreakpoints = true,
                    SupportsConditionalBreakpoints = true,
                    SupportsHitConditionalBreakpoints = true,
                    SupportsLogPoints = true,
                    SupportsBreakpointFilters = true,
                    SupportsGotoTargetsRequest = true,
                    SupportsStepInTargetsRequest = true,
                    SupportsFreezingThreads = true,
                    SupportsObjectIds = true,
                    SupportsSymbolLoading = true,
                    SupportsReadMemoryRequest = true,
                    SupportsWriteMemoryRequest = true,
                    SupportsDisassembleRequest = Disassembler.IsSupported(Disassembler.Architecture),
                    SupportsSteppingGranularity = Disassembler.IsSupported(Disassembler.Architecture),
                    SupportsRegisters = Disassembler.IsSupported(Disassembler.Architecture),
                    SupportsTypeProxies = true,
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
                var variables = Read(arguments, json.VariablesArguments)!;
                return JsonSerializer.SerializeToElement(new VariablesResponse([.. Variables(variables.VariablesReference, variables.Start, variables.Count)]), json.VariablesResponse);
            case "evaluate":
                var evaluate = Read(arguments, json.EvaluateArguments)!;
                return JsonSerializer.SerializeToElement(Evaluate(evaluate.Expression, evaluate.FrameId), json.EvaluateResponse);
            case "setVariable":
                return JsonSerializer.SerializeToElement(SetVariable(Read(arguments, json.SetVariableArguments)!), json.SetVariableResponse);
            case "setExpression":
                var assignment = Read(arguments, json.SetExpressionArguments)!;
                return JsonSerializer.SerializeToElement(SetExpression(assignment.Expression, assignment.Value, assignment.FrameId), json.SetExpressionResponse);
            case "continue":
                Resume();
                return JsonSerializer.SerializeToElement(new ContinueResponse(AllThreadsContinued: true), json.ContinueResponse);
            case "next":
                var next = Read(arguments, json.StepInArguments)!;
                Step(next.ThreadId, ClrStepKind.Over, instruction: next.Granularity == StepInArguments.Instruction);
                return null;
            case "stepIn":
                var stepIn = Read(arguments, json.StepInArguments)!;
                Step(stepIn.ThreadId, ClrStepKind.Into, stepIn.TargetId, stepIn.Granularity == StepInArguments.Instruction);
                return null;
            case "readMemory":
                return JsonSerializer.SerializeToElement(ReadMemory(Read(arguments, json.ReadMemoryArguments)!), json.ReadMemoryResponse);
            case "writeMemory":
                return JsonSerializer.SerializeToElement(WriteMemory(Read(arguments, json.WriteMemoryArguments)!), json.WriteMemoryResponse);
            case "disassemble":
                return JsonSerializer.SerializeToElement(new DisassembleResponse(Disassemble(Read(arguments, json.DisassembleArguments)!)), json.DisassembleResponse);
            case LoadSymbolsCommand:
                LoadSymbols(Read(arguments, json.LoadSymbolsArguments)!.ModuleId);
                return null;
            case RegistersCommand:
                return JsonSerializer.SerializeToElement(new RegistersResponse(Registers(Read(arguments, json.RegistersArguments)!.FrameId)), json.RegistersResponse);
            case "stepInTargets":
                return JsonSerializer.SerializeToElement(new StepInTargetsResponse(StepInTargets(Read(arguments, json.StepInTargetsArguments)!.FrameId)), json.StepInTargetsResponse);
            case "gotoTargets":
                return JsonSerializer.SerializeToElement(new GotoTargetsResponse(GotoTargets(Read(arguments, json.GotoTargetsArguments)!)), json.GotoTargetsResponse);
            case "goto":
                var jump = Read(arguments, json.GotoArguments)!;
                await GotoAsync(jump.ThreadId, jump.TargetId).ConfigureAwait(false);
                return null;
            case MakeObjectIdCommand:
                var make = Read(arguments, json.MakeObjectIdArguments)!;
                return JsonSerializer.SerializeToElement(new ObjectIdResponse(MakeObjectId(make.Expression, make.FrameId)), json.ObjectIdResponse);
            case DeleteObjectIdCommand:
                DeleteObjectId(Read(arguments, json.DeleteObjectIdArguments)!.Name);
                return null;
            case FreezeThreadCommand:
                var freeze = Read(arguments, json.FreezeThreadArguments)!;
                lock (_gate)
                {
                    Thread(freeze.ThreadId).Freeze(freeze.Frozen);
                }

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
            case HotReloadCapabilitiesCommand:
                return JsonSerializer.SerializeToElement(new HotReloadCapabilitiesResponse(HotReloadCapabilities()), json.HotReloadCapabilitiesResponse);
            case ApplyChangesCommand:
                return JsonSerializer.SerializeToElement(ApplyChanges(Read(arguments, json.ApplyChangesArguments)?.Updates ?? []), json.ApplyChangesResponse);
            default:
                return null;
        }
    }

    private static T? Read<T>(JsonElement arguments, JsonTypeInfo<T> info) =>
        arguments.ValueKind == JsonValueKind.Object ? arguments.Deserialize(info) : arguments.ValueKind == JsonValueKind.Undefined ? s_empty.Deserialize(info) : default;

    /// <summary>Keeps a thread from running when the program runs on, or lets it run again (M58).</summary>
    public const string FreezeThreadCommand = "codalus/freezeThread";

    /// <summary>Looks for a loaded module's symbols in the symbol paths and on the symbol servers (M58).</summary>
    public const string LoadSymbolsCommand = "codalus/loadSymbols";

    /// <summary>A frame's registers (M58).</summary>
    public const string RegistersCommand = "codalus/registers";

    /// <summary>Gives an object an ID (<c>$1</c>) that expressions reach it by for the rest of the session (M58).</summary>
    public const string MakeObjectIdCommand = "codalus/makeObjectId";

    public const string DeleteObjectIdCommand = "codalus/deleteObjectId";

    // ------------------------------------------------------------------ code changes (M55)

    /// <summary>Applies an edit's deltas to the modules the program has loaded (Roslyn's, from the language host).</summary>
    public const string ApplyChangesCommand = "codalus/applyChanges";

    /// <summary>What the debugged runtime can apply, for Roslyn's analysis of an edit.</summary>
    public const string HotReloadCapabilitiesCommand = "codalus/hotReloadCapabilities";

    /// <summary>
    /// The runtime's Hot Reload capabilities, by its version (CoreCLR's <c>MetadataUpdater.GetCapabilities</c>, read on
    /// .NET 8 to 11: the generic ones from .NET 7, <c>AddFieldRva</c> from .NET 10). The version is the one dbgshim
    /// reported for the attach (<c>11.0.0-rc.3…</c>, the runtime's folder), known before any module loads; a
    /// self-contained program's folder isn't one, and then the core library's version says it. None for the .NET
    /// Framework, whose debugger doesn't apply edits here, nor before the program has started.
    /// <para>
    /// Plus <c>AddExplicitInterfaceImplementation</c>, which every .NET runtime supports without reporting it, as
    /// <c>dotnet watch</c> adds it: an edit to an async method without an <c>await</c> gives it a new state machine,
    /// which implements <c>IAsyncStateMachine</c> explicitly, and without it was CS9346, a restart.
    /// </para>
    /// </summary>
    private string[] HotReloadCapabilities()
    {
        if (debugger is not CoreClr.DbgShimDebugger || _process is not { } process)
        {
            return [];
        }

        if (!int.TryParse(process.RuntimeVersion.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            lock (_gate)
            {
                if (_modules.FirstOrDefault(m => m.Info.AssemblyName == "System.Private.CoreLib").Info is not { Metadata.IsAssembly: true } coreLibrary)
                {
                    return [];
                }

                major = coreLibrary.Metadata.GetAssemblyDefinition().Version.Major;
            }
        }

        if (major < 6)
        {
            return [];
        }

        string[] capabilities = ["Baseline", "AddMethodToExistingType", "AddStaticFieldToExistingType", "AddInstanceFieldToExistingType", "NewTypeDefinition", "ChangeCustomAttributes", "UpdateParameters", "AddExplicitInterfaceImplementation"];
        if (major >= 7)
        {
            capabilities = [.. capabilities, "GenericUpdateMethod", "GenericAddMethodToExistingType", "GenericAddFieldToExistingType"];
        }

        return major >= 10 ? [.. capabilities, "AddFieldRva"] : capabilities;
    }

    /// <summary>
    /// Applies each module's delta (the program stopped for it, if it runs), then binds the breakpoints again: in an
    /// edited method's new version, which later calls run, beside the old one's.
    /// </summary>
    private ApplyChangesResponse ApplyChanges(ModuleDelta[] updates)
    {
        var results = new List<ModuleDeltaResult>();
        lock (_gate)
        {
            WhileStopped(() =>
            {
                foreach (var update in updates)
                {
                    if (_modules.FirstOrDefault(m => m.Info.Mvid == update.ModuleId) is not ({ } module, { } info))
                    {
                        // Not loaded yet (a library the program hasn't called): the module it loads is the one built, so
                        // the edits apply as it loads, in order.
                        if (!_pendingDeltas.TryGetValue(update.ModuleId, out var pending))
                        {
                            _pendingDeltas[update.ModuleId] = pending = [];
                        }

                        pending.Add(update);
                        results.Add(new ModuleDeltaResult(update.ModuleId, ModuleDeltaResult.Pending));
                        continue;
                    }

                    try
                    {
                        Process.ApplyChanges(module, update.MetadataDelta, update.IlDelta);
                    }
                    catch (ClrException ex)
                    {
                        log.WriteLine($"Applying an edit to {info.AssemblyName}: {ex.Message}");
                        results.Add(new ModuleDeltaResult(update.ModuleId, ModuleDeltaResult.Failed, ex.Message));
                        continue;
                    }

                    info.ApplyDelta(update.MetadataDelta, update.PdbDelta);
                    foreach (var (path, states) in _breakpoints)
                    {
                        Bind(path, states, module, info);
                    }

                    results.Add(new ModuleDeltaResult(update.ModuleId, ModuleDeltaResult.Applied));
                }
            });
        }

        return new ApplyChangesResponse([.. results]);
    }

    // ------------------------------------------------------------------ starting

    private void Start(NetFxDebugArguments arguments, bool attach)
    {
        if (_process is not null)
        {
            throw new DapRequestException("The debugger is already attached to a program.");
        }

        _justMyCode = arguments.JustMyCode;
        _stepFiltering = arguments.EnableStepFiltering;
        _clientViews = new HashSet<string>(arguments.ClientViews ?? [], StringComparer.Ordinal);
        if (arguments.Symbols is { } symbols && (symbols.SearchPaths.Count > 0 || symbols.Servers.Count > 0))
        {
            _http = new HttpClient();
            _symbols = new Codalus.Debugging.Symbols.SymbolLocator(symbols, _http, log.WriteLine);
            _localSymbols = new Codalus.Debugging.Symbols.SymbolLocator(symbols with { Servers = [] }, _http, log.WriteLine);
        }

        _attached = attach;
        if (attach)
        {
            var id = arguments.ProcessId ?? throw new DapRequestException("Attach needs a process id.");
            log.WriteLine($"Attaching to process {id.ToString(CultureInfo.InvariantCulture)} ({debugger.Runtime})…");
            _process = debugger.Attach(id);
        }
        else
        {
            if (string.IsNullOrEmpty(arguments.Program))
            {
                throw new DapRequestException("Launch needs a program.");
            }

            log.WriteLine($"Starting {arguments.Program} ({debugger.Runtime})…");
            _process = debugger.Launch(arguments, (category, text) => _ = dap.SendEventAsync(DapEvents.Output, new OutputEvent(text, category), DapJsonContext.Default.OutputEvent));
        }

        if (_process is CorDebugTarget target)
        {
            target.DescribeThreads = ThreadsOfTimedOutEval;
            target.EvalTimedOut = log.WriteLine;
        }

        log.WriteLine($"Debugging process {_process.Id.ToString(CultureInfo.InvariantCulture)} ({debugger.Runtime}, CLR {_process.RuntimeVersion}).");
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
                _objectIds.Clear();
            }
        }
    }

    // ------------------------------------------------------------------ modules

    /// <summary>
    /// Registers a module a stop's frames are in that its load event hasn't brought yet: an attach's module loads arrive
    /// after <c>DebugActiveProcess</c> returns, in order, and a Break All right after the attach (or a stop in between)
    /// can come first, which showed the program's own frames without source (CI, run 40792217459, Linux). Its load event
    /// then finds it registered.
    /// </summary>
    private void EnsureModule(IClrModule module)
    {
        if (!_loaded.Contains(module))
        {
            LoadModule(module);
        }
    }

    private void LoadModule(IClrModule module)
    {
        if (!_loaded.Add(module))
        {
            return; // registered at a stop that came first (EnsureModule)
        }

        if (_expressionModules.Contains(module))
        {
            return; // a compiled expression's assembly (M58): not one of the program's modules
        }

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

        // No PDB next to it: one in a symbol path, or downloaded before (the cache), is read now, so the module's
        // breakpoints bind as it loads (M58). The servers are asked after it loaded (below).
        if (info is { HasSymbols: false } && _localSymbols is { } local && local.Options.Includes(module.Path))
        {
            try
            {
                var found = local.FindAsync(module.Path, ignoreFilter: false, _symbolsCancel.Token).GetAwaiter().GetResult();
                if (found.PdbPath is { } pdb && ClrModuleInfo.Open(module.Path, pdb, found.Status) is var withSymbols)
                {
                    if (withSymbols.HasSymbols)
                    {
                        info.Dispose();
                        info = withSymbols;
                    }
                    else
                    {
                        withSymbols.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or OperationCanceledException)
            {
                log.WriteLine($"Symbols for {Path.GetFileName(module.Path)}: {ex.Message}");
            }
        }

        // Just My Code: the user's code is what has symbols and isn't optimized.
        var userCode = info is { HasSymbols: true, IsOptimized: false };
        if (userCode && debugger is CoreClr.DbgShimDebugger && !_process!.EnableEditAndContinue(module))
        {
            // Loaded before an attach: edits apply only when the program runs with DOTNET_MODIFIABLE_ASSEMBLIES=debug.
            log.WriteLine($"Edit and Continue's JIT flags weren't set for {info!.AssemblyName}.");
        }

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

            // Edits made before the module loaded (M55): the program is stopped in its load, as ApplyChanges wants.
            if (_pendingDeltas.Remove(info.Mvid, out var deltas))
            {
                foreach (var delta in deltas)
                {
                    try
                    {
                        _process!.ApplyChanges(module, delta.MetadataDelta, delta.IlDelta);
                        info.ApplyDelta(delta.MetadataDelta, delta.PdbDelta);
                    }
                    catch (ClrException ex)
                    {
                        log.WriteLine($"Applying an earlier edit to {info.AssemblyName} as it loaded: {ex.Message}");
                        break;
                    }
                }
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

        // A module without symbols next to it: looked up in the symbol paths and on the servers, after it loaded (the
        // program doesn't wait for a download). With Just My Code its code isn't stepped into, so only the modules the
        // settings name are looked up by themselves; Load Symbols asks for any.
        if (info is { HasSymbols: false } && _symbols is { Options.Servers.Count: > 0 } locator && locator.Options.Includes(module.Path)
            && (!_justMyCode || locator.Options.ModuleFilter == Codalus.Debugging.Symbols.SymbolModuleFilter.OnlySpecified))
        {
            FindSymbols(module, ignoreFilter: false);
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

    /// <summary>Load Symbols: a module's symbols, whatever the module lists say; a <c>module</c> event says what was found.</summary>
    private void LoadSymbols(string moduleId)
    {
        lock (_gate)
        {
            if (_modules.FirstOrDefault(m => RuntimeHelpersId(m.Module).ToString(CultureInfo.InvariantCulture) == moduleId) is not ({ } module, { } info))
            {
                throw new DapRequestException("That module isn't loaded any more.");
            }

            if (info.HasSymbols)
            {
                return;
            }

            if (_symbols is null)
            {
                throw new DapRequestException("No symbol paths or symbol servers are set (Options › Debugging › Symbols).");
            }

            _symbols.ForgetFailures();
            FindSymbols(module, ignoreFilter: true);
        }
    }

    /// <summary>Looks a module's PDB up in the background, and loads it when it's found.</summary>
    private void FindSymbols(IClrModule module, bool ignoreFilter)
    {
        var locator = _symbols!;
        var cancellation = _symbolsCancel.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await locator.FindAsync(module.Path, ignoreFilter, cancellation).ConfigureAwait(false);
                ApplySymbols(module, result);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                log.WriteLine($"Symbols for {Path.GetFileName(module.Path)}: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    /// <summary>A lookup's end: the PDB found replaces the module's symbols (breakpoints in its files bind), and the Modules window hears what happened.</summary>
    private void ApplySymbols(IClrModule module, Codalus.Debugging.Symbols.SymbolResult result)
    {
        ClrModuleInfo? loaded = null;
        var status = result.Status;
        lock (_gate)
        {
            var index = _modules.FindIndex(m => ReferenceEquals(m.Module, module));
            if (index < 0 || _ended.Task.IsCompleted)
            {
                return;
            }

            var current = _modules[index].Info;
            if (result.PdbPath is { } pdb && !current.HasSymbols)
            {
                try
                {
                    loaded = ClrModuleInfo.Open(module.Path, pdb, result.Status);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
                {
                    status = $"{Path.GetFileName(pdb)} couldn't be read: {ex.Message}";
                }

                if (loaded is { HasSymbols: true })
                {
                    WhileStopped(() =>
                    {
                        _modules[index] = (module, loaded);
                        foreach (var (path, states) in _breakpoints)
                        {
                            Bind(path, states, module, loaded);
                        }

                        foreach (var state in _functionBreakpoints)
                        {
                            BindFunction(state, module, loaded);
                        }
                    });
                    current.Dispose();
                    current = loaded;
                    status = loaded.SymbolStatus;
                }
                else if (loaded is not null)
                {
                    status = loaded.SymbolStatus;
                    loaded.Dispose();
                }
            }

            _ = dap.SendEventAsync(DapEvents.Module, new ModuleEvent("changed", new DapModule(
                ModuleId(module),
                Path.GetFileName(module.Path),
                module.Path,
                status,
                current.PdbPath,
                IsOptimized: current.IsOptimized,
                IsUserCode: current is { HasSymbols: true, IsOptimized: false })), DapJsonContext.Default.ModuleEvent);
        }
    }

    private static JsonElement ModuleId(IClrModule module) =>
        JsonSerializer.SerializeToElement(RuntimeHelpersId(module).ToString(CultureInfo.InvariantCulture), DapJsonContext.Default.String);

    private static int RuntimeHelpersId(IClrModule module) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(module);

    private void UnloadModule(IClrModule module)
    {
        _loaded.Remove(module);
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

                states = [.. arguments.Breakpoints.Select(b => KeepHits(old, new BreakpointState(_nextBreakpoint++, path, b.Line, Text(b.Condition))
                {
                    HitCondition = Text(b.HitCondition),
                    LogMessage = string.IsNullOrEmpty(b.LogMessage) ? null : b.LogMessage,
                    FilterText = Text(b.Filter),
                    Filter = Text(b.Filter) is { } filter && BreakpointFilter.TryParse(filter, out var parsed, out _) ? parsed : null,
                    DependsOn = b.DependsOn is { } dependency ? (Path.GetFullPath(dependency.Path), dependency.Line) : null,
                }))];
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

    private static string? Text(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>A breakpoint sent again keeps the hits it counted (the client sends a file's breakpoints whenever one changes).</summary>
    private static BreakpointState KeepHits(List<BreakpointState>? old, BreakpointState state)
    {
        state.Hits = old?.FirstOrDefault(state.SameAs)?.Hits ?? 0;
        return state;
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
        IClrBreakpoint breakpoint;
        int version;
        try
        {
            // In the method's current version (an edit makes a new one, M55); one in an older version stays for the
            // frames still running it.
            version = Process.CodeVersion(module, method);
            if (state.Bound.Any(b => ReferenceEquals(b.Module, module) && b.Method == method && b.Offset == offset && b.Version == version))
            {
                return;
            }

            breakpoint = Process.CreateBreakpoint(module, method, offset);
        }
        catch (InvalidOperationException ex)
        {
            log.WriteLine($"Breakpoint {state.Id.ToString(CultureInfo.InvariantCulture)} couldn't be set: {ex.Message}");
            return;
        }

        state.Bound.Add((module, method, offset, version, breakpoint));
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
                List<BreakpointState> old = [.. _functionBreakpoints];
                _functionBreakpoints.Clear();
                _functionBreakpoints.AddRange(breakpoints.Select(b => KeepHits(old, new BreakpointState(_nextBreakpoint++, null, 0, Text(b.Condition), b.Name.Trim()) { HitCondition = Text(b.HitCondition) })));
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
                if (TopFrame(hit.Thread) is not { } frame || !Evaluator(frame).IsTrue(EvaluateIn(frame, condition)))
                {
                    return false;
                }
            }
            catch (InvalidOperationException ex)
            {
                _ = dap.SendEventAsync(DapEvents.Output, new OutputEvent($"The breakpoint's condition '{condition}' couldn't be evaluated: {ex.Message}\n", "console"), DapJsonContext.Default.OutputEvent);
            }
        }

        // Its filter, its dependency, its hit count and its message, here (M58): a hit that doesn't break doesn't stop
        // the program for the IDE to decide, as netcoredbg's does.
        if (state.Filter is { } filter && !filter.Matches(new BreakpointFilterContext(hit.Thread.Id, ThreadName(hit.Thread), Process.Id, ProcessName())))
        {
            return false;
        }

        if (state.DependsOn is { } dependency && !_hitLocations.Contains(dependency))
        {
            return false;
        }

        state.Hits++;
        if (state.HitCondition is { } hitCondition && !HitConditions.Met(hitCondition, state.Hits))
        {
            return false;
        }

        if (state.Path is { } path)
        {
            _hitLocations.Add((path, state.Line));
        }

        if (state.LogMessage is { } message)
        {
            _ = dap.SendEventAsync(DapEvents.Output, new OutputEvent(LogMessage(message, hit.Thread) + "\n", "console"), DapJsonContext.Default.OutputEvent);
            return false;
        }

        Process.CancelStep();
        _step = null;
        Stop(state.Function is null ? "breakpoint" : "function breakpoint", hit.Thread, [state.Id]);
        return true;
    }

    private string? _processName;

    /// <summary>The debugged process's name, for breakpoint filters (<c>ProcessName = "app"</c>).</summary>
    private string? ProcessName()
    {
        if (_processName is null)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(Process.Id);
                _processName = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _processName = string.Empty;
            }
        }

        return _processName.Length == 0 ? null : _processName;
    }

    /// <summary>
    /// A logpoint's message: <c>{expression}</c> is its value in the thread's top frame (<c>{{</c> and <c>}}</c> are
    /// braces), <c>$FUNCTION</c> the frame's method and <c>$TID</c> the thread.
    /// </summary>
    private string LogMessage(string message, IClrThread thread)
    {
        var frame = TopFrame(thread);
        ClrEvaluator? evaluator = null;
        var result = new System.Text.StringBuilder();
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
                    if (frame is null)
                    {
                        throw new ClrException("no frame to evaluate it in");
                    }

                    evaluator ??= Evaluator(frame);
                    result.Append(EvaluateIn(frame, expression) switch
                    {
                        LocalValue local => ClrTypeSystem.FormatPrimitive(local.Value),
                        RemoteValue remote => Types.Format(remote.Value),
                        _ => "<" + expression.Trim() + " isn't a value>",
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    result.Append('<').Append(ex.Message).Append('>');
                }

                i = end;
            }
            else
            {
                result.Append(message[i]);
            }
        }

        var function = frame?.Module is { } module && Types.Info(module) is { } info ? info.MethodDisplayName(frame.MethodToken) : "?";
        return result.ToString().Replace("$FUNCTION", function, StringComparison.Ordinal).Replace("$TID", thread.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
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
            RunsOn();
        }

        TryContinue(Process);
    }

    /// <summary>The user lets the program run on (continue or a step): what the last stop held is let go.</summary>
    private void RunsOn()
    {
        _returnValue = null;
        Process.ReleaseKept();
    }

    private void Step(int threadId, ClrStepKind kind, int? targetId = null, bool instruction = false)
    {
        lock (_gate)
        {
            RequireStopped();
            var thread = Thread(threadId);
            (string Name, int Ordinal)? target = null;
            if (targetId is { } id)
            {
                target = _stepInTargets.TryGetValue(id, out var found) ? (found.Name, found.Ordinal) : throw new DapRequestException("The call to step into is no longer valid: the program has run since.");
            }

            _userStopped = false;
            ClearHandles();
            _exceptions.Clear();
            RunsOn();
            var top = TopFrame(thread);
            // One machine instruction (the Disassembly window): the range is the instruction the frame is at.
            if (instruction && top?.NativeCode() is { } code && Process.ReadMemory(code.Ip, Disassembler.MaxInstructionLength(Disassembler.Architecture)) is { Length: > 0 } bytes
                && Disassembler.Decode(Disassembler.Architecture, bytes, code.Ip) is [var current, ..])
            {
                var offset = (int)(code.Ip - code.Start);
                _step = new StepState(kind, threadId, top.Module, top.MethodToken) { Instruction = true };
                Process.Step(thread, new ClrStep(kind, [(offset, offset + current.Bytes.Length)], _justMyCode) { Native = true });
                return;
            }

            _step = new StepState(kind, threadId, top?.Module, top?.MethodToken ?? 0) { Target = target, Depth = SafeFrames(thread).Count };
            StartStep(thread, kind, top);
        }
    }

    // ------------------------------------------------------------------ Step Into Specific, Set Next Statement (M58)

    /// <summary>The calls of the rest of a frame's line, in IL order (the order they run in, but for branches): the frame must be its thread's innermost.</summary>
    private StepInTarget[] StepInTargets(int frameId)
    {
        lock (_gate)
        {
            RequireStopped();
            var frame = Frame(frameId);
            if (frame.Module is not { } module || Types.Info(module) is not { } info || !_innermost.Contains(frame))
            {
                return [];
            }

            if (frame.Il() is not { Length: > 0 } il || info.RangeAt(frame.MethodToken, frame.IlOffset, frame.CodeVersion) is not var (_, end))
            {
                return [];
            }

            // Mid-line after a call returned (a step out), the frame's offset is the instruction boundary before that call
            // (IsIlOffsetExact is false): the line's calls up to and including it ran already.
            var calls = IlCalls.Of(il);
            var first = frame.IsIlOffsetExact ? frame.IlOffset : IlCalls.ReturnedAt(il, frame.IlOffset) is { } returned ? returned.End : frame.IlOffset;
            var targets = new List<StepInTarget>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var call in calls)
            {
                if (call.Offset < first || call.Offset >= end || info.CallTarget(call.Token) is not { } target)
                {
                    continue;
                }

                var ordinal = seen[target.Name] = seen.GetValueOrDefault(target.Name) + 1;
                var id = _nextHandle++;
                _stepInTargets[id] = (frameId, target.Name, ordinal);
                targets.Add(new StepInTarget(id, target.Display));
            }

            return [.. targets];
        }
    }

    /// <summary>The places of a source line the next statement could be set to: one target for the line, resolved in the thread's method when it's used.</summary>
    private GotoTarget[] GotoTargets(GotoTargetsArguments arguments)
    {
        var path = arguments.Source.Path is { } p ? Path.GetFullPath(p) : throw new DapRequestException("Set Next Statement needs a source file path.");
        lock (_gate)
        {
            RequireStopped();
            if (!_modules.Any(m => m.Info.MatchDocument(path) is { } document && m.Info.Locations(document, arguments.Line).Count > 0))
            {
                return [];
            }

            var id = _nextHandle++;
            _gotoTargets[id] = (path, arguments.Line);
            return [new GotoTarget(id, "Line " + arguments.Line.ToString(CultureInfo.InvariantCulture), arguments.Line)];
        }
    }

    /// <summary>Set Next Statement: moves the thread's innermost frame to a line of its own method, and reports the new place as a stop.</summary>
    private async Task GotoAsync(int threadId, int targetId)
    {
        IClrThread thread;
        lock (_gate)
        {
            RequireStopped();
            if (!_gotoTargets.TryGetValue(targetId, out var target))
            {
                throw new DapRequestException("The line is no longer valid: the program has run since.");
            }

            thread = Thread(threadId);
            var frames = thread.Frames();
            if (frames.Count == 0 || frames[0].Module is not { } module || Types.Info(module) is not { } info)
            {
                throw new DapRequestException("The next statement can be set only in a thread stopped in managed code.");
            }

            var frame = frames[0];
            if (info.MatchDocument(target.Path) is not { } document
                || info.SequencePoints(frame.MethodToken, frame.CodeVersion).Where(point => !point.IsHidden && point.Document == document && point.StartLine >= target.Line)
                    .OrderBy(point => point.StartLine).ThenBy(point => point.Offset).Cast<ClrSequencePoint?>().FirstOrDefault() is not { } point
                || (point.StartLine != target.Line && !info.Locations(document, target.Line).Any(l => l.Method == frame.MethodToken && l.Offset == point.Offset)))
            {
                throw new DapRequestException("The next statement can be set only to a line of the method the thread is in.");
            }

            try
            {
                frame.SetIp(point.Offset);
            }
            catch (ClrException ex)
            {
                throw new DapRequestException(ex.Message);
            }

            ClearHandles();
        }

        await dap.SendEventAsync(DapEvents.Stopped, new StoppedEvent("goto", ThreadId: thread.Id, AllThreadsStopped: true), DapJsonContext.Default.StoppedEvent).ConfigureAwait(false);
    }

    /// <summary>
    /// What the call the thread just returned from returned (M58): the stop is right after it, in its caller. Kept for
    /// the Locals of the stop that follows.
    /// </summary>
    private void ReadReturnValue(IClrThread thread, IClrFrame frame, ClrModuleInfo info)
    {
        try
        {
            if (frame.Il() is not { Length: > 0 } il || IlCalls.ReturnedAt(il, frame.IlOffset) is not { } call || info.CallTarget(call.Token) is not { ReturnsVoid: false } target
                || Process.ReturnValue(thread, call.Offset) is not { } value)
            {
                return;
            }

            _returnValue = (thread.Id, call.IsConstructor ? "new " + target.TypeName : target.TypeName + "." + target.Name, value);
        }
        catch (InvalidOperationException ex)
        {
            log.WriteLine("The returned value: " + ex.Message);
        }
    }

    /// <summary>Steps from <paramref name="frame"/>: over or into the rest of its source line (its sequence point's range), or out.</summary>
    private void StartStep(IClrThread thread, ClrStepKind kind, IClrFrame? frame)
    {
        var ranges = new List<(int, int)>();
        if (kind != ClrStepKind.Out && frame?.Module is { } module && Types.Info(module) is { } info && info.RangeAt(frame.MethodToken, frame.IlOffset, frame.CodeVersion) is { } range)
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
        if (frame is null || step.Instruction || step.Repeats++ > 50)
        {
            _step = null;
            Stop("step", thread);
            return true;
        }

        // Back in the caller, right after the call (a step out, or a step over or into at the method's end): what it returned.
        if (info is not null && step.Depth > 0 && SafeFrames(thread).Count < step.Depth)
        {
            step.Depth = 0;
            ReadReturnValue(thread, frame, info);
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
        if (step.Target is { } target && !sameMethod && info is not null)
        {
            // Step Into Specific: out of every call but the chosen one (by its name, and which of the line's calls of that
            // name), then on into the rest of the line.
            if (info.Method(frame.MethodToken).Name != target.Name || ++step.TargetCalls != target.Ordinal)
            {
                step.ResumeInto = true;
                StartStep(thread, ClrStepKind.Out, frame);
                return true;
            }
        }
        else if (_stepFiltering && step.Kind == ClrStepKind.Into && !sameMethod && info is not null && IsFiltered(info.Method(frame.MethodToken)))
        {
            // Step filtering: a property accessor or an operator is stepped over, then on into the rest of the line.
            step.ResumeInto = true;
            StartStep(thread, ClrStepKind.Out, frame);
            return true;
        }

        if (info is not null && info.SequencePointAt(frame.MethodToken, frame.IlOffset, frame.CodeVersion) is { } point && (point.IsHidden || (point.Offset != frame.IlOffset && step.Kind != ClrStepKind.Out)))
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
            foreach (var module in threads.SelectMany(t => t.Frames()).Select(f => f.Module).OfType<IClrModule>().Distinct())
            {
                EnsureModule(module);
            }

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
            return [.. Process.Threads().Select(t => new DapThread(t.Id, ThreadName(t), t.Frozen ? true : null))];
        }
    }

    private string ThreadName(IClrThread thread)
    {
        // System.Threading.Thread's name: m_Name on the desktop CLR, _name on CoreCLR.
        var name = SafeValue(thread.ThreadObject) is { Kind: ClrValueKind.Object } value ? SafeValue(() => Types.Field(value, "m_Name") ?? Types.Field(value, "_name")) : null;
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
    private IClrFrame? TopFrame(IClrThread thread) => VisibleFrames(thread).FirstOrDefault(f => f.Module is not null);

    /// <summary>
    /// A thread's frames as the user sees them. On .NET 9 and later CoreCLR reports a thrown exception from its managed
    /// exception dispatcher: the frames above the throw are <c>System.Runtime.EH</c>'s and native ones, which are left
    /// out, so the throw is on top (as the stop's location, a module condition's module and evaluation's frame).
    /// </summary>
    private IReadOnlyList<IClrFrame> VisibleFrames(IClrThread thread)
    {
        var frames = thread.Frames();
        var skip = 0;
        var dispatcher = false;
        while (skip < frames.Count && (frames[skip].Module is null || IsExceptionDispatch(frames[skip])))
        {
            dispatcher |= frames[skip].Module is not null;
            skip++;
        }

        return dispatcher && skip < frames.Count ? [.. frames.Skip(skip)] : frames;
    }

    private bool IsExceptionDispatch(IClrFrame frame) =>
        frame.Module is { } module && Types.Info(module)?.MethodDisplayName(frame.MethodToken) is { } name && name.StartsWith("System.Runtime.EH.", StringComparison.Ordinal);

    private StackTraceResponse StackTrace(int threadId, int start, int levels)
    {
        lock (_gate)
        {
            RequireStopped();
            var frames = VisibleFrames(Thread(threadId));
            foreach (var module in frames.Select(f => f.Module).OfType<IClrModule>().Distinct())
            {
                EnsureModule(module);
            }

            var result = new List<StackFrame>();
            var innermost = frames.FirstOrDefault(f => f.Module is not null);
            foreach (var frame in frames.Skip(start).Take(levels > 0 ? levels : int.MaxValue))
            {
                var id = _nextHandle++;
                _frames[id] = frame;
                if (ReferenceEquals(frame, innermost))
                {
                    _innermost.Add(frame);
                }

                if (frame.Module is not { } module)
                {
                    result.Add(new StackFrame(id, frame.Description ?? "[External Code]", null, 0, 0, PresentationHint: "label"));
                    continue;
                }

                var info = Types.Info(module);
                var name = info?.MethodDisplayName(frame.MethodToken) ?? $"[{Path.GetFileName(module.Path)}]";
                var point = info?.LineAt(frame.MethodToken, frame.IlOffset, frame.CodeVersion);
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
                    ModuleId: ModuleId(module))
                {
                    InstructionPointerReference = frame.NativeCode() is { } code ? Hex(code.Ip) : null,
                });
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

            foreach (var (slot, name) in info.Locals(frame.MethodToken, frame.IlOffset, frame.CodeVersion))
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

        if (_returnValue is { } returned && returned.ThreadId == frame.Thread.Id)
        {
            variables.Add((ReturnValueName, () => returned.Value));
        }

        // The exception the thread stopped on (or is throwing), and the objects with an ID.
        var thread = frame.Thread;
        variables.Add((ExceptionName, () => _exceptions.GetValueOrDefault(thread.Id) ?? SafeValue(Live(thread.CurrentException))));
        foreach (var (name, id) in _objectIds)
        {
            variables.Add((name, () => id.Value));
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
        if (_returnValue is { } returned && returned.ThreadId == context.Frame.Thread.Id && _innermost.Contains(context.Frame))
        {
            // As the classic IDE lists it, first, under the method's name.
            result.Add(VariableOf(returned.Method + " returned", returned.Value, ReturnValueName));
        }

        if (context.This is { } self)
        {
            result.Add(VariableOf("this", SafeValue(self), "this"));
        }

        foreach (var (name, read) in context.Variables.Where(v => !v.Name.StartsWith('$')))
        {
            result.Add(VariableOf(name, SafeValue(read), name));
        }

        return result;
    }

    /// <summary>The pseudo-variable for the value the last step's method returned.</summary>
    public const string ReturnValueName = "$ReturnValue";

    /// <summary>The pseudo-variable for the exception the thread stopped on.</summary>
    public const string ExceptionName = "$exception";

    // ------------------------------------------------------------------ memory, machine code and registers (M58)

    private static string Hex(ulong address) => "0x" + address.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>An address as a client sends it back: <c>0x…</c>, or decimal.</summary>
    private static ulong Address(string reference, long? offset)
    {
        var text = reference.Trim();
        var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : (ulong?)null
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
        return parsed is { } address ? unchecked(address + (ulong)(offset ?? 0)) : throw new DapRequestException($"'{reference}' isn't an address.");
    }

    /// <summary>Where an object, an array or a string is in memory; null for null, a primitive and a value without an address.</summary>
    private static string? MemoryReference(IClrValue? value)
    {
        try
        {
            return value is { Kind: ClrValueKind.Object or ClrValueKind.Array or ClrValueKind.String, Address: not 0 and var address } ? Hex(address) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private ReadMemoryResponse ReadMemory(ReadMemoryArguments arguments)
    {
        var address = Address(arguments.MemoryReference, arguments.Offset);
        var count = Math.Clamp(arguments.Count, 0, 1 << 20);
        lock (_gate)
        {
            RequireStopped();
            var bytes = Process.ReadMemory(address, count);
            return new ReadMemoryResponse(Hex(address), count - bytes.Length is > 0 and var unreadable ? unreadable : null, bytes.Length == 0 ? null : Convert.ToBase64String(bytes));
        }
    }

    private WriteMemoryResponse WriteMemory(WriteMemoryArguments arguments)
    {
        var address = Address(arguments.MemoryReference, arguments.Offset);
        byte[] data;
        try
        {
            data = Convert.FromBase64String(arguments.Data);
        }
        catch (FormatException)
        {
            throw new DapRequestException("The bytes to write aren't base64.");
        }

        lock (_gate)
        {
            RequireStopped();
            try
            {
                return new WriteMemoryResponse(BytesWritten: Process.WriteMemory(address, data));
            }
            catch (ClrException ex)
            {
                throw new DapRequestException(ex.Message);
            }
        }
    }

    private RegisterValue[] Registers(int frameId)
    {
        lock (_gate)
        {
            RequireStopped();
            var names = Disassembler.RegisterNames(Disassembler.Architecture);
            return [.. Frame(frameId).Registers().Where(r => r.Register < names.Count).Select(r => new RegisterValue(names[r.Register], "0x" + r.Value.ToString("x16", CultureInfo.InvariantCulture)))];
        }
    }

    /// <summary>
    /// The instructions around an address. In a method a stopped frame runs, the method's code is decoded from its
    /// start (so instructions before the address are whole ones too) and each source line's first instruction says
    /// its line; elsewhere, from the address on.
    /// </summary>
    private DisassembledInstruction[] Disassemble(DisassembleArguments arguments)
    {
        var address = Address(arguments.MemoryReference, arguments.Offset);
        var count = Math.Clamp(arguments.InstructionCount, 0, 5000);
        var first = arguments.InstructionOffset ?? 0;
        var architecture = Disassembler.Architecture;
        if (!Disassembler.IsSupported(architecture))
        {
            throw new DapRequestException($"Machine code for {architecture} isn't decoded here.");
        }

        lock (_gate)
        {
            RequireStopped();
            foreach (var frame in _frames.Values)
            {
                if (frame.Module is not { } module || frame.NativeCode() is not { } code || address < code.Start || address >= code.Start + (ulong)code.Size)
                {
                    continue;
                }

                var info = Types.Info(module);
                var instructions = Disassembler.Decode(architecture, Process.ReadMemory(code.Start, code.Size), code.Start);
                var at = instructions.FindIndex(i => address >= i.Address && address < i.Address + (ulong)i.Bytes.Length);
                var result = new List<DisassembledInstruction>();
                int? lastLine = null;
                for (var i = Math.Max(0, at + first); i < instructions.Count && result.Count < count; i++)
                {
                    var instruction = instructions[i];
                    var offset = (int)(instruction.Address - code.Start);
                    ClrSequencePoint? point = null;
                    if (info is not null && code.Map.Where(m => m.IlOffset >= 0 && offset >= m.NativeStart && offset < m.NativeEnd).Select(m => (int?)m.IlOffset).FirstOrDefault() is { } il)
                    {
                        point = info.LineAt(frame.MethodToken, il, frame.CodeVersion);
                    }

                    var newLine = point is { Document.Length: > 0 } && point.Value.StartLine != lastLine;
                    lastLine = point?.StartLine ?? lastLine;
                    result.Add(new DisassembledInstruction(
                        Hex(instruction.Address),
                        instruction.Text,
                        Convert.ToHexString(instruction.Bytes).ToLowerInvariant(),
                        i == 0 ? info?.MethodDisplayName(frame.MethodToken) : null,
                        newLine ? new Source(Path.GetFileName(point!.Value.Document), point.Value.Document) : null,
                        newLine ? point!.Value.StartLine : null));
                }

                return [.. result];
            }

            // Not a frame's method: what's at the address, from it on.
            var raw = Disassembler.Decode(architecture, Process.ReadMemory(address, Math.Max(1, count) * Disassembler.MaxInstructionLength(architecture)), address);
            return [.. raw.Skip(Math.Max(0, first)).Take(count).Select(i => new DisassembledInstruction(Hex(i.Address), i.Text, Convert.ToHexString(i.Bytes).ToLowerInvariant()))];
        }
    }

    // ------------------------------------------------------------------ object IDs (M58)

    /// <summary>Gives the object an expression evaluates to an ID: held by a strong handle, so it's reachable (and alive) when no variable in scope holds it.</summary>
    private string MakeObjectId(string expression, int? frameId)
    {
        lock (_gate)
        {
            RequireStopped();
            var frame = frameId is { } id ? Frame(id) : _frames.Values.FirstOrDefault(f => f.Module is not null) ?? throw new DapRequestException("An object ID needs a stopped frame.");
            if (EvaluateIn(frame, expression) is not RemoteValue { Value: { Kind: ClrValueKind.Object or ClrValueKind.Array or ClrValueKind.String } value })
            {
                throw new DapRequestException($"'{expression.Trim()}' isn't an object: only objects can have an ID.");
            }

            if (ObjectIdOf(value) is { } existing)
            {
                return existing;
            }

            var kept = Process.Keep(value, out var release)
                ?? throw new DapRequestException($"'{expression.Trim()}' is a value type's value, which isn't an object of its own: only objects can have an ID.");
            var name = "$" + (++_nextObjectId).ToString(CultureInfo.InvariantCulture);
            _objectIds[name] = (kept, release);
            return name;
        }
    }

    private void DeleteObjectId(string name)
    {
        lock (_gate)
        {
            if (_objectIds.Remove(name.Trim(), out var id))
            {
                id.Release();
            }
        }
    }

    /// <summary>The ID of the object a value is (the same object, by where it is now), if it has one.</summary>
    private string? ObjectIdOf(IClrValue value)
    {
        if (_objectIds.Count == 0 || value.Kind is ClrValueKind.Null or ClrValueKind.Primitive)
        {
            return null;
        }

        try
        {
            var address = value.Address;
            if (address == 0)
            {
                return null;
            }

            foreach (var (name, id) in _objectIds)
            {
                if (id.Value.Address == address)
                {
                    return name;
                }
            }
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    private Dictionary<string, IClrValue> Values(ClrFrameContext context)
    {
        var values = new Dictionary<string, IClrValue>(StringComparer.Ordinal);
        foreach (var (name, read) in context.Variables.Where(v => !v.Name.StartsWith('$')))
        {
            if (SafeValue(read) is { } value)
            {
                values.TryAdd(name, value);
            }
        }

        return values;
    }

    /// <summary>
    /// A reference's children; an array's from <paramref name="start"/>, <paramref name="count"/> of them, when asked
    /// (without a count, the first 100 and how many more).
    /// </summary>
    private IReadOnlyList<Variable> Variables(int reference, int? start = null, int? count = null)
    {
        lock (_gate)
        {
            if (count is > 0 && _pages.TryGetValue(reference, out var page))
            {
                return page(Math.Max(0, start ?? 0), count.Value);
            }

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
            var text = Types.Format(value);
            if (value is not null && ObjectIdOf(value) is { } id && name != id)
            {
                text += " {" + id + "}"; // as the classic IDE marks an object that has an ID
            }

            return new(name, text, value is null ? null : TypeName(value), ChildrenOf(value, evaluateName), evaluateName, IndexedVariables: Indexed(value)) { MemoryReference = MemoryReference(value) };
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

    /// <summary>An array's length, as DAP's indexed variables (M56); null for any other value.</summary>
    private static int? Indexed(IClrValue? value) => value is { Kind: ClrValueKind.Array, Length: > 0 } ? value.Length : null;

    private int ChildrenOf(IClrValue? value, string? evaluateName)
    {
        if (value is null)
        {
            return 0;
        }

        switch (value.Kind)
        {
            case ClrValueKind.Array when value.Length > 0:
                var handle = Handle(() => Elements(value, evaluateName));
                _pages[handle] = (start, count) => Elements(value, evaluateName, start, count);
                return handle;
            case ClrValueKind.Object:
                var full = Types.FullName(value.Type);
                if (full is "System.Decimal" or "System.DateTime" or "System.TimeSpan" || Types.Unboxed(value) is not null || (Types.Definition(value.Type) is { } d && Types.Info(d.Module) is { } info && info.IsEnum(d.Token)))
                {
                    return 0;
                }

                return Handle(() => Members(value, evaluateName), () => ProxyOf(value) is null ? MemberValues(value) : []);
            default:
                return 0;
        }
    }

    /// <summary>The first 100 elements, and how many more: what a request without a count gets.</summary>
    private List<Variable> Elements(IClrValue array, string? evaluateName)
    {
        var count = Math.Min(array.Length, MaxChildren);
        var result = Elements(array, evaluateName, 0, count);
        if (array.Length > count)
        {
            result.Add(new Variable("…", $"{(array.Length - count).ToString(CultureInfo.InvariantCulture)} more"));
        }

        return result;
    }

    /// <summary><paramref name="count"/> elements from <paramref name="start"/> (fewer past the end): a page of a range (M56).</summary>
    private List<Variable> Elements(IClrValue array, string? evaluateName, int start, int count)
    {
        var end = (int)Math.Min(array.Length, (long)start + count);
        var result = new List<Variable>(Math.Max(0, end - start));
        var dimensions = array.Dimensions;
        for (var i = start; i < end; i++)
        {
            var at = i;
            var index = dimensions.Count > 1 ? string.Join(", ", Indices(at, dimensions).Select(n => n.ToString(CultureInfo.InvariantCulture))) : at.ToString(CultureInfo.InvariantCulture);
            result.Add(VariableOf("[" + index + "]", SafeValue(() => array.Element(at)), evaluateName is null ? null : $"{evaluateName}[{index}]"));
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

    private const string TypeProxyAttribute = "System.Diagnostics.DebuggerTypeProxyAttribute";
    private static readonly VariablePresentationHint s_virtual = new(VariablePresentationHint.Virtual);
    private static readonly VariablePresentationHint s_virtualLazy = new(VariablePresentationHint.Virtual, Lazy: true);

    /// <summary>
    /// An object's children (M58): through its type's <c>[DebuggerTypeProxy]</c> when it has one (the proxy's public
    /// members, then the object's own under Raw View), else its own members, with a Results View when it can be
    /// enumerated. A type the client shows in a view of its own gets its own members only.
    /// </summary>
    private List<Variable> Members(IClrValue target, string? evaluateName)
    {
        if (ProxyOf(target) is { } proxy && CreateProxy(target, proxy) is { } view)
        {
            var members = ProxyMembers(view);
            members.Add(new Variable(Variable.RawView, string.Empty, TypeName(target), Handle(() => RawMembers(target, evaluateName), () => MemberValues(target)), evaluateName, PresentationHint: s_virtual));
            return members;
        }

        var raw = RawMembers(target, evaluateName);
        if (!ShownByClient(target) && IsEnumerable(target))
        {
            raw.Add(new Variable(Variable.ResultsView, "Expanding the Results View will enumerate the IEnumerable", string.Empty, Handle(() => Enumerate(target)), PresentationHint: s_virtualLazy));
        }

        return raw;
    }

    private bool ShownByClient(IClrValue value) => _clientViews.Count > 0 && _clientViews.Contains(Types.FullName(value.Type));

    /// <summary>The proxy type of a value's type or of one of its bases (with that type's arguments, for a generic proxy); null without one.</summary>
    private (IClrModule Module, int Token, ClrModuleInfo Info, IReadOnlyList<ClrType> TypeArguments)? ProxyOf(IClrValue value)
    {
        if (value.Kind != ClrValueKind.Object || ShownByClient(value))
        {
            return null;
        }

        foreach (var (module, token, info, typeArguments) in Types.ChainWithArguments(value.Type))
        {
            if (!info.TypeHasAttribute(token, TypeProxyAttribute))
            {
                continue;
            }

            // The attribute's type as it's written there: "Ns.Proxy`1, Assembly, Version=…", or without an assembly in its own.
            if (_attributes.Find(info.TypeFullName(token), [info.Path]).TypeProxy is not { Length: > 0 } name)
            {
                return null;
            }

            var comma = name.IndexOf(',', StringComparison.Ordinal);
            var typeName = (comma < 0 ? name : name[..comma]).Trim();
            var assembly = comma < 0 ? null : name[(comma + 1)..].Split(',')[0].Trim();
            foreach (var (candidate, candidateInfo) in Types.Modules.Where(m => assembly is not null && string.Equals(m.Info.AssemblyName, assembly, StringComparison.OrdinalIgnoreCase)).Append((module, info)))
            {
                if (candidateInfo.FindType(typeName) is var found and not 0)
                {
                    return (candidate, found, candidateInfo, typeArguments);
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>The proxy object for a value: its type's one-parameter constructor run on the value (func-eval); null when it can't be made (the log says why).</summary>
    private IClrValue? CreateProxy(IClrValue target, (IClrModule Module, int Token, ClrModuleInfo Info, IReadOnlyList<ClrType> TypeArguments) proxy)
    {
        var name = proxy.Info.TypeDisplayName(proxy.Token);
        var arity = proxy.Info.GenericParameterCount(proxy.Token);
        if (CurrentThread() is not { } thread || (arity != 0 && arity != proxy.TypeArguments.Count)
            || proxy.Info.Methods(proxy.Token).FirstOrDefault(m => m.Name == ".ctor" && !m.IsStatic && m.ParameterTypes.Count == 1) is not { } constructor)
        {
            return null;
        }

        try
        {
            var created = Process.NewObject(thread, proxy.Module, constructor.Token, arity == 0 ? [] : proxy.TypeArguments, [target], EvalTimeout);
            return ClrLiveValue.Of(Process, created, () => created);
        }
        catch (ClrEvalException ex)
        {
            log.WriteLine($"The type proxy {name} threw {Types.FullName(ex.Exception.Type)}: the object's own members are shown.");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            log.WriteLine($"The type proxy {name} couldn't be created ({ex.Message}): the object's own members are shown.");
        }

        return null;
    }

    /// <summary>
    /// A proxy's view: its public instance properties and fields, without those <c>[DebuggerBrowsable]</c> hides, and
    /// with a root-hidden one replaced by its elements or members (a collection's <c>Items</c>).
    /// </summary>
    private List<Variable> ProxyMembers(IClrValue proxy)
    {
        var result = new List<Variable>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var thread = CurrentThread();
        foreach (var (module, token, info, typeArguments) in Types.ChainWithArguments(proxy.Type).Where(c => c.Info.TypeFullName(c.Token) is not ("System.Object" or "System.ValueType")))
        {
            var browsable = _attributes.Find(info.TypeFullName(token), [info.Path]);
            void Add(string name, Func<IClrValue?> read)
            {
                if (browsable.Hidden.Contains(name) || !seen.Add(name) || result.Count >= MaxChildren)
                {
                    return;
                }

                IClrValue? value;
                try
                {
                    value = read();
                }
                catch (ClrEvalException ex)
                {
                    value = ex.Exception;
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
                {
                    result.Add(new Variable(name, "(" + ex.Message + ")"));
                    return;
                }

                if (browsable.RootHidden.Contains(name))
                {
                    result.AddRange(Flattened(value));
                }
                else
                {
                    result.Add(Displayed(VariableOf(name, value, null), value));
                }
            }

            foreach (var property in info.Properties(token).Where(p => p is { IsStatic: false, IndexParameters: 0, IsPublic: true } && p.GetterToken != 0))
            {
                var getter = property.GetterToken;
                Add(property.Name, () =>
                {
                    if (thread is null)
                    {
                        return null;
                    }

                    var value = Process.Call(thread, module, getter, typeArguments, [proxy], EvalTimeout);
                    return ClrLiveValue.Of(Process, value, () => value);
                });
            }

            foreach (var field in info.Fields(token).Where(f => f is { IsStatic: false, IsPublic: true }))
            {
                var fieldToken = field.Token;
                Add(field.Name, () => proxy.Field(module, token, fieldToken));
            }
        }

        return result;
    }

    /// <summary>A root-hidden member's value in its place: an array's elements, an object's members; nothing for null.</summary>
    private List<Variable> Flattened(IClrValue? value)
    {
        switch (value?.Kind)
        {
            case ClrValueKind.Array:
                var count = Math.Min(value.Length, MaxChildren);
                var elements = new List<Variable>(count + 1);
                for (var i = 0; i < count; i++)
                {
                    var at = i;
                    var element = SafeValue(() => value.Element(at));
                    elements.Add(Displayed(VariableOf("[" + at.ToString(CultureInfo.InvariantCulture) + "]", element, null), element));
                }

                if (value.Length > count)
                {
                    elements.Add(new Variable("…", $"{(value.Length - count).ToString(CultureInfo.InvariantCulture)} more"));
                }

                return elements;
            case ClrValueKind.Object:
                return Members(value, null);
            default:
                return [];
        }
    }

    /// <summary>
    /// A variable with its type's <c>[DebuggerDisplay]</c> applied (its value, and its name when the attribute names
    /// it): for the rows of a proxy's view and of a Results View, which no expression of the client's reaches (a
    /// dictionary's entries show as <c>[key]</c> and their value).
    /// </summary>
    private Variable Displayed(Variable variable, IClrValue? value)
    {
        if (value is not { Kind: ClrValueKind.Object } || _frames.Values.FirstOrDefault(f => f.Module is not null) is not { } frame)
        {
            return variable;
        }

        try
        {
            var chain = Types.Chain(value.Type).ToList();
            if (chain.Count == 0)
            {
                return variable;
            }

            var attributes = _attributes.Find(chain[0].Info.TypeFullName(chain[0].Token), [.. chain.Select(c => c.Info.Path).Distinct()]);
            if (attributes.Display is null && attributes.Name is null)
            {
                return variable;
            }

            var typeName = chain[0].Info.TypeFullName(chain[0].Token);
            var evaluator = new ClrEvaluator(Types, Process, new ClrFrameContext(frame, [], () => value, (chain[0].Module, chain[0].Token), typeName.LastIndexOf('.') is var dot and > 0 ? typeName[..dot] : null), EvalTimeout);
            return variable with
            {
                Value = attributes.Display is { } display ? Fill(display, evaluator) : variable.Value,
                Name = attributes.Name is { } name ? Fill(name, evaluator) : variable.Name,
            };
        }
        catch (InvalidOperationException ex)
        {
            log.WriteLine("A [DebuggerDisplay] couldn't be read: " + ex.Message);
            return variable;
        }
    }

    /// <summary>A <c>[DebuggerDisplay]</c> string with each <c>{expression}</c> evaluated on the object (<c>,nq</c>: a string without its quotes).</summary>
    private string Fill(string format, ClrEvaluator evaluator)
    {
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '{' || format.IndexOf('}', i) is not (var end and > 0))
            {
                result.Append(format[i]);
                continue;
            }

            var expression = format[(i + 1)..end];
            var noQuotes = false;
            if (expression.LastIndexOf(',') is var comma and > 0 && expression[(comma + 1)..].Trim() is "nq" or "d" or "h" or "x" or "raw" or "nse" or "ac")
            {
                noQuotes = expression[(comma + 1)..].Trim() == "nq";
                expression = expression[..comma];
            }

            try
            {
                result.Append(evaluator.Evaluate(expression) switch
                {
                    LocalValue { Value: string text } when noQuotes => text,
                    LocalValue local => ClrTypeSystem.FormatPrimitive(local.Value),
                    RemoteValue { Value: { Kind: ClrValueKind.String, Text: { } text } } when noQuotes => text,
                    RemoteValue remote => Types.Format(remote.Value),
                    _ => "{" + expression + "}",
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                result.Append('{').Append(expression).Append('}');
            }

            i = end;
        }

        return result.ToString();
    }

    private const string EnumerableInterface = "System.Collections.IEnumerable";

    /// <summary>Whether an object can be enumerated: its type or a base implements <c>IEnumerable</c> (a string and an array show their own elements).</summary>
    private bool IsEnumerable(IClrValue value) =>
        value.Kind == ClrValueKind.Object
        && Types.Chain(value.Type).Any(c => c.Info.Interfaces(c.Token).Any(i => i == EnumerableInterface || i.StartsWith("System.Collections.Generic.IEnumerable`1", StringComparison.Ordinal)));

    /// <summary>
    /// A Results View's rows: the first elements <c>IEnumerable.GetEnumerator()</c> gives, read through func-evals when
    /// the node is expanded (enumerating runs the program's code, a query's included).
    /// </summary>
    private List<Variable> Enumerate(IClrValue target)
    {
        var result = new List<Variable>();
        if (CurrentThread() is not { } thread || Types.CoreLibrary is not var (module, info)
            || info.FindType(EnumerableInterface) is not (var enumerable and not 0) || info.FindType("System.Collections.IEnumerator") is not (var enumeratorType and not 0)
            || info.Methods(enumerable).FirstOrDefault(m => m.Name == "GetEnumerator") is not { } getEnumerator
            || info.Methods(enumeratorType).FirstOrDefault(m => m.Name == "MoveNext") is not { } moveNext
            || info.Methods(enumeratorType).FirstOrDefault(m => m.Name == "get_Current") is not { } current)
        {
            return [new Variable("(not evaluated)", "The Results View needs a stopped thread.")];
        }

        try
        {
            var enumerator = Process.Call(thread, module, getEnumerator.Token, [], [target], EvalTimeout);
            for (var i = 0; ; i++)
            {
                if (Process.Call(thread, module, moveNext.Token, [], [enumerator], EvalTimeout) is not { Kind: ClrValueKind.Primitive, Primitive: true })
                {
                    break;
                }

                if (i == MaxChildren)
                {
                    result.Add(new Variable("…", $"More: the first {MaxChildren.ToString(CultureInfo.InvariantCulture)} are listed."));
                    break;
                }

                var element = Process.Call(thread, module, current.Token, [], [enumerator], EvalTimeout);
                var live = ClrLiveValue.Of(Process, element, () => element);
                result.Add(Displayed(VariableOf("[" + i.ToString(CultureInfo.InvariantCulture) + "]", live, null), live));
            }

            if (result.Count == 0)
            {
                result.Add(new Variable("Empty", "\"Enumeration yielded no results\"", "string"));
            }
        }
        catch (ClrEvalException ex)
        {
            result.Add(VariableOf("Error", ex.Exception, null));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            result.Add(new Variable("Error", "(" + ex.Message + ")"));
        }

        return result;
    }

    /// <summary>An object's instance fields (auto-properties' backing fields under the property's name) and its other properties, read through their getters.</summary>
    private List<Variable> RawMembers(IClrValue target, string? evaluateName)
    {
        var result = new List<Variable>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chain = Types.ChainWithArguments(target.Type).Where(c => c.Info.TypeFullName(c.Token) is not ("System.Object" or "System.ValueType")).ToList();
        foreach (var (module, token, info, _) in chain)
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
        foreach (var (module, token, info, typeArguments) in chain)
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
                    var value = Process.Call(thread, module, property.GetterToken, typeArguments, [target], EvalTimeout);
                    result.Add(VariableOf(property.Name, ClrLiveValue.Of(Process, value, () => value), path));
                }
                catch (ClrEvalException ex)
                {
                    // As netcoredbg shows it: the exception, typed (the data tip marks the getter as having thrown) and expandable.
                    result.Add(VariableOf(property.Name, ex.Exception, path));
                }
                catch (TimeoutException ex)
                {
                    timedOut = true;
                    log.WriteLine($"{property.Name}'s getter: " + (ex is ClrEvalTimeoutException ? "timed out (above)" : ex.Message));
                    result.Add(new Variable(property.Name, "(the getter timed out)", null, 0, path));
                }
                catch (InvalidOperationException ex)
                {
                    result.Add(new Variable(property.Name, "(" + ex.Message + ")", null, 0, path));
                }
            }
        }

        var members = result.OrderBy(v => v.Name, StringComparer.Ordinal).Take(MaxChildren).ToList();

        // The type's static members under one node, last, as netcoredbg lists them (the data tip's static group, M54):
        // no value or type, the type's name to evaluate.
        if (Types.Definition(target.Type) is { } own && Types.Info(own.Module) is { } ownInfo
            && (ownInfo.Fields(own.Token).Any(f => f.IsStatic && !f.IsLiteral) || ownInfo.Properties(own.Token).Any(p => p.IsStatic && p.IndexParameters == 0 && p.GetterToken != 0)))
        {
            var type = target.Type;
            members.Add(new Variable(StaticMembers, string.Empty, string.Empty, Handle(() => Statics(own.Module, own.Token, type)), ownInfo.TypeFullName(own.Token)));
        }

        return members;
    }

    private const string StaticMembers = "Static members";

    /// <summary>A type's own static fields and properties (through their getters).</summary>
    private List<Variable> Statics(IClrModule module, int token, ClrType type)
    {
        var result = new List<Variable>();
        var info = Types.Info(module)!;
        var name = info.TypeFullName(token);
        var thread = CurrentThread();
        foreach (var field in info.Fields(token).Where(f => f.IsStatic && !f.IsLiteral))
        {
            if (ClrTypeSystem.DisplayFieldName(field.Name) is { } display && thread is not null)
            {
                var fieldToken = field.Token;
                result.Add(VariableOf(display, SafeValue(() => Process.GetStaticField(thread, type, fieldToken)), name + "." + display));
            }
        }

        foreach (var property in info.Properties(token).Where(p => p.IsStatic && p.IndexParameters == 0 && p.GetterToken != 0 && result.All(v => v.Name != p.Name)))
        {
            if (thread is null)
            {
                break;
            }

            try
            {
                result.Add(VariableOf(property.Name, Process.Call(thread, module, property.GetterToken, type.TypeArguments, [], EvalTimeout), name + "." + property.Name));
            }
            catch (ClrEvalException ex)
            {
                result.Add(VariableOf(property.Name, ex.Exception, name + "." + property.Name));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                result.Add(new Variable(property.Name, "(" + ex.Message + ")", null, 0, name + "." + property.Name));
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

    /// <summary>
    /// An expression's value in a frame: by the evaluator here, and what it doesn't read (a lambda, a query, a generic or
    /// an extension method, Visual Basic) compiled by the client and run in the program (M58, ADR 0094).
    /// </summary>
    private EvalValue EvaluateIn(IClrFrame frame, string expression)
    {
        // The evaluator reads C#. In a Visual Basic frame it reads what means the same in both (a name, its members),
        // and everything else is the compiler's: Me, =, <>, And, Not, items(0), a / b.
        var visualBasic = _compilesExpressions && IsVisualBasic(frame);
        if (visualBasic && !IsMemberChain(expression))
        {
            return Compiled(frame, expression, new UnsupportedExpressionException("Visual Basic expressions need the compiler."));
        }

        try
        {
            return Evaluator(frame).Evaluate(expression);
        }
        catch (UnsupportedExpressionException ex) when (_compilesExpressions)
        {
            return Compiled(frame, expression, ex);
        }
        catch (ExpressionException ex) when (visualBasic)
        {
            return Compiled(frame, expression, new UnsupportedExpressionException(ex.Message));
        }
    }

    private bool IsVisualBasic(IClrFrame frame) =>
        frame.Module is { } module && Types.Info(module)?.LineAt(frame.MethodToken, frame.IlOffset, frame.CodeVersion) is { Document: { } document }
        && document.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

    /// <summary>A name and its members (<c>order.Customer.Name</c>): the same in C# and Visual Basic.</summary>
    internal static bool IsMemberChain(string expression)
    {
        var text = expression.AsSpan().Trim();
        if (text.IsEmpty || text[^1] == '.' || !(char.IsLetter(text[0]) || text[0] is '_' or '$'))
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!(char.IsLetterOrDigit(text[i]) || text[i] == '_' || (text[i] == '.' && text[i - 1] != '.')))
            {
                return false;
            }
        }

        return !text.Equals("Me", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("Me.", StringComparison.OrdinalIgnoreCase)
            && !text.Equals("Nothing", StringComparison.OrdinalIgnoreCase) && !text.Equals("True", StringComparison.OrdinalIgnoreCase) && !text.Equals("False", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The reverse request for an expression the evaluator doesn't read.</summary>
    public const string CompileExpressionCommand = "codalus/compileExpression";

    private const string Base64 = "System.Convert";

    private RemoteValue Compiled(IClrFrame frame, string expression, UnsupportedExpressionException reason)
    {
        if (frame.Module is not { } module || Types.Info(module) is not { } info || info.LineAt(frame.MethodToken, frame.IlOffset, frame.CodeVersion) is not { Document.Length: > 0 } point)
        {
            throw reason; // no source line to compile it at
        }

        // The same expression at the same place is the same method: a watch compiles once.
        var key = string.Join('\n', expression.Trim(), info.Mvid.ToString("N"), frame.MethodToken.ToString(CultureInfo.InvariantCulture), frame.CodeVersion.ToString(CultureInfo.InvariantCulture),
            point.StartLine.ToString(CultureInfo.InvariantCulture), point.StartColumn.ToString(CultureInfo.InvariantCulture));
        if (!_compiledExpressions.TryGetValue(key, out var compiled))
        {
            _compiledExpressions[key] = compiled = Compile(frame, expression, module, point, reason);
        }

        if (compiled.Error is { } error || compiled.Module is not { } assembly)
        {
            throw new ExpressionException(compiled.Error ?? reason.Message);
        }

        var evaluator = Evaluator(frame);
        var arguments = new List<IClrValue>(compiled.Parameters.Length);
        foreach (var parameter in compiled.Parameters)
        {
            arguments.Add(evaluator.Evaluate(parameter) is RemoteValue { Value: var value } ? value : throw new ExpressionException($"'{parameter}' has no value to pass to the expression."));
        }

        var typeArguments = compiled.TypeParameters == 0 ? [] : frame.TypeArguments;
        if (typeArguments.Count != compiled.TypeParameters)
        {
            throw new ExpressionException("The frame's type arguments aren't available, so the expression can't be run here.");
        }

        try
        {
            var result = Process.Call(frame.Thread, assembly, compiled.MethodToken, typeArguments, arguments, EvalTimeout);
            return new RemoteValue(ClrLiveValue.Of(Process, result, () => result));
        }
        catch (ClrEvalException ex)
        {
            throw new ExpressionException($"The expression threw {Types.FullName(ex.Exception.Type)}" + (Types.Field(ex.Exception, "_message") is { Kind: ClrValueKind.String } message ? ": " + message.Text : "."));
        }
        catch (TimeoutException)
        {
            throw new ExpressionException($"Evaluating the expression timed out after {EvalTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    /// Asks the client to compile an expression where the frame is, and loads the assembly it sends into the program:
    /// its bytes as a base64 string (func-eval's <c>NewString</c>), <c>Convert.FromBase64String</c>, then
    /// <c>Assembly.Load</c>. A failure is the compiler's words.
    /// </summary>
    private CompiledExpression Compile(IClrFrame frame, string expression, IClrModule module, ClrSequencePoint point, UnsupportedExpressionException reason)
    {
        static CompiledExpression Failed(string error) => new(null, 0, [], 0, error);
        CompileExpressionResponse? response;
        try
        {
            response = dap.RequestAsync(CompileExpressionCommand, new CompileExpressionArguments(expression, point.Document, point.StartLine, point.StartColumn, module.Path), DapJsonContext.Default.CompileExpressionArguments, CancellationToken.None)
                .GetAwaiter().GetResult()?.Deserialize(DapJsonContext.Default.CompileExpressionResponse);
        }
        catch (Exception ex) when (ex is DapRequestException or IOException)
        {
            log.WriteLine("The expression couldn't be compiled: " + ex.Message);
            return Failed(reason.Message);
        }

        if (response?.Errors is { Length: > 0 } errors)
        {
            return Failed(string.Join(" ", errors));
        }

        if (response is not { Assembly: { Length: > 0 } bytes, TypeName: { } typeName, MethodName: { } methodName })
        {
            return Failed(reason.Message);
        }

        try
        {
            // The method's token and the module's MVID, from the bytes.
            Guid mvid;
            int token;
            using (var pe = new System.Reflection.PortableExecutable.PEReader(System.Collections.Immutable.ImmutableArray.Create(bytes)))
            {
                var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
                token = 0;
                foreach (var handle in metadata.MethodDefinitions)
                {
                    var method = metadata.GetMethodDefinition(handle);
                    var type = metadata.GetTypeDefinition(method.GetDeclaringType());
                    var ns = metadata.GetString(type.Namespace);
                    if (metadata.GetString(method.Name) == methodName && (ns.Length == 0 ? string.Empty : ns + ".") + metadata.GetString(type.Name) == typeName)
                    {
                        token = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(handle);
                        break;
                    }
                }
            }

            if (token == 0 || Types.CoreLibrary is not var (core, coreInfo)
                || coreInfo.FindType(Base64) is not (var convert and not 0) || coreInfo.Methods(convert).FirstOrDefault(m => m is { Name: "FromBase64String", IsStatic: true, ParameterTypes: ["string"] }) is not { } fromBase64
                || coreInfo.FindType("System.Reflection.Assembly") is not (var assemblyType and not 0) || coreInfo.Methods(assemblyType).FirstOrDefault(m => m is { Name: "Load", IsStatic: true, ParameterTypes: ["byte[]"] }) is not { } load)
            {
                return Failed("The compiled expression couldn't be loaded into the program: its core library has no Assembly.Load.");
            }

            var text = Process.NewString(frame.Thread, Convert.ToBase64String(bytes), EvalTimeout);
            var array = Process.Call(frame.Thread, core, fromBase64.Token, [], [text], EvalTimeout);
            Process.Call(frame.Thread, core, load.Token, [], [array], EvalTimeout);
            if (Process.FindModule(mvid) is not { } loaded)
            {
                return Failed("The compiled expression's assembly wasn't found in the program after it was loaded.");
            }

            _expressionModules.Add(loaded);
            return new CompiledExpression(loaded, token, response.Parameters ?? [], response.TypeParameters, null);
        }
        catch (ClrEvalException ex)
        {
            return Failed($"The compiled expression couldn't be loaded into the program: {Types.FullName(ex.Exception.Type)}" + (Types.Field(ex.Exception, "_message") is { Kind: ClrValueKind.String } message ? ": " + message.Text : "."));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or BadImageFormatException)
        {
            return Failed("The compiled expression couldn't be loaded into the program: " + ex.Message);
        }
    }

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

            var result = EvaluateIn(frame, expression);
            return result switch
            {
                LocalValue local => new EvaluateResponse(ClrTypeSystem.FormatPrimitive(local.Value), local.Value is null ? null : LocalTypeName(local.Value)),
                RemoteValue remote => new EvaluateResponse(Types.Format(remote.Value), TypeName(remote.Value), ChildrenOf(remote.Value, expression.Trim()), IndexedVariables: Indexed(remote.Value)) { MemoryReference = MemoryReference(remote.Value) },
                TypeValue type => throw new DapRequestException($"'{expression.Trim()}' is a type ({Types.Name(ClrTypeSystem.TypeOf(type.Module, type.Token))}), not a value."),
                _ => throw new DapRequestException($"'{expression.Trim()}' is a namespace, not a value."),
            };
        }
    }

    /// <summary>Assigns a value to an expression (a member, an element, a variable) in a stopped frame (M54).</summary>
    private SetExpressionResponse SetExpression(string expression, string value, int? frameId)
    {
        lock (_gate)
        {
            RequireStopped();
            var frame = frameId is { } id ? Frame(id) : _frames.Values.FirstOrDefault(f => f.Module is not null) ?? throw new DapRequestException("Assignment needs a stopped frame.");
            if (frame.Module is null)
            {
                throw new DapRequestException("Expressions can't be assigned in this frame (it isn't managed code).");
            }

            return Evaluator(frame).Assign(expression, value) switch
            {
                LocalValue local => new SetExpressionResponse(ClrTypeSystem.FormatPrimitive(local.Value), local.Value is null ? null : LocalTypeName(local.Value)),
                RemoteValue remote => new SetExpressionResponse(Types.Format(remote.Value), TypeName(remote.Value), ChildrenOf(remote.Value, expression.Trim())),
                _ => throw new DapRequestException($"'{expression.Trim()}' isn't a value."),
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
                if (info.LineAt(frame.MethodToken, frame.IlOffset, frame.CodeVersion) is { Document.Length: > 0 } point)
                {
                    line += $" in {point.Document}:line {point.StartLine.ToString(CultureInfo.InvariantCulture)}";
                }

                lines.Add(line);
            }
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    /// <summary>The frames logged per thread when a func-eval times out.</summary>
    private const int TimedOutEvalFrames = 30;

    /// <summary>
    /// What every thread is doing while a func-eval that timed out is stopped before its abort (<see cref="CorDebugTarget.DescribeThreads"/>):
    /// the eval thread's frames above the evaluation, and where the suspended threads are, to tell what it waits on.
    /// </summary>
    private string ThreadsOfTimedOutEval()
    {
        var text = new System.Text.StringBuilder();
        foreach (var thread in Process.Threads())
        {
            var frames = SafeFrames(thread);
            text.Append(text.Length == 0 ? "" : Environment.NewLine).Append("  thread ").Append(thread.Id.ToString(CultureInfo.InvariantCulture)).Append(':');
            foreach (var frame in frames.Take(TimedOutEvalFrames))
            {
                text.AppendLine().Append("    ").Append(FrameText(frame));
            }

            if (frames.Count > TimedOutEvalFrames)
            {
                text.AppendLine().Append("    (").Append((frames.Count - TimedOutEvalFrames).ToString(CultureInfo.InvariantCulture)).Append(" more)");
            }
        }

        return text.ToString();
    }

    /// <summary>A frame for the log: its method and IL offset, or what an internal or native frame is.</summary>
    private string FrameText(IClrFrame frame)
    {
        if (frame.Module is not { } module)
        {
            return frame.Description ?? "[?]";
        }

        var token = "0x" + frame.MethodToken.ToString("X8", CultureInfo.InvariantCulture);
        string name;
        try
        {
            name = Types.Info(module)?.MethodDisplayName(frame.MethodToken) ?? $"[{Path.GetFileName(module.Path)}] {token}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or BadImageFormatException or ArgumentException)
        {
            name = $"[{Path.GetFileName(module.Path)}] {token}";
        }

        return name + " (IL 0x" + frame.IlOffset.ToString("X", CultureInfo.InvariantCulture) + ")";
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
        _pages.Clear();
        _gotoTargets.Clear();
        _stepInTargets.Clear();
        _innermost.Clear();
    }
}
