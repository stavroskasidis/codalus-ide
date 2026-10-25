using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Build;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

// ------------------------------------------------------------------ build events

/// <summary>When the post-build event runs, as the Build Events page offers it.</summary>
public sealed record RunPostBuildOption(string Value, string Label);

/// <summary>
/// The Build Events page (ADR 0034): the pre-build and post-build command lines, kept as the classic IDE keeps them
/// (<c>PreBuild</c>/<c>PostBuild</c> targets with an <c>Exec</c>), and when the post-build event runs.
/// </summary>
public sealed class BuildEventsViewModel : ObservableObject
{
    private readonly BuildDepthService _depth;
    private readonly string _project;
    private BuildEventsResult? _loaded;
    private string _preBuild = string.Empty;
    private string _postBuild = string.Empty;
    private RunPostBuildOption _run = s_options[0];
    private string? _error;
    private string? _status;
    private bool _isBusy;

    private static readonly RunPostBuildOption[] s_options =
    [
        new("OnBuildSuccess", "On successful build"),
        new("Always", "Always"),
        new("OnOutputUpdated", "When the build updates the project output"),
    ];

    public BuildEventsViewModel(BuildDepthService depth, string project)
    {
        _depth = depth;
        _project = project;
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => IsDirty && !_isBusy);
        RevertCommand = new RelayCommand(Revert, () => IsDirty && !_isBusy);
    }

    public static IReadOnlyList<RunPostBuildOption> RunOptions => s_options;

    /// <summary>The macros MSBuild expands in the command lines.</summary>
    public static string Macros => "$(ProjectDir)  $(ProjectPath)  $(ProjectName)  $(TargetDir)  $(TargetPath)  $(TargetName)  $(TargetFileName)  $(OutDir)  $(Configuration)  $(Platform)  $(SolutionDir)";

    public string PreBuild
    {
        get => _preBuild;
        set
        {
            if (SetField(ref _preBuild, value ?? string.Empty))
            {
                Changed();
            }
        }
    }

    public string PostBuild
    {
        get => _postBuild;
        set
        {
            if (SetField(ref _postBuild, value ?? string.Empty))
            {
                Changed();
            }
        }
    }

    public RunPostBuildOption RunPostBuildEvent
    {
        get => _run;
        set
        {
            if (value is not null && SetField(ref _run, value))
            {
                Changed();
            }
        }
    }

    /// <summary>Whether the project's events have been read (edits before that would be lost).</summary>
    public bool IsLoaded => _loaded is not null;

    public bool IsDirty => _loaded is not null && (Normalize(_preBuild) != Normalize(_loaded.PreBuild) || Normalize(_postBuild) != Normalize(_loaded.PostBuild) || _run.Value != _loaded.RunPostBuildEvent);

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

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public ICommand SaveCommand { get; }

    public ICommand RevertCommand { get; }

    public async Task LoadAsync()
    {
        if (IsDirty)
        {
            return; // never throw the user's edits away on a reload
        }

        try
        {
            Apply(await _depth.GetBuildEventsAsync(_project, CancellationToken.None).ConfigureAwait(true));
            Error = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Error = ex.Message;
        }
    }

    public async Task SaveAsync()
    {
        if (_loaded is null)
        {
            return;
        }

        _isBusy = true;
        Changed();
        try
        {
            Apply(await _depth.SetBuildEventsAsync(
                _project,
                Normalize(_preBuild) == Normalize(_loaded.PreBuild) ? null : _preBuild,
                Normalize(_postBuild) == Normalize(_loaded.PostBuild) ? null : _postBuild,
                _run.Value == _loaded.RunPostBuildEvent ? null : _run.Value,
                CancellationToken.None).ConfigureAwait(true));
            Error = null;
            Status = "Saved to " + Path.GetFileName(_project) + ".";
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            _isBusy = false;
            Changed();
        }
    }

    private void Revert()
    {
        if (_loaded is not null)
        {
            var loaded = _loaded;
            _loaded = null;
            Apply(loaded);
        }
    }

    private void Apply(BuildEventsResult events)
    {
        _loaded = null;
        PreBuild = events.PreBuild;
        PostBuild = events.PostBuild;
        RunPostBuildEvent = s_options.FirstOrDefault(o => o.Value == events.RunPostBuildEvent) ?? s_options[0];
        _loaded = events;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsLoaded));
        ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RevertCommand).RaiseCanExecuteChanged();
    }

    private static string Normalize(string text) => text.Trim().ReplaceLineEndings("\n");
}

// ------------------------------------------------------------------ batch build

/// <summary>A project × configuration × platform of the Batch Build page.</summary>
public sealed class BatchBuildRowViewModel(string project, string configuration, string platform, bool isChecked) : ObservableObject
{
    private bool _isChecked = isChecked;
    private string? _status;
    private bool _failed;

    public string Project { get; } = project;

    public string Name { get; } = Path.GetFileNameWithoutExtension(project);

    public string Configuration { get; } = configuration;

    public string Platform { get; } = platform;

    public string Key => $"{Project}|{Configuration}|{Platform}";

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }

    public string? Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool Failed
    {
        get => _failed;
        set => SetField(ref _failed, value);
    }

    public string AccessibleName => $"{Name} {Configuration} {Platform}" + (Status is null ? string.Empty : ", " + Status);
}

/// <summary>
/// Batch Build (ADR 0034): every project × configuration × platform the projects declare, checked ones built (or rebuilt,
/// or cleaned) in turn in one build. The checks are remembered for the session.
/// </summary>
public sealed class BatchBuildViewModel : ObservableObject, ILoadable
{
    private static readonly HashSet<string> s_checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProjectManagementService _management;
    private readonly Func<string, IReadOnlyList<BatchBuildEntry>, Task<BatchBuildResult?>> _build;
    private string? _status;
    private bool _isBusy;

    public BatchBuildViewModel(ProjectManagementService management, Func<string, IReadOnlyList<BatchBuildEntry>, Task<BatchBuildResult?>> build)
    {
        _management = management;
        _build = build;
        BuildCommand = new RelayCommand(() => _ = RunAsync(BuildTargets.Build), CanRun);
        RebuildCommand = new RelayCommand(() => _ = RunAsync(BuildTargets.Rebuild), CanRun);
        CleanCommand = new RelayCommand(() => _ = RunAsync(BuildTargets.Clean), CanRun);
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        DeselectAllCommand = new RelayCommand(() => SetAll(false));
    }

    public ObservableCollection<BatchBuildRowViewModel> Rows { get; } = [];

    public ICommand BuildCommand { get; }

    public ICommand RebuildCommand { get; }

    public ICommand CleanCommand { get; }

    public ICommand SelectAllCommand { get; }

    public ICommand DeselectAllCommand { get; }

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                foreach (var command in new[] { BuildCommand, RebuildCommand, CleanCommand })
                {
                    ((RelayCommand)command).RaiseCanExecuteChanged();
                }
            }
        }
    }

    private bool CanRun() => !_isBusy;

    public async Task LoadAsync()
    {
        try
        {
            var configurations = await _management.GetConfigurationsAsync(CancellationToken.None).ConfigureAwait(true);
            Rows.Clear();
            foreach (var project in configurations.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var platforms = project.ProjectPlatforms.Length == 0 ? ["AnyCPU"] : project.ProjectPlatforms;
                foreach (var configuration in project.ProjectConfigurations.Length == 0 ? ["Debug", "Release"] : project.ProjectConfigurations)
                {
                    foreach (var platform in platforms)
                    {
                        var row = new BatchBuildRowViewModel(project.Project, configuration, platform.Replace(" ", string.Empty, StringComparison.Ordinal), false);
                        row.IsChecked = s_checked.Contains(row.Key);
                        row.PropertyChanged += (_, e) =>
                        {
                            if (e.PropertyName == nameof(BatchBuildRowViewModel.IsChecked))
                            {
                                _ = row.IsChecked ? s_checked.Add(row.Key) : s_checked.Remove(row.Key);
                            }
                        };
                        Rows.Add(row);
                    }
                }
            }

            Status = $"{Rows.Count.ToString(CultureInfo.InvariantCulture)} project configurations.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Status = ex.Message;
        }
    }

    private void SetAll(bool value)
    {
        foreach (var row in Rows)
        {
            row.IsChecked = value;
        }
    }

    /// <summary>Builds the checked rows with <paramref name="target"/>; completes when the batch has finished.</summary>
    public async Task RunAsync(string target)
    {
        var rows = Rows.Where(r => r.IsChecked).ToList();
        if (rows.Count == 0)
        {
            Status = "Check the project configurations to build.";
            return;
        }

        IsBusy = true;
        foreach (var row in Rows)
        {
            row.Status = row.IsChecked ? "waiting" : null;
            row.Failed = false;
        }

        Status = $"{target} of {rows.Count.ToString(CultureInfo.InvariantCulture)} project configuration(s)…";
        try
        {
            if (await _build(target, [.. rows.Select(r => new BatchBuildEntry(r.Project, r.Configuration, r.Platform))]).ConfigureAwait(true) is not { } result)
            {
                Status = "The batch build didn't run.";
                foreach (var row in rows)
                {
                    row.Status = null;
                }

                return;
            }

            foreach (var row in rows)
            {
                var entry = result.Entries.FirstOrDefault(e => SolutionService.PathComparer.Equals(e.Project, row.Project) && e.Configuration == row.Configuration && e.Platform == row.Platform);
                row.Status = entry is null ? (result.Canceled ? "cancelled" : "skipped")
                    : entry.Skipped ? "up to date"
                    : entry.Succeeded ? $"succeeded ({entry.Warnings.ToString(CultureInfo.InvariantCulture)} warning(s))"
                    : $"failed ({entry.Errors.ToString(CultureInfo.InvariantCulture)} error(s))";
                row.Failed = entry is { Succeeded: false };
            }

            Status = result.Canceled ? "Batch build cancelled."
                : result.Succeeded ? $"Batch {target} succeeded in {TimeSpan.FromMilliseconds(result.ElapsedMilliseconds):mm\\:ss\\.f}."
                : $"Batch {target} failed: {result.Errors.ToString(CultureInfo.InvariantCulture)} error(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// ------------------------------------------------------------------ build timings

/// <summary>A project build on the Build Timings page: its bar on the timeline (scaled to the page's width).</summary>
public sealed class ProjectTimingRowViewModel(ProjectTiming timing, bool isCritical, double scale)
{
    public const double TimelineWidth = 560;

    public ProjectTiming Timing { get; } = timing;

    public string Name => Timing.TargetFramework is { } framework ? $"{Timing.Name} ({framework})" : Timing.Name;

    public string Targets => Timing.Targets.Length == 0 ? "(default targets)" : string.Join(", ", Timing.Targets);

    public string Node => "node " + Timing.Node.ToString(CultureInfo.InvariantCulture);

    public string Duration => FormatDuration(Timing.Duration);

    public string StartText => "+" + FormatDuration(Timing.Start);

    public bool IsCritical { get; } = isCritical;

    public bool Failed => !Timing.Succeeded;

    public Thickness BarMargin => new(Math.Round(Timing.Start * scale), 0, 0, 0);

    public double BarWidth => Math.Max(2, Math.Round(Timing.Duration * scale));

    public string AccessibleName => $"{Name}, {Duration}, starts at {StartText}, {Node}" + (IsCritical ? ", on the critical path" : string.Empty);

    internal static string FormatDuration(long milliseconds) =>
        milliseconds >= 60_000 ? TimeSpan.FromMilliseconds(milliseconds).ToString(@"m\:ss\.f", CultureInfo.InvariantCulture)
        : milliseconds >= 1000 ? (milliseconds / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s"
        : milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
}

public sealed record DurationRowViewModel(string Name, string Detail, string Duration);

/// <summary>
/// Build Timings (ADR 0034): the last build's project builds on a timeline, one row per build, the critical path
/// highlighted, and the targets and tasks that took longest.
/// </summary>
public sealed class BuildTimingsViewModel : ObservableObject, ILoadable
{
    private readonly BuildDepthService _depth;
    private readonly Func<string, Task> _openBinaryLog;
    private BuildTimingsResult? _timings;
    private bool _showAll;
    private string? _summary;
    private string? _error;

    public BuildTimingsViewModel(BuildDepthService depth, Func<string, Task> openBinaryLog)
    {
        _depth = depth;
        _openBinaryLog = openBinaryLog;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        OpenBinaryLogCommand = new RelayCommand(() => _ = _timings?.BinaryLog is { } log ? _openBinaryLog(log) : Task.CompletedTask, () => _timings?.BinaryLog is not null);
    }

    public ObservableCollection<ProjectTimingRowViewModel> Projects { get; } = [];

    public ObservableCollection<string> CriticalPath { get; } = [];

    public string CriticalPathText => CriticalPath.Count == 0 ? "(none)" : string.Join(" → ", CriticalPath);

    public ObservableCollection<DurationRowViewModel> Targets { get; } = [];

    public ObservableCollection<DurationRowViewModel> Tasks { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand OpenBinaryLogCommand { get; }

    /// <summary>Also the short project builds the project reference protocol makes (GetTargetPath…).</summary>
    public bool ShowAll
    {
        get => _showAll;
        set
        {
            if (SetField(ref _showAll, value))
            {
                Fill();
            }
        }
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

    public bool HasBinaryLog => _timings?.BinaryLog is not null;

    public async Task LoadAsync()
    {
        try
        {
            _timings = await _depth.GetTimingsAsync(CancellationToken.None).ConfigureAwait(true);
            Error = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            _timings = null;
            Error = ex is JsonRpcRemoteException ? ex.Message : "Build Timings shows the last build of the open solution: " + ex.Message;
        }

        OnPropertyChanged(nameof(HasBinaryLog));
        ((RelayCommand)OpenBinaryLogCommand).RaiseCanExecuteChanged();
        Fill();
    }

    private void Fill()
    {
        Projects.Clear();
        CriticalPath.Clear();
        Targets.Clear();
        Tasks.Clear();
        if (_timings is not { } timings)
        {
            Summary = null;
            return;
        }

        var critical = new HashSet<string>(timings.CriticalPath, StringComparer.Ordinal);
        var scale = timings.TotalMilliseconds <= 0 ? 0 : ProjectTimingRowViewModel.TimelineWidth / timings.TotalMilliseconds;
        foreach (var project in timings.Projects.Where(p => _showAll || critical.Contains(p.Id) || p.Duration >= 50 || p.Targets.Length == 0 || p.Targets.Any(t => t is "Build" or "Rebuild" or "Clean" or "Pack")))
        {
            Projects.Add(new ProjectTimingRowViewModel(project, critical.Contains(project.Id), scale));
        }

        var byId = timings.Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var id in timings.CriticalPath)
        {
            if (byId.TryGetValue(id, out var project))
            {
                CriticalPath.Add($"{project.Name} ({ProjectTimingRowViewModel.FormatDuration(project.Duration)})");
            }
        }

        foreach (var target in timings.Targets.Take(20))
        {
            Targets.Add(new DurationRowViewModel(target.Name, Path.GetFileNameWithoutExtension(target.Project), ProjectTimingRowViewModel.FormatDuration(target.Duration)));
        }

        foreach (var task in timings.Tasks.Take(20))
        {
            Tasks.Add(new DurationRowViewModel(task.Name, task.Count == 1 ? "1 run" : task.Count.ToString(CultureInfo.InvariantCulture) + " runs", ProjectTimingRowViewModel.FormatDuration(task.Duration)));
        }

        OnPropertyChanged(nameof(CriticalPathText));
        var nodes = timings.Projects.Select(p => p.Node).Distinct().Count();
        Summary = $"{ProjectTimingRowViewModel.FormatDuration(timings.TotalMilliseconds)} in all · {timings.Projects.Length.ToString(CultureInfo.InvariantCulture)} project builds on {nodes.ToString(CultureInfo.InvariantCulture)} node(s)"
                  + (Projects.Count < timings.Projects.Length ? $" ({(timings.Projects.Length - Projects.Count).ToString(CultureInfo.InvariantCulture)} short ones hidden)" : string.Empty);
    }
}

// ------------------------------------------------------------------ binary logs

/// <summary>A node of a binary log's tree; its children load when it is expanded.</summary>
public sealed class BinaryLogNodeViewModel : ObservableObject
{
    private readonly BinaryLogViewModel _owner;
    private bool _loaded;
    private bool _isExpanded;
    private bool _isSelected;

    public BinaryLogNodeViewModel(BinaryLogViewModel owner, BinaryLogNode node)
    {
        _owner = owner;
        Node = node;
        if (node.HasChildren)
        {
            Children.Add(new BinaryLogNodeViewModel(owner, new BinaryLogNode(-1, "Placeholder", "Loading…", false)));
        }
    }

    public BinaryLogNode Node { get; }

    public string Text => Node.Text;

    public string Kind => Node.Kind;

    public string Icon => Node.IsError ? "error" : Node.IsWarning ? "warning" : Node.Kind switch
    {
        "Project" => "project",
        "Target" => "play-outline",
        "Task" => "gear",
        "Property" or "Metadata" => "properties",
        "Item" or "AddItem" or "RemoveItem" => "file",
        "Folder" or "Import" or "NoImport" => "folder",
        "Build" => "solution",
        _ => "info",
    };

    public string? Duration => Node.DurationMilliseconds is { } ms ? ProjectTimingRowViewModel.FormatDuration(ms) : null;

    public bool HasDuration => Node.DurationMilliseconds is not null;

    public bool IsPlaceholder => Node.Id < 0;

    public bool IsError => Node.IsError;

    public bool IsWarning => Node.IsWarning;

    public ObservableCollection<BinaryLogNodeViewModel> Children { get; } = [];

    public string AccessibleName => $"{Node.Kind}: {Node.Text}" + (Duration is null ? string.Empty : ", " + Duration);

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

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>Loads the children (once).</summary>
    public async Task LoadChildrenAsync()
    {
        if (_loaded || !Node.HasChildren)
        {
            return;
        }

        _loaded = true;
        var children = await _owner.ChildrenAsync(Node.Id).ConfigureAwait(true);
        Children.Clear();
        foreach (var child in children)
        {
            Children.Add(new BinaryLogNodeViewModel(_owner, child));
        }
    }
}

/// <summary>A search result in the binary log viewer.</summary>
public sealed class BinaryLogSearchRowViewModel(BinaryLogSearchResult result)
{
    public BinaryLogSearchResult Result { get; } = result;

    public string Text => Result.Node.Text;

    public string Kind => Result.Node.Kind;

    public string AccessibleName => $"{Kind}: {Text}";
}

/// <summary>
/// The binary log viewer (ADR 0034): a <c>.binlog</c>'s tree (build, projects, targets, tasks, messages, properties…),
/// loaded a level at a time from the build host, with search; a result reveals its node, and nodes with a file open it.
/// </summary>
public sealed class BinaryLogViewModel : ObservableObject, ILoadable, IDisposable
{
    private readonly BuildDepthService _depth;
    private readonly Func<string, int, Task> _openFile;
    private BinaryLogInfo? _info;
    private string _query = string.Empty;
    private string? _summary;
    private string? _error;
    private BinaryLogNodeViewModel? _selected;
    private BinaryLogSearchRowViewModel? _selectedResult;
    private CancellationTokenSource _search = new();

    public BinaryLogViewModel(BuildDepthService depth, string path, Func<string, int, Task> openFile)
    {
        _depth = depth;
        Path = path;
        _openFile = openFile;
        SearchCommand = new RelayCommand(() => _ = SearchAsync());
        OpenCommand = new RelayCommand(() => _ = OpenSelectedAsync(), () => _selected?.Node.File is not null);
    }

    public string Path { get; }

    public string Title => System.IO.Path.GetFileName(Path);

    public ObservableCollection<BinaryLogNodeViewModel> Roots { get; } = [];

    public ObservableCollection<BinaryLogSearchRowViewModel> Results { get; } = [];

    public ICommand SearchCommand { get; }

    public ICommand OpenCommand { get; }

    public string Query
    {
        get => _query;
        set => SetField(ref _query, value ?? string.Empty);
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

    public bool HasResults => Results.Count > 0;

    public BinaryLogNodeViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                ((RelayCommand)OpenCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public BinaryLogSearchRowViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (SetField(ref _selectedResult, value) && value is not null)
            {
                _ = RevealAsync(value.Result);
            }
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            Summary = "Reading " + Title + "…";
            _info = await _depth.OpenBinaryLogAsync(Path, CancellationToken.None).ConfigureAwait(true);
            Roots.Clear();
            var root = new BinaryLogNodeViewModel(this, _info.Root);
            Roots.Add(root);
            root.IsExpanded = true;
            Summary = (_info.Succeeded ? "Build succeeded" : "Build failed")
                      + $" in {ProjectTimingRowViewModel.FormatDuration(_info.DurationMilliseconds)} · {_info.Errors.ToString(CultureInfo.InvariantCulture)} error(s), {_info.Warnings.ToString(CultureInfo.InvariantCulture)} warning(s) · {_info.Nodes.ToString(CultureInfo.InvariantCulture)} nodes";
            Error = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Summary = null;
            Error = $"{Title} could not be read: {ex.Message}";
        }
    }

    internal async Task<IReadOnlyList<BinaryLogNode>> ChildrenAsync(int node)
    {
        if (_info is null)
        {
            return [];
        }

        try
        {
            return await _depth.GetChildrenAsync(_info.Id, node, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Error = ex.Message;
            return [];
        }
    }

    public async Task SearchAsync()
    {
        _search.Cancel();
        _search.Dispose();
        _search = new CancellationTokenSource();
        var token = _search.Token;
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        if (_info is null || _query.Trim().Length == 0)
        {
            return;
        }

        try
        {
            var found = await _depth.SearchAsync(_info.Id, _query, token).ConfigureAwait(true);
            foreach (var result in found.Results)
            {
                Results.Add(new BinaryLogSearchRowViewModel(result));
            }

            Summary = found.Results.Length == 0 ? $"Nothing matches '{_query.Trim()}'."
                : $"{found.Results.Length.ToString(CultureInfo.InvariantCulture)}{(found.Truncated ? "+" : string.Empty)} result(s) for '{_query.Trim()}'.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            Error = ex.Message;
        }

        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>Expands the tree along a result's path and selects its node.</summary>
    public async Task RevealAsync(BinaryLogSearchResult result)
    {
        var level = Roots;
        BinaryLogNodeViewModel? node = null;
        foreach (var id in result.Path)
        {
            node = level.FirstOrDefault(n => n.Node.Id == id);
            if (node is null)
            {
                return;
            }

            if (id != result.Path[^1])
            {
                await node.LoadChildrenAsync().ConfigureAwait(true);
                node.IsExpanded = true;
                level = node.Children;
            }
        }

        if (node is not null)
        {
            if (Selected is { } previous)
            {
                previous.IsSelected = false;
            }

            node.IsSelected = true;
            Selected = node;
        }
    }

    public Task OpenSelectedAsync() =>
        _selected?.Node.File is { } file && File.Exists(file) ? _openFile(file, Math.Max(0, _selected.Node.Line - 1)) : Task.CompletedTask;

    public void Dispose()
    {
        _search.Cancel();
        _search.Dispose();
        if (_info is { } info)
        {
            _ = CloseAsync(info.Id);
        }
    }

    private async Task CloseAsync(string id)
    {
        try
        {
            await _depth.CloseBinaryLogAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            // The host is gone, and the log with it.
        }
    }
}
