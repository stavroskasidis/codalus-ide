using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Codalus.Diagnostics.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Diagnostics;

namespace Codalus.Shell.ViewModels;

/// <summary>A stack profile of the report (CPU, allocations, lock contention, exceptions) to show in its stack views.</summary>
public sealed record ProfileChoice(string Kind, string Text, string Unit);

/// <summary>An exception type thrown at a site, how often, and where (the Exceptions tab).</summary>
public sealed record ExceptionGroupRow(ExceptionGroup Group)
{
    public string Type => Group.Type;

    public string ThrowSite => Group.ThrowSite;

    public string Count => Group.Count.ToString("N0", CultureInfo.CurrentCulture);

    public string? Message => Group.Message;

    public string? Location => Group.File is { } file ? $"{Path.GetFileName(file)}:{Group.Line?.ToString(CultureInfo.InvariantCulture)}" : null;

    public string AccessibleName => $"{Group.Type} thrown {Count} times by {Group.ThrowSite}{(Location is null ? string.Empty : " at " + Location)}";
}

/// <summary>An exception as it was thrown.</summary>
public sealed record ExceptionEventRow(ExceptionEvent Event)
{
    public string Time => (Event.At / 1000).ToString("0.000 's'", CultureInfo.CurrentCulture);

    public string Text => Event.Message is { } message ? $"{Event.Type}: {message}" : Event.Type;

    public string AccessibleName => $"At {Time}: {Text}";
}

/// <summary>A task or async method in the .NET Async tab, with its bar on the trace's time axis.</summary>
public sealed record AsyncTaskRow(AsyncTask Task, double TraceDuration)
{
    public string Name => Task.Method is { } method ? $"{method} ({Task.Name})" : Task.Name;

    public string Duration => Task.Duration.ToString("N1", CultureInfo.CurrentCulture) + " ms";

    public string Start => (Task.Start / 1000).ToString("0.000 's'", CultureInfo.CurrentCulture);

    public string Status => Task.Status ?? "running at the end";

    public double BarStart => TraceDuration <= 0 ? 0 : Task.Start / TraceDuration;

    public double BarLength => TraceDuration <= 0 ? 0 : Task.Duration / TraceDuration;

    public string AccessibleName => $"{Name}: {Duration} from {Start}, {Status}";
}

/// <summary>A thread's states over time in the Threads tab.</summary>
public sealed record ThreadTimelineRow(ThreadTimeline Thread)
{
    public string Name => Thread.Name;

    public string States => Thread.States;

    public string Summary => string.Create(CultureInfo.CurrentCulture, $"running {Thread.Running:N0} ms · waiting {Thread.Waiting:N0} ms · blocked {Thread.Contention:N0} ms");

    public string AccessibleName => $"{Name}: {Summary}";
}

/// <summary>A thread pool injection, adjustment or retirement.</summary>
public sealed record ThreadPoolRow(ThreadPoolEvent Event)
{
    public string Time => (Event.At / 1000).ToString("0.000 's'", CultureInfo.CurrentCulture);

    public string Text => Event.Kind switch
    {
        "adjustment" => $"Worker threads → {Event.Workers.ToString(CultureInfo.CurrentCulture)} ({Event.Reason})",
        "start" => $"A worker thread started ({Event.Workers.ToString(CultureInfo.CurrentCulture)} active)",
        _ => $"A worker thread retired ({Event.Workers.ToString(CultureInfo.CurrentCulture)} active)",
    };

    public bool IsStarvation => Event.Reason is "Starvation" or "CooperativeBlocking";

    public string AccessibleName => $"At {Time}: {Text}";
}

/// <summary>A request, command or span in the Activities tab, indented by its depth, with its bar.</summary>
public sealed record ActivityRow(ActivityItem Item, double TraceDuration)
{
    public string Name => Item.Name.Replace('\n', ' ').Replace('\r', ' ');

    public string Duration => Item.Duration.ToString("N1", CultureInfo.CurrentCulture) + " ms";

    public string Start => (Item.Start / 1000).ToString("0.000 's'", CultureInfo.CurrentCulture);

    public string? Detail => Item.Status is { } status ? (Item.Detail is { } detail ? status + " · " + detail : status) : Item.Detail;

    public Avalonia.Thickness Indent => new(Item.Depth * 14, 0, 0, 0);

    public double BarStart => TraceDuration <= 0 ? 0 : Item.Start / TraceDuration;

    public double BarLength => TraceDuration <= 0 ? 0 : Item.Duration / TraceDuration;

    public bool Failed => Item.Failed;

    public string AccessibleName => $"{Name}: {Duration} from {Start}{(Detail is null ? string.Empty : ", " + Detail)}";
}

/// <summary>An event in the events viewer.</summary>
public sealed record TraceEventRowViewModel(TraceEventRow Event)
{
    public string Time => (Event.At / 1000).ToString("0.000000 's'", CultureInfo.CurrentCulture);

    public string Name => Event.Provider + "/" + Event.Name;

    public string Thread => Event.Thread.ToString(CultureInfo.InvariantCulture);

    public string Payload => Event.Payload;

    public string AccessibleName => $"At {Time}, {Name} on thread {Thread}: {Payload}";
}

/// <summary>An event's count by provider and name (the events viewer's summary).</summary>
public sealed record EventCountRow(EventCount Count)
{
    public string Name => Count.Provider + "/" + Count.Name;

    public string Text => Count.Count.ToString("N0", CultureInfo.CurrentCulture);

    public string AccessibleName => $"{Name}: {Text} events";
}

/// <summary>A heap snapshot of the session.</summary>
public sealed record HeapSnapshotRow(int Number, string Path, HeapSnapshot Snapshot)
{
    public string Text => string.Create(CultureInfo.CurrentCulture, $"Snapshot {Number}: {Snapshot.Summary.Objects:N0} objects, {DumpHeapRow.Bytes(Snapshot.Summary.Bytes)}");

    public string AccessibleName => Text;
}

/// <summary>The baseline a snapshot is compared with (none: types by size).</summary>
public sealed record HeapBaselineChoice(HeapSnapshotRow? Snapshot, string Text);

/// <summary>A type in a heap snapshot, with its growth against the baseline.</summary>
public sealed record HeapTypeRow(HeapType Type)
{
    public string Name => Type.Name;

    public string Count => Type.Count.ToString("N0", CultureInfo.CurrentCulture);

    public string Size => DumpHeapRow.Bytes(Type.Bytes);

    public string? CountDiff => Type.CountDelta is { } delta ? delta.ToString("+#,0;-#,0;0", CultureInfo.CurrentCulture) : null;

    public string? SizeDiff => Type.BytesDelta is { } delta ? (delta >= 0 ? "+" : "-") + DumpHeapRow.Bytes(Math.Abs(delta)) : null;

    public string AccessibleName => $"{Name}: {Count} objects, {Size}{(CountDiff is null ? string.Empty : $", {CountDiff} objects and {SizeDiff} since the baseline")}";
}

/// <summary>A referrer in the paths to roots tree: a type holding the objects (expandable) or a root.</summary>
public sealed class HeapReferrerNodeViewModel : ObservableObject
{
    private readonly ProfilingReportViewModel _owner;
    private bool _loaded;
    private bool _isExpanded;

    public HeapReferrerNodeViewModel(ProfilingReportViewModel owner, HeapReferrer referrer, IReadOnlyList<int> path)
    {
        _owner = owner;
        Referrer = referrer;
        Path = path;
        if (referrer.HasChildren)
        {
            Children.Add(new HeapReferrerNodeViewModel(owner, new HeapReferrer(int.MaxValue, "Loading…", 0, 0, false), []));
        }
    }

    public HeapReferrer Referrer { get; }

    /// <summary>Type ids from the type up to this referrer.</summary>
    public IReadOnlyList<int> Path { get; }

    public string Name => Referrer.Name;

    public string Count => Referrer.Id == int.MaxValue ? string.Empty : Referrer.Count.ToString("N0", CultureInfo.CurrentCulture) + " objects";

    public string Size => Referrer.Id == int.MaxValue ? string.Empty : DumpHeapRow.Bytes(Referrer.Bytes);

    public bool IsRoot => Referrer.Id < 0;

    public string AccessibleName => Referrer.Id == int.MaxValue ? "Loading" : $"{Name}{(IsRoot ? " (a root)" : string.Empty)}: holds {Count}, {Size}";

    public ObservableCollection<HeapReferrerNodeViewModel> Children { get; } = [];

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
        if (_loaded || !Referrer.HasChildren)
        {
            return;
        }

        _loaded = true;
        var result = await _owner.ReferrersAsync(Path).ConfigureAwait(true);
        Children.Clear();
        foreach (var referrer in result.Referrers)
        {
            Children.Add(new HeapReferrerNodeViewModel(_owner, referrer, [.. Path, referrer.Id]));
        }
    }
}

/// <summary>
/// The report's other tools (M18b, ADR 0046): the stack profile choice (CPU, allocations, contention, exceptions) for
/// the stack views, and the Allocations, Memory, .NET Async, Threads, Exceptions, File I/O, Activities and Events tabs,
/// each loaded from the host a page at a time.
/// </summary>
public sealed partial class ProfilingReportViewModel
{
    public const int CountersTab = 5;
    public const int AllocationsTab = 6;
    public const int MemoryTab = 7;
    public const int AsyncTab = 8;
    public const int ThreadsTab = 9;
    public const int ExceptionsTab = 10;
    public const int FileIoTab = 11;
    public const int ActivitiesTab = 12;
    public const int EventsTab = 13;

    private const int EventsPage = 200;

    private ProfileChoice? _selectedProfile;
    private bool _toolsLoaded;
    private string _taskSearch = string.Empty;
    private AsyncTaskRow? _selectedTask;
    private IReadOnlyList<int>? _exceptionTimeline;
    private IReadOnlyList<CounterPoint> _ioRead = [];
    private IReadOnlyList<CounterPoint> _ioWrite = [];
    private string? _ioSummary;
    private string? _ioSource;
    private double _ioWindow = 60;
    private int _activityKind;
    private bool _activitiesByTime;
    private string? _activitiesSummary;
    private string _eventProvider = string.Empty;
    private string _eventName = string.Empty;
    private string _eventText = string.Empty;
    private int _eventsMore;
    private string? _eventsSummary;
    private HeapSnapshotRow? _selectedSnapshot;
    private HeapBaselineChoice? _selectedBaseline;
    private HeapTypeRow? _selectedHeapType;
    private string _heapSearch = string.Empty;
    private string? _heapSummary;
    private string? _threadsSummary;
    private string? _exceptionsSummary;
    private string? _asyncSummary;

    /// <summary>Saves a file (title, suggested name, filter name, patterns); the shell's picker.</summary>
    public Func<string, string, string, string[], Task<string?>>? SaveFile { get; init; }

    /// <summary>Opens a full dump in the dump view (M14's, through the debug companion host).</summary>
    public Func<string, Task<DumpDocument?>>? OpenDump { get; init; }

    public ObservableCollection<ProfileChoice> Profiles { get; } = [];

    /// <summary>The stack profile the Call Tree, Bottom-Up, Functions, Flame Graph and the timeline show.</summary>
    public ProfileChoice? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetField(ref _selectedProfile, value) && value is not null && _trace is not null)
            {
                _zoom.Clear();
                _flameRoot = -1;
                _ = RefreshAsync();
            }
        }
    }

    public bool HasProfiles => Profiles.Count > 1;

    /// <summary>Every tool tab has loaded (tests, the smoke test).</summary>
    public bool ToolsLoaded { get; private set; }

    public bool HasAllocations => Has(ProfileKinds.Allocations);

    public bool HasExceptions => Has(ProfileKinds.Exceptions) || Session.Tools.Contains(ProfilerTools.Exceptions);

    public bool HasAsync => _trace?.Summary.Tasks > 0 || Session.Tools.Contains(ProfilerTools.Async);

    public bool HasThreads => Has(ProfileKinds.Contention) || _trace?.Summary.ThreadPoolEvents > 0 || Session.Tools.Contains(ProfilerTools.Threads);

    public bool HasFileIo => _trace?.Summary.FileIo == true || Session.Tools.Contains(ProfilerTools.FileIo);

    public bool HasActivities => _trace is { Summary: { } s } && (s.Requests + s.Commands + s.Spans > 0) || Session.Tools.Contains(ProfilerTools.Activities);

    public bool HasEvents => _trace is not null;

    public bool HasMemory => Snapshots.Count > 0 || Session.DumpPath is not null;

    public bool HasDump => Session.DumpPath is { } dump && File.Exists(dump);

    private bool Has(string kind) => _trace?.Summary.Profiles?.Any(p => p.Kind == kind) == true;

    private void InitializeProfiles(TraceSummary summary)
    {
        Profiles.Clear();
        foreach (var profile in summary.Profiles ?? [])
        {
            var text = profile.Kind switch
            {
                ProfileKinds.Cpu => "CPU Usage (samples)",
                ProfileKinds.Allocations => "Allocations (KB allocated)",
                ProfileKinds.Contention => "Lock Contention (µs waited)",
                ProfileKinds.Exceptions => "Exceptions (thrown)",
                _ => profile.Kind,
            };
            Profiles.Add(new ProfileChoice(profile.Kind, text, profile.Unit));
        }

        if (Profiles.Count == 0)
        {
            Profiles.Add(new ProfileChoice(ProfileKinds.Cpu, "CPU Usage (samples)", "samples"));
        }

        _selectedProfile = Profiles.FirstOrDefault(p => p.Kind == ProfileKinds.Cpu) ?? Profiles[0];
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(HasProfiles));
    }

    private void RaiseHas()
    {
        foreach (var name in new[] { nameof(HasAllocations), nameof(HasExceptions), nameof(HasAsync), nameof(HasThreads), nameof(HasFileIo), nameof(HasActivities), nameof(HasEvents), nameof(HasMemory), nameof(HasDump) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Loads every tool tab the session has (after the stack views).</summary>
    public async Task LoadToolsAsync()
    {
        if (_toolsLoaded || _trace is null)
        {
            return;
        }

        _toolsLoaded = true;
        RaiseHas();
        var token = _refresh.Token;
        try
        {
            if (HasAllocations)
            {
                await LoadAllocationsAsync(token).ConfigureAwait(true);
            }

            if (HasAsync)
            {
                await LoadTasksAsync().ConfigureAwait(true);
            }

            if (HasThreads)
            {
                await LoadThreadsAsync(token).ConfigureAwait(true);
            }

            if (HasExceptions)
            {
                await LoadExceptionsAsync(token).ConfigureAwait(true);
            }

            if (HasFileIo)
            {
                await LoadFileIoAsync(token).ConfigureAwait(true);
            }

            if (HasActivities)
            {
                if (_trace.Summary.Requests == 0)
                {
                    _activityKind = _trace.Summary.Spans > 0 ? 2 : _trace.Summary.Commands > 0 ? 1 : 0;
                    OnPropertyChanged(nameof(ActivityKind));
                }

                await LoadActivitiesAsync().ConfigureAwait(true);
            }

            await LoadEventStatsAsync(token).ConfigureAwait(true);
            await LoadEventsAsync(append: false).ConfigureAwait(true);
            await LoadMemoryAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            Error = ex.Message;
        }

        RaiseHas();
        ToolsLoaded = true;
        OnPropertyChanged(nameof(ToolsLoaded));
    }

    private CpuFilter RangeFilter(string? profile = null) => new(_filter.StartMilliseconds, _filter.EndMilliseconds, Profile: profile);

    // ------------------------------------------------------------------ allocations

    /// <summary>The allocated types (by KB), each expanding into the stacks that allocate it.</summary>
    public ObservableCollection<CpuTreeNodeViewModel> AllocationTypes { get; } = [];

    private async Task LoadAllocationsAsync(CancellationToken cancellationToken)
    {
        var filter = RangeFilter(ProfileKinds.Allocations);
        var types = await _trace!.BottomUpAsync(filter, [], 0, Page, cancellationToken).ConfigureAwait(true);
        AllocationTypes.Clear();
        foreach (var node in types.Nodes)
        {
            AllocationTypes.Add(new CpuTreeNodeViewModel(this, node, types.Total, bottomUp: true, [node.Function], filter));
        }
    }

    // ------------------------------------------------------------------ .NET Async

    public ObservableCollection<AsyncTaskRow> Tasks { get; } = [];

    public ObservableCollection<string> TaskStack { get; } = [];

    public string TaskSearch
    {
        get => _taskSearch;
        set => SetField(ref _taskSearch, value ?? string.Empty);
    }

    public string? AsyncSummary
    {
        get => _asyncSummary;
        private set => SetField(ref _asyncSummary, value);
    }

    public ICommand SearchTasksCommand => new RelayCommand(() => _ = LoadTasksAsync());

    public AsyncTaskRow? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetField(ref _selectedTask, value))
            {
                TaskStack.Clear();
                foreach (var frame in value?.Task.Stack ?? [])
                {
                    TaskStack.Add(frame);
                }
            }
        }
    }

    public async Task LoadTasksAsync()
    {
        if (_trace is null)
        {
            return;
        }

        var result = await _trace.AsyncTasksAsync(RangeFilter(), _taskSearch.Length == 0 ? null : _taskSearch, 0, Page, _refresh.Token).ConfigureAwait(true);
        Tasks.Clear();
        foreach (var task in result.Tasks)
        {
            Tasks.Add(new AsyncTaskRow(task, result.DurationMilliseconds));
        }

        AsyncSummary = string.Create(CultureInfo.CurrentCulture, $"{result.Total:N0} tasks and async methods, the longest first{(result.More > 0 ? $" (the first {Tasks.Count:N0})" : string.Empty)}");
    }

    // ------------------------------------------------------------------ threads

    public ObservableCollection<ThreadTimelineRow> ThreadRows { get; } = [];

    public ObservableCollection<ThreadPoolRow> PoolEvents { get; } = [];

    /// <summary>The waiting stacks under the "Lock contention" frame, by time waited.</summary>
    public ObservableCollection<CpuTreeNodeViewModel> ContentionTree { get; } = [];

    public string? ThreadsSummary
    {
        get => _threadsSummary;
        private set => SetField(ref _threadsSummary, value);
    }

    private async Task LoadThreadsAsync(CancellationToken cancellationToken)
    {
        var threads = await _trace!.ThreadsAsync(150, cancellationToken).ConfigureAwait(true);
        ThreadRows.Clear();
        foreach (var thread in threads.Threads)
        {
            ThreadRows.Add(new ThreadTimelineRow(thread));
        }

        PoolEvents.Clear();
        foreach (var e in threads.Pool.Where(p => p.Kind == "adjustment").Concat(threads.Pool.Where(p => p.Kind != "adjustment").Take(300)).OrderBy(p => p.At))
        {
            PoolEvents.Add(new ThreadPoolRow(e));
        }

        var starvation = PoolEvents.Count(p => p.IsStarvation);
        ContentionTree.Clear();
        var contention = RangeFilter(ProfileKinds.Contention);
        if (Has(ProfileKinds.Contention))
        {
            var locks = await _trace.BottomUpAsync(contention, [], 0, Page, cancellationToken).ConfigureAwait(true);
            foreach (var node in locks.Nodes)
            {
                ContentionTree.Add(new CpuTreeNodeViewModel(this, node, locks.Total, bottomUp: true, [node.Function], contention));
            }
        }

        ThreadsSummary = string.Create(CultureInfo.CurrentCulture, $"{ThreadRows.Count:N0} threads (green: running managed code, grey: waiting or in native code, red: blocked on a lock) · {threads.Threads.Sum(t => t.Contention):N0} ms blocked · {starvation:N0} thread pool injections for starvation or blocking");
    }

    // ------------------------------------------------------------------ exceptions

    public ObservableCollection<ExceptionGroupRow> ExceptionGroups { get; } = [];

    public ObservableCollection<ExceptionEventRow> RecentExceptions { get; } = [];

    public IReadOnlyList<int>? ExceptionTimeline
    {
        get => _exceptionTimeline;
        private set => SetField(ref _exceptionTimeline, value);
    }

    public string? ExceptionsSummary
    {
        get => _exceptionsSummary;
        private set => SetField(ref _exceptionsSummary, value);
    }

    private async Task LoadExceptionsAsync(CancellationToken cancellationToken)
    {
        var result = await _trace!.ExceptionsAsync(RangeFilter(), cancellationToken).ConfigureAwait(true);
        ExceptionGroups.Clear();
        foreach (var group in result.Groups)
        {
            ExceptionGroups.Add(new ExceptionGroupRow(group));
        }

        RecentExceptions.Clear();
        foreach (var e in result.Recent.Reverse().Take(300))
        {
            RecentExceptions.Add(new ExceptionEventRow(e));
        }

        ExceptionTimeline = (await _trace.TimelineAsync(new CpuFilter(Profile: ProfileKinds.Exceptions), 200, cancellationToken).ConfigureAwait(true)).Samples;
        ExceptionsSummary = string.Create(CultureInfo.CurrentCulture, $"{result.Total:N0} first-chance exceptions of {result.Groups.Select(g => g.Type).Distinct().Count():N0} types at {result.Groups.Count:N0} throw sites");
    }

    /// <summary>Opens a throw site's line.</summary>
    public async Task OpenExceptionAsync(ExceptionGroupRow row)
    {
        if (row.Group is { File: { } file, Line: { } line } && File.Exists(file))
        {
            await _openFile(file, line).ConfigureAwait(true);
        }
        else
        {
            Status = $"{row.ThrowSite} has no source here (no symbols, or framework code).";
        }
    }

    // ------------------------------------------------------------------ file I/O

    public IReadOnlyList<CounterPoint> IoRead
    {
        get => _ioRead;
        private set => SetField(ref _ioRead, value);
    }

    public IReadOnlyList<CounterPoint> IoWrite
    {
        get => _ioWrite;
        private set => SetField(ref _ioWrite, value);
    }

    /// <summary>The I/O charts' time axis: the whole trace.</summary>
    public double IoWindow
    {
        get => _ioWindow;
        private set => SetField(ref _ioWindow, value);
    }

    public string? IoSummary
    {
        get => _ioSummary;
        private set => SetField(ref _ioSummary, value);
    }

    public string? IoSource
    {
        get => _ioSource;
        private set => SetField(ref _ioSource, value);
    }

    private async Task LoadFileIoAsync(CancellationToken cancellationToken)
    {
        var io = await _trace!.FileIoAsync(cancellationToken).ConfigureAwait(true);
        IoSource = $"{io.Platform}: {io.Source}";
        if (!io.Available)
        {
            IoSummary = "No I/O counters were recorded with this trace.";
            return;
        }

        // KB per second, at each sample's start.
        static IReadOnlyList<CounterPoint> Rate(IReadOnlyList<IoSample> samples, Func<IoSample, long> bytes) =>
            [.. samples.Select((s, i) => new CounterPoint(s.At / 1000, bytes(s) / 1024.0 / Math.Max(0.001, ((i + 1 < samples.Count ? samples[i + 1].At : s.At + 250) - s.At) / 1000)))];

        IoWindow = Math.Max(1, (io.Samples.Count == 0 ? 0 : io.Samples[^1].At / 1000) + 0.5);
        IoRead = Rate(io.Samples, s => s.ReadBytes);
        IoWrite = Rate(io.Samples, s => s.WriteBytes);
        var operations = io.ReadOperations is { } reads && io.WriteOperations is { } writes ? string.Create(CultureInfo.CurrentCulture, $" in {reads:N0} reads and {writes:N0} writes") : string.Empty;
        IoSummary = $"{DumpHeapRow.Bytes(io.ReadBytes)} read, {DumpHeapRow.Bytes(io.WriteBytes)} written{operations}";
    }

    // ------------------------------------------------------------------ activities

    public ObservableCollection<ActivityRow> Activities { get; } = [];

    /// <summary>0: HTTP requests, 1: database commands, 2: spans.</summary>
    public int ActivityKind
    {
        get => _activityKind;
        set
        {
            if (SetField(ref _activityKind, value))
            {
                _ = LoadActivitiesAsync();
            }
        }
    }

    /// <summary>In time order (spans then nest under their parents), not the longest first.</summary>
    public bool ActivitiesByTime
    {
        get => _activitiesByTime;
        set
        {
            if (SetField(ref _activitiesByTime, value))
            {
                _ = LoadActivitiesAsync();
            }
        }
    }

    public string? ActivitiesSummary
    {
        get => _activitiesSummary;
        private set => SetField(ref _activitiesSummary, value);
    }

    public async Task LoadActivitiesAsync()
    {
        if (_trace is null)
        {
            return;
        }

        var kind = _activityKind switch
        {
            1 => ActivityKinds.Database,
            2 => ActivityKinds.Span,
            _ => ActivityKinds.Http,
        };
        try
        {
            var result = await _trace.ActivitiesAsync(kind, RangeFilter(), _activitiesByTime, 0, 500, _refresh.Token).ConfigureAwait(true);
            Activities.Clear();
            foreach (var item in result.Items)
            {
                Activities.Add(new ActivityRow(item, result.DurationMilliseconds));
            }

            var what = kind switch
            {
                ActivityKinds.Database => "database commands",
                ActivityKinds.Span => "spans",
                _ => "HTTP requests",
            };
            ActivitiesSummary = string.Create(CultureInfo.CurrentCulture, $"{result.Total:N0} {what}{(result.More > 0 ? $" (the first {Activities.Count:N0})" : string.Empty)}, {(_activitiesByTime ? "in time order" : "the longest first")}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            ActivitiesSummary = ex.Message;
        }
    }

    // ------------------------------------------------------------------ events

    public ObservableCollection<EventCountRow> EventCounts { get; } = [];

    public ObservableCollection<TraceEventRowViewModel> Events { get; } = [];

    /// <summary>Providers to show, comma-separated (empty: all).</summary>
    public string EventProvider
    {
        get => _eventProvider;
        set => SetField(ref _eventProvider, value ?? string.Empty);
    }

    /// <summary>Event names (<c>Request/Start</c> or <c>Provider/Name</c>), comma-separated.</summary>
    public string EventName
    {
        get => _eventName;
        set => SetField(ref _eventName, value ?? string.Empty);
    }

    /// <summary>Text the payload contains.</summary>
    public string EventText
    {
        get => _eventText;
        set => SetField(ref _eventText, value ?? string.Empty);
    }

    public int EventsMore
    {
        get => _eventsMore;
        private set
        {
            if (SetField(ref _eventsMore, value))
            {
                OnPropertyChanged(nameof(HasMoreEvents));
            }
        }
    }

    public bool HasMoreEvents => _eventsMore > 0;

    public string? EventsSummary
    {
        get => _eventsSummary;
        private set => SetField(ref _eventsSummary, value);
    }

    public ICommand FilterEventsCommand => new RelayCommand(() => _ = LoadEventsAsync(append: false));

    public ICommand MoreEventsCommand => new RelayCommand(() => _ = LoadEventsAsync(append: true));

    public ICommand ExportSpeedscopeCommand => new RelayCommand(() => _ = ExportAsync(ExportFormats.Speedscope));

    public ICommand ExportChromiumCommand => new RelayCommand(() => _ = ExportAsync(ExportFormats.Chromium));

    public ICommand SaveTraceCommand => new RelayCommand(() => _ = SaveTraceAsync());

    private static string[]? List(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } parts ? parts : null;

    private async Task LoadEventStatsAsync(CancellationToken cancellationToken)
    {
        var stats = await _trace!.EventStatsAsync(cancellationToken).ConfigureAwait(true);
        EventCounts.Clear();
        foreach (var count in stats.Events)
        {
            EventCounts.Add(new EventCountRow(count));
        }
    }

    /// <summary>Picks an event (from the counts) as the filter.</summary>
    public Task FilterByAsync(EventCountRow row)
    {
        EventProvider = row.Count.Provider;
        EventName = row.Count.Name;
        return LoadEventsAsync(append: false);
    }

    public async Task LoadEventsAsync(bool append)
    {
        if (_trace is null)
        {
            return;
        }

        try
        {
            var filter = new EventFilter(List(_eventProvider), List(_eventName), _eventText.Length == 0 ? null : _eventText, _filter.StartMilliseconds, _filter.EndMilliseconds);
            var result = await _trace.EventsAsync(filter, append ? Events.Count : 0, EventsPage, _refresh.Token).ConfigureAwait(true);
            if (!append)
            {
                Events.Clear();
            }

            foreach (var e in result.Events)
            {
                Events.Add(new TraceEventRowViewModel(e));
            }

            EventsMore = result.More;
            EventsSummary = string.Create(CultureInfo.CurrentCulture, $"{result.Total:N0} events match; showing {Events.Count:N0}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            EventsSummary = ex.Message;
        }
    }

    /// <summary>Exports the current stack profile and filter as Speedscope or Chromium trace JSON.</summary>
    public async Task<string?> ExportAsync(string format, string? path = null)
    {
        if (_trace is null)
        {
            return null;
        }

        var speedscope = format == ExportFormats.Speedscope;
        var name = $"{Session.ProcessName}-{_selectedProfile?.Kind ?? "cpu"}{(speedscope ? ".speedscope.json" : ".trace.json")}";
        path ??= SaveFile is null ? null : await SaveFile(speedscope ? "Export for Speedscope" : "Export as a Chromium Trace", name, "JSON files", ["*.json"]).ConfigureAwait(true);
        if (path is null)
        {
            return null;
        }

        try
        {
            var result = await _trace.ExportAsync(_filter, format, path, _refresh.Token).ConfigureAwait(true);
            Status = $"Exported {DumpHeapRow.Bytes(result.Bytes)} to {result.Path}";
            return result.Path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Status = "The export failed: " + ex.Message;
            return null;
        }
    }

    /// <summary>Saves a copy of the trace (<c>.nettrace</c>, with its I/O counters) where the user picks.</summary>
    public async Task<string?> SaveTraceAsync(string? path = null)
    {
        if (Session.TracePath is not { } source)
        {
            return null;
        }

        path ??= SaveFile is null ? null : await SaveFile("Save Trace As", Session.ProcessName + ".nettrace", "EventPipe traces", ["*.nettrace"]).ConfigureAwait(true);
        if (path is null)
        {
            return null;
        }

        try
        {
            File.Copy(source, path, overwrite: true);
            var io = Path.ChangeExtension(source, ".io.json");
            if (File.Exists(io))
            {
                File.Copy(io, Path.ChangeExtension(path, ".io.json"), overwrite: true);
            }

            Status = "Saved the trace to " + path;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "The trace could not be saved: " + ex.Message;
            return null;
        }
    }

    // ------------------------------------------------------------------ memory

    public ObservableCollection<HeapSnapshotRow> Snapshots { get; } = [];

    public ObservableCollection<HeapBaselineChoice> Baselines { get; } = [];

    public ObservableCollection<HeapTypeRow> HeapTypes { get; } = [];

    public ObservableCollection<HeapReferrerNodeViewModel> Referrers { get; } = [];

    public HeapSnapshotRow? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (SetField(ref _selectedSnapshot, value) && value is not null)
            {
                SyncBaselines();
                _ = LoadHeapTypesAsync();
            }
        }
    }

    public HeapBaselineChoice? SelectedBaseline
    {
        get => _selectedBaseline;
        set
        {
            if (SetField(ref _selectedBaseline, value) && _selectedSnapshot is not null)
            {
                _ = LoadHeapTypesAsync();
            }
        }
    }

    public HeapTypeRow? SelectedHeapType
    {
        get => _selectedHeapType;
        set
        {
            if (SetField(ref _selectedHeapType, value))
            {
                _ = LoadRootsAsync();
            }
        }
    }

    public string HeapSearch
    {
        get => _heapSearch;
        set => SetField(ref _heapSearch, value ?? string.Empty);
    }

    public string? HeapSummary
    {
        get => _heapSummary;
        private set => SetField(ref _heapSummary, value);
    }

    public ICommand SearchHeapCommand => new RelayCommand(() => _ = LoadHeapTypesAsync());

    public ICommand InspectDumpCommand => new RelayCommand(() => _ = InspectDumpAsync());

    public async Task LoadMemoryAsync()
    {
        if (Snapshots.Count > 0 || Session.SnapshotPaths.Count == 0)
        {
            RaiseHas();
            return;
        }

        var number = 0;
        foreach (var path in Session.SnapshotPaths.Where(File.Exists))
        {
            number++;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var snapshot = await _diagnostics.OpenHeapSnapshotAsync(path, timeout.Token).ConfigureAwait(true);
                Snapshots.Add(new HeapSnapshotRow(number, path, snapshot));
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
            {
                HeapSummary = $"Snapshot {number} could not be read: {ex.Message}";
            }
        }

        RaiseHas();
        if (Snapshots.Count > 0)
        {
            SelectedSnapshot = Snapshots[^1];
        }
    }

    private void SyncBaselines()
    {
        Baselines.Clear();
        Baselines.Add(new HeapBaselineChoice(null, "No baseline (types by size)"));
        foreach (var snapshot in Snapshots.Where(s => s != _selectedSnapshot))
        {
            Baselines.Add(new HeapBaselineChoice(snapshot, $"Compared with snapshot {snapshot.Number.ToString(CultureInfo.CurrentCulture)} (growth)"));
        }

        // A later snapshot is compared with the one before it by default: the leak hunt.
        _selectedBaseline = _selectedSnapshot is { Number: > 1 } current ? Baselines.FirstOrDefault(b => b.Snapshot?.Number == current.Number - 1) ?? Baselines[0] : Baselines[0];
        OnPropertyChanged(nameof(SelectedBaseline));
    }

    public async Task LoadHeapTypesAsync()
    {
        if (_selectedSnapshot is not { } row)
        {
            return;
        }

        try
        {
            var baseline = _selectedBaseline?.Snapshot;
            var result = await row.Snapshot.TypesAsync(baseline?.Snapshot, _heapSearch.Length == 0 ? null : _heapSearch, 0, Page, _refresh.Token).ConfigureAwait(true);
            HeapTypes.Clear();
            foreach (var type in result.Types)
            {
                HeapTypes.Add(new HeapTypeRow(type));
            }

            HeapSummary = string.Create(CultureInfo.CurrentCulture, $"Snapshot {row.Number}: {result.Objects:N0} objects, {DumpHeapRow.Bytes(result.Bytes)}{(baseline is null ? string.Empty : $"; changes since snapshot {baseline.Number}, the most growth first")}");
            Referrers.Clear();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            HeapSummary = ex.Message;
        }
    }

    private async Task LoadRootsAsync()
    {
        Referrers.Clear();
        if (_selectedHeapType is not { Type.Id: >= 0 } type)
        {
            return;
        }

        var result = await ReferrersAsync([type.Type.Id]).ConfigureAwait(true);
        foreach (var referrer in result.Referrers)
        {
            Referrers.Add(new HeapReferrerNodeViewModel(this, referrer, [type.Type.Id, referrer.Id]));
        }
    }

    internal async Task<HeapRootsResult> ReferrersAsync(IReadOnlyList<int> path)
    {
        try
        {
            return _selectedSnapshot is { } row ? await row.Snapshot.ReferrersAsync(path, 0, 50, _refresh.Token).ConfigureAwait(true) : new HeapRootsResult([], 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            return new HeapRootsResult([], 0);
        }
    }

    /// <summary>Opens the session's full dump in the dump view (objects, fields; ClrMD in the debug companion host).</summary>
    public async Task InspectDumpAsync()
    {
        if (Session.DumpPath is { } dump && File.Exists(dump) && OpenDump is { } open)
        {
            await open(dump).ConfigureAwait(true);
        }
    }

    private async Task DisposeSnapshotsAsync()
    {
        foreach (var row in Snapshots.ToList())
        {
            await row.Snapshot.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>A function in a comparison of two sessions.</summary>
public sealed record CompareRowViewModel(CpuCompareRow Row)
{
    public string Name => Row.Name;

    public string Baseline => Row.BaselineTotal.ToString("P1", CultureInfo.CurrentCulture);

    public string Current => Row.Total.ToString("P1", CultureInfo.CurrentCulture);

    public string Delta => Row.Delta.ToString("+0.0%;-0.0%;0.0%", CultureInfo.CurrentCulture);

    public string Self => $"{Row.BaselineSelf.ToString("P1", CultureInfo.CurrentCulture)} → {Row.Self.ToString("P1", CultureInfo.CurrentCulture)}";

    public bool Worse => Row.Delta > 0;

    public string AccessibleName => $"{Name}: total {Baseline} before, {Current} now ({Delta}); self {Self}";
}

/// <summary>
/// Two sessions compared (M18b, ADR 0046): their CPU functions (each one's share of its own session, the largest
/// changes first) and, when both have heap snapshots, the growth of the heap from the baseline's last snapshot to the
/// session's last.
/// </summary>
public sealed class ProfilingComparisonViewModel : ObservableObject, ILoadable, IAsyncDisposable
{
    private readonly DiagnosticsService _diagnostics;
    private readonly CancellationTokenSource _lifetime = new();
    private CpuTrace? _baselineTrace;
    private CpuTrace? _trace;
    private HeapSnapshot? _baselineHeap;
    private HeapSnapshot? _heap;
    private string? _status;
    private Task? _load;

    public ProfilingComparisonViewModel(DiagnosticsService diagnostics, ProfilerSession baseline, ProfilerSession session)
    {
        _diagnostics = diagnostics;
        Baseline = baseline;
        Session = session;
    }

    public ProfilerSession Baseline { get; }

    public ProfilerSession Session { get; }

    public string Title => $"{Session.Name} compared with {Baseline.Name}";

    public ObservableCollection<CompareRowViewModel> Functions { get; } = [];

    public ObservableCollection<HeapTypeRow> HeapGrowth { get; } = [];

    public bool HasHeap => HeapGrowth.Count > 0;

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public Task LoadAsync() => _load ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        var token = _lifetime.Token;
        try
        {
            if (Baseline.TracePath is { } before && Session.TracePath is { } after && File.Exists(before) && File.Exists(after))
            {
                Status = "Reading both traces…";
                _baselineTrace = await _diagnostics.OpenTraceAsync(before, token).ConfigureAwait(true);
                _trace = await _diagnostics.OpenTraceAsync(after, token).ConfigureAwait(true);
                var result = await _trace.CompareAsync(_baselineTrace, new CpuFilter(), 500, token).ConfigureAwait(true);
                foreach (var row in result.Functions)
                {
                    Functions.Add(new CompareRowViewModel(row));
                }

                Status = string.Create(CultureInfo.CurrentCulture, $"CPU: {result.BaselineTotal:N0} samples before, {result.Total:N0} now; shares of each session's own samples, the largest changes first.");
            }
            else
            {
                Status = "Both sessions need a trace to compare their functions.";
            }

            if (Baseline.SnapshotPaths.LastOrDefault(File.Exists) is { } beforeHeap && Session.SnapshotPaths.LastOrDefault(File.Exists) is { } afterHeap)
            {
                _baselineHeap = await _diagnostics.OpenHeapSnapshotAsync(beforeHeap, token).ConfigureAwait(true);
                _heap = await _diagnostics.OpenHeapSnapshotAsync(afterHeap, token).ConfigureAwait(true);
                var growth = await _heap.TypesAsync(_baselineHeap, null, 0, 200, token).ConfigureAwait(true);
                foreach (var type in growth.Types)
                {
                    HeapGrowth.Add(new HeapTypeRow(type));
                }

                OnPropertyChanged(nameof(HasHeap));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Status = "The sessions could not be compared: " + ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var disposable in new IAsyncDisposable?[] { _baselineTrace, _trace, _baselineHeap, _heap })
        {
            if (disposable is not null)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }

        _lifetime.Dispose();
    }
}
