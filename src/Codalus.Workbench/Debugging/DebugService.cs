using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Codalus.Debugging;
using Codalus.Workbench.Build;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Debugging;

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

/// <summary>
/// Why the program stopped. <see cref="Exception"/> is set for exception stops. <see cref="Elapsed"/> is the time
/// from resuming the program (or starting it) to this stop, for PerfTips.
/// </summary>
public sealed record StopInfo(string Reason, int ThreadId, string? Description, ExceptionInfoResponse? Exception, TimeSpan? Elapsed = null);

/// <summary>A module the debuggee loaded (from the adapter's <c>module</c> events).</summary>
public sealed record DebugModule(string Id, string Name, string? Path, string? SymbolStatus, bool? IsOptimized, bool? IsUserCode, int Order);

/// <summary>A line of debugger output: <see cref="Category"/> is <c>stdout</c>, <c>stderr</c>, <c>console</c> (the debugger's own) or <c>important</c>.</summary>
public sealed record DebugOutput(string Category, string Text);

/// <summary>
/// Debug sessions on netcoredbg (ADR 0007): launch or attach, breakpoints (with client-side hit counts and logpoints),
/// stepping, threads, frames, variables and evaluation. One session at a time, which may span a server and its page
/// in the browser (a compound session, ADR 0027). Events may be raised on any thread.
/// </summary>
public sealed class DebugService : IAsyncDisposable
{
    public const string ExceptionsAll = "all";
    public const string ExceptionsUserUnhandled = "user-unhandled";

    private readonly Func<string?> _resolveAdapter;
    private readonly Func<string?> _resolveX64Adapter;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, BreakpointSpec> _adapterBreakpoints = [];
    private readonly Dictionary<BreakpointSpec, int> _hits = [];
    private readonly Dictionary<int, FunctionBreakpointSpec> _adapterFunctionBreakpoints = [];
    private readonly HashSet<int> _runToIds = [];
    private readonly List<BreakpointLocation> _hitLocations = [];
    private readonly Dictionary<string, DebugModule> _modules = new(StringComparer.Ordinal);
    private readonly List<DebugProcess> _processes = [];
    private readonly List<int> _terminalProcesses = [];
    private RunTarget? _runTo;
    private long _resumedAt;
    private IDebugAdapter? _adapter;
    private bool _attached;
    private int _exitCode;
    private CancellationTokenSource _session = new();

    /// <summary>Completes when the current session has ended (and when there is none).</summary>
    private TaskCompletionSource _ended = Completed();
    private TaskCompletionSource<int> _process = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A Run to Cursor in progress: where to, and whether other breakpoints are skipped (Force Run to Cursor).</summary>
    private sealed record RunTarget(BreakpointLocation Location, bool Force);

    /// <param name="resolveAdapter">Finds netcoredbg (<see cref="DebugAdapterLocator.Resolve"/>).</param>
    /// <param name="resolveX64Adapter">
    /// Finds the x64 netcoredbg, for programs that run as x64 on an Arm64 machine (M33, ADR 0030); by default
    /// <see cref="DebugAdapterLocator.ResolveX64"/>.
    /// </param>
    public DebugService(Func<string?> resolveAdapter, Func<string?>? resolveX64Adapter = null)
    {
        _resolveAdapter = resolveAdapter;
        _resolveX64Adapter = resolveX64Adapter ?? DebugAdapterLocator.ResolveX64;
        Breakpoints.Changed += (_, path) => _ = SyncBreakpointsAsync(path);
        Breakpoints.FunctionsChanged += (_, _) => _ = SyncFunctionBreakpointsAsync();
        ExceptionSettings.Changed += (_, _) => _ = ApplyExceptionSettingsAsync();
    }

    public BreakpointStore Breakpoints { get; } = new();

    /// <summary>Data tips pinned to source lines, evaluated again at each pause (M14b).</summary>
    public PinnedDataTipStore PinnedTips { get; } = new();

    /// <summary>Which exceptions break when thrown (applied to a running session as they change).</summary>
    public ExceptionSettings ExceptionSettings { get; } = new();

    public bool JustMyCode { get; set; } = true;

    /// <summary>Step over properties and operators (netcoredbg's <c>enableStepFiltering</c>, M14b).</summary>
    public bool StepFiltering { get; set; } = true;

    /// <summary>The modules the debuggee loaded, in load order.</summary>
    public IReadOnlyList<DebugModule> Modules
    {
        get
        {
            lock (_gate)
            {
                return [.. _modules.Values.OrderBy(m => m.Order)];
            }
        }
    }

    /// <summary>A module was loaded, changed or unloaded (or the list was cleared).</summary>
    public event EventHandler? ModulesChanged;

    /// <summary>Whether a Run to Cursor is in progress.</summary>
    public bool IsRunningToCursor => _runTo is not null;

    public DebugState State { get; private set; }

    public bool IsActive => State != DebugState.Idle;

    /// <summary>Whether the session attached to a running process (Stop detaches instead of terminating).</summary>
    public bool IsAttached => _attached;

    /// <summary>Starts programs in the integrated terminal (M14c); without it, launches use the Debug Console.</summary>
    public ITerminalLauncher? Terminal { get; set; }

    /// <summary>Starts programs in console windows of their own (Windows, <see cref="DebugConsoleKind.Window"/>).</summary>
    public ConsoleWindowLauncher? ConsoleWindows { get; set; }

    /// <summary>The startup hook that holds a program launched in the terminal until the debugger attached (<see cref="HeldLaunch"/>).</summary>
    public string? StartupHook { get; set; }

    /// <summary>How long a program launched in the terminal may take to reach the startup hook.</summary>
    public TimeSpan TerminalStartTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The session's programs (several with multiple startup projects, M14c), for the Processes window.</summary>
    public IReadOnlyList<DebugProcess> Processes
    {
        get
        {
            lock (_gate)
            {
                return [.. _processes];
            }
        }
    }

    /// <summary>A program of the session started, got its process id, or ended.</summary>
    public event EventHandler? ProcessesChanged;

    /// <summary>
    /// Whether Detach All leaves the programs running: each was attached to or launched in the terminal (a program
    /// launched with the Debug Console has the debugger's console, and ends with it).
    /// </summary>
    public bool CanDetach
    {
        get
        {
            lock (_gate)
            {
                return State != DebugState.Idle && _processes.Count > 0 && _processes.Where(p => !p.Ended).All(p => p.CanDetach);
            }
        }
    }

    private void UpdateProcess(int index, Func<DebugProcess, DebugProcess> update)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _processes.Count)
            {
                return;
            }

            _processes[index] = update(_processes[index]);
        }

        ProcessesChanged?.Invoke(this, EventArgs.Empty);
    }

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

    /// <summary>
    /// Starts a plugin's debug adapter (API 1.0, M22a) and sends <paramref name="launch"/> (the launch configuration, its
    /// variables expanded) as its <c>launch</c> request. The session is as any other: breakpoints, stepping, the Debug
    /// Console. Throws when the adapter doesn't start or refuses the launch.
    /// </summary>
    public Task LaunchAdapterAsync(string name, DebugAdapterStartInfo adapter, System.Text.Json.JsonElement launch, CancellationToken cancellationToken) =>
        StartAsync(
            async ct => await DebugAdapter.StartAsync(adapter, ct, Terminal).ConfigureAwait(false),
            started => started.LaunchAsync(launch, DapJsonContext.Default.JsonElement, cancellationToken),
            attached: false,
            [new DebugProcess(name, null, DebugProcess.Launched, null)],
            cancellationToken);

    /// <summary>Starts <paramref name="plan"/> under the debugger. Throws when no adapter is installed or the launch fails.</summary>
    public Task LaunchAsync(LaunchPlan plan, CancellationToken cancellationToken) =>
        LaunchAsync([new DebugLaunch(ProgramName(plan), plan)], cancellationToken);

    private LaunchArguments Arguments(LaunchPlan plan) =>
        new(plan.FileName, [.. plan.SplitArguments()], plan.WorkingDirectory, new Dictionary<string, string>(plan.Environment), JustMyCode: JustMyCode, EnableStepFiltering: StepFiltering);

    /// <summary>
    /// Starts <paramref name="launches"/> under the debugger (M14c). One program is one session as before; several
    /// (multiple startup projects) are one compound session, each program on a netcoredbg of its own, which lasts
    /// until the last one ends. A program in the terminal starts there and the debugger attaches; a program in a WSL
    /// distribution runs under netcoredbg there. Throws when the first program can't start; a later one that can't
    /// is reported in the Debug Console and the others go on.
    /// </summary>
    /// <param name="compound">A compound session even for one program, so programs can join it later (<see cref="AddLaunchAsync"/>, M17b).</param>
    public async Task LaunchAsync(IReadOnlyList<DebugLaunch> launches, CancellationToken cancellationToken, bool compound = false)
    {
        if (launches.Count == 0)
        {
            throw new ArgumentException("Nothing to launch.", nameof(launches));
        }

        if (launches.Count > CompoundDebugAdapter.MaxParts)
        {
            throw new InvalidOperationException($"A debug session starts at most {CompoundDebugAdapter.MaxParts.ToString(CultureInfo.InvariantCulture)} programs.");
        }

        var parts = launches.Select((launch, index) => Part(launch, index, cancellationToken)).ToList();
        if (parts.Count == 1 && !compound)
        {
            await StartAsync(parts[0].CreateAsync, adapter => parts[0].StartAsync(adapter), attached: false, [parts[0].Process], cancellationToken, parts[0].ConfiguredAsync).ConfigureAwait(false);
            return;
        }

        await StartAsync(
            async ct =>
            {
                var compound = new CompoundDebugAdapter(await parts[0].CreateAsync(ct).ConfigureAwait(false), parts[0].Name, independent: true);
                compound.Reserve(parts.Count - 1); // the session waits for the others, even if the first program ends at once
                compound.PartChanged += OnPartChanged;
                return compound;
            },
            compound => parts[0].StartAsync(compound.Primary),
            attached: false,
            [.. parts.Select(p => p.Process)],
            cancellationToken,
            parts[0].ConfiguredAsync).ConfigureAwait(false);

        foreach (var part in parts.Skip(1))
        {
            if (_adapter is not CompoundDebugAdapter session)
            {
                return; // the session ended meanwhile
            }

            await using var released = new AsyncRelease(session);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
            try
            {
                var adapter = await part.CreateAsync(linked.Token).ConfigureAwait(false);
                await session.AddAsync(adapter, part.Name, part.StartAsync, linked.Token).ConfigureAwait(false);
                if (part.ConfiguredAsync is { } configured)
                {
                    await configured().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is DapException or DapConnectionLostException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
            {
                UpdateProcess(part.Index, p => p with { Ended = true });
                Output?.Invoke(this, new DebugOutput("console", $"[{part.Name}] Could not start: {ex.Message}\n"));
            }
        }
    }

    /// <summary>Releases a compound session's reserved place for a program once it joined or failed to.</summary>
    private sealed class AsyncRelease(CompoundDebugAdapter session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(session.ReleaseAsync());
    }

    /// <summary>
    /// Adds a program to the running multi-program session (an Aspire AppHost's projects, M17b, ADR 0043) and returns
    /// its index in <see cref="Processes"/>, where its process id and exit appear. Throws when no such session runs or
    /// the program can't start.
    /// </summary>
    public async Task<int> AddLaunchAsync(DebugLaunch launch, CancellationToken cancellationToken)
    {
        if (_adapter is not CompoundDebugAdapter { Independent: true } session)
        {
            throw new InvalidOperationException("No multi-program debug session is running.");
        }

        LaunchPart part;
        lock (_gate)
        {
            if (_processes.Count >= CompoundDebugAdapter.MaxParts)
            {
                throw new InvalidOperationException($"A debug session holds at most {CompoundDebugAdapter.MaxParts.ToString(CultureInfo.InvariantCulture)} programs.");
            }

            part = Part(launch, _processes.Count, cancellationToken);
            _processes.Add(part.Process);
        }

        ProcessesChanged?.Invoke(this, EventArgs.Empty);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
        try
        {
            var adapter = await part.CreateAsync(linked.Token).ConfigureAwait(false);
            await session.AddAsync(adapter, part.Name, part.StartAsync, linked.Token).ConfigureAwait(false);
            if (part.ConfiguredAsync is { } configured)
            {
                await configured().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        {
            UpdateProcess(part.Index, p => p with { Ended = true });
            throw new InvalidOperationException($"{part.Name} could not start: {ex.Message}", ex);
        }

        return part.Index;
    }

    /// <summary>A program of a launch: how its adapter starts, how it starts the program, and what runs once the session is configured.</summary>
    private sealed record LaunchPart(
        string Name,
        int Index,
        DebugProcess Process,
        Func<CancellationToken, Task<IDebugAdapter>> CreateAsync,
        Func<IDebugAdapter, Task> StartAsync,
        Func<Task>? ConfiguredAsync = null);

    private LaunchPart Part(DebugLaunch launch, int index, CancellationToken cancellationToken)
    {
        if (launch.Remote is { } remote)
        {
            return new LaunchPart(
                launch.Name,
                index,
                new DebugProcess(launch.Name, null, DebugProcess.Launched, remote.Target),
                async ct => new PathMappingDebugAdapter(await DebugAdapter.StartAsync(remote.Adapter, ct).ConfigureAwait(false), remote.Map),
                adapter => ((PathMappingDebugAdapter)adapter).Inner.LaunchAsync(remote.Arguments with { JustMyCode = JustMyCode, EnableStepFiltering = StepFiltering }, DapJsonContext.Default.LaunchArguments, cancellationToken));
        }

        if (launch.Console is DebugConsoleKind.Terminal or DebugConsoleKind.Window)
        {
            var window = launch.Console == DebugConsoleKind.Window;
            if ((window ? ConsoleWindows : Terminal) is { } terminal && StartupHook is { } hook)
            {
                HeldLaunch? held = null;
                return new LaunchPart(
                    launch.Name,
                    index,
                    new DebugProcess(launch.Name, null, window ? DebugProcess.ConsoleWindow : DebugProcess.Terminal, null),
                    async ct => await StartForPlanAsync(launch.Plan, ct).ConfigureAwait(false),
                    async adapter =>
                    {
                        held = await HeldLaunch.StartAsync(terminal, hook, launch.Plan, launch.Name, TerminalStartTimeout, cancellationToken).ConfigureAwait(false);
                        lock (_gate)
                        {
                            _terminalProcesses.Add(held.ProcessId);
                        }

                        UpdateProcess(index, p => p with { Id = held.ProcessId });
                        if (index == 0)
                        {
                            OnProcess(held.ProcessId);
                        }

                        await ((DebugAdapter)adapter).AttachAsync(new AttachArguments(held.ProcessId, JustMyCode, EnableStepFiltering: StepFiltering), cancellationToken).ConfigureAwait(false);
                    },
                    async () =>
                    {
                        // Once the program's own module is loaded, its breakpoints are bound: let it run.
                        await WaitForModuleAsync(EntryModule(launch.Plan), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                        held?.Release();
                    });
            }

            Output?.Invoke(this, new DebugOutput("console", (window ? "A console window" : "The integrated terminal") + " isn't available here: the program's console is the Debug Console.\n"));
        }

        return new LaunchPart(
            launch.Name,
            index,
            new DebugProcess(launch.Name, null, DebugProcess.Launched, null),
            async ct => await StartForPlanAsync(launch.Plan, ct).ConfigureAwait(false),
            adapter => ((DebugAdapter)adapter).LaunchAsync(Arguments(launch.Plan), cancellationToken));
    }

    /// <summary>A program of an independent compound session started or ended.</summary>
    private void OnPartChanged(object? sender, CompoundPartEvent e)
    {
        UpdateProcess(e.Index, p => e.Ended ? p with { Ended = true, ExitCode = e.ExitCode } : p with { Id = e.ProcessId ?? p.Id });
        if (e.ProcessId is { } pid && e.Index == 0)
        {
            OnProcess(pid);
        }
    }

    /// <summary>The name of the program's main module: <c>App.dll</c> for <c>dotnet exec App.dll</c>, <c>App.exe</c> and <c>App</c>.</summary>
    public static string EntryModule(LaunchPlan plan)
    {
        var program = Path.GetFileName(plan.FileName);
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && plan.SplitArguments().FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) is { } dll)
        {
            return dll[(dll.LastIndexOfAny(['/', '\\']) + 1)..];
        }

        return program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? program : Path.GetFileNameWithoutExtension(program) + ".dll";
    }

    private static string ProgramName(LaunchPlan plan) => Path.GetFileNameWithoutExtension(EntryModule(plan));

    /// <summary>Waits until a module named <paramref name="name"/> is loaded (or <paramref name="timeout"/> passes).</summary>
    private async Task WaitForModuleAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs e)
        {
            if (Modules.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(m.Path), name, StringComparison.OrdinalIgnoreCase)))
            {
                loaded.TrySetResult();
            }
        }

        ModulesChanged += Check;
        try
        {
            Check(null, EventArgs.Empty);
            await loaded.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Output?.Invoke(this, new DebugOutput("console", $"{name} didn't load within {timeout.TotalSeconds:0} seconds; the program goes on, and early breakpoints may be missed.\n"));
        }
        finally
        {
            ModulesChanged -= Check;
        }
    }

    /// <summary>
    /// Starts <paramref name="plan"/> (a web server) under netcoredbg as the first adapter of a compound session
    /// (ADR 0027). Its page joins with <see cref="AddBrowserAsync"/> once the server listens.
    /// </summary>
    public Task LaunchCompoundAsync(LaunchPlan plan, CancellationToken cancellationToken) =>
        StartAsync(
            async ct => new CompoundDebugAdapter(await (plan.RunsAsX64 ? StartX64NetcoredbgAsync(ct, plan.Environment) : StartNetcoredbgAsync(ct, plan.Environment)).ConfigureAwait(false), "Server"),
            adapter => ((DebugAdapter)adapter.Primary).LaunchAsync(Arguments(plan), cancellationToken),
            attached: false,
            [new DebugProcess(ProgramName(plan), null, DebugProcess.Launched, null)],
            cancellationToken);

    /// <summary>Whether the session is a compound one that a browser can join.</summary>
    public bool IsCompound => _adapter is CompoundDebugAdapter;

    /// <summary>
    /// The browser joins the running compound session: <paramref name="start"/> starts vscode-js-debug, which opens
    /// the page with <paramref name="arguments"/>. The session's breakpoints bind in both. Closing the browser ends
    /// the session, as in a browser-only one.
    /// </summary>
    public async Task AddBrowserAsync(Func<CancellationToken, Task<JsDebugAdapter>> start, BrowserLaunchArguments arguments, CancellationToken cancellationToken)
    {
        if (_adapter is not CompoundDebugAdapter compound)
        {
            throw new InvalidOperationException("No debug session that a browser can join is running.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
        var browser = await start(linked.Token).ConfigureAwait(false);
        try
        {
            await compound.AddAsync(browser, "Browser", adapter => ((JsDebugAdapter)adapter).LaunchAsync(arguments, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or IOException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>Starts <paramref name="program"/> with arguments that are already split (no command-line parsing).</summary>
    public Task LaunchAsync(string program, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken) =>
        LaunchAsync(new LaunchPlan(program, string.Empty, workingDirectory, environment, null) { ArgumentList = arguments }, cancellationToken);

    /// <summary>
    /// Attaches to process <paramref name="processId"/>: here, or with <paramref name="remote"/> on another machine
    /// (M14c: over SSH, in a container or a WSL distribution) through netcoredbg there.
    /// </summary>
    public async Task AttachAsync(int processId, CancellationToken cancellationToken, RemoteAttach? remote = null, string? name = null)
    {
        var process = new DebugProcess(name ?? ProcessName(processId), processId, DebugProcess.Attached, remote?.Target);
        if (remote is null)
        {
            await StartAsync(ct => StartNetcoredbgAsync(ct), adapter => adapter.AttachAsync(new AttachArguments(processId, JustMyCode, EnableStepFiltering: StepFiltering), cancellationToken), attached: true, [process], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StartAsync(
                async ct => new PathMappingDebugAdapter(await DebugAdapter.StartAsync(remote.Adapter, ct).ConfigureAwait(false), remote.Map),
                adapter => adapter.Inner.AttachAsync(new AttachArguments(processId, JustMyCode, EnableStepFiltering: StepFiltering), cancellationToken),
                attached: true,
                [process],
                cancellationToken).ConfigureAwait(false);
        }

        OnProcess(processId);
    }

    private static string ProcessName(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return processId.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Debugs a page in a browser through vscode-js-debug (ADR 0026): <paramref name="start"/> starts the adapter,
    /// which launches the browser with <paramref name="arguments"/>. Stopping the session closes the browser.
    /// </summary>
    public Task LaunchBrowserAsync(Func<CancellationToken, Task<JsDebugAdapter>> start, BrowserLaunchArguments arguments, CancellationToken cancellationToken) =>
        StartAsync(start, adapter => adapter.LaunchAsync(arguments, cancellationToken), attached: false, [new DebugProcess("Browser", null, DebugProcess.Launched, null)], cancellationToken);

    /// <summary>The debuggee's process id: completes when the adapter reports it; cancelled when the session ends first.</summary>
    public Task<int> WaitForProcessIdAsync(CancellationToken cancellationToken) => _process.Task.WaitAsync(cancellationToken);

    private void OnProcess(int processId)
    {
        ProcessId = processId;
        _process.TrySetResult(processId);
        UpdateProcess(0, p => p.Id is null ? p with { Id = processId } : p);
    }

    /// <summary>The netcoredbg a plan needs: the x64 one for a program that runs as x64 on Arm64 (M33), else the regular one.</summary>
    private Task<DebugAdapter> StartForPlanAsync(LaunchPlan plan, CancellationToken cancellationToken) =>
        plan.RunsAsX64 || DebugAdapterLocator.RunsAsX64OnArm64Mac(plan.FileName) ? StartX64NetcoredbgAsync(cancellationToken, plan.Environment) : StartNetcoredbgAsync(cancellationToken, plan.Environment);

    private Task<DebugAdapter> StartNetcoredbgAsync(CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? debuggee = null)
    {
        var path = _resolveAdapter() ?? throw new InvalidOperationException(
            "The debugger (netcoredbg) was not found. Install it next to Codalus IDE (debugger/), set CODALUS_NETCOREDBG, or run eng/get-netcoredbg.sh in a source checkout.");
        return DebugAdapter.StartAsync(path, cancellationToken, AdapterEnvironment(debuggee));
    }

    /// <summary>
    /// The x64 netcoredbg, under Rosetta 2 (or the Windows emulator): a debugger debugs processes of its own architecture,
    /// and frameworks without an Arm64 runtime run as x64 (M33, ADR 0030).
    /// </summary>
    private Task<DebugAdapter> StartX64NetcoredbgAsync(CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? debuggee = null)
    {
        var path = _resolveX64Adapter() ?? throw new InvalidOperationException(
            "This program runs as x64, which needs the x64 debugger (netcoredbg). Install it next to Codalus IDE (debugger/x64/), set "
            + DebugAdapterLocator.X64EnvironmentVariable + ", or run eng/get-netcoredbg.sh --x64 in a source checkout.");
        return DebugAdapter.StartAsync(path, cancellationToken, AdapterEnvironment(debuggee));
    }

    private static IReadOnlyDictionary<string, string> AdapterEnvironment(IReadOnlyDictionary<string, string>? debuggee) =>
        AdapterEnvironment(debuggee, DotnetFrameworks.CurrentOs, Environment.GetEnvironmentVariable, RuntimeSelection.SystemIcuMajor);

    /// <summary>
    /// What netcoredbg's own process needs on Linux (M33, ADR 0030): it reads symbols with managed code that it runs on
    /// the debuggee's runtime, in its own process, so .NET Core 2.x to 3.1 need the system's ICU there as in the program
    /// (<c>CLR_ICU_VERSION_OVERRIDE</c>), or the debugger stops at the program's start. The launch's choice wins
    /// (the override, or invariant globalization); otherwise the system's ICU, which newer runtimes use anyway, so an
    /// attach to an old program works too. Empty elsewhere.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AdapterEnvironment(IReadOnlyDictionary<string, string>? debuggee, System.Runtime.InteropServices.OSPlatform os, Func<string, string?> getEnvironmentVariable, Func<int?> icuMajor)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (os != System.Runtime.InteropServices.OSPlatform.Linux)
        {
            return environment;
        }

        foreach (var name in (string[])[RuntimeSelection.IcuOverride, RuntimeSelection.Invariant])
        {
            if (debuggee is not null && debuggee.TryGetValue(name, out var value))
            {
                environment[name] = value;
            }
        }

        if (environment.Count == 0 && string.IsNullOrEmpty(getEnvironmentVariable(RuntimeSelection.IcuOverride))
            && string.IsNullOrEmpty(getEnvironmentVariable(RuntimeSelection.Invariant)) && icuMajor() is { } icu)
        {
            environment[RuntimeSelection.IcuOverride] = icu.ToString(CultureInfo.InvariantCulture);
        }

        return environment;
    }

    private async Task StartAsync<TAdapter>(
        Func<CancellationToken, Task<TAdapter>> create,
        Func<TAdapter, Task> start,
        bool attached,
        IReadOnlyList<DebugProcess> processes,
        CancellationToken cancellationToken,
        Func<Task>? configured = null)
        where TAdapter : class, IDebugAdapter
    {
        if (IsActive)
        {
            throw new InvalidOperationException("A debug session is already running.");
        }

        lock (_gate)
        {
            _processes.Clear();
            _processes.AddRange(processes);
            _terminalProcesses.Clear();
        }

        ProcessesChanged?.Invoke(this, EventArgs.Empty);
        SetState(DebugState.Starting);
        _session = new CancellationTokenSource();
        _ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _attached = attached;
        _exitCode = 0;
        ProcessId = null;
        _process = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _hits.Clear();
            _adapterBreakpoints.Clear();
            _adapterFunctionBreakpoints.Clear();
            _runToIds.Clear();
            _hitLocations.Clear();
            _modules.Clear();
            _runTo = null;
        }

        ModulesChanged?.Invoke(this, EventArgs.Empty);
        _resumedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            if (await StartAttemptAsync(create, start, attached, processes, configured, lastAttempt: attempt == StartupAttempts, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            Output?.Invoke(this, new DebugOutput("console", $"The debugger didn't see the program's runtime start within its 5 seconds; starting again ({(attempt + 1).ToString(CultureInfo.InvariantCulture)} of {StartupAttempts.ToString(CultureInfo.InvariantCulture)}).\n"));
        }
    }

    /// <summary>How many times a session starts when netcoredbg gives up waiting for the program's runtime (<see cref="IsStartupTimeout"/>).</summary>
    internal const int StartupAttempts = 3;

    /// <summary>
    /// netcoredbg's answer to <c>configurationDone</c> when the program's runtime didn't report to it within 5 seconds
    /// of the launch or attach (its fixed <c>startupWaitTimeout</c>; E_FAIL). On a loaded machine the runtime can take
    /// longer (the first Windows runs: a VSTest test host attached to, Visual Basic and F# programs launched); the
    /// program is ended (or detached from) and the session starts again. TODO(issue): ask Samsung/netcoredbg for a
    /// configurable wait.
    /// </summary>
    internal static bool IsStartupTimeout(Exception ex) =>
        ex is DapException { Command: "configurationDone" } && ex.Message.Contains("0x80004005", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One start of the session: the adapter, the launch or attach, the configuration. False when netcoredbg timed out
    /// waiting for the runtime and it's worth starting again: the attempt is undone without ending the session.
    /// </summary>
    private async Task<bool> StartAttemptAsync<TAdapter>(
        Func<CancellationToken, Task<TAdapter>> create,
        Func<TAdapter, Task> start,
        bool attached,
        IReadOnlyList<DebugProcess> processes,
        Func<Task>? configured,
        bool lastAttempt,
        CancellationToken cancellationToken)
        where TAdapter : class, IDebugAdapter
    {
        TAdapter? adapter = null;
        Task? launching = null;
        var abandoned = new StrongBox<bool>();
        try
        {
            adapter = await create(cancellationToken).ConfigureAwait(false);
            adapter.EventReceived += e => Volatile.Read(ref abandoned.Value) ? Task.CompletedTask : OnEventAsync(e);
            adapter.Log += (_, line) => Output?.Invoke(this, new DebugOutput("console", line + "\n"));
            _adapter = adapter;
            _ = adapter.Closed.ContinueWith(_ => Volatile.Read(ref abandoned.Value) ? Task.CompletedTask : EndAsync(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

            // DAP's order: launch, and configure while it runs. Some adapters (js-debug) answer the launch only after
            // configurationDone.
            launching = start(adapter);
            await Task.WhenAny(launching, adapter.Initialized.WaitAsync(cancellationToken)).ConfigureAwait(false);
            if (launching.IsFaulted || launching.IsCanceled)
            {
                await launching.ConfigureAwait(false);
            }

            await adapter.Initialized.WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var file in Breakpoints.Files)
            {
                await SendBreakpointsAsync(adapter, file, cancellationToken).ConfigureAwait(false);
            }

            if (Breakpoints.Functions.Count > 0)
            {
                await SendFunctionBreakpointsAsync(adapter, cancellationToken).ConfigureAwait(false);
            }

            var (filters, options) = ExceptionSettings.ToFilters();
            await adapter.SetExceptionBreakpointsAsync(filters, cancellationToken, options).ConfigureAwait(false);
            if (State == DebugState.Starting)
            {
                SetState(DebugState.Running);
            }

            await adapter.ConfigurationDoneAsync(cancellationToken).ConfigureAwait(false);
            await launching.ConfigureAwait(false);
            if (configured is not null)
            {
                await configured().ConfigureAwait(false);
            }

            return true;
        }
        catch (DapException ex) when (!lastAttempt && adapter is not null && IsStartupTimeout(ex) && !cancellationToken.IsCancellationRequested && CanStartAgain(attached, processes))
        {
            // Its events and its end are no longer the session's: the next attempt's adapter takes over.
            Volatile.Write(ref abandoned.Value, true);
            _ = launching?.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await AbandonAttemptAsync(adapter, attached, processes).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            if (adapter is not null)
            {
                if (attached || !IsStartupTimeout(ex) || !KillWithProgram(adapter))
                {
                    await adapter.DisconnectAsync(terminateDebuggee: !attached, CancellationToken.None).ConfigureAwait(false);
                }

                KillTerminalPrograms();
                await EndAsync().ConfigureAwait(false);
            }
            else
            {
                // The adapter didn't start (not installed, or it failed): there is no session to end.
                SetState(DebugState.Idle);
                _ended.TrySetResult();
            }

            throw ex is OperationCanceledException or InvalidOperationException ? ex : new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Whether a start that timed out can be made again: a launch can (its program is ended first); an attach only to a
    /// local process that is still there.
    /// </summary>
    private static bool CanStartAgain(bool attached, IReadOnlyList<DebugProcess> processes)
    {
        if (!attached)
        {
            return true;
        }

        if (processes is not [{ Id: { } processId, Target: null }])
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Undoes a start attempt without ending the session: the program ended (or detached from), its state cleared.</summary>
    private async Task AbandonAttemptAsync(IDebugAdapter adapter, bool attached, IReadOnlyList<DebugProcess> processes)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_adapter, adapter))
            {
                _adapter = null;
            }
        }

        if (attached || !KillWithProgram(adapter))
        {
            await adapter.DisconnectAsync(terminateDebuggee: !attached, CancellationToken.None).ConfigureAwait(false);
        }

        KillTerminalPrograms();
        await adapter.DisposeAsync().ConfigureAwait(false);

        SetState(DebugState.Starting);
        _exitCode = 0;
        ProcessId = null;
        _process = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _processes.Clear();
            _processes.AddRange(processes);
            _hits.Clear();
            _adapterBreakpoints.Clear();
            _adapterFunctionBreakpoints.Clear();
            _runToIds.Clear();
            _hitLocations.Clear();
            _modules.Clear();
            _runTo = null;
        }

        Breakpoints.ClearStates();
        ProcessesChanged?.Invoke(this, EventArgs.Empty);
        ModulesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ends a timed-out launch's local adapter with the program it started, instead of a <c>disconnect</c>, and says
    /// whether it did. netcoredbg's terminate only reaches a program whose runtime it saw start (its
    /// <c>TerminateProcess</c> returns early while unattached), so each abandoned start left its program running; on
    /// Windows one whose runtime had already signalled its start waits for the gone debugger forever (the runtime's
    /// <c>NotifyDebuggerOfStartup</c>), and on a loaded machine the leftovers compete with the next start. The program is
    /// the adapter's child: killing the adapter's process tree ends both.
    /// </summary>
    private static bool KillWithProgram(IDebugAdapter adapter)
    {
        if ((adapter is CompoundDebugAdapter compound ? compound.Primary : adapter) is DebugAdapter { Transport: null } local)
        {
            local.Kill();
            return true;
        }

        return false;
    }

    /// <summary>Ends the session: terminates a launched program, detaches from an attached one.</summary>
    public async Task StopAsync()
    {
        if (_adapter is not { } adapter)
        {
            // Ending already (the adapter reported it terminated): done when that is.
            await _ended.Task.ConfigureAwait(false);
            return;
        }

        if (_attached)
        {
            await PrepareDetachAsync(adapter).ConfigureAwait(false);
        }

        await adapter.DisconnectAsync(terminateDebuggee: !_attached, CancellationToken.None).ConfigureAwait(false);
        KillTerminalPrograms();
        await EndAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Before a detach from a stopped program (M14c): its breakpoints and exception filters are removed, and a trivial
    /// function evaluation runs on the stopped thread. netcoredbg 3.2.0 on Windows ends an apphost program
    /// (<c>App.exe</c>, not <c>dotnet App.dll</c>) with 0xC0000409 when it detaches while the program is stopped at a
    /// breakpoint, unless a function evaluation ran since the stop; resuming first instead races with the program's
    /// own debug events (0xC0000354). TODO(issue): report upstream (Samsung/netcoredbg).
    /// </summary>
    private async Task PrepareDetachAsync(IDebugAdapter adapter)
    {
        if (State != DebugState.Stopped)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            foreach (var file in Breakpoints.Files)
            {
                await adapter.SetBreakpointsAsync(file, [], timeout.Token).ConfigureAwait(false);
            }

            if (Breakpoints.Functions.Count > 0)
            {
                await adapter.SetFunctionBreakpointsAsync([], timeout.Token).ConfigureAwait(false);
            }

            await adapter.SetExceptionBreakpointsAsync([], timeout.Token).ConfigureAwait(false);

            await adapter.EvaluateAsync("System.Environment.ProcessId", SelectedFrame?.Id, "watch", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException)
        {
            Output?.Invoke(this, new DebugOutput("console", $"Preparing the detach: {ex.Message}\n"));
        }
    }

    /// <summary>
    /// Stop ends programs launched in the terminal too: the debugger attached to them, so its disconnect may only
    /// detach. The terminal shows that they ended.
    /// </summary>
    private void KillTerminalPrograms()
    {
        List<int> programs;
        lock (_gate)
        {
            programs = [.. _terminalProcesses];
            _terminalProcesses.Clear();
        }

        ConsoleWindows?.CloseAll(); // as a classic IDE closes the console when debugging stops

        foreach (var pid in programs)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException or NotSupportedException)
            {
                // Already gone.
            }
        }
    }

    /// <summary>
    /// Detach All (M14b, M14c): ends the session and leaves the programs running, when each was attached to or
    /// launched in the terminal (<see cref="CanDetach"/>). A program launched with the Debug Console has netcoredbg's
    /// console, so it can't outlive the session: <see cref="StopAsync"/> ends it.
    /// </summary>
    public async Task DetachAsync()
    {
        if (_adapter is not { } adapter || !CanDetach)
        {
            await StopAsync().ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            _terminalProcesses.Clear(); // they go on
        }

        await PrepareDetachAsync(adapter).ConfigureAwait(false);
        await adapter.DisconnectAsync(terminateDebuggee: false, CancellationToken.None).ConfigureAwait(false);
        await EndAsync().ConfigureAwait(false);
    }

    private async Task EndAsync()
    {
        IDebugAdapter? adapter;
        lock (_gate)
        {
            adapter = _adapter;
            _adapter = null;
        }

        if (adapter is null)
        {
            await _ended.Task.ConfigureAwait(false);
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
        _runTo = null;
        _process.TrySetCanceled();
        Breakpoints.ClearStates();
        SetState(DebugState.Idle);
        Ended?.Invoke(this, _exitCode);
        _ended.TrySetResult();
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
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

    /// <summary>
    /// Run to Cursor: continues until <paramref name="line"/> of <paramref name="path"/> is reached, through a hidden
    /// breakpoint there. Any other stop ends it, except that <paramref name="force"/> continues past other breakpoints.
    /// </summary>
    public async Task RunToCursorAsync(string path, int line, bool force = false)
    {
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            return;
        }

        _runTo = new RunTarget(new BreakpointLocation(path, line), force);
        try
        {
            await SendBreakpointsAsync(adapter, path, _session.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException)
        {
            _runTo = null;
            Output?.Invoke(this, new DebugOutput("console", $"Run to Cursor failed: {ex.Message}\n"));
            return;
        }

        MarkRunning();
        await adapter.ContinueAsync(CurrentThread, _session.Token).ConfigureAwait(false);
    }

    /// <summary>Ends a Run to Cursor: removes its hidden breakpoint.</summary>
    private async Task EndRunToCursorAsync(IDebugAdapter adapter)
    {
        if (_runTo is not { } target)
        {
            return;
        }

        _runTo = null;
        try
        {
            await SendBreakpointsAsync(adapter, target.Location.Path, _session.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException)
        {
            Output?.Invoke(this, new DebugOutput("console", $"Breakpoints could not be set: {ex.Message}\n"));
        }
    }

    private async Task ResumeAsync(Func<IDebugAdapter, Task> resume)
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
        _resumedAt = System.Diagnostics.Stopwatch.GetTimestamp();
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
        RefreshPinnedTips();
    }

    public void SelectFrame(StackFrame frame)
    {
        SelectedFrame = frame;
        FrameChanged?.Invoke(this, EventArgs.Empty);
        RefreshPinnedTips();
    }

    /// <summary>The pinned data tips' values in the selected frame (in the background).</summary>
    private void RefreshPinnedTips()
    {
        if (State == DebugState.Stopped && PinnedTips.All.Count > 0)
        {
            _ = PinnedTips.RefreshAsync((expression, cancellationToken) => EvaluateAsync(expression, "watch", cancellationToken), _session.Token);
        }
    }

    public async Task<IReadOnlyList<Scope>> ScopesAsync(StackFrame frame) =>
        _adapter is { } adapter ? await adapter.ScopesAsync(frame.Id, _session.Token).ConfigureAwait(false) : [];

    /// <summary>
    /// The debugger attributes (M14b): <c>[DebuggerDisplay]</c> and <c>[DebuggerBrowsable]</c> applied to variables and
    /// evaluations, from the companion host. Null: netcoredbg's values as they are.
    /// </summary>
    public DebuggerDisplays? Displays { get; set; }

    public async Task<IReadOnlyList<Variable>> VariablesAsync(int variablesReference)
    {
        if (_adapter is not { } adapter || variablesReference <= 0)
        {
            return [];
        }

        var token = _session.Token;
        var variables = await adapter.VariablesAsync(variablesReference, token).ConfigureAwait(false);
        return Displays is { } displays && State == DebugState.Stopped
            ? await displays.ApplyAsync(
                variablesReference,
                variables,
                ModulePaths(),
                async reference => await adapter.VariablesAsync(reference, token).ConfigureAwait(false),
                (expression, cancellationToken) => adapter.EvaluateAsync(expression, SelectedFrame?.Id, "watch", cancellationToken),
                token).ConfigureAwait(false)
            : variables;
    }

    private string[] ModulePaths() => [.. Modules.Select(m => m.Path).OfType<string>()];

    /// <summary>Evaluates in the selected frame. <paramref name="context"/>: <c>watch</c>, <c>repl</c> or <c>hover</c>.</summary>
    public async Task<EvaluateResponse> EvaluateAsync(string expression, string context, CancellationToken cancellationToken = default)
    {
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            throw new InvalidOperationException("Expressions can be evaluated only while the program is stopped.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
        var response = await adapter.EvaluateAsync(expression, SelectedFrame?.Id, context, linked.Token).ConfigureAwait(false);
        return Displays is { } displays
            ? await displays.ApplyAsync(expression, response, ModulePaths(), (member, token) => adapter.EvaluateAsync(member, SelectedFrame?.Id, "watch", token), linked.Token).ConfigureAwait(false)
            : response;
    }

    public async Task<SetVariableResponse> SetVariableAsync(int variablesReference, string name, string value)
    {
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            throw new InvalidOperationException("Variables can be changed only while the program is stopped.");
        }

        return await adapter.SetVariableAsync(variablesReference, name, value, _session.Token).ConfigureAwait(false);
    }

    /// <summary>Assigns a value to an expression (a watch, or a member by its evaluate name) in the selected frame.</summary>
    public async Task<SetExpressionResponse> SetExpressionAsync(string expression, string value)
    {
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            throw new InvalidOperationException("Values can be changed only while the program is stopped.");
        }

        return await adapter.SetExpressionAsync(expression, value, SelectedFrame?.Id, _session.Token).ConfigureAwait(false);
    }

    /// <summary>The top frame of each thread (for the Threads window), while stopped. Threads without frames are left out.</summary>
    public async Task<IReadOnlyDictionary<int, StackFrame>> TopFramesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, StackFrame>();
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            return result;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
        foreach (var thread in Threads)
        {
            try
            {
                var frames = await adapter.StackTraceAsync(thread.Id, 1, linked.Token).ConfigureAwait(false);
                if (frames.StackFrames.Length > 0)
                {
                    result[thread.Id] = frames.StackFrames[0];
                }
            }
            catch (DapException)
            {
                // The thread is gone or has no managed frames.
            }
        }

        return result;
    }

    /// <summary>Every thread's stack, top frame first, up to <paramref name="levels"/> frames (Parallel Stacks and Parallel Watch, M14b).</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<StackFrame>>> AllStacksAsync(int levels, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, IReadOnlyList<StackFrame>>();
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            return result;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
        foreach (var thread in Threads)
        {
            try
            {
                var frames = await adapter.StackTraceAsync(thread.Id, levels, linked.Token).ConfigureAwait(false);
                if (frames.StackFrames.Length > 0)
                {
                    result[thread.Id] = frames.StackFrames;
                }
            }
            catch (DapException)
            {
                // The thread is gone or has no managed frames.
            }
        }

        return result;
    }

    /// <summary>Evaluates in a given frame of any thread (Parallel Watch); the attributes apply as in Watch.</summary>
    public async Task<EvaluateResponse> EvaluateInFrameAsync(string expression, int frameId, CancellationToken cancellationToken)
    {
        if (_adapter is not { } adapter || State != DebugState.Stopped)
        {
            throw new InvalidOperationException("Expressions can be evaluated only while the program is stopped.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
        var response = await adapter.EvaluateAsync(expression, frameId, "watch", linked.Token).ConfigureAwait(false);
        return Displays is { } displays
            ? await displays.ApplyAsync(expression, response, ModulePaths(), (member, token) => adapter.EvaluateAsync(member, frameId, "watch", token), linked.Token).ConfigureAwait(false)
            : response;
    }

    // ------------------------------------------------------------------ settings

    /// <summary>Sends <see cref="ExceptionSettings"/> to the running session (it also happens on every change).</summary>
    public async Task ApplyExceptionSettingsAsync()
    {
        if (_adapter is not { } adapter || State == DebugState.Starting)
        {
            return;
        }

        var (filters, options) = ExceptionSettings.ToFilters();
        try
        {
            await adapter.SetExceptionBreakpointsAsync(filters, _session.Token, options).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException)
        {
            Output?.Invoke(this, new DebugOutput("console", $"Exception settings could not be applied: {ex.Message}\n"));
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

    private async Task SendBreakpointsAsync(IDebugAdapter adapter, string path, CancellationToken cancellationToken)
    {
        var specs = Breakpoints.For(path).Where(b => b.Enabled).ToList();
        var sent = specs.Select(b => new SourceBreakpoint(b.Line, Condition: b.Condition)).ToList();

        // Run to Cursor's hidden breakpoint, unless a breakpoint without a condition is already on that line.
        var runTo = _runTo is { } target && SolutionService.PathComparer.Equals(target.Location.Path, path)
            && !specs.Any(b => b.Line == target.Location.Line && b.Condition is null)
            ? target.Location.Line
            : (int?)null;
        if (runTo is { } line)
        {
            sent.RemoveAll(b => b.Line == line);
            specs.RemoveAll(b => b.Line == line);
            sent.Add(new SourceBreakpoint(line));
        }

        var result = await adapter.SetBreakpointsAsync(path, [.. sent], cancellationToken).ConfigureAwait(false);
        var states = new List<(int, BreakpointState)>();
        lock (_gate)
        {
            foreach (var stale in _adapterBreakpoints.Where(p => SolutionService.PathComparer.Equals(p.Value.Path, path)).Select(p => p.Key).ToList())
            {
                _adapterBreakpoints.Remove(stale);
            }

            if (runTo is not null)
            {
                _runToIds.Clear();
            }

            for (var i = 0; i < specs.Count && i < result.Length; i++)
            {
                if (result[i].Id is { } id)
                {
                    _adapterBreakpoints[id] = specs[i];
                }

                states.Add((specs[i].Line, new BreakpointState(result[i].Verified, result[i].Message)));
            }

            if (runTo is not null && result.Length == sent.Count && result[^1].Id is { } runToId)
            {
                _runToIds.Add(runToId);
            }
        }

        Breakpoints.SetStates(path, states);
    }

    private async Task SyncFunctionBreakpointsAsync()
    {
        if (_adapter is not { } adapter || State == DebugState.Starting)
        {
            return;
        }

        try
        {
            await SendFunctionBreakpointsAsync(adapter, _session.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException)
        {
            Output?.Invoke(this, new DebugOutput("console", $"Function breakpoints could not be set: {ex.Message}\n"));
        }
    }

    private async Task SendFunctionBreakpointsAsync(IDebugAdapter adapter, CancellationToken cancellationToken)
    {
        var specs = Breakpoints.Functions.Where(f => f.Enabled).ToList();
        var result = await adapter.SetFunctionBreakpointsAsync([.. specs.Select(f => new FunctionBreakpoint(f.Name, f.Condition))], cancellationToken).ConfigureAwait(false);
        var states = new List<(string, BreakpointState)>();
        lock (_gate)
        {
            _adapterFunctionBreakpoints.Clear();
            for (var i = 0; i < specs.Count && i < result.Length; i++)
            {
                if (result[i].Id is { } id)
                {
                    _adapterFunctionBreakpoints[id] = specs[i];
                }

                states.Add((specs[i].Name, new BreakpointState(result[i].Verified, result[i].Message)));
            }
        }

        Breakpoints.SetFunctionStates(states);
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
            case DapEvents.Output when DebugAdapter.Read(e.Body, DapJsonContext.Default.OutputEvent) is { Category: not "telemetry" } output:
                Output?.Invoke(this, new DebugOutput(output.Category ?? "console", output.Output));
                break;
            case DapEvents.Exited when DebugAdapter.Read(e.Body, DapJsonContext.Default.ExitedEvent) is { } exited:
                _exitCode = exited.ExitCode;
                var program = _adapter is CompoundDebugAdapter { Independent: true } programs ? $"[{programs.PartNames[0]}] " : string.Empty;
                Output?.Invoke(this, new DebugOutput("console", $"{program}The program has exited with code {exited.ExitCode.ToString(CultureInfo.InvariantCulture)} (0x{exited.ExitCode:x}).\n"));
                break;
            case DapEvents.Terminated:
                _ = Task.Run(EndAsync);
                break;
            case DapEvents.Process when DebugAdapter.Read(e.Body, DapJsonContext.Default.ProcessEvent) is { SystemProcessId: { } pid }:
                OnProcess(pid);
                break;
            case DapEvents.Breakpoint when DebugAdapter.Read(e.Body, DapJsonContext.Default.BreakpointEvent) is { Breakpoint.Id: { } id } changed:
                BreakpointSpec? spec;
                FunctionBreakpointSpec? function;
                lock (_gate)
                {
                    spec = _adapterBreakpoints.GetValueOrDefault(id);
                    function = _adapterFunctionBreakpoints.GetValueOrDefault(id);
                }

                var state = new BreakpointState(changed.Breakpoint.Verified, changed.Breakpoint.Message);
                if (spec is not null)
                {
                    Breakpoints.SetStates(spec.Path, [(spec.Line, state)]);
                }
                else if (function is not null)
                {
                    Breakpoints.SetFunctionState(function.Name, state);
                }

                break;
            case DapEvents.Module when DebugAdapter.Read(e.Body, DapJsonContext.Default.ModuleEvent) is { } module:
                OnModule(module);
                break;
        }
    }

    private void OnModule(ModuleEvent e)
    {
        var id = e.Module.Id.ValueKind == System.Text.Json.JsonValueKind.String ? e.Module.Id.GetString()! : e.Module.Id.GetRawText();
        lock (_gate)
        {
            if (e.Reason == "removed")
            {
                _modules.Remove(id);
            }
            else
            {
                var order = _modules.TryGetValue(id, out var existing) ? existing.Order : _modules.Count;
                _modules[id] = new DebugModule(id, e.Module.Name, e.Module.Path, e.Module.SymbolStatus, e.Module.IsOptimized, e.Module.IsUserCode, order);
            }
        }

        ModulesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task OnStoppedAsync(StoppedEvent stopped)
    {
        if (_adapter is not { } adapter)
        {
            return;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_resumedAt);
        try
        {
            var (threadId, frames) = await StoppedThreadAsync(adapter, stopped).ConfigureAwait(false);
            var atRunTarget = _runTo is { } target && (IsRunToHit(stopped) || frames.Length > 0 && frames[0] is { Source.Path: { } topPath } top
                && target.Location.Matches(new BreakpointLocation(topPath, top.Line)));
            if (stopped.Reason == "breakpoint" && !atRunTarget && _runTo is { Force: true })
            {
                // Force Run to Cursor: past every other breakpoint.
                await adapter.ContinueAsync(threadId, _session.Token).ConfigureAwait(false);
                return;
            }

            var breakpoint = stopped.Reason == "breakpoint" ? FindBreakpoint(stopped, frames) : null;
            if (breakpoint is { NeedsClientHandling: true } && !atRunTarget
                && !await ShouldBreakAsync(adapter, breakpoint, frames, threadId).ConfigureAwait(false))
            {
                await adapter.ContinueAsync(threadId, _session.Token).ConfigureAwait(false);
                return;
            }

            if (breakpoint is not null)
            {
                RecordHit(breakpoint.Location);
                if (breakpoint.Temporary)
                {
                    Breakpoints.Remove(breakpoint);
                }
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

            // Module conditions (M14b): a thrown exception breaks only in the modules its type's condition names.
            if (exception is { BreakMode: "always" } && !ExceptionSettings.ModuleAllows(ExceptionType(exception), ModuleOf(frames)))
            {
                await adapter.ContinueAsync(threadId, _session.Token).ConfigureAwait(false);
                return;
            }

            await EndRunToCursorAsync(adapter).ConfigureAwait(false);

            Threads = await adapter.ThreadsAsync(_session.Token).ConfigureAwait(false);
            SelectedThreadId = threadId;
            Frames = frames;
            SelectedFrame = frames.FirstOrDefault(f => f.Source?.Path is not null) ?? (frames.Length > 0 ? frames[0] : null);
            var info = new StopInfo(stopped.Reason, threadId, stopped.Description ?? stopped.Text, exception, elapsed);
            CurrentStop = info;
            SetState(DebugState.Stopped);
            Stopped?.Invoke(this, info);
            RefreshPinnedTips();
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException)
        {
            Output?.Invoke(this, new DebugOutput("console", $"Could not read the stopped program's state: {ex.Message}\n"));
        }
    }

    /// <summary>
    /// The thread a stop shows, and its frames: the stopped event's thread. A pause (Break All) stops every thread and
    /// names one the adapter picked: netcoredbg names the thread it was asked to pause, the first it lists, by the
    /// lowest thread id, which on Windows can be a runtime thread without managed frames, whose stack is empty (or
    /// unreadable). The stop then shows the first thread with code of the program's own (frames with source), as
    /// Visual Studio does after Break All, or else the first thread with any frames. An event that names no thread
    /// shows one the same way.
    /// </summary>
    private async Task<(int ThreadId, StackFrame[] Frames)> StoppedThreadAsync(IDebugAdapter adapter, StoppedEvent stopped)
    {
        var pause = stopped.Reason == "pause";
        StackFrame[] frames = [];
        if (stopped.ThreadId is { } reported)
        {
            try
            {
                frames = (await adapter.StackTraceAsync(reported, 200, _session.Token).ConfigureAwait(false)).StackFrames;
            }
            catch (DapException) when (pause)
            {
            }

            if (!pause || frames.Any(HasSource))
            {
                return (reported, frames);
            }
        }

        (int ThreadId, StackFrame[] Frames)? withFrames = frames.Length > 0 ? (stopped.ThreadId!.Value, frames) : null;
        DapThread[] threads;
        try
        {
            threads = await adapter.ThreadsAsync(_session.Token).ConfigureAwait(false);
        }
        catch (DapException) when (withFrames is not null)
        {
            threads = [];
        }

        foreach (var thread in threads)
        {
            if (thread.Id == stopped.ThreadId)
            {
                continue;
            }

            StackFrame[] stack;
            try
            {
                stack = (await adapter.StackTraceAsync(thread.Id, 200, _session.Token).ConfigureAwait(false)).StackFrames;
            }
            catch (DapException)
            {
                continue;
            }

            if (stack.Any(HasSource))
            {
                return (thread.Id, stack);
            }

            if (withFrames is null && stack.Length > 0)
            {
                withFrames = (thread.Id, stack);
            }
        }

        return withFrames ?? (stopped.ThreadId ?? 0, frames);

        static bool HasSource(StackFrame frame) => frame.Source?.Path is not null;
    }

    private static string ExceptionType(ExceptionInfoResponse exception) =>
        exception.Details?.FullTypeName ?? exception.Details?.TypeName ?? exception.ExceptionId.Replace("CLR/", string.Empty, StringComparison.Ordinal);

    /// <summary>The module (file name) of the top frame, where an exception was thrown.</summary>
    private string? ModuleOf(StackFrame[] frames)
    {
        if (frames.Length == 0 || frames[0].ModuleId is not { } id)
        {
            return null;
        }

        var key = id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString()! : id.GetRawText();
        lock (_gate)
        {
            return _modules.TryGetValue(key, out var module) ? module.Name : null;
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

    private bool IsRunToHit(StoppedEvent stopped)
    {
        lock (_gate)
        {
            return stopped.HitBreakpointIds?.Any(_runToIds.Contains) == true;
        }
    }

    /// <summary>Remembers that the breakpoint at <paramref name="location"/> was hit (for dependent breakpoints).</summary>
    private void RecordHit(BreakpointLocation location)
    {
        lock (_gate)
        {
            if (!_hitLocations.Any(l => l.Matches(location)))
            {
                _hitLocations.Add(location);
            }
        }
    }

    /// <summary>Client-side dependencies, hit counts and logpoints: whether this hit shows as a stop.</summary>
    /// <summary>The stopped thread and the process, for breakpoint filters.</summary>
    private async Task<BreakpointFilterContext> FilterContextAsync(IDebugAdapter adapter, int threadId)
    {
        string? threadName = null;
        try
        {
            threadName = (await adapter.ThreadsAsync(_session.Token).ConfigureAwait(false)).FirstOrDefault(t => t.Id == threadId)?.Name;
        }
        catch (DapException)
        {
        }

        string? processName = null;
        if (ProcessId is { } pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                processName = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
            }
        }

        return new BreakpointFilterContext(threadId, threadName, ProcessId, processName);
    }

    private async Task<bool> ShouldBreakAsync(IDebugAdapter adapter, BreakpointSpec breakpoint, StackFrame[] frames, int threadId)
    {
        if (breakpoint.Filter is { } filterText && BreakpointFilter.TryParse(filterText, out var filter, out _)
            && !filter.Matches(await FilterContextAsync(adapter, threadId).ConfigureAwait(false)))
        {
            return false;
        }

        int hits;
        lock (_gate)
        {
            if (breakpoint.DependsOn is { } dependency && !_hitLocations.Any(l => l.Matches(dependency)))
            {
                return false;
            }

            hits = _hits[breakpoint] = _hits.GetValueOrDefault(breakpoint) + 1;
        }

        if (breakpoint.HitCondition is { } condition && !BreakpointStore.HitConditionMet(condition, hits))
        {
            return false;
        }

        if (breakpoint.LogMessage is { } message)
        {
            RecordHit(breakpoint.Location);
            var text = await FormatLogMessageAsync(adapter, message, frames.Length > 0 ? frames[0] : null, threadId).ConfigureAwait(false);
            Output?.Invoke(this, new DebugOutput("console", text + "\n"));
            return false;
        }

        return true;
    }

    /// <summary>Replaces <c>{expression}</c> with its value, <c>$FUNCTION</c> and <c>$TID</c> with the frame and thread.</summary>
    internal async Task<string> FormatLogMessageAsync(IDebugAdapter adapter, string message, StackFrame? frame, int threadId)
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
        if (state == DebugState.Running)
        {
            Displays?.Resumed();
        }
        else if (state == DebugState.Idle)
        {
            Displays?.SessionEnded();
        }

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
