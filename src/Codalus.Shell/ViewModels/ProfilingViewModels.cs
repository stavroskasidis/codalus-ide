using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using Codalus.Diagnostics.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Diagnostics;

namespace Codalus.Shell.ViewModels;

/// <summary>A .NET process to attach to.</summary>
public sealed record ProcessRowViewModel(DotnetProcess Process)
{
    public int Id => Process.Id;

    public string Name => Process.Name;

    public string Text => string.Create(CultureInfo.InvariantCulture, $"{Process.Name} ({Process.Id})");

    public string? Detail => Process.CommandLine;

    public string AccessibleName => $"{Process.Name}, process {Process.Id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Whether the row matches a filter: a process id, or part of its name or command line.</summary>
    public bool Matches(string filter) =>
        filter.Length == 0
        || Process.Id.ToString(CultureInfo.InvariantCulture) == filter.Trim()
        || Process.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
        || (Process.CommandLine?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>What the Performance Profiler starts: the tools (<see cref="ProfilerTools"/>), and the startup project (launched) or a running process.</summary>
public sealed record ProfilerRequest(IReadOnlyList<string> Tools, bool Launch, int? ProcessId)
{
    public bool Has(string tool) => Tools.Contains(tool);
}

/// <summary>What the Performance Profiler needs from the shell.</summary>
public interface IProfilerHost
{
    /// <summary>The startup project's name, or null without one.</summary>
    string? StartupProjectName { get; }

    /// <summary>The collection in progress, or null.</summary>
    ProfilingRun? Active { get; }

    Task<IReadOnlyList<DotnetProcess>> ProcessesAsync(CancellationToken cancellationToken);

    /// <summary>Starts collecting; null (with the reason in a notification) when it couldn't.</summary>
    Task<ProfilingRun?> StartAsync(ProfilerRequest request);

    /// <summary>The saved sessions, the newest first.</summary>
    IReadOnlyList<ProfilerSession> Sessions();

    void OpenSession(ProfilerSession session);

    void DeleteSession(ProfilerSession session);

    /// <summary>Opens a comparison of two sessions (the baseline first).</summary>
    void CompareSessions(ProfilerSession baseline, ProfilerSession session);
}

/// <summary>A collection in progress (the Performance Profiler or the Diagnostic Tools window's Record CPU Profile).</summary>
public sealed class ProfilingRun
{
    private readonly Func<ProfilingRun, Task<ProfilerSession?>> _stop;
    private Task<ProfilerSession?>? _stopping;

    public ProfilingRun(string target, int processId, IReadOnlyList<string> tools, Func<ProfilingRun, Task<ProfilerSession?>> stop)
    {
        Target = target;
        ProcessId = processId;
        Tools = tools;
        _stop = stop;
    }

    /// <summary>Memory Usage: takes a heap snapshot now (null: the tool isn't on); its file name in the session folder, or null.</summary>
    public Func<Task<string?>>? TakeSnapshot { get; init; }

    /// <summary>Memory Usage: writes a full dump of the process into the session (null: the tool isn't on).</summary>
    public Func<Task<string?>>? CreateDump { get; init; }

    /// <summary>The heap snapshots taken so far (file names in the session folder).</summary>
    public List<string> Snapshots { get; } = [];

    /// <summary>The full dump written, if any (a file name in the session folder).</summary>
    public string? Dump { get; set; }

    public string Target { get; }

    public int ProcessId { get; }

    public IReadOnlyList<string> Tools { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public bool IsStopping => _stopping is not null;

    /// <summary>Stopped (by the user or because the process exited), with the saved session or null.</summary>
    public event EventHandler<ProfilerSession?>? Finished;

    /// <summary>Stops collecting (once) and saves the session.</summary>
    public Task<ProfilerSession?> StopAsync()
    {
        if (_stopping is not null)
        {
            return _stopping;
        }

        _stopping = FinishAsync();
        return _stopping;
    }

    private async Task<ProfilerSession?> FinishAsync()
    {
        var session = await _stop(this).ConfigureAwait(true);
        Finished?.Invoke(this, session);
        return session;
    }
}

/// <summary>A saved session in the Performance Profiler's list.</summary>
public sealed record ProfilerSessionRowViewModel(ProfilerSession Session)
{
    public string Name => Session.Name;

    public string Detail => Session.Description;

    public string AccessibleName => $"{Session.Name}, {Session.Description}";
}

/// <summary>A tool of the Performance Profiler, on or off.</summary>
public sealed class ProfilerToolChoice(string id, string title, string description, Action changed) : ObservableObject
{
    private bool _isSelected;

    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Description { get; } = description;

    public string Text => Title + ": " + Description;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
            {
                changed();
            }
        }
    }
}

/// <summary>
/// The Performance Profiler (Alt+F2, M18, ADR 0045 and 0046): pick tools (CPU Usage, .NET Counters, .NET Object
/// Allocation, Memory Usage, .NET Async, Threads and Contention, Exceptions, File I/O, Activities) and a target (the
/// startup project, launched with the tools attached from its first instruction, or a running .NET process), start
/// and stop collecting (taking heap snapshots or a full dump meanwhile); the session is saved under the solution's
/// <c>.codalus/profiler/</c> and its report opens. Earlier sessions are listed to reopen, compare or delete.
/// </summary>
public sealed class PerformanceProfilerViewModel : ObservableObject, ILoadable, IDisposable
{
    private readonly IProfilerHost _host;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private List<ProcessRowViewModel> _allProcesses = [];
    private int _targetIndex;
    private string _processFilter = string.Empty;
    private ProcessRowViewModel? _selectedProcess;
    private ProfilerSessionRowViewModel? _selectedSession;
    private ProfilerSessionRowViewModel? _compareBaseline;
    private string? _status;
    private string _elapsed = string.Empty;
    private ProfilingRun? _run;

    public PerformanceProfilerViewModel(IProfilerHost host)
    {
        _host = host;
        Tools =
        [
            new(ProfilerTools.Cpu, "CPU Usage", "where the time goes, as a call tree, hot paths, functions and a flame graph (EventPipe's sampling profiler)", Changed) { IsSelected = true },
            new(ProfilerTools.Counters, ".NET Counters", "CPU, memory, garbage collections and exceptions over time (the runtime's counters)", Changed) { IsSelected = true },
            new(ProfilerTools.Allocations, ".NET Object Allocation", "the types allocated and the stacks that allocate them (an allocation sample about every 100 KB)", Changed),
            new(ProfilerTools.Memory, "Memory Usage", "GC heap snapshots while collecting, with type statistics, paths to roots and diffs; a full dump to inspect objects", Changed),
            new(ProfilerTools.Async, ".NET Async", "async methods and tasks over time, the longest first, with the stacks that started them", Changed),
            new(ProfilerTools.Threads, "Threads and Contention", "lock contention with the waiting stacks, thread pool injections, each thread's state over time", Changed),
            new(ProfilerTools.Exceptions, "Exceptions", "first-chance exceptions over time, grouped by type and throw site", Changed),
            new(ProfilerTools.FileIo, "File I/O", "bytes and operations read and written over time (the process's I/O counters)", Changed),
            new(ProfilerTools.Activities, "Activities", "HTTP requests, database commands and Activity spans in a timeline", Changed),
        ];
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => CanStart);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => IsCollecting);
        SnapshotCommand = new RelayCommand(() => _ = TakeSnapshotAsync(), () => CanSnapshot);
        DumpCommand = new RelayCommand(() => _ = CreateDumpAsync(), () => CanSnapshot);
        RefreshCommand = new RelayCommand(() => _ = RefreshProcessesAsync());
        OpenSessionCommand = new RelayCommand(OpenSelectedSession, () => _selectedSession is not null);
        DeleteSessionCommand = new RelayCommand(DeleteSelectedSession, () => _selectedSession is not null);
        CompareCommand = new RelayCommand(CompareSelected, () => _selectedSession is not null && _compareBaseline is not null && _compareBaseline != _selectedSession);
        _clock.Tick += (_, _) => UpdateElapsed();
        if (host.Active is { } active)
        {
            Track(active);
        }
    }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand SnapshotCommand { get; }

    public ICommand DumpCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenSessionCommand { get; }

    public ICommand DeleteSessionCommand { get; }

    public ICommand CompareCommand { get; }

    public IReadOnlyList<ProfilerToolChoice> Tools { get; }

    public ObservableCollection<ProcessRowViewModel> Processes { get; } = [];

    public ObservableCollection<ProfilerSessionRowViewModel> Sessions { get; } = [];

    public string StartupProject => _host.StartupProjectName is { } name ? "Startup project: " + name : "Startup project (none: open a solution with an executable project)";

    private ProfilerToolChoice Tool(string id) => Tools.First(t => t.Id == id);

    public bool UseCpu
    {
        get => Tool(ProfilerTools.Cpu).IsSelected;
        set => Tool(ProfilerTools.Cpu).IsSelected = value;
    }

    public bool UseCounters
    {
        get => Tool(ProfilerTools.Counters).IsSelected;
        set => Tool(ProfilerTools.Counters).IsSelected = value;
    }

    /// <summary>Turns a tool on or off (the check boxes; tests and the smoke test).</summary>
    public void Use(string tool, bool on) => Tool(tool).IsSelected = on;

    /// <summary>The tools on, in the order shown.</summary>
    public IReadOnlyList<string> SelectedTools => [.. Tools.Where(t => t.IsSelected).Select(t => t.Id)];

    /// <summary>0: the startup project, launched; 1: a running process.</summary>
    public int TargetIndex
    {
        get => _targetIndex;
        set
        {
            if (SetField(ref _targetIndex, value))
            {
                OnPropertyChanged(nameof(IsProcessTarget));
                Changed();
                if (value == 1 && _allProcesses.Count == 0)
                {
                    _ = RefreshProcessesAsync();
                }
            }
        }
    }

    public bool IsProcessTarget => _targetIndex == 1;

    public string ProcessFilter
    {
        get => _processFilter;
        set
        {
            if (SetField(ref _processFilter, value ?? string.Empty))
            {
                ApplyProcessFilter();
            }
        }
    }

    public ProcessRowViewModel? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetField(ref _selectedProcess, value))
            {
                Changed();
            }
        }
    }

    public ProfilerSessionRowViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetField(ref _selectedSession, value))
            {
                ((RelayCommand)OpenSessionCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DeleteSessionCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CompareCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The session to compare the selected one with (the baseline).</summary>
    public ProfilerSessionRowViewModel? CompareBaseline
    {
        get => _compareBaseline;
        set
        {
            if (SetField(ref _compareBaseline, value))
            {
                ((RelayCommand)CompareCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string Elapsed
    {
        get => _elapsed;
        private set => SetField(ref _elapsed, value);
    }

    public bool IsCollecting => _run is not null;

    /// <summary>Collecting with Memory Usage: snapshots and dumps can be taken.</summary>
    public bool CanSnapshot => _run is { TakeSnapshot: not null, IsStopping: false };

    public string SnapshotText => _run is { } run ? $"Snapshots taken: {run.Snapshots.Count.ToString(CultureInfo.CurrentCulture)}{(run.Dump is null ? string.Empty : " · full dump written")}" : string.Empty;

    public bool CanStart =>
        !IsCollecting && Tools.Any(t => t.IsSelected)
        && (_targetIndex == 0 ? _host.StartupProjectName is not null : _selectedProcess is not null);

    private void Changed()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanSnapshot));
        OnPropertyChanged(nameof(SnapshotText));
        foreach (var command in new[] { StartCommand, StopCommand, SnapshotCommand, DumpCommand })
        {
            ((RelayCommand?)command)?.RaiseCanExecuteChanged();
        }
    }

    public async Task LoadAsync()
    {
        LoadSessions();
        if (_targetIndex == 1)
        {
            await RefreshProcessesAsync().ConfigureAwait(true);
        }
    }

    public void LoadSessions()
    {
        var selected = _selectedSession?.Session.Folder;
        var baseline = _compareBaseline?.Session.Folder;
        Sessions.Clear();
        foreach (var session in _host.Sessions())
        {
            Sessions.Add(new ProfilerSessionRowViewModel(session));
        }

        SelectedSession = Sessions.FirstOrDefault(s => s.Session.Folder == selected);
        CompareBaseline = Sessions.FirstOrDefault(s => s.Session.Folder == baseline);
    }

    public async Task RefreshProcessesAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _allProcesses = [.. (await _host.ProcessesAsync(timeout.Token).ConfigureAwait(true)).Select(p => new ProcessRowViewModel(p))];
            ApplyProcessFilter();
            Status = _allProcesses.Count == 0 ? "No .NET processes are running." : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Status = "The processes could not be listed: " + ex.Message;
        }
    }

    private void ApplyProcessFilter()
    {
        var selected = _selectedProcess?.Id;
        Processes.Clear();
        foreach (var process in _allProcesses.Where(p => p.Matches(_processFilter)))
        {
            Processes.Add(process);
        }

        SelectedProcess = Processes.FirstOrDefault(p => p.Id == selected) ?? (Processes.Count == 1 ? Processes[0] : null);
    }

    public async Task<ProfilingRun?> StartAsync()
    {
        if (!CanStart)
        {
            return null;
        }

        Status = _targetIndex == 0 ? "Building and starting the startup project…" : "Attaching…";
        var run = await _host.StartAsync(new ProfilerRequest(SelectedTools, _targetIndex == 0, _targetIndex == 1 ? _selectedProcess?.Id : null)).ConfigureAwait(true);
        if (run is null)
        {
            Status = "Nothing is being collected.";
            return null;
        }

        Track(run);
        return run;
    }

    private void Track(ProfilingRun run)
    {
        _run = run;
        run.Finished += (_, session) => Ui.Post(() => OnFinished(run, session));
        Status = $"Collecting from {run.Target} (process {run.ProcessId.ToString(CultureInfo.InvariantCulture)}). Stop Collection to see the report.";
        _clock.Start();
        UpdateElapsed();
        OnPropertyChanged(nameof(IsCollecting));
        Changed();
    }

    private void UpdateElapsed() =>
        Elapsed = _run is { } run ? (DateTimeOffset.Now - run.StartedAt).ToString(@"mm\:ss", CultureInfo.InvariantCulture) : string.Empty;

    public async Task StopAsync()
    {
        if (_run is { } run)
        {
            Status = "Stopping and reading the trace…";
            Changed();
            await run.StopAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Takes a heap snapshot of the program being profiled (Memory Usage).</summary>
    public async Task<string?> TakeSnapshotAsync()
    {
        if (_run is not { TakeSnapshot: { } take } run)
        {
            return null;
        }

        Status = "Taking a heap snapshot (a full garbage collection)…";
        var file = await take().ConfigureAwait(true);
        Status = file is null ? "The snapshot could not be taken." : $"Snapshot {run.Snapshots.Count.ToString(CultureInfo.CurrentCulture)} taken.";
        Changed();
        return file;
    }

    /// <summary>Writes a full dump of the program being profiled, to inspect its objects (Memory Usage).</summary>
    public async Task<string?> CreateDumpAsync()
    {
        if (_run is not { CreateDump: { } dump })
        {
            return null;
        }

        Status = "Writing a full dump…";
        var file = await dump().ConfigureAwait(true);
        Status = file is null ? "The dump could not be written." : "Full dump written; the report opens it.";
        Changed();
        return file;
    }

    private void OnFinished(ProfilingRun run, ProfilerSession? session)
    {
        if (_run != run)
        {
            return;
        }

        _run = null;
        _clock.Stop();
        UpdateElapsed();
        Status = session is null ? "The collection was not saved." : $"Saved {session.Name}.";
        LoadSessions();
        OnPropertyChanged(nameof(IsCollecting));
        Changed();
    }

    private void OpenSelectedSession()
    {
        if (_selectedSession is { } row)
        {
            _host.OpenSession(row.Session);
        }
    }

    private void DeleteSelectedSession()
    {
        if (_selectedSession is { } row)
        {
            _host.DeleteSession(row.Session);
            LoadSessions();
        }
    }

    private void CompareSelected()
    {
        if (_selectedSession is { } session && _compareBaseline is { } baseline && session != baseline)
        {
            _host.CompareSessions(baseline.Session, session.Session);
        }
    }

    public void Dispose() => _clock.Stop();
}

// ------------------------------------------------------------------ the report

/// <summary>A thread choice of the report's filter (null id: all threads).</summary>
public sealed record ThreadChoice(int? Id, string Text);

/// <summary>A counter's summary in the report (from the session's counters.json).</summary>
public sealed record CounterSummaryRow(string Provider, string Name, string Minimum, string Average, string Maximum, string Last, string? Unit)
{
    public string AccessibleName => $"{Name}: average {Average}, from {Minimum} to {Maximum}{(Unit is null ? string.Empty : " " + Unit)}";
}

/// <summary>A source file with samples (the report's Hot Files).</summary>
public sealed record HotFileRow(string Path, string Share)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string AccessibleName => $"{Name}, {Share} of the samples";
}

/// <summary>A node of the call tree (top-down) or of the callers tree (bottom-up), with its children loaded on expansion.</summary>
public sealed class CpuTreeNodeViewModel : ObservableObject
{
    private readonly ProfilingReportViewModel _owner;
    private readonly bool _bottomUp;
    private readonly CpuFilter? _filter;
    private bool _loaded;
    private bool _isExpanded;

    /// <param name="filter">The filter its children are loaded with (null: the report's current one).</param>
    public CpuTreeNodeViewModel(ProfilingReportViewModel owner, CpuNode node, int total, bool bottomUp, IReadOnlyList<int> path, CpuFilter? filter = null)
    {
        _owner = owner;
        Node = node;
        Total = total;
        _bottomUp = bottomUp;
        _filter = filter;
        Path = path;
        if (node.HasChildren)
        {
            Children.Add(new CpuTreeNodeViewModel(owner, new CpuNode(-1, -1, "Loading…", null, 0, 0, false), total, bottomUp, []));
        }
    }

    public CpuNode Node { get; }

    public int Total { get; }

    /// <summary>Bottom-up: the function ids from the hot function up to this node.</summary>
    public IReadOnlyList<int> Path { get; }

    public bool IsPlaceholder => Node.Id < 0 && Node.Function < 0;

    public string Name => Node.Name;

    public string? Module => Node.Module;

    public string Inclusive => IsPlaceholder ? string.Empty : FlameGraphPercent(Node.Inclusive);

    public string Exclusive => IsPlaceholder ? string.Empty : FlameGraphPercent(Node.Exclusive);

    public string Samples => IsPlaceholder ? string.Empty : Node.Inclusive.ToString("N0", CultureInfo.CurrentCulture);

    private string FlameGraphPercent(int samples) => Controls.FlameGraph.Percent(samples, Total);

    public string AccessibleName => IsPlaceholder ? "Loading" : $"{Node.Name}: {Inclusive} total, {Exclusive} self";

    public ObservableCollection<CpuTreeNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value) && value)
            {
                _ = LoadChildrenAsync();
            }
        }
    }

    public async Task LoadChildrenAsync()
    {
        if (_loaded || !Node.HasChildren)
        {
            return;
        }

        _loaded = true;
        var children = _bottomUp
            ? await _owner.CallersAsync(Path, _filter).ConfigureAwait(true)
            : await _owner.ChildrenAsync(Node.Id, _filter).ConfigureAwait(true);
        Children.Clear();
        foreach (var child in children.Nodes)
        {
            Children.Add(new CpuTreeNodeViewModel(_owner, child, Total, _bottomUp, _bottomUp ? [.. Path, child.Function] : [], _filter));
        }

        if (children.More > 0)
        {
            Children.Add(new CpuTreeNodeViewModel(_owner, new CpuNode(-1, -1, $"… {children.More.ToString("N0", CultureInfo.CurrentCulture)} more", null, 0, 0, false), Total, _bottomUp, []));
        }
    }
}

/// <summary>A function in the report's Functions list, or a caller or callee.</summary>
public sealed record CpuFunctionRowViewModel(CpuFunction Function, int Total)
{
    public string Name => Function.Name;

    public string? Module => Function.Module;

    public string Inclusive => Controls.FlameGraph.Percent(Function.Inclusive, Total);

    public string Exclusive => Controls.FlameGraph.Percent(Function.Exclusive, Total);

    public string? Location => Function.File is { } file ? $"{System.IO.Path.GetFileName(file)}:{Function.Line?.ToString(CultureInfo.InvariantCulture)}" : null;

    public string AccessibleName => $"{Function.Name}: {Inclusive} total, {Exclusive} self{(Location is null ? string.Empty : ", " + Location)}";
}

/// <summary>
/// A profiling report (M18, ADR 0045): a saved session's CPU trace, opened in the diagnostics host and shown a view at a
/// time (Call Tree, Bottom-Up, Functions with Caller/Callee, Flame Graph, Hot Files), filtered by a time range (dragged
/// on the timeline), a thread and native samples; its counters' summary; and its hot lines in the editor gutter.
/// </summary>
public sealed partial class ProfilingReportViewModel : ObservableObject, ILoadable, IAsyncDisposable
{
    private const int Page = 200;
    private const int MaxHotFiles = 100;

    private readonly DiagnosticsService _diagnostics;
    private readonly Func<string, int, Task> _openFile;
    private readonly Stack<int> _zoom = new();
    private CpuTrace? _trace;
    private CpuFilter _filter = new();
    private string? _summary;
    private string? _error;
    private string? _status;
    private ThreadChoice? _selectedThread;
    private bool _includeExternal;
    private double _selectionStart = double.NaN;
    private double _selectionEnd = double.NaN;
    private IReadOnlyList<int>? _timeline;
    private IReadOnlyList<FlameFrame>? _flameFrames;
    private int _flameTotal;
    private int _flameRoot = -1;
    private FlameFrame? _selectedFlameFrame;
    private string _flamePath = string.Empty;
    private string _search = string.Empty;
    private CpuFunctionRowViewModel? _selectedFunction;
    private CpuFunctionRowViewModel? _currentFunction;
    private int _selectedTab;
    private int _total;
    private bool _loading;
    private CancellationTokenSource _refresh = new();
    private Task? _load;

    /// <summary>Summarizes the CPU profile in AI Chat (M28b), set by the shell.</summary>
    public Func<ProfilingReportViewModel, Task>? SummarizeWithAi { get; set; }

    public System.Windows.Input.ICommand SummarizeWithAiCommand => new RelayCommand(() => _ = SummarizeWithAi?.Invoke(this));

    public ProfilingReportViewModel(DiagnosticsService diagnostics, ProfilerSession session, Func<string, int, Task> openFile)
    {
        _diagnostics = diagnostics;
        Session = session;
        _openFile = openFile;
        ClearRangeCommand = new RelayCommand(() => _ = SetRangeAsync(double.NaN, double.NaN));
        SearchCommand = new RelayCommand(() => _ = LoadFunctionsAsync(_refresh.Token));
        GoToSourceCommand = new RelayCommand(() => _ = GoToSourceAsync(_selectedFunction ?? _currentFunction));
        ZoomOutCommand = new RelayCommand(() => _ = ZoomOutAsync());
        ResetZoomCommand = new RelayCommand(() => _ = ResetZoomAsync());
    }

    public ProfilerSession Session { get; }

    public string Title => Session.Name;

    public bool HasCpu => Session.TracePath is not null;

    public bool HasCounters => Session.CountersPath is not null;

    public ICommand ClearRangeCommand { get; }

    public ICommand SearchCommand { get; }

    public ICommand GoToSourceCommand { get; }

    public ICommand ZoomOutCommand { get; }

    public ICommand ResetZoomCommand { get; }

    public ObservableCollection<ThreadChoice> Threads { get; } = [];

    public ObservableCollection<CpuTreeNodeViewModel> CallTree { get; } = [];

    public ObservableCollection<CpuTreeNodeViewModel> BottomUp { get; } = [];

    public ObservableCollection<CpuFunctionRowViewModel> Functions { get; } = [];

    public ObservableCollection<CpuFunctionRowViewModel> Callers { get; } = [];

    public ObservableCollection<CpuFunctionRowViewModel> Callees { get; } = [];

    public ObservableCollection<HotFileRow> HotFiles { get; } = [];

    public ObservableCollection<CounterSummaryRow> CounterSummaries { get; } = [];

    /// <summary>The trace (tests).</summary>
    internal CpuTrace? Trace => _trace;

    /// <summary>The samples the current filter counts.</summary>
    public int Total
    {
        get => _total;
        private set => SetField(ref _total, value);
    }

    public string? Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    /// <summary>What is loading or what the filter shows; announced to screen readers.</summary>
    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public bool IsLoading
    {
        get => _loading;
        private set => SetField(ref _loading, value);
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set => SetField(ref _selectedTab, value);
    }

    public ThreadChoice? SelectedThread
    {
        get => _selectedThread;
        set
        {
            if (SetField(ref _selectedThread, value) && _trace is not null)
            {
                _ = ApplyFilterAsync();
            }
        }
    }

    public bool IncludeExternal
    {
        get => _includeExternal;
        set
        {
            if (SetField(ref _includeExternal, value) && _trace is not null)
            {
                _ = ApplyFilterAsync();
            }
        }
    }

    public double SelectionStart
    {
        get => _selectionStart;
        set => SetField(ref _selectionStart, value);
    }

    public double SelectionEnd
    {
        get => _selectionEnd;
        set => SetField(ref _selectionEnd, value);
    }

    public string RangeText =>
        _filter.StartMilliseconds is { } start && _filter.EndMilliseconds is { } end
            ? string.Create(CultureInfo.CurrentCulture, $"{start / 1000:0.00} s to {end / 1000:0.00} s")
            : "The whole trace (drag on the timeline to select a range)";

    public bool HasRange => _filter.StartMilliseconds is not null;

    public IReadOnlyList<int>? Timeline
    {
        get => _timeline;
        private set => SetField(ref _timeline, value);
    }

    public IReadOnlyList<FlameFrame>? FlameFrames
    {
        get => _flameFrames;
        private set => SetField(ref _flameFrames, value);
    }

    public int FlameTotal
    {
        get => _flameTotal;
        private set => SetField(ref _flameTotal, value);
    }

    public FlameFrame? SelectedFlameFrame
    {
        get => _selectedFlameFrame;
        set
        {
            if (SetField(ref _selectedFlameFrame, value))
            {
                OnPropertyChanged(nameof(SelectedFlameText));
            }
        }
    }

    public string SelectedFlameText => _selectedFlameFrame is { } frame ? Controls.FlameGraph.Describe(frame, _flameTotal).Replace('\n', ' ') : "Select a frame (click, or the arrow keys); double-click or Enter shows its calls; Backspace goes back.";

    /// <summary>The flame graph's zoom: the path from the top down to its root.</summary>
    public string FlamePath
    {
        get => _flamePath;
        private set => SetField(ref _flamePath, value);
    }

    public string Search
    {
        get => _search;
        set => SetField(ref _search, value ?? string.Empty);
    }

    public CpuFunctionRowViewModel? SelectedFunction
    {
        get => _selectedFunction;
        set
        {
            if (SetField(ref _selectedFunction, value) && value is not null)
            {
                _ = ShowCallerCalleeAsync(value.Function.Id);
            }
        }
    }

    /// <summary>The function the Caller/Callee view is about.</summary>
    public CpuFunctionRowViewModel? CurrentFunction
    {
        get => _currentFunction;
        private set
        {
            if (SetField(ref _currentFunction, value))
            {
                OnPropertyChanged(nameof(HasCurrentFunction));
            }
        }
    }

    public bool HasCurrentFunction => _currentFunction is not null;

    public bool ShowHotLines
    {
        get => _diagnostics.HotLines.IsVisible;
        set
        {
            _diagnostics.HotLines.IsVisible = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Loads the report (once; the view and tests may both ask).</summary>
    public Task LoadAsync() => _load ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        LoadCounters();
        if (Session.TracePath is not { } path)
        {
            Summary = $"{Session.ProcessName} · {Session.Description}";
            SelectedTab = CountersTab;
            await LoadMemoryAsync().ConfigureAwait(true);
            if (HasMemory)
            {
                SelectedTab = MemoryTab;
            }

            return;
        }

        IsLoading = true;
        Status = "Reading the trace and its symbols…";
        try
        {
            _trace = await _diagnostics.OpenTraceAsync(path, CancellationToken.None).ConfigureAwait(true);
            var summary = _trace.Summary;
            Summary = string.Create(
                CultureInfo.CurrentCulture,
                $"{Session.ProcessName} ({Session.ProcessId}) · {summary.DurationMilliseconds / 1000:0.0} s · {summary.Samples:N0} samples in managed code, {summary.ExternalSamples:N0} in native code or waiting · {summary.Threads.Count} threads · {summary.Functions:N0} functions");
            Threads.Clear();
            Threads.Add(new ThreadChoice(null, "All threads"));
            foreach (var thread in summary.Threads.Where(t => t.Samples > 0))
            {
                Threads.Add(new ThreadChoice(thread.Id, string.Create(CultureInfo.CurrentCulture, $"{thread.Name}: {thread.Samples:N0} samples")));
            }

            _selectedThread = Threads[0];
            OnPropertyChanged(nameof(SelectedThread));
            InitializeProfiles(summary);
            await RefreshAsync().ConfigureAwait(true);
            await LoadToolsAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Error = $"The trace could not be read: {ex.Message}";
            Status = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void LoadCounters()
    {
        CounterSummaries.Clear();
        if (Session.CountersPath is not { } path || !File.Exists(path) || CounterExport.ReadJson(File.ReadAllText(path)) is not { } file)
        {
            return;
        }

        static string Format(double value) => Controls.TimeSeriesChart.Format(value);
        foreach (var counter in file.Counters.Where(c => c.Values.Count > 0))
        {
            var values = counter.Values.Select(v => v.Value).ToList();
            CounterSummaries.Add(new CounterSummaryRow(counter.Provider, counter.DisplayName + (counter.Tags is null ? string.Empty : $" [{counter.Tags}]"), Format(values.Min()), Format(values.Average()), Format(values.Max()), Format(values[^1]), counter.Unit));
        }
    }

    private async Task SetRangeAsync(double start, double end)
    {
        SelectionStart = start;
        SelectionEnd = end;
        await ApplyFilterAsync().ConfigureAwait(true);
    }

    /// <summary>The timeline's selection (0 to 1 across the trace; NaN clears it).</summary>
    public Task SelectRangeAsync(double start, double end) => SetRangeAsync(start, end);

    public async Task ApplyFilterAsync()
    {
        if (_trace is null)
        {
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private CpuFilter CurrentFilter()
    {
        var duration = _trace?.Summary.DurationMilliseconds ?? 0;
        var ranged = !double.IsNaN(_selectionStart) && !double.IsNaN(_selectionEnd);
        return new CpuFilter(
            ranged ? Math.Min(_selectionStart, _selectionEnd) * duration : null,
            ranged ? Math.Max(_selectionStart, _selectionEnd) * duration : null,
            _selectedThread?.Id is { } thread ? [thread] : null,
            _includeExternal,
            _selectedProfile?.Kind is { } kind && kind != ProfileKinds.Cpu ? kind : null);
    }

    /// <summary>Loads every view again for the current filter (an earlier refresh still running is cancelled).</summary>
    public async Task RefreshAsync()
    {
        if (_trace is null)
        {
            return;
        }

        await _refresh.CancelAsync().ConfigureAwait(true);
        _refresh.Dispose();
        _refresh = new CancellationTokenSource();
        var token = _refresh.Token;
        _filter = CurrentFilter();
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(HasRange));
        IsLoading = true;
        try
        {
            var timeline = await _trace.TimelineAsync(_filter, 200, token).ConfigureAwait(true);
            Timeline = timeline.Samples;
            var roots = await _trace.TopDownAsync(_filter, -1, 0, Page, token).ConfigureAwait(true);
            Total = roots.Total;
            CallTree.Clear();
            foreach (var node in roots.Nodes)
            {
                CallTree.Add(new CpuTreeNodeViewModel(this, node, roots.Total, bottomUp: false, []));
            }

            // The hot path opens by itself: the widest child at each level, while it holds most of its parent.
            var at = CallTree.FirstOrDefault();
            for (var depth = 0; at is not null && depth < 40 && at.Node.HasChildren; depth++)
            {
                at.IsExpanded = true;
                await at.LoadChildrenAsync().ConfigureAwait(true);
                var next = at.Children.FirstOrDefault(c => !c.IsPlaceholder);
                at = next is not null && next.Node.Inclusive * 2 >= at.Node.Inclusive ? next : null;
            }

            var bottom = await _trace.BottomUpAsync(_filter, [], 0, Page, token).ConfigureAwait(true);
            BottomUp.Clear();
            foreach (var node in bottom.Nodes)
            {
                BottomUp.Add(new CpuTreeNodeViewModel(this, node, bottom.Total, bottomUp: true, [node.Function]));
            }

            await LoadFunctionsAsync(token).ConfigureAwait(true);
            await LoadFlameAsync(_flameRoot, token).ConfigureAwait(true);
            if (_currentFunction is { } current)
            {
                await ShowCallerCalleeAsync(current.Function.Id).ConfigureAwait(true);
            }

            Status = string.Create(CultureInfo.CurrentCulture, $"{roots.Total:N0} {_selectedProfile?.Unit ?? "samples"} · {RangeText}{(_selectedThread?.Id is null ? string.Empty : " · " + _selectedThread.Text)}");
            if (_filter.Profile is null)
            {
                await LoadHotLinesAsync(token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer filter is loading.
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            Error = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    internal async Task<CpuTreeResult> ChildrenAsync(int node, CpuFilter? filter = null)
    {
        try
        {
            return _trace is null ? new CpuTreeResult(0, [], 0) : await _trace.TopDownAsync(filter ?? _filter, node, 0, Page, _refresh.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            return new CpuTreeResult(0, [], 0);
        }
    }

    internal async Task<CpuTreeResult> CallersAsync(IReadOnlyList<int> path, CpuFilter? filter = null)
    {
        try
        {
            return _trace is null ? new CpuTreeResult(0, [], 0) : await _trace.BottomUpAsync(filter ?? _filter, path, 0, Page, _refresh.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            return new CpuTreeResult(0, [], 0);
        }
    }

    private async Task LoadFunctionsAsync(CancellationToken cancellationToken)
    {
        if (_trace is null)
        {
            return;
        }

        try
        {
            var functions = await _trace.FunctionsAsync(_filter, _search.Length == 0 ? null : _search, 0, Page, cancellationToken).ConfigureAwait(true);
            Functions.Clear();
            foreach (var function in functions.Functions)
            {
                Functions.Add(new CpuFunctionRowViewModel(function, functions.Total));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Shows a function's callers and callees.</summary>
    public async Task ShowCallerCalleeAsync(int function)
    {
        if (_trace is null)
        {
            return;
        }

        try
        {
            var result = await _trace.CallerCalleeAsync(_filter, function, _refresh.Token).ConfigureAwait(true);
            CurrentFunction = new CpuFunctionRowViewModel(result.Function, result.Total);
            Callers.Clear();
            Callees.Clear();
            foreach (var caller in result.Callers)
            {
                Callers.Add(new CpuFunctionRowViewModel(caller, result.Total));
            }

            foreach (var callee in result.Callees)
            {
                Callees.Add(new CpuFunctionRowViewModel(callee, result.Total));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Status = ex.Message;
        }
    }

    // ------------------------------------------------------------------ flame graph

    private async Task LoadFlameAsync(int root, CancellationToken cancellationToken)
    {
        if (_trace is null)
        {
            return;
        }

        var flame = await _trace.FlameAsync(_filter, root, 0.0005, 6000, cancellationToken).ConfigureAwait(true);
        _flameRoot = root;
        FlameTotal = flame.Total;
        FlameFrames = flame.Frames;
        SelectedFlameFrame = null;
        FlamePath = flame.Path.Count == 0 ? "All samples" : "All samples › " + string.Join(" › ", flame.Path.Select(n => Controls.FlameGraph.ShortName(n.Name)));
    }

    /// <summary>Shows a frame's subtree in the flame graph.</summary>
    public async Task ZoomToAsync(FlameFrame frame)
    {
        if (frame.Node == _flameRoot)
        {
            return;
        }

        _zoom.Push(_flameRoot);
        await LoadFlameAsync(frame.Node, _refresh.Token).ConfigureAwait(true);
    }

    public async Task ZoomOutAsync()
    {
        if (_zoom.Count > 0)
        {
            await LoadFlameAsync(_zoom.Pop(), _refresh.Token).ConfigureAwait(true);
        }
    }

    public async Task ResetZoomAsync()
    {
        _zoom.Clear();
        await LoadFlameAsync(-1, _refresh.Token).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ source

    private async Task LoadHotLinesAsync(CancellationToken cancellationToken)
    {
        if (_trace is null)
        {
            return;
        }

        var files = await _trace.FilesAsync(_filter, cancellationToken).ConfigureAwait(true);
        HotFiles.Clear();
        var lines = new Dictionary<string, IReadOnlyList<CpuLine>>(StringComparer.Ordinal);
        foreach (var file in files.Files.Where(f => File.Exists(f.Path)).Take(MaxHotFiles))
        {
            HotFiles.Add(new HotFileRow(file.Path, Controls.FlameGraph.Percent(file.Inclusive, files.Total)));
            lines[file.Path] = (await _trace.LinesAsync(_filter, file.Path, cancellationToken).ConfigureAwait(true)).Lines;
        }

        _diagnostics.HotLines.Set(Title, files.Total, lines);
    }

    /// <summary>Opens a function's hottest line (once symbols are read).</summary>
    public async Task GoToSourceAsync(CpuFunctionRowViewModel? row)
    {
        if (row?.Function is { File: { } file, Line: { } line } && File.Exists(file))
        {
            await _openFile(file, line).ConfigureAwait(true);
        }
        else if (row is not null)
        {
            Status = $"{row.Name} has no source here (no symbols, or framework code).";
        }
    }

    public Task OpenHotFileAsync(HotFileRow row)
    {
        var lines = _diagnostics.HotLines.LinesOf(row.Path);
        var hottest = lines.Count == 0 ? 1 : lines.MaxBy(l => l.Exclusive)!.Line;
        return _openFile(row.Path, hottest);
    }

    public async ValueTask DisposeAsync()
    {
        await _refresh.CancelAsync().ConfigureAwait(false);
        _diagnostics.HotLines.Clear(Title);
        await DisposeSnapshotsAsync().ConfigureAwait(false);
        if (_trace is { } trace)
        {
            _trace = null;
            await trace.DisposeAsync().ConfigureAwait(false);
        }

        _refresh.Dispose();
    }
}

// ------------------------------------------------------------------ live windows

/// <summary>A counter's latest value.</summary>
public sealed class CounterRowViewModel(CounterKey key) : ObservableObject
{
    private string _value = "—";
    private string _name = key.Name;
    private string? _unit;

    public CounterKey Key { get; } = key;

    public string Provider => Key.Provider;

    public string Name
    {
        get => _name;
        private set => SetField(ref _name, value);
    }

    public string Value
    {
        get => _value;
        private set => SetField(ref _value, value);
    }

    public string? Unit
    {
        get => _unit;
        private set => SetField(ref _unit, value);
    }

    public string AccessibleName => $"{Provider} {Name}: {Value} {Unit}";

    internal void Update(CounterSeries series)
    {
        Name = series.DisplayName + (series.Key.Tags is null ? string.Empty : $" [{series.Key.Tags}]");
        Unit = series.Unit;
        Value = series.Last is { } last ? Controls.TimeSeriesChart.Format(last.Value) : "—";
        OnPropertyChanged(nameof(AccessibleName));
    }
}

/// <summary>A GC or an exception in the Diagnostic Tools window's event list.</summary>
public sealed record RuntimeEventRowViewModel(RuntimeEvent Event)
{
    public string Time => Event.At.ToString("0.00 's'", CultureInfo.CurrentCulture);

    public string Kind => Event.Kind == RuntimeEventKinds.GarbageCollection ? "GC" : "Exception";

    public string Text => Event.Text;

    public string AccessibleName => $"{Kind} at {Time}: {Text}";
}

/// <summary>
/// Keeps a list of counter rows in step with a monitor's series (sorted by provider and name), a few times a second.
/// </summary>
internal sealed class CounterRows(ObservableCollection<CounterRowViewModel> rows, Func<CounterSeries, bool>? include = null)
{
    private readonly Dictionary<CounterKey, CounterRowViewModel> _byKey = [];

    public void Clear()
    {
        rows.Clear();
        _byKey.Clear();
    }

    public void Update(IReadOnlyList<CounterSeries> series)
    {
        foreach (var s in series.Where(s => include?.Invoke(s) ?? true).OrderBy(s => s.Key.Provider, StringComparer.Ordinal).ThenBy(s => s.Key.Name, StringComparer.Ordinal).ThenBy(s => s.Key.Tags, StringComparer.Ordinal))
        {
            if (!_byKey.TryGetValue(s.Key, out var row))
            {
                row = new CounterRowViewModel(s.Key);
                _byKey[s.Key] = row;
                var index = 0;
                while (index < rows.Count && string.CompareOrdinal(rows[index].Key.ToString(), s.Key.ToString()) < 0)
                {
                    index++;
                }

                rows.Insert(index, row);
            }

            row.Update(s);
        }
    }
}

/// <summary>
/// The Diagnostic Tools window (M18, ADR 0045): while a program runs (Ctrl+F5) or is debugged (F5), its CPU usage, GC
/// heap and working set over time with GCs and exceptions marked, the events, and the live <c>System.Runtime</c>
/// counters; Record CPU Profile records a trace of it and opens the report. It follows the newest program, and only
/// while it is open.
/// </summary>
public sealed class DiagnosticToolsViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "debug.diagnosticTools";
    public const string Runtime = "System.Runtime";
    private const int MaxEvents = 300;

    private readonly DiagnosticsService _diagnostics;
    private readonly Func<int, string, Task<ProfilingRun?>> _record;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CounterRows _counterRows;
    private CounterMonitor? _monitor;
    private ProfilingRun? _recording;
    private string _status = "Start a program (F5 or Ctrl+F5) to see its CPU, memory and events here.";
    private string? _processName;
    private int _processId;
    private IReadOnlyList<CounterPoint> _cpu = [];
    private IReadOnlyList<CounterPoint> _heap = [];
    private IReadOnlyList<CounterPoint> _workingSet = [];
    private IReadOnlyList<double> _gcMarks = [];
    private IReadOnlyList<double> _exceptionMarks = [];
    private string _eventSummary = string.Empty;
    private bool _dirty;
    private int _attachGeneration;

    public DiagnosticToolsViewModel(DiagnosticsService diagnostics, Func<int, string, Task<ProfilingRun?>> record)
    {
        _diagnostics = diagnostics;
        _record = record;
        _counterRows = new CounterRows(Counters, s => s.Key.Provider == Runtime);
        RecordCommand = new RelayCommand(() => _ = ToggleRecordingAsync(), () => IsMonitoring);
        _timer.Tick += (_, _) => Flush();
    }

    public ICommand RecordCommand { get; }

    public ObservableCollection<CounterRowViewModel> Counters { get; } = [];

    public ObservableCollection<RuntimeEventRowViewModel> Events { get; } = [];

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string? ProcessName
    {
        get => _processName;
        private set => SetField(ref _processName, value);
    }

    public int ProcessId => _processId;

    public bool IsMonitoring => _monitor is { IsRunning: true };

    public bool IsRecording => _recording is not null;

    public string RecordText => IsRecording ? "Stop Recording" : "Record CPU Profile";

    public IReadOnlyList<CounterPoint> Cpu
    {
        get => _cpu;
        private set => SetField(ref _cpu, value);
    }

    public IReadOnlyList<CounterPoint> Heap
    {
        get => _heap;
        private set => SetField(ref _heap, value);
    }

    public IReadOnlyList<CounterPoint> WorkingSet
    {
        get => _workingSet;
        private set => SetField(ref _workingSet, value);
    }

    public IReadOnlyList<double> GcMarks
    {
        get => _gcMarks;
        private set => SetField(ref _gcMarks, value);
    }

    public IReadOnlyList<double> ExceptionMarks
    {
        get => _exceptionMarks;
        private set => SetField(ref _exceptionMarks, value);
    }

    public string EventSummary
    {
        get => _eventSummary;
        private set => SetField(ref _eventSummary, value);
    }

    /// <summary>The monitor (tests).</summary>
    internal CounterMonitor? Monitor => _monitor;

    /// <summary>Follows a process (the one already followed is left).</summary>
    public async Task AttachAsync(int processId, string name)
    {
        if (_processId == processId && IsMonitoring)
        {
            return;
        }

        await DetachAsync().ConfigureAwait(true);
        var generation = ++_attachGeneration;
        _processId = processId;
        ProcessName = name;
        Status = $"Connecting to {name} ({processId.ToString(CultureInfo.InvariantCulture)})…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var monitor = await _diagnostics.StartCountersAsync(processId, [new CounterProvider(Runtime, CounterProviderKinds.EventCounters)], 1, runtimeEvents: true, timeout.Token).ConfigureAwait(true);
            if (generation != _attachGeneration)
            {
                await monitor.StopAsync(CancellationToken.None).ConfigureAwait(true);
                return;
            }

            _monitor = monitor;
            monitor.Updated += (_, _) => _dirty = true;
            monitor.EventsReceived += (_, _) => _dirty = true;
            monitor.Stopped += (_, error) => Ui.Post(() => OnStopped(monitor, error));
            Status = $"{name} ({processId.ToString(CultureInfo.InvariantCulture)})";
            _timer.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            if (generation == _attachGeneration)
            {
                Status = $"{name} can't be monitored: {ex.Message}";
            }
        }

        OnPropertyChanged(nameof(IsMonitoring));
        ((RelayCommand)RecordCommand).RaiseCanExecuteChanged();
    }

    public async Task DetachAsync()
    {
        _attachGeneration++;
        _timer.Stop();
        if (_monitor is { } monitor)
        {
            _monitor = null;
            await monitor.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }

        Cpu = Heap = WorkingSet = [];
        GcMarks = ExceptionMarks = [];
        Events.Clear();
        _counterRows.Clear();
        EventSummary = string.Empty;
        OnPropertyChanged(nameof(IsMonitoring));
        ((RelayCommand)RecordCommand).RaiseCanExecuteChanged();
    }

    private void OnStopped(CounterMonitor monitor, string? error)
    {
        if (_monitor != monitor)
        {
            return;
        }

        Flush(force: true);
        _timer.Stop();
        Status = $"{ProcessName} has exited" + (error is null ? "." : $" ({error}).");
        OnPropertyChanged(nameof(IsMonitoring));
        ((RelayCommand)RecordCommand).RaiseCanExecuteChanged();
    }

    /// <summary>Copies the monitor's data to the charts and lists (throttled to the timer).</summary>
    internal void Flush(bool force = false)
    {
        if (_monitor is not { } monitor || (!_dirty && !force))
        {
            return;
        }

        _dirty = false;
        Cpu = monitor.Find(Runtime, "cpu-usage")?.Points ?? [];
        Heap = monitor.Find(Runtime, "gc-heap-size")?.Points ?? [];
        WorkingSet = monitor.Find(Runtime, "working-set")?.Points ?? [];
        var events = monitor.Events;
        GcMarks = [.. events.Where(e => e.Kind == RuntimeEventKinds.GarbageCollection).Select(e => e.At)];
        ExceptionMarks = [.. events.Where(e => e.Kind == RuntimeEventKinds.Exception).Select(e => e.At)];
        EventSummary = string.Create(CultureInfo.CurrentCulture, $"{GcMarks.Count:N0} garbage collections, {ExceptionMarks.Count:N0} exceptions");
        Events.Clear();
        foreach (var e in events.TakeLast(MaxEvents).Reverse())
        {
            Events.Add(new RuntimeEventRowViewModel(e));
        }

        _counterRows.Update(monitor.Series);
    }

    private async Task ToggleRecordingAsync()
    {
        if (_recording is { } recording)
        {
            _recording = null;
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(RecordText));
            await recording.StopAsync().ConfigureAwait(true);
            return;
        }

        if (!IsMonitoring)
        {
            return;
        }

        _recording = await _record(_processId, ProcessName ?? "process").ConfigureAwait(true);
        if (_recording is { } started)
        {
            started.Finished += (_, _) => Ui.Post(() =>
            {
                if (_recording == started)
                {
                    _recording = null;
                    OnPropertyChanged(nameof(IsRecording));
                    OnPropertyChanged(nameof(RecordText));
                }
            });
        }

        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordText));
    }

    public void Dispose()
    {
        _timer.Stop();
        _ = DetachAsync();
    }
}

/// <summary>
/// The .NET Counters window (M18, ADR 0045): attach to any .NET process by id or name and watch the counters of the
/// EventSources and meters named (System.Runtime by default; a library's own EventCounters; System.Diagnostics.Metrics
/// meters), with export as CSV or JSON.
/// </summary>
public sealed class CountersViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "diagnostics.counters";

    private readonly DiagnosticsService _diagnostics;
    private readonly Func<string, string, string, string[], Task<string?>> _save;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CounterRows _rows;
    private List<ProcessRowViewModel> _all = [];
    private CounterMonitor? _monitor;
    private string _target = string.Empty;
    private string _eventSources = DiagnosticToolsViewModel.Runtime;
    private string _meters = string.Empty;
    private string _interval = "1";
    private string? _status = "Choose a process (its id or name) and Start.";
    private ProcessRowViewModel? _selectedProcess;
    private bool _dirty;

    public CountersViewModel(DiagnosticsService diagnostics, Func<string, string, string, string[], Task<string?>> save)
    {
        _diagnostics = diagnostics;
        _save = save;
        _rows = new CounterRows(Rows);
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => !IsRunning);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => IsRunning);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        ExportCsvCommand = new RelayCommand(() => _ = ExportAsync(csv: true), () => _monitor is not null);
        ExportJsonCommand = new RelayCommand(() => _ = ExportAsync(csv: false), () => _monitor is not null);
        _timer.Tick += (_, _) => Flush();
    }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand ExportCsvCommand { get; }

    public ICommand ExportJsonCommand { get; }

    public ObservableCollection<ProcessRowViewModel> Processes { get; } = [];

    public ObservableCollection<CounterRowViewModel> Rows { get; } = [];

    /// <summary>A process id or (part of) a name.</summary>
    public string Target
    {
        get => _target;
        set => SetField(ref _target, value ?? string.Empty);
    }

    public ProcessRowViewModel? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetField(ref _selectedProcess, value) && value is not null)
            {
                Target = value.Id.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>EventSources whose counters to watch, comma-separated; <c>Name[counter;counter]</c> picks counters.</summary>
    public string EventSources
    {
        get => _eventSources;
        set => SetField(ref _eventSources, value ?? string.Empty);
    }

    /// <summary>Meters to watch, comma-separated; <c>Meter[instrument;instrument]</c> picks instruments.</summary>
    public string Meters
    {
        get => _meters;
        set => SetField(ref _meters, value ?? string.Empty);
    }

    public string Interval
    {
        get => _interval;
        set => SetField(ref _interval, value ?? string.Empty);
    }

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public bool IsRunning => _monitor is { IsRunning: true };

    /// <summary>The monitor (tests).</summary>
    internal CounterMonitor? Monitor => _monitor;

    public async Task RefreshAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _all = [.. (await _diagnostics.ProcessesAsync(timeout.Token).ConfigureAwait(true)).Select(p => new ProcessRowViewModel(p))];
            Processes.Clear();
            foreach (var process in _all)
            {
                Processes.Add(process);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Status = "The processes could not be listed: " + ex.Message;
        }
    }

    /// <summary>Parses <c>A, B[x;y]</c> into providers of <paramref name="kind"/>.</summary>
    internal static IEnumerable<CounterProvider> ParseProviders(string text, string kind)
    {
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var open = part.IndexOf('[', StringComparison.Ordinal);
            if (open > 0 && part.EndsWith(']'))
            {
                var names = part[(open + 1)..^1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                yield return new CounterProvider(part[..open].Trim(), kind, names.Length == 0 ? null : names);
            }
            else
            {
                yield return new CounterProvider(part, kind);
            }
        }
    }

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        int? processId = int.TryParse(_target.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
        if (processId is null)
        {
            if (_all.Count == 0)
            {
                await RefreshAsync().ConfigureAwait(true);
            }

            var matches = _all.Where(p => p.Matches(_target)).ToList();
            if (matches.Count != 1)
            {
                Status = matches.Count == 0 ? $"No .NET process matches '{_target}'." : $"{matches.Count} processes match '{_target}': enter its id.";
                return;
            }

            processId = matches[0].Id;
        }

        List<CounterProvider> providers = [.. ParseProviders(_eventSources, CounterProviderKinds.EventCounters), .. ParseProviders(_meters, CounterProviderKinds.Meter)];
        if (providers.Count == 0)
        {
            Status = "Name at least one EventSource or meter.";
            return;
        }

        var interval = double.TryParse(_interval, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) && seconds > 0 ? seconds : 1;
        try
        {
            Status = "Connecting…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var monitor = await _diagnostics.StartCountersAsync(processId.Value, providers, interval, runtimeEvents: false, timeout.Token).ConfigureAwait(true);
            _monitor = monitor;
            _rows.Clear();
            monitor.Updated += (_, _) => _dirty = true;
            monitor.Stopped += (_, error) => Ui.Post(() =>
            {
                if (_monitor == monitor)
                {
                    Flush(force: true);
                    _timer.Stop();
                    Status = $"Stopped: {monitor.ProcessName} ({monitor.ProcessId.ToString(CultureInfo.InvariantCulture)})" + (error is null ? string.Empty : $" — {error}");
                    Changed();
                }
            });
            Status = $"Watching {monitor.ProcessName} ({monitor.ProcessId.ToString(CultureInfo.InvariantCulture)}) every {interval.ToString(CultureInfo.CurrentCulture)} s";
            _timer.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Status = ex.Message;
        }

        Changed();
    }

    public async Task StopAsync()
    {
        if (_monitor is { IsRunning: true } monitor)
        {
            await monitor.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }

        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsRunning));
        foreach (var command in new[] { StartCommand, StopCommand, ExportCsvCommand, ExportJsonCommand })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
    }

    internal void Flush(bool force = false)
    {
        if (_monitor is { } monitor && (_dirty || force))
        {
            _dirty = false;
            _rows.Update(monitor.Series);
        }
    }

    /// <summary>Writes the values so far to a file the user picks.</summary>
    public async Task<string?> ExportAsync(bool csv, string? path = null)
    {
        if (_monitor is not { } monitor)
        {
            return null;
        }

        var name = $"{monitor.ProcessName}-counters.{(csv ? "csv" : "json")}";
        path ??= await _save(csv ? "Export Counters as CSV" : "Export Counters as JSON", name, csv ? "CSV files" : "JSON files", [csv ? "*.csv" : "*.json"]).ConfigureAwait(true);
        if (path is null)
        {
            return null;
        }

        try
        {
            await File.WriteAllTextAsync(path, csv ? CounterExport.ToCsv(monitor.Series) : CounterExport.ToJson(monitor)).ConfigureAwait(true);
            Status = "Exported to " + path;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "The export failed: " + ex.Message;
            return null;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_monitor is { IsRunning: true } monitor)
        {
            _ = monitor.StopAsync(CancellationToken.None);
        }
    }
}
