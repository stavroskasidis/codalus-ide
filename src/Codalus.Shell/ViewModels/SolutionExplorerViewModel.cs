using System.Collections.ObjectModel;
using Avalonia;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>One visible line of the Solution Explorer (the tree is flattened so the list can virtualize).</summary>
public sealed class ExplorerRowViewModel(ExplorerNode node, int depth, bool expanded, Func<string, ProjectInfo?>? project = null) : ObservableObject, ITreeRow
{
    private readonly Func<string, ProjectInfo?> _project = project ?? (_ => null);
    private ExplorerNode _node = node;
    private bool _isExpanded = expanded;
    private bool _isStartup;

    public ExplorerNode Node
    {
        get => _node;
        set
        {
            if (SetField(ref _node, value))
            {
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(HasDetail));
                OnPropertyChanged(nameof(ToolTip));
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(IsWarning));
                OnPropertyChanged(nameof(IsLinked));
                OnPropertyChanged(nameof(IsExcluded));
                OnPropertyChanged(nameof(IsDimmed));
                OnPropertyChanged(nameof(HasChildren));
                OnPropertyChanged(nameof(Chevron));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string Id => _node.Id;

    public int Depth { get; } = depth;

    public Thickness Indent => new(4 + (Depth * 16), 0, 0, 0);

    public string Name => _node.Name;

    public string? Detail => _node.Detail;

    public bool HasDetail => _node.Detail is not null;

    /// <summary>
    /// The row's tooltip: the path, or for a message (a project's reason it can't build, M43a) its whole text, which
    /// trims in a narrow pane (M34g).
    /// </summary>
    public string? ToolTip => _node.Path ?? (_node.Kind == ExplorerNodeKind.Message ? _node.Name : null);

    /// <summary>File-type, folder and project icons (M34a, <see cref="Controls.FileIcons"/>); folders open when expanded.</summary>
    public string Icon => Controls.FileIcons.ForNode(_node, _isExpanded, _project);

    public bool IsWarning => _node.IsWarning;

    public bool IsLinked => _node.IsLinked;

    public bool IsUnloaded => _node.IsUnloaded;

    /// <summary>Show All Files: on disk, not in the project (ADR 0033).</summary>
    public bool IsExcluded => _node.IsExcluded;

    /// <summary>
    /// Shown dimmed: an unloaded project, a file that isn't in the project, or in the folder view and Show All Files a folder
    /// listed only when it's expanded (node_modules, .vs, .idea) with what is in it, and a listing's cut row (M57).
    /// </summary>
    public bool IsDimmed => _node.IsUnloaded || _node.IsExcluded || _node.IsMuted;

    public bool HasChildren => _node.Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(Chevron));
            }
        }
    }

    /// <summary>The startup project's row (shown in bold).</summary>
    public bool IsStartup
    {
        get => _isStartup;
        set => SetField(ref _isStartup, value);
    }

    public string? Chevron => HasChildren ? (_isExpanded ? "chevron-down" : "chevron-right") : null;

    /// <summary>
    /// The row's name for a screen reader: its expansion, level and place are its tree item's (<see cref="Views.TreeRowsList"/>,
    /// M57), not words in its name.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var detail = _node.Detail is { } d ? " " + d : string.Empty;
            var warning = _node.IsWarning ? ", warning" : string.Empty;
            var excluded = _node.IsExcluded ? ", not in the project" : string.Empty;
            return $"{_node.Name}{detail}{warning}{excluded}";
        }
    }
}

/// <summary>
/// The Solution Explorer. The tree is rebuilt from the build host's model (<see cref="SolutionTree"/>) and shown as
/// a flattened list; expansion and selection survive rebuilds by node id. Operations act on the selected row.
/// Show All Files adds the files on disk that aren't items (ADR 0033), and in folder mode the folder view shows the
/// folder as it is on disk, each folder listed once it's expanded (M57). TODO(issue): multi-selection. (Open documents whose files were deleted are flagged in their tabs.)
/// </summary>
#pragma warning disable CA1001 // The disk listings' token sources are only cancelled (a newer listing, Show All Files off, the solution closed), never waited on: no handle to free.
public sealed class SolutionExplorerViewModel : ObservableObject
#pragma warning restore CA1001
{
    public const string ToolWindowId = "solutionExplorer";

    private readonly ShellHost _shell;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private ExplorerNode? _root;
    private ExplorerRowViewModel? _selected;
    private int _refreshQueued; // set from the model's events, which come on any thread
    private string? _pendingReveal;
    private (IReadOnlyList<string> Paths, bool Cut)? _clipboard;
    private string? _startupProject;
    private string _filter = string.Empty;
    private bool _showAllFiles;
    private bool _folderView;

    // Both listings of the disk below start from one posted job (QueueDiskLoad).
    private bool _diskQueued;

    // Show All Files: each loaded project's files on disk, down to the folders its listing went into (see LoadProjectsAsync).
    private readonly Dictionary<string, IReadOnlyList<DirectoryEntry>> _disk = new(SolutionService.PathComparer);
    private readonly Dictionary<string, IReadOnlySet<string>> _diskFolders = new(SolutionService.PathComparer);
    private readonly HashSet<string> _diskQueue = new(SolutionService.PathComparer);
    private bool _diskQueueAll;
    private HashSet<string>? _diskBatch; // the projects of the batch on its way: one at a time, the queue waits for it
    private int _diskGeneration; // Show All Files went off or the solution closed: the batch on its way is stale
    private CancellationTokenSource _diskCancellation = new();
    private string? _syncWhenListed; // Sync with Active Document's file, once the listings that can bring it have come

    // The folder view (see LoadFoldersAsync): each listed folder's own listing, one level, by folder. The workspace folder
    // and the expanded folders are listed; a collapsed folder keeps its last listing until the workspace closes.
    private readonly Dictionary<string, FolderListing> _folders = new(SolutionService.PathComparer);
    private readonly HashSet<string> _folderQueue = new(SolutionService.PathComparer);
    private HashSet<string>? _folderBatch; // the folders of the batch on its way: one at a time, the queue waits for it
    private int _folderGeneration; // the workspace closed or the solution view shown: the batch on its way is stale
    private CancellationTokenSource _folderCancellation = new();
    private string? _folderRoot; // the workspace folder the listings are of
    private readonly HashSet<FolderListing> _folderStandIns = new(ReferenceEqualityComparer.Instance); // a failed listing's empty one
    private bool _rootIsFolderView;

    // The search box's listing of the whole workspace folder, kept while the box has text (see LoadFolderSearchAsync).
    private Dictionary<string, FolderListing>? _folderSearch;
    private int _folderSearchGeneration;
    private bool _folderSearchLoading;

    /// <summary>What the build host knows about a project (its icon: test, web, library…).</summary>
    private ProjectInfo? ProjectInfoOf(string path) => _shell.Solutions?.Projects.GetValueOrDefault(path);

    public SolutionExplorerViewModel(ShellHost shell)
    {
        _shell = shell;
        var solutions = shell.Solutions;
        solutions.SolutionChanged += (_, _) => QueueRefresh();
        solutions.ProjectChanged += (_, _) => QueueRefresh();
        shell.Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(ShowMembersSetting))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
            }

            if (e.Affects(TrackActiveItemSetting))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    OnPropertyChanged(nameof(TrackActiveItem));
                    OnActiveDocumentChanged();
                });
            }
        };
        solutions.CompilerInputsChanged += (_, _) => QueueRefresh();
        solutions.SolutionChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(OnSolutionChangedForDisk);
        solutions.HostRestarted += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(OnHostRestarted);
        solutions.ProjectChanged += (_, project) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // An evaluation can change the folders that hold the project's items: its folder is listed again (and only its).
            if (_showAllFiles)
            {
                QueueDiskRefresh([project.Path]);
            }
        });
        ShowAllFilesCommand = new RelayCommand(() => ShowAllFiles = !ShowAllFiles);
        SwitchViewsCommand = new RelayCommand(() => SetFolderView(!_folderView), () => shell.Solutions.Solution?.IsFolder == true);
        OpenSolutionCommand = new RelayCommand(() => shell.Run("workbench.openSolution"));
        HomeCommand = new RelayCommand(Home);
        SyncCommand = new RelayCommand(SyncWithActiveDocument);
        CollapseAllCommand = new RelayCommand(CollapseAll);
        PropertiesCommand = new RelayCommand(() => shell.Run("project.properties"));
        Refresh();
    }

    /// <summary>The visible rows. Changes are applied as ranges, one event each (see <see cref="Apply"/>).</summary>
    public ResettableCollection<ExplorerRowViewModel> Rows { get; } = [];

    public RelayCommand OpenSolutionCommand { get; }

    public RelayCommand HomeCommand { get; }

    public RelayCommand SyncCommand { get; }

    public RelayCommand CollapseAllCommand { get; }

    public RelayCommand PropertiesCommand { get; }

    public RelayCommand ShowAllFilesCommand { get; }

    public RelayCommand SwitchViewsCommand { get; }

    /// <summary>
    /// Show All Files: files and folders on disk that aren't in their project show dimmed (ADR 0033). A folder that isn't in
    /// its project shows at once, and what is in it once it's expanded, as the classic IDE's does (M56).
    /// </summary>
    public bool ShowAllFiles
    {
        get => _showAllFiles;
        set
        {
            if (SetField(ref _showAllFiles, value))
            {
                _shell.Workbench.Context.Set("explorerShowAllFiles", value);
                if (value)
                {
                    QueueDiskRefresh(projects: null);
                }
                else
                {
                    ForgetListings();
                    Refresh();
                }
            }
        }
    }

    /// <summary>Folder mode's folder view (the disk), as opposed to its solution view (the loaded projects).</summary>
    public bool IsFolderView => _folderView && _shell.Solutions.Solution?.IsFolder == true;

    /// <summary>The switch between the folder view and the solution view (folder mode).</summary>
    public bool CanSwitchViews => _shell.Solutions.Solution?.IsFolder == true;

    public void SetFolderView(bool folderView)
    {
        _folderView = folderView;
        _shell.Workbench.Context.Set("explorerFolderView", IsFolderView);
        OnPropertyChanged(nameof(IsFolderView));
        if (IsFolderView)
        {
            QueueShownFolders();
        }
        else
        {
            StopFolderListings(); // what the batch on its way brings is stale; the listings stay for the folder view's return
            Refresh();
        }
    }

    private void OnSolutionChangedForDisk()
    {
        OnPropertyChanged(nameof(CanSwitchViews));
        OnPropertyChanged(nameof(IsFolderView));
        SwitchViewsCommand.RaiseCanExecuteChanged();
        _shell.Workbench.Context.Set("folderMode", _shell.Solutions.Solution?.IsFolder == true);
        _shell.Workbench.Context.Set("explorerFolderView", IsFolderView);
        if (_shell.Solutions.Solution is not { } solution)
        {
            ForgetListings();
            ForgetFolderListings();
            _folderView = false;
            _analyzersFound.Clear();
            return;
        }

        // The projects the solution lost lose their listings (and what a batch on its way brings for them); the ones it
        // gained are listed once they've loaded.
        var projects = solution.Projects.Select(p => p.Path).ToHashSet(SolutionService.PathComparer);
        foreach (var gone in _diskFolders.Keys.Where(p => !projects.Contains(p)).ToList())
        {
            _disk.Remove(gone);
            _diskFolders.Remove(gone);
        }

        // The folder view lists nothing again for a new set of projects (Load Project), whose marks come with the tree's
        // build the solution's change queues; another workspace folder starts over.
        if (IsFolderView && !SolutionService.PathComparer.Equals(_folderRoot, System.IO.Path.TrimEndingDirectorySeparator(solution.Directory)))
        {
            QueueShownFolders();
        }
    }

    /// <summary>
    /// The build host restarted and opened the workspace folder again: the same folder, so its change lists nothing. What
    /// changed on disk while the build host was down went unseen, and a listing that failed meanwhile left its folder empty,
    /// with no chevron to expand it again by. The failed listings' stand-ins go (their folders show "Loading…" and are
    /// listed once they're shown), and every folder listed and shown is listed again, as for a rescan. Nothing listed the
    /// folder view again until a file changed in the folder concerned (M57). (Show All Files lists each project again as it
    /// loads.)
    /// </summary>
    private void OnHostRestarted()
    {
        foreach (var folder in _folders.Where(f => _folderStandIns.Contains(f.Value)).Select(f => f.Key).ToList())
        {
            _folders.Remove(folder);
        }

        _folderStandIns.Clear();
        if (IsFolderView)
        {
            QueueFolderListings([.. _folders.Keys.Where(IsShownFolder)]);
            QueueShownFolders();
        }
    }

    /// <summary>
    /// Folders changed on disk (the build host's watcher, which leaves out bin and obj): the folder view lists again the
    /// folders listed and shown among them, and Show All Files the projects whose folders hold them; with
    /// <paramref name="rescan"/> (the watcher dropped events, and reports only the folders it watches), every folder listed
    /// and shown, and every project.
    /// </summary>
    public void OnFilesChanged(IReadOnlyList<string> directories, bool rescan = false)
    {
        if (IsFolderView)
        {
            // A folder not listed, or collapsed, isn't listed again: it is once it's expanded. One whose first listing is on
            // its way is, with the next batch: that listing may be older than the change (M57).
            QueueFolderListings(rescan
                ? [.. ListedOrListingFolders().Where(IsShownFolder)]
                : [.. directories.Select(System.IO.Path.TrimEndingDirectorySeparator).Where(d => (_folders.ContainsKey(d) || IsFolderListing(d)) && IsShownFolder(d))]);
        }

        if (_showAllFiles)
        {
            QueueDiskRefresh(rescan ? null : ProjectsHolding(_shell.Solutions.Projects.Keys, directories));
        }
    }

    /// <summary>
    /// A build finished (of <paramref name="project"/> and the projects it references; null: of the solution): Show All
    /// Files lists them again, and the analyzers the build host marked missing are looked for again.
    /// </summary>
    public void OnBuildFinished(string? project)
    {
        RefreshDisk(project);
        FindMissingAnalyzers();
    }

    /// <summary>
    /// Lists the disk again, when Show All Files or the folder view needs it, after a build of <paramref name="project"/>
    /// (with the projects it references, built first; null: of every project). The folder view lists again the folders
    /// listed and shown that the watcher leaves out (bin, obj, .vs, .idea, .codalus, and what is in them) and the built
    /// projects' folders, which a build's new bin or obj changes unseen, as Show All Files lists the projects (M57).
    /// TODO(issue): a build outside the IDE (a terminal's, a task's) lists none of them again, here as in Show All Files.
    /// </summary>
    public void RefreshDisk(string? project = null)
    {
        if (IsFolderView)
        {
            IEnumerable<string> projects = project is null ? _shell.Solutions.Projects.Keys : WithReferences(_shell.Solutions.Projects, project);
            var built = projects.Select(p => System.IO.Path.GetDirectoryName(p)!).ToHashSet(SolutionService.PathComparer);
            QueueFolderListings([.. ListedOrListingFolders().Where(f => IsShownFolder(f) && (built.Contains(f) || IsUnwatched(f)))]);
        }

        if (_showAllFiles)
        {
            QueueDiskRefresh(project is null ? null : WithReferences(_shell.Solutions.Projects, project));
        }
    }

    /// <summary>The projects whose folders hold any of <paramref name="directories"/>.</summary>
    internal static HashSet<string> ProjectsHolding(IEnumerable<string> projects, IReadOnlyList<string> directories)
    {
        var byFolder = projects.ToLookup(p => System.IO.Path.GetDirectoryName(p)!, SolutionService.PathComparer);
        var holding = new HashSet<string>(SolutionService.PathComparer);
        foreach (var directory in directories)
        {
            for (var folder = System.IO.Path.TrimEndingDirectorySeparator(directory); !string.IsNullOrEmpty(folder); folder = System.IO.Path.GetDirectoryName(folder))
            {
                holding.UnionWith(byFolder[folder]);
            }
        }

        return holding;
    }

    /// <summary><paramref name="project"/> and the projects it references, directly or not.</summary>
    internal static HashSet<string> WithReferences(IReadOnlyDictionary<string, ProjectInfo> projects, string project)
    {
        var built = new HashSet<string>(SolutionService.PathComparer);
        var pending = new Stack<string>([project]);
        while (pending.TryPop(out var next))
        {
            if (built.Add(next) && projects.TryGetValue(next, out var info))
            {
                foreach (var reference in info.References)
                {
                    if (reference is { Kind: ReferenceKinds.Project, Path: { } path })
                    {
                        pending.Push(path);
                    }
                }
            }
        }

        return built;
    }

    /// <summary>Queues listings of the projects' folders (null: every loaded project's).</summary>
    private void QueueDiskRefresh(IEnumerable<string>? projects)
    {
        if (projects is null)
        {
            _diskQueueAll = true;
        }
        else
        {
            _diskQueue.UnionWith(projects);
        }

        QueueDiskLoad();
    }

    /// <summary>Queues listings of folders (the folder view).</summary>
    private void QueueFolderListings(IReadOnlyCollection<string> folders)
    {
        var queued = false;
        foreach (var folder in folders)
        {
            queued |= _folderQueue.Add(folder);
        }

        if (queued)
        {
            QueueDiskLoad();
        }
    }

    /// <summary>
    /// Queues listings of the folders shown (the folder view's open, its return from the solution view): the workspace
    /// folder and the expanded folders whose parents are shown, so the expansion restored with the folder is listed in the
    /// first batch, with one build of the tree.
    /// </summary>
    private void QueueShownFolders()
    {
        if (_shell.Solutions.Solution is not { IsFolder: true } solution)
        {
            return;
        }

        EnsureFolderRoot(solution);
        var shown = new List<string> { _folderRoot! };
        foreach (var id in _expanded)
        {
            if (id.StartsWith("d:", StringComparison.Ordinal) && IsShownFolder(id[2..]))
            {
                shown.Add(id[2..]);
            }
        }

        QueueFolderListings(shown);
    }

    /// <summary>The listings are of <paramref name="solution"/>'s folder: another one's are dropped.</summary>
    private void EnsureFolderRoot(SolutionInfo solution)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(solution.Directory);
        if (!SolutionService.PathComparer.Equals(root, _folderRoot))
        {
            ForgetFolderListings();
            _folderRoot = root;
        }
    }

    /// <summary>Whether <paramref name="folder"/> shows in the folder view: the workspace folder, or expanded with its parents.</summary>
    private bool IsShownFolder(string folder)
    {
        if (_folderRoot is not { } root)
        {
            return false;
        }

        for (var up = folder; up is not null; up = System.IO.Path.GetDirectoryName(up))
        {
            if (SolutionService.PathComparer.Equals(up, root))
            {
                return true;
            }

            if (up.Length <= root.Length || !_expanded.Contains("d:" + up))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// A folder whose changes the build host's watcher leaves out (<c>ProjectWatcher.IsBuildOutput</c>; .git isn't listed):
    /// it, or a folder it's in below the workspace folder, is bin, obj, .vs, .idea or .codalus.
    /// </summary>
    private bool IsUnwatched(string folder)
    {
        if (_folderRoot is not { } root || folder.Length <= root.Length)
        {
            return false;
        }

        foreach (var segment in folder[(root.Length + 1)..].Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment is ".vs" or ".idea" or ".codalus")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Stops the folder view's batch on its way (what it brings is stale) and drops the search box's listing.</summary>
    private void StopFolderListings()
    {
        _folderCancellation.Cancel();
        _folderCancellation.Dispose();
        _folderCancellation = new CancellationTokenSource();
        _folderGeneration++;
        _folderBatch = null;
        _folderQueue.Clear();
        DropFolderSearch();
    }

    /// <summary>Drops the folder view's listings (the workspace closed, or another opened).</summary>
    private void ForgetFolderListings()
    {
        StopFolderListings();
        _folders.Clear();
        _folderStandIns.Clear();
        _folderRoot = null;
    }

    private void DropFolderSearch()
    {
        _folderSearch = null;
        _folderSearchGeneration++;
        _folderSearchLoading = false;
    }

    private void QueueDiskLoad()
    {
        if (_diskQueued)
        {
            return;
        }

        _diskQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _diskQueued = false;
            _ = LoadDiskAsync();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Lists what is queued, through the build host: the folder view's folders and projects' folders (Show All Files).</summary>
    public Task LoadDiskAsync() => Task.WhenAll(LoadFoldersAsync(), LoadProjectsAsync());

    /// <summary>Drops Show All Files' listings, and stops the batch on its way: what it brings is stale.</summary>
    private void ForgetListings()
    {
        _diskCancellation.Cancel();
        _diskCancellation.Dispose();
        _diskCancellation = new CancellationTokenSource();
        _diskGeneration++;
        _diskBatch = null;
        _disk.Clear();
        _diskFolders.Clear();
        _diskQueue.Clear();
        _diskQueueAll = false;
        _syncWhenListed = null;
    }

    /// <summary>Whether a listing of <paramref name="project"/> is queued or on its way.</summary>
    private bool IsListing(string project) => _diskQueueAll || _diskQueue.Contains(project) || _diskBatch?.Contains(project) == true;

    /// <summary>Whether a listing of the disk is queued or on its way (tests).</summary>
    internal bool IsListingDisk => _diskQueued || _folderQueue.Count > 0 || _folderBatch is not null || _folderSearchLoading
                                   || _diskQueueAll || _diskQueue.Count > 0 || _diskBatch is not null;

    /// <summary>Whether a listing of <paramref name="folder"/> is queued or on its way (the folder view).</summary>
    private bool IsFolderListing(string folder) => _folderQueue.Contains(folder) || _folderBatch?.Contains(folder) == true;

    /// <summary>
    /// The folders listed, and those whose first listing is on its way: a change of one of those, or a build, lists it again
    /// with the next batch. Dropped, the batch kept a listing that could be older than the change, and nothing listed the
    /// folder again until its next change (M57).
    /// </summary>
    private IEnumerable<string> ListedOrListingFolders() =>
        _folderBatch is { } batch ? _folders.Keys.Union(batch, SolutionService.PathComparer) : _folders.Keys;

    /// <summary>
    /// The folder view's listings of the queued folders, through the build host and off the UI thread, each folder alone,
    /// one level (M57): one batch at a time, four listings at a time in it, and what is queued meanwhile goes with the next
    /// batch. A listing equal to the folder's last keeps its instance; the tree is built again once, with the refreshes
    /// queued meanwhile, if any changed. A folder gone from its parent's listing loses its own. The whole workspace folder,
    /// up to 20,000 entries, was listed again for every change of any folder, every build and every change of the loaded
    /// projects, and its whole tree built on the UI thread; past 20,000 entries the rest showed as empty folders.
    /// </summary>
    private async Task LoadFoldersAsync()
    {
        if (_folderBatch is not null)
        {
            return; // the batch on its way starts the next one when it ends
        }

        if (!IsFolderView || _shell.Solutions.Solution is not { } solution)
        {
            _folderQueue.Clear();
            return;
        }

        EnsureFolderRoot(solution);
        var below = _folderRoot + System.IO.Path.DirectorySeparatorChar;
        var comparison = SolutionService.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var requests = _folderQueue
            .Where(f => SolutionService.PathComparer.Equals(f, _folderRoot) || f.StartsWith(below, comparison))
            .Select(f => (Folder: f, Old: _folders.GetValueOrDefault(f)))
            .ToList();
        _folderQueue.Clear();
        if (requests.Count == 0)
        {
            ResolveSyncWhenListed(); // nothing on its way can bring it
            return;
        }

        var generation = _folderGeneration;
        _folderBatch = requests.Select(r => r.Folder).ToHashSet(SolutionService.PathComparer);
        var listings = new System.Collections.Concurrent.ConcurrentBag<(string Folder, FolderListing? Listing, string? Error)>();
        try
        {
            FolderListingsOnTheirWay = Parallel.ForEachAsync(requests, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _folderCancellation.Token }, async (r, cancellationToken) =>
                listings.Add(await ListFolderAsync(r.Folder, r.Old, cancellationToken).ConfigureAwait(false)));
            await FolderListingsOnTheirWay.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The solution view shown, or the workspace closed: the generation tells.
        }

        if (generation != _folderGeneration)
        {
            return; // stale: a batch since has the queue
        }

        _folderBatch = null;
        var changed = false;
        var failed = new List<string>();
        foreach (var (folder, listing, error) in listings)
        {
            FolderListings++;
            if (error is not null)
            {
                // The build host restarting, its two minutes up: the folder keeps its last listing, or counts as listed with
                // nothing, so no "Loading…" stays for good. Empty, it has no chevron: it's listed again by a change in it,
                // a rescan, its parent's next expansion, the folder view's return (the workspace folder) or the restart.
                failed.Add(error);
                var standIn = new FolderListing([], false);
                if (_folders.TryAdd(folder, standIn))
                {
                    _folderStandIns.Add(standIn); // listed again once the build host restarts (OnHostRestarted)
                    changed = true;
                }
            }
            else if (!_folders.TryGetValue(folder, out var old) || !ReferenceEquals(old, listing))
            {
                if (old is not null)
                {
                    _folderStandIns.Remove(old);
                }

                _folders[folder] = listing!;
                changed = true;
            }
        }

        if (changed)
        {
            PruneFolders();
        }

        if (failed.Count > 0)
        {
            // One line for the batch, as Show All Files' (M56).
            _shell.Workbench.Log.AppendLine($"[explorer] Could not list {failed.Count} folder(s) on disk: {failed[0]}");
        }

        DiskLoaded?.Invoke(this, EventArgs.Empty);
        if (_folderQueue.Count > 0)
        {
            QueueDiskLoad(); // what was queued meanwhile, as the next batch
        }

        if (changed || !_rootIsFolderView)
        {
            QueueRefresh(); // with the refreshes queued meanwhile, one build of the tree
        }
        else
        {
            ResolveSyncWhenListed(); // the tree is as it was
        }
    }

    /// <summary>One folder's listing, one level (off the UI thread); equal to the last one, that one.</summary>
    private async Task<(string Folder, FolderListing? Listing, string? Error)> ListFolderAsync(string folder, FolderListing? old, CancellationToken cancellationToken)
    {
        try
        {
            var tree = await _shell.ProjectItems.ListTreeAsync(folder, cancellationToken, folders: []).ConfigureAwait(false);
            return (folder, old is not null && old.Truncated == tree.Truncated && old.Entries.SequenceEqual(tree.Entries) ? old : new FolderListing(tree.Entries, tree.Truncated), null);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (folder, null, ex.Message);
        }
    }

    /// <summary>Drops the listings of folders gone from their parents' listings (parents first).</summary>
    private void PruneFolders()
    {
        foreach (var folder in _folders.Keys.Where(f => !SolutionService.PathComparer.Equals(f, _folderRoot)).OrderBy(f => f.Length).ToList())
        {
            if (System.IO.Path.GetDirectoryName(folder) is not { } parent || !_folders.TryGetValue(parent, out var listing) || !listing.HasFolder(folder))
            {
                _folders.Remove(folder);
            }
        }
    }

    /// <summary>The folder view's listings taken so far, failed ones too (tests).</summary>
    internal int FolderListings { get; private set; }

    /// <summary>The folder view's last batch's listings, which end on the thread pool before the batch takes them (tests).</summary>
    internal Task? FolderListingsOnTheirWay { get; private set; }

    /// <summary>
    /// The search box's listing of the whole workspace folder (the folder view), off the UI thread: the filter then finds
    /// files in folders not listed yet, as it did when the whole folder was listed. Kept while the box has text, with the
    /// folders' own listings over its own (it may stop at its limit before them, and goes into no node_modules); dropped
    /// when the box is cleared. TODO(issue): a
    /// name search in the build host without the listing's 20,000-entry limit (and Code Search indexing the folder's files
    /// in folder mode).
    /// </summary>
    private async Task LoadFolderSearchAsync()
    {
        if (_folderSearchLoading || _folderRoot is not { } root)
        {
            return;
        }

        _folderSearchLoading = true;
        var generation = _folderSearchGeneration;
        var cancellationToken = _folderCancellation.Token;
        var listed = new Dictionary<string, FolderListing>(_folders, SolutionService.PathComparer);
        Dictionary<string, FolderListing> whole;
        try
        {
            whole = await Task.Run(async () =>
            {
                var tree = await _shell.ProjectItems.ListTreeAsync(root, cancellationToken).ConfigureAwait(false);
                // A folder listed alone is complete, where the whole listing may have stopped before it (and goes into no
                // node_modules); the workspace folder's own entry keeps the whole listing's cut, if any.
                var byFolder = FolderListing.FromTree(root, tree.Entries, tree.Truncated);
                foreach (var (folder, listing) in listed)
                {
                    if (!SolutionService.PathComparer.Equals(folder, root))
                    {
                        byFolder[folder] = listing;
                    }
                }

                return byFolder;
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            if (generation == _folderSearchGeneration)
            {
                _folderSearchLoading = false;
                _shell.Workbench.Log.AppendLine($"[explorer] Could not list the folder for the search: {ex.Message}");
            }

            return;
        }

        if (generation != _folderSearchGeneration)
        {
            return; // the box was cleared, or the workspace closed
        }

        _folderSearchLoading = false;
        if (_filter.Trim().Length > 0 && IsFolderView)
        {
            _folderSearch = whole;
            Refresh();
        }
    }

    /// <summary>
    /// Show All Files' listings of the queued projects' folders, through the build host and off the UI thread: one batch
    /// at a time, four listings at a time in it, and what is queued meanwhile goes with the next batch. Each goes into the
    /// folders that hold the project's items and the folders expanded: another folder (bin, obj) shows, without what is in
    /// it until it's expanded. A listing equal to the project's last keeps its instance, so the tree keeps the project's
    /// subtree; the tree is built again once, with the refreshes queued meanwhile, if any changed. Every project's whole
    /// folder, bin and obj with hundreds of files after a build, was listed again one project at a time for each change of
    /// any folder and each build, and every project's subtree built again on the UI thread (M56).
    /// </summary>
    private async Task LoadProjectsAsync()
    {
        if (_diskBatch is not null)
        {
            return; // the batch on its way starts the next one when it ends
        }

        var queued = _diskQueueAll ? [.. _shell.Solutions.Projects.Keys] : _diskQueue.ToList();
        _diskQueue.Clear();
        _diskQueueAll = false;
        var requests = new List<(string Project, ProjectInfo Info, IReadOnlyList<DirectoryEntry>? Old)>();
        foreach (var project in _showAllFiles ? queued : [])
        {
            if (_shell.Solutions.Projects.TryGetValue(project, out var info) && info.State == ProjectStates.Loaded)
            {
                requests.Add((project, info, _disk.GetValueOrDefault(project)));
            }
        }

        if (requests.Count == 0)
        {
            ResolveSyncWhenListed(); // nothing on its way can bring it
            return;
        }

        var generation = _diskGeneration;
        _diskBatch = requests.Select(r => r.Project).ToHashSet(SolutionService.PathComparer);
        var expanded = _expanded.Where(id => id.StartsWith("d:", StringComparison.Ordinal)).Select(id => id[2..]).ToList();
        var listings = new System.Collections.Concurrent.ConcurrentBag<ProjectListing>();
        try
        {
            ListingsOnTheirWay = Parallel.ForEachAsync(requests, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _diskCancellation.Token }, async (r, cancellationToken) =>
                listings.Add(await ListProjectAsync(r.Project, r.Info, r.Old, expanded, cancellationToken).ConfigureAwait(false)));
            await ListingsOnTheirWay.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Show All Files went off, or the solution closed: the generation tells.
        }

        if (generation != _diskGeneration)
        {
            return; // stale: Show All Files went off or the solution closed meanwhile, and a batch since has the queue
        }

        _diskBatch = null;
        var changed = false;
        var failed = new List<string>();
        var inSolution = _shell.Solutions.Solution?.Projects.Select(p => p.Path).ToHashSet(SolutionService.PathComparer) ?? [];
        foreach (var listing in listings.Where(l => inSolution.Contains(l.Project))) // not the projects the solution lost meanwhile
        {
            // The folders listed decide which folders have a "Loading…" row, so they count too. A listing that failed (the
            // build host restarting, its two minutes up) brings nothing new, and the folders it was to go into count as
            // listed: the expanded ones showed "Loading…" until the project's next listing, if any came.
            var entries = _disk.GetValueOrDefault(listing.Project);
            var folders = _diskFolders.GetValueOrDefault(listing.Project);
            var listed = listing.Error is null || folders is null ? listing.Folders : folders.Union(listing.Folders, SolutionService.PathComparer).ToHashSet(SolutionService.PathComparer);
            if (listing.Error is { } error)
            {
                failed.Add(error);
            }

            changed |= (listing.Error is null && !ReferenceEquals(entries, listing.Entries)) || folders is null || !folders.SetEquals(listed);
            _disk[listing.Project] = listing.Error is null ? listing.Entries : entries ?? [];
            _diskFolders[listing.Project] = listed;
            Listings++;
        }

        if (failed.Count > 0)
        {
            // One line for the batch: with the build host down, each project's own line flooded the log after a build.
            _shell.Workbench.Log.AppendLine($"[explorer] Could not list the files on disk of {failed.Count} project(s): {failed[0]}");
        }

        DiskLoaded?.Invoke(this, EventArgs.Empty);
        if (_diskQueueAll || _diskQueue.Count > 0)
        {
            QueueDiskLoad(); // what was queued meanwhile, as the next batch
        }

        if (changed)
        {
            QueueRefresh(); // with the refreshes queued meanwhile (evaluations', expansions'), one build of the tree
        }
        else
        {
            ResolveSyncWhenListed(); // the tree is as it was
        }
    }

    private sealed record ProjectListing(string Project, IReadOnlyList<DirectoryEntry> Entries, IReadOnlySet<string> Folders, string? Error = null);

    /// <summary>
    /// A project's folder, going into the folders that hold its items and the expanded ones (off the UI thread); into
    /// node_modules, .vs and .idea only once they're expanded, items or not: a plain SDK project's default globs take all of
    /// node_modules, and each listing went through its thousands of files, up to the limit, after which the project's other
    /// files went missing (M57).
    /// </summary>
    private async Task<ProjectListing> ListProjectAsync(string project, ProjectInfo info, IReadOnlyList<DirectoryEntry>? old, IReadOnlyList<string> expanded, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(project)!;
        var below = System.IO.Path.TrimEndingDirectorySeparator(directory) + System.IO.Path.DirectorySeparatorChar;
        var comparison = SolutionService.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var folders = new HashSet<string>(SolutionTree.ItemFolders(project, info).Where(f => !SolutionTree.IsInOnDemandFolder(f, directory)), SolutionService.PathComparer);
        folders.UnionWith(expanded.Where(f => f.StartsWith(below, comparison)));
        try
        {
            var tree = await _shell.ProjectItems.ListTreeAsync(directory, cancellationToken, folders: folders).ConfigureAwait(false);
            return new ProjectListing(project, old is not null && old.SequenceEqual(tree.Entries) ? old : tree.Entries, folders);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new ProjectListing(project, [], folders, ex.Message);
        }
    }

    /// <summary>The projects' listings taken so far, failed ones too (tests).</summary>
    internal int Listings { get; private set; }

    /// <summary>The last batch's listings, which end on the thread pool before the batch takes them (tests).</summary>
    internal Task? ListingsOnTheirWay { get; private set; }

    /// <summary>
    /// Show All Files: a folder that isn't in its project, or is or is in node_modules, .vs or .idea (M57), whose contents
    /// on disk aren't listed until it's expanded (it has a "Loading…" row, so it can be). Not in the folder view, which
    /// Show All Files can stay on in: its folders carry their projects too, and its node_modules had a "Loading…" row
    /// after its packages, and listed its project when expanded (M57).
    /// </summary>
    private bool IsUnlisted(ExplorerNode node) =>
        !_rootIsFolderView && node is { Kind: ExplorerNodeKind.Folder, Path: { } path, ProjectPath: { } project }
        && _diskFolders.TryGetValue(project, out var folders) && !folders.Contains(path)
        && (node.IsExcluded || SolutionTree.IsInOnDemandFolder(path, System.IO.Path.GetDirectoryName(project)!));

    /// <summary>A disk listing arrived (tests wait for it).</summary>
    public event EventHandler? DiskLoaded;

    /// <summary>
    /// Filters the tree to the items whose names contain the text, with their parents (the search box, M29). The matches
    /// are found off the UI thread once typing pauses: in a large solution a short filter matches tens of thousands of
    /// nodes, and each keystroke walked every node and applied the rows one at a time on the UI thread (M56). Clearing it
    /// shows the tree again at once.
    /// </summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                if (_filter.Trim().Length == 0)
                {
                    CancelFilter();
                    Refresh();
                }
                else
                {
                    (_filterDebounce ??= new UiDebouncer(Refresh, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(600))).Request();
                }
            }
        }
    }

    private UiDebouncer? _filterDebounce;
    private int _filterGeneration; // a newer filter, tree or clear makes the matches being found stale
    private bool _filtering;

    /// <summary>Whether a filter's matches are still being found, or the folder view's listing for it is on its way (tests).</summary>
    internal bool IsFiltering => _filterDebounce?.IsPending == true || _filtering || _folderSearchLoading;

    /// <summary>The filter is cleared (or there's no tree to filter): its matches being found are stale, and the folder view's listing for it goes.</summary>
    private void CancelFilter()
    {
        Interlocked.Increment(ref _filterGeneration);
        _filtering = false;
        DropFolderSearch();
    }

    /// <summary>Clears the search box and shows the whole tree now (Sync, Track Active Item, Home).</summary>
    private void ClearFilter()
    {
        if (_filter.Length > 0)
        {
            _filter = string.Empty;
            OnPropertyChanged(nameof(Filter));
            CancelFilter();
            Refresh();
        }
    }

    /// <summary>Raised to focus the search box (Ctrl+;).</summary>
    public event EventHandler? SearchRequested;

    public void RequestSearch() => SearchRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Home: back to the solution node, collapsed to its projects.</summary>
    public void Home()
    {
        _syncWhenListed = null;
        ClearFilter();
        CollapseAll();
        Selected = Rows.FirstOrDefault();
        if (Selected is { } top)
        {
            RevealRequested?.Invoke(this, top); // the list only scrolls for reveals
        }
    }

    /// <summary>Sync with Active Document: selects the active document's file in the tree, at once.</summary>
    public void SyncWithActiveDocument()
    {
        _syncWhenListed = null; // before the refresh below, which would reveal an earlier Sync's file first
        RefreshIfQueued(); // a project that just loaded is in the tree to find
        if (_shell.ActiveEditor?.Model.Path is not { } path)
        {
            ShowNotInSolution();
            return;
        }

        if (FindByPath(path) is not null)
        {
            // The folder view's search box shows the whole folder (M57): the tree it goes back to may not have it listed yet.
            ClearFilter();
            if (FindByPath(path) is { } node)
            {
                Reveal(node.Id);
                return;
            }
        }

        if (ListDownTo(path))
        {
            ClearFilter();
            _syncWhenListed = path; // revealed once the listings that can bring it have come (ResolveSyncWhenListed)
            return;
        }

        ShowNotInSolution(path);
    }

    /// <summary>
    /// The active document (<paramref name="path"/>, if any) isn't in the tree: the solution's; in folder mode, which has no
    /// solution (M57), the folder's in the folder view or for a file outside the folder, else the loaded projects'.
    /// </summary>
    private void ShowNotInSolution(string? path = null)
    {
        var message = _shell.Solutions.Solution is not { IsFolder: true } folder
            ? Localizer.Get("msg.solutionExplorerViewModel.theActiveDocumentIsntInThe", "The active document isn't in the solution.")
            : IsFolderView || path is null || !IsInFolder(path, System.IO.Path.TrimEndingDirectorySeparator(folder.Directory))
                ? Localizer.Get("msg.solutionExplorerViewModel.theActiveDocumentIsntInTheFolder", "The active document isn't in the folder.")
                : Localizer.Get("msg.solutionExplorerViewModel.theActiveDocumentIsntInALoadedProject", "The active document isn't in a loaded project.");
        _shell.StatusBar.ShowMessage(message, TimeSpan.FromSeconds(4));
    }

    /// <summary>
    /// Show All Files: a file in a folder that isn't in its project shows once the folder is expanded and listed. This
    /// expands the folders down to <paramref name="path"/> in the project whose folder holds it most closely, and queues
    /// listings of every project whose folder holds it (two projects can share a folder); true while a listing of one of
    /// them is queued or on its way, the file may come with it.
    /// </summary>
    private bool ListDownTo(string path)
    {
        if (IsFolderView)
        {
            return ListFolderDownTo(path);
        }

        if (!_showAllFiles || System.IO.Path.GetDirectoryName(path) is not { } folder
            || ProjectsHolding(_shell.Solutions.Projects.Keys, [folder]) is not { Count: > 0 } projects)
        {
            return false;
        }

        var directory = System.IO.Path.GetDirectoryName(projects.MaxBy(p => System.IO.Path.GetDirectoryName(p)!.Length))!;
        var expanded = false;
        for (var down = folder; down.Length > directory.Length; down = System.IO.Path.GetDirectoryName(down)!)
        {
            expanded |= _expanded.Add("d:" + down);
        }

        if (expanded)
        {
            QueueDiskRefresh(projects);
        }

        return projects.Any(IsListing);
    }

    /// <summary>
    /// The folder view (M57): a file in a folder not listed yet shows once its folder and the folders above it are listed.
    /// This expands the folders from the workspace folder down to <paramref name="path"/>'s and queues listings of those not
    /// listed or just expanded, in one batch; true while one of them is queued or on its way. A file outside the workspace
    /// folder has none.
    /// </summary>
    private bool ListFolderDownTo(string path)
    {
        if (_folderRoot is not { } root || System.IO.Path.GetDirectoryName(path) is not { } folder || !IsInFolder(folder, root))
        {
            return false;
        }

        var chain = new List<string>();
        for (var down = folder; down.Length > root.Length; down = System.IO.Path.GetDirectoryName(down)!)
        {
            if (_expanded.Add("d:" + down) || !_folders.ContainsKey(down))
            {
                chain.Add(down);
            }
        }

        QueueFolderListings(chain);
        return IsListingFolderOf(path);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or below it.</summary>
    private static bool IsInFolder(string path, string root) =>
        SolutionService.PathComparer.Equals(path, root)
        || (path.Length > root.Length && path[root.Length] == System.IO.Path.DirectorySeparatorChar && SolutionService.PathComparer.Equals(path[..root.Length], root));

    /// <summary>Whether a listing that can bring <paramref name="path"/> is queued or on its way: its folder's, or one above it.</summary>
    private bool IsListingFolderOf(string path)
    {
        for (var up = System.IO.Path.GetDirectoryName(path); up is not null && _folderRoot is { } root && up.Length >= root.Length; up = System.IO.Path.GetDirectoryName(up))
        {
            if (IsFolderListing(up))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a listing that can bring <paramref name="path"/> into the tree is queued or on its way.</summary>
    private bool IsListingFor(string path) => IsFolderView
        ? IsListingFolderOf(path)
        : System.IO.Path.GetDirectoryName(path) is { } folder && ProjectsHolding(_shell.Solutions.Projects.Keys, [folder]).Any(IsListing);

    /// <summary>
    /// Sync with Active Document's file, once a listing brought it: selected as soon as it's in the tree, else the usual
    /// message once no listing that can bring it is queued or on its way. It waited as a node id, which never came for a
    /// file no listing has (in a folder the build host leaves out, a listing cut short or failed), with no message, and a
    /// refresh minutes later could jump to it (M56). Dropped once another document is active, or another reveal, Home or
    /// Collapse All came meanwhile: the user moved on.
    /// </summary>
    private void ResolveSyncWhenListed()
    {
        if (_syncWhenListed is not { } path)
        {
            return;
        }

        if (!SolutionService.PathComparer.Equals(_shell.ActiveEditor?.Model.Path, path))
        {
            _syncWhenListed = null;
        }
        else if (FindByPath(path) is { } node)
        {
            Reveal(node.Id); // (which drops it)
        }
        else if (Volatile.Read(ref _refreshQueued) == 1)
        {
            // The tree isn't built yet with the listings that came: its build looks again. Another folder's batch, started
            // in between, found it nowhere in the old tree and said it wasn't in the folder (M57).
        }
        else if (!IsListingFor(path))
        {
            _syncWhenListed = null;
            ShowNotInSolution(path);
        }
    }

    /// <summary>
    /// Track Active Item (M50, the classic IDE's): Solution Explorer selects each document as it becomes active. Off by
    /// default; the toolbar's toggle and <see cref="TrackActiveItemSetting"/> turn it on.
    /// </summary>
    public const string TrackActiveItemSetting = "solutionExplorer.trackActiveItem";

    public bool TrackActiveItem
    {
        get => _shell.Workbench.Settings.GetBoolean(TrackActiveItemSetting, false);
        set
        {
            if (value != TrackActiveItem)
            {
                _ = _shell.Workbench.Settings.SetAsync(TrackActiveItemSetting, value, Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// The active document changed: with Track Active Item on, its file is selected, without a message when it isn't in
    /// the solution, and the search box's filter kept when the file is among its matches. One reveal for a burst of
    /// activations (Ctrl+Tab held, Close All, opening many files, debugger steps), after the new editor's first frame and
    /// any queued refresh, and none while the explorer isn't shown: it catches up when shown (M56). Each reveal used to
    /// run inside the activation, so every tab passed through was revealed, and its folders expanded, before the next.
    /// </summary>
    public void OnActiveDocumentChanged()
    {
        if (!TrackActiveItem || _trackQueued)
        {
            return;
        }

        _trackQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(TrackActiveDocument, Avalonia.Threading.DispatcherPriority.Background);
    }

    private bool _trackQueued;
    private bool _trackWhenShown;
    private string? _trackWhenFound;
    private readonly ShownViews _views = new();

    private void TrackActiveDocument()
    {
        _trackQueued = false;
        _trackWhenFound = null;
        if (!TrackActiveItem || _shell.ActiveEditor?.Model.Path is not { } path)
        {
            return;
        }

        if (!_views.Any)
        {
            _trackWhenShown = true;
            return;
        }

        _trackWhenShown = false;
        if (FindByPath(path) is not { } node)
        {
            _trackWhenFound = path; // its project, or its folder, may not be in the tree yet: tried again after the next refresh
            ListDownTo(path);
            return;
        }

        if (_filter.Length > 0 && !Rows.Any(r => r.Id == node.Id))
        {
            ClearFilter();
            if (FindByPath(path) is not { } listed)
            {
                // The folder view's search box showed the whole folder; the tree it went back to hasn't the file listed (M57).
                _trackWhenFound = path;
                ListDownTo(path);
                return;
            }

            node = listed;
        }

        Reveal(node.Id);
    }

    /// <summary>Whether a view said the explorer is shown (tests).</summary>
    internal bool IsViewShown => _views.Any;

    /// <summary>
    /// A view tells whether it shows the explorer (attached and visible): Track Active Item does nothing while none does,
    /// and reveals the active document when one does again.
    /// </summary>
    public void SetViewShown(object view, bool shown)
    {
        if (_views.Set(view, shown) && shown && _trackWhenShown)
        {
            _trackWhenShown = false;
            OnActiveDocumentChanged();
        }
    }

    /// <summary>Raised when a row was revealed: the view scrolls it into view at once, selected or not before.</summary>
    public event EventHandler<ExplorerRowViewModel>? RevealRequested;

    // ------------------------------------------------------------------ the tree's index

    // The tree's nodes by id (with their parents) and its files, folders and projects by path, built on the first lookup
    // after a refresh: Track and Sync walked the whole tree twice for each activation, dependencies first (M56).
    private Dictionary<string, (ExplorerNode Node, ExplorerNode? Parent)>? _byId;
    private Dictionary<string, ExplorerNode>? _byPath;

    private void EnsureIndex()
    {
        if (_byId is not null || _root is null)
        {
            return;
        }

        var byId = new Dictionary<string, (ExplorerNode, ExplorerNode?)>(StringComparer.Ordinal);
        var byPath = new Dictionary<string, ExplorerNode>(SolutionService.PathComparer);
        var stack = new Stack<(ExplorerNode Node, ExplorerNode? Parent)>();
        stack.Push((_root, null));
        while (stack.Count > 0)
        {
            var (node, parent) = stack.Pop();
            byId.TryAdd(node.Id, (node, parent));

            // A project's file is its node (the classic IDE's); its dependencies are never revealed by path, and a
            // project reference carries the referenced project's path.
            if (node is { Kind: ExplorerNodeKind.Project or ExplorerNodeKind.File or ExplorerNodeKind.Folder or ExplorerNodeKind.SolutionItem, Path: { } path })
            {
                byPath.TryAdd(path, node);
            }

            if (node.Kind == ExplorerNodeKind.Dependencies)
            {
                continue; // thousands of transitive packages per project: found by walking their project's when asked for
            }

            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push((node.Children[i], node)); // tree order, so the first node with a path wins
            }
        }

        _byId = byId;
        _byPath = byPath;
    }

    /// <summary>The file's, folder's or project's node with <paramref name="path"/>, or null.</summary>
    private ExplorerNode? FindByPath(string path)
    {
        EnsureIndex();
        return _byPath?.GetValueOrDefault(path);
    }

    /// <summary>The nodes from the root down to the node with <paramref name="id"/>, or null.</summary>
    private List<ExplorerNode>? PathTo(string id)
    {
        EnsureIndex();
        if (_byId is null)
        {
            return null;
        }

        if (!_byId.TryGetValue(id, out var entry))
        {
            // Under a project's dependencies: walk that project's subtree, the only one that can hold it.
            foreach (var (_, (node, _)) in _byId)
            {
                if (node.Kind == ExplorerNodeKind.Dependencies && Path(node, id) is { } below)
                {
                    var path = PathTo(node.Id)!;
                    path.AddRange(below.Skip(1));
                    return path;
                }
            }

            return null;
        }

        var chain = new List<ExplorerNode> { entry.Node };
        for (var parent = entry.Parent; parent is not null; parent = _byId[parent.Id].Parent)
        {
            chain.Insert(0, parent);
        }

        return chain;
    }

    public bool HasSolution => _shell.Solutions.IsOpen;

    public ExplorerRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                SetSelectionContext(value);
            }
        }
    }

    /// <summary>The context keys that describe the selected item, which commands' enablement and the context menu read.</summary>
    private void SetSelectionContext(ExplorerRowViewModel? value)
    {
        _shell.Workbench.Context.Set("explorerItemKind", value is null ? null : KindKey(value.Node.Kind));
        _shell.Workbench.Context.Set("explorerItemIsProjectReference", value?.Node is { Kind: ExplorerNodeKind.Dependency } n && n.Id.Contains("|project|", StringComparison.Ordinal));
        _shell.Workbench.Context.Set("explorerProjectUnloaded", value?.Node.IsUnloaded == true);
        _shell.Workbench.Context.Set("explorerProjectNonSdkStyle", value?.Node is { Kind: ExplorerNodeKind.Project, ProjectPath: { } nonSdk } && _shell.Solutions.Projects.TryGetValue(nonSdk, out var nonSdkInfo) && nonSdkInfo.IsNonSdkStyle);
        _shell.Workbench.Context.Set("explorerItemName", value?.Node is { Kind: ExplorerNodeKind.File, Path: { } file } ? System.IO.Path.GetFileName(file) : null);
        _shell.Workbench.Context.Set("explorerItemExcluded", value?.Node.IsExcluded == true);
        _shell.Workbench.Context.Set("explorerItemLinked", value?.Node.IsLinked == true);
        _shell.Workbench.Context.Set("explorerItemInProject", value?.Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, ProjectPath: not null, IsExcluded: false } n2 && !SolutionTree.IsProjectFile(n2.Path ?? string.Empty) && !IsFolderView);
        _shell.Workbench.Context.Set("explorerItemOrdered", value?.Node is { Kind: ExplorerNodeKind.File, Path: { } ordered, ProjectPath: { } orderedProject } && SolutionTree.IsOrdered(orderedProject)
            && _shell.Solutions.Projects.TryGetValue(orderedProject, out var orderedInfo) && CompileOrder.Of(orderedInfo).Contains(ordered, SolutionService.PathComparer));
        _shell.Workbench.Context.Set("explorerItemIsProjectFile", value?.Node is { Kind: ExplorerNodeKind.File, Path: { } projectFile } && SolutionTree.IsProjectFile(projectFile));
        _shell.Workbench.Context.Set("explorerProjectLoaded", value?.Node is { Kind: ExplorerNodeKind.File, Path: { } loadedFile } && _shell.Solutions.Solution?.Projects.Any(p => SolutionService.PathComparer.Equals(p.Path, loadedFile)) == true);
    }

    public ExplorerNode? Root => _root;

    /// <summary>Marks the startup project's row.</summary>
    public void SetStartupProject(string? projectPath)
    {
        _startupProject = projectPath;
        MarkStartupProject();
    }

    private void MarkStartupProject()
    {
        foreach (var row in Rows)
        {
            row.IsStartup = row.Node.Kind == ExplorerNodeKind.Project && _startupProject is not null
                            && SolutionService.PathComparer.Equals(row.Node.Path, _startupProject);
        }
    }

    public static string KindKey(ExplorerNodeKind kind) => kind switch
    {
        ExplorerNodeKind.SolutionFolder => "solutionFolder",
        ExplorerNodeKind.SolutionItem => "solutionItem",
        ExplorerNodeKind.DependencyGroup => "dependencyGroup",
        _ => kind.ToString().ToLowerInvariant(),
    };

    // ------------------------------------------------------------------ tree

    /// <summary>The projects' subtrees of the last refresh: a refresh builds again only the projects that changed.</summary>
    private readonly SolutionTreeCache _treeCache = new();

    /// <summary>The projects whose subtrees the last refresh reused (tests).</summary>
    internal int ReusedProjects => _treeCache.Reused;

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshIfQueued, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Runs a queued refresh now (Sync looks for a file whose project just loaded); the queued job then does nothing.</summary>
    private void RefreshIfQueued()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 0) == 1)
        {
            Refresh();
        }
    }

    /// <summary>The tree was built again (tests count them: an expansion or a reveal doesn't).</summary>
    internal int Builds { get; private set; }

    public void Refresh()
    {
        var solutions = _shell.Solutions;
        if (IsFolderView && solutions.Solution is { } folder)
        {
            EnsureFolderRoot(folder);
            if (_filter.Trim() is { Length: > 0 } search)
            {
                // The search box: the whole folder's tree, built and filtered off the UI thread (M57). Until its listing has
                // come, the folders listed are filtered.
                if (_folderSearch is { } whole)
                {
                    _ = FilterAsync(null, search, null, () => SolutionTree.FolderView(folder, whole));
                    return;
                }

                _ = LoadFolderSearchAsync();
            }
        }

        SetRoot(solutions.Solution is { } solution
            ? SolutionTree.Build(solution, solutions.Projects, WithAnalyzersFound(solutions.CompilerInputs), new ExplorerOptions
            {
                DiskEntries = _showAllFiles ? _disk : null,
                FolderListings = IsFolderView ? _folders : null,
            }, _treeCache)
            : null);
        OnPropertyChanged(nameof(HasSolution));
        var reveal = TakePendingReveal();
        if (_root is not null && _filter.Trim() is { Length: > 0 } filter)
        {
            _ = FilterAsync(_root, filter, reveal);
            return;
        }

        CancelFilter();
        var rows = new List<(ExplorerNode Node, int Depth)>();
        if (_root is not null)
        {
            Flatten(_root, 0, rows);
        }

        Apply(rows);
        AfterRefresh(reveal);
    }

    private void SetRoot(ExplorerNode? root)
    {
        _root = root;
        _rootIsFolderView = _root is not null && IsFolderView;
        Builds++;
        _byId = null;
        _byPath = null;
        if (_root is null)
        {
            _withMembers.Clear();
            _loadingRows.Clear();
        }
    }

    /// <summary>A reveal that waited for its node, now in the tree: its ancestors expand in this build's rows.</summary>
    private string? TakePendingReveal()
    {
        if (_pendingReveal is not { } pending || PathTo(pending) is not { } path)
        {
            return null;
        }

        _pendingReveal = null;
        foreach (var ancestor in path.SkipLast(1))
        {
            _expanded.Add(ancestor.Id);
        }

        return pending;
    }

    private void AfterRefresh(string? reveal)
    {
        if (reveal is not null && Rows.FirstOrDefault(r => r.Id == reveal) is { } row)
        {
            Selected = row;
            RevealRequested?.Invoke(this, row);
        }

        if (_trackWhenFound is { } path && SolutionService.PathComparer.Equals(_shell.ActiveEditor?.Model.Path, path) && FindByPath(path) is not null)
        {
            _trackWhenFound = null;
            OnActiveDocumentChanged();
        }

        ResolveSyncWhenListed();
    }

    /// <summary>
    /// A filter's matches, found off the UI thread and shown with one reset, unless the filter or the tree changed meanwhile.
    /// With <paramref name="build"/> (the folder view's search box), the tree is built there too, and taken with the matches.
    /// </summary>
    private async Task FilterAsync(ExplorerNode? root, string filter, string? reveal, Func<ExplorerNode>? build = null)
    {
        var generation = Interlocked.Increment(ref _filterGeneration);
        _filtering = true;
        bool Stale() => Volatile.Read(ref _filterGeneration) != generation;
        ExplorerNode tree;
        List<(ExplorerNode Node, int Depth)> rows;
        try
        {
            (tree, rows) = await Task.Run(() =>
            {
                var built = root ?? build!();
                var matches = new List<(ExplorerNode, int)>();
                FlattenMatching(built, 0, filter, matches, Stale);
                return (built, matches);
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Stale() || (root is not null && !ReferenceEquals(_root, root)) || _filter.Trim() != filter)
        {
            return;
        }

        if (root is null)
        {
            SetRoot(tree);
            reveal = TakePendingReveal();
        }

        _filtering = false;
        var selectedId = _selected?.Id;
        Rows.Reset(rows.Select(r => new ExplorerRowViewModel(r.Node, r.Depth, true, ProjectInfoOf)));
        MarkStartupProject();
        Selected = selectedId is null ? null : Rows.FirstOrDefault(r => r.Id == selectedId);
        AfterRefresh(reveal);
    }

    private void Flatten(ExplorerNode node, int depth, List<(ExplorerNode, int)> rows)
    {
        if (_seen.Add(node.Id) && node.Kind is ExplorerNodeKind.Solution or ExplorerNodeKind.SolutionFolder)
        {
            _expanded.Add(node.Id);
        }

        node = WithMembers(node);

        rows.Add((node, depth));
        if (_expanded.Contains(node.Id))
        {
            foreach (var child in node.Children)
            {
                Flatten(child, depth + 1, rows);
            }
        }
    }

    /// <summary>The nodes whose names contain <paramref name="filter"/>, with their ancestors, all expanded.</summary>
    private static bool FlattenMatching(ExplorerNode node, int depth, string filter, List<(ExplorerNode, int)> rows, Func<bool> stale)
    {
        if (stale())
        {
            throw new OperationCanceledException();
        }

        var at = rows.Count;
        var any = false;
        foreach (var child in node.Children)
        {
            any |= FlattenMatching(child, depth + 1, filter, rows, stale);
        }

        // A listing's cut row shows with the matches: what it cut wasn't searched (M57).
        if (any || depth == 0 || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (node.Kind == ExplorerNodeKind.Message && node.Id.EndsWith(SolutionTree.CutSuffix, StringComparison.Ordinal)))
        {
            rows.Insert(at, (node, depth));
            if (node is { Kind: ExplorerNodeKind.Folder, IsUnlisted: true })
            {
                // The folder view: a folder the search's listing didn't go into (node_modules, or one past its limit) has
                // nothing to show, and showed as an empty folder (M57).
                rows.Add((new ExplorerNode(node.Id + "|unsearched", ExplorerNodeKind.Message, Localizer.Get("msg.solutionExplorerViewModel.notSearched",
                    "Not searched. Clear the search box and expand the folder to see what is in it.")) { Icon = "dot", IsMuted = true }, depth + 1));
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Updates <see cref="Rows"/> in place: only the changed middle part is replaced, with one removal and one insertion,
    /// so scrolling is kept. Row by row, each insertion above the selected row made Avalonia queue a scroll to it: the
    /// list walked through every inserted row, a frame each, with input waiting (M56).
    /// </summary>
    private void Apply(List<(ExplorerNode Node, int Depth)> rows)
    {
        var selectedId = _selected?.Id;
        var prefix = 0;
        while (prefix < rows.Count && prefix < Rows.Count && Rows[prefix].Id == rows[prefix].Node.Id && Rows[prefix].Depth == rows[prefix].Depth)
        {
            Rows[prefix].Node = rows[prefix].Node;
            Rows[prefix].IsExpanded = _expanded.Contains(rows[prefix].Node.Id);
            prefix++;
        }

        var suffix = 0;
        while (suffix < rows.Count - prefix && suffix < Rows.Count - prefix
               && Rows[Rows.Count - 1 - suffix].Id == rows[rows.Count - 1 - suffix].Node.Id
               && Rows[Rows.Count - 1 - suffix].Depth == rows[rows.Count - 1 - suffix].Depth)
        {
            var row = Rows[Rows.Count - 1 - suffix];
            row.Node = rows[rows.Count - 1 - suffix].Node;
            row.IsExpanded = _expanded.Contains(row.Id);
            suffix++;
        }

        Rows.RemoveRange(prefix, Rows.Count - suffix - prefix);
        Rows.InsertRange(prefix, rows.Skip(prefix).Take(rows.Count - suffix - prefix).Select(r => new ExplorerRowViewModel(r.Node, r.Depth, _expanded.Contains(r.Node.Id), ProjectInfoOf)).ToList());

        MarkStartupProject();
        var before = _selected;
        Selected = Rows.FirstOrDefault(r => r.Id == selectedId) ?? (selectedId is null ? null : Selected);
        if (Selected is not null && !Rows.Contains(Selected))
        {
            Selected = null;
        }

        // The same row with a new node (excluded or included under Show All Files, unloaded): its keys follow the node.
        if (_selected is not null && ReferenceEquals(_selected, before))
        {
            SetSelectionContext(_selected);
        }
    }

    public void SetExpanded(ExplorerRowViewModel row, bool expanded)
    {
        if (expanded && row.Node is { Kind: ExplorerNodeKind.File, Path: { } file } && ShowsMembers(row.Node) && !_members.ContainsKey(file))
        {
            _ = LoadMembersAsync(file);
        }
        else if (expanded && IsAnalyzer(row.Node) && !_generated.ContainsKey(row.Node.ProjectPath!))
        {
            _ = LoadGeneratedAsync(row.Node.ProjectPath!);
        }
        else if (expanded && IsUnlisted(row.Node))
        {
            // Its projects' listings go into it now (it's in the expanded ones by then): each project whose folder holds it
            // has it, under the same id, and expands with it (two projects can share a folder).
            QueueDiskRefresh(ProjectsHolding(_shell.Solutions.Projects.Keys, [row.Node.Path!]));
        }

        if (!row.HasChildren || (expanded ? !_expanded.Add(row.Id) : !_expanded.Remove(row.Id)))
        {
            return;
        }

        if (expanded && IsFolderView && row.Node is { Kind: ExplorerNodeKind.Folder, Path: { } folder })
        {
            // The folder view (M57): the folder is listed now, and so are the listed folders shown again below it, whose
            // rows show at once from their last listings while the new ones come.
            QueueFolderListings([folder, .. _folders.Keys.Where(f => f.Length > folder.Length && IsInFolder(f, folder) && IsShownFolder(f))]);
        }

        // Only the row's own rows change: the tree isn't built again (a filter shows every match expanded anyway).
        if (_filter.Trim().Length > 0 || Rows.IndexOf(row) < 0)
        {
            Refresh();
        }
        else if (expanded)
        {
            ExpandRows(row);
        }
        else
        {
            CollapseRows(row);
        }
    }

    /// <summary>Shows an expanded row's descendants (with the expansion they had) right below it, with one insertion.</summary>
    private void ExpandRows(ExplorerRowViewModel row)
    {
        var rows = new List<(ExplorerNode Node, int Depth)>();
        foreach (var child in row.Node.Children)
        {
            Flatten(child, row.Depth + 1, rows);
        }

        row.IsExpanded = true;
        Rows.InsertRange(Rows.IndexOf(row) + 1, rows.Select(r => new ExplorerRowViewModel(r.Node, r.Depth, _expanded.Contains(r.Node.Id), ProjectInfoOf)).ToList());
        MarkStartupProject();
    }

    /// <summary>Hides a collapsed row's descendants, with one removal; the selection goes with a hidden row.</summary>
    private void CollapseRows(ExplorerRowViewModel row)
    {
        var index = Rows.IndexOf(row);
        var end = index + 1;
        while (end < Rows.Count && Rows[end].Depth > row.Depth)
        {
            end++;
        }

        row.IsExpanded = false;
        var selectionHidden = _selected is not null && Rows.IndexOf(_selected) is var at && at > index && at < end;
        Rows.RemoveRange(index + 1, end - index - 1);
        if (selectionHidden)
        {
            Selected = null;
        }
    }

    public void Toggle(ExplorerRowViewModel row) => SetExpanded(row, !_expanded.Contains(row.Id));

    public void CollapseAll()
    {
        _syncWhenListed = null;
        _expanded.RemoveWhere(id => id != "solution");
        Refresh();
    }

    /// <summary>
    /// The expansion to keep with the solution (ADR 0070): the expanded nodes, and the solution and solution folders
    /// the user collapsed (they start expanded). Node ids are stable across sessions: <c>p:</c>, <c>d:</c> and <c>f:</c>
    /// with the item's path, <c>sf:</c> with the solution folder's id in the solution file.
    /// </summary>
    public (IReadOnlyList<string> Expanded, IReadOnlyList<string> Collapsed) CaptureExpansion() =>
        ([.. _expanded.Order(StringComparer.Ordinal)],
         [.. _seen.Where(id => (id == "solution" || id.StartsWith("sf:", StringComparison.Ordinal)) && !_expanded.Contains(id)).Order(StringComparer.Ordinal)]);

    /// <summary>
    /// Restores the expansion kept with the solution (before its tree loads): nodes expand as they appear, so a
    /// project's folders expand once the project has loaded.
    /// </summary>
    public void LoadExpansion(IEnumerable<string>? expanded, IEnumerable<string>? collapsed)
    {
        _expanded.Clear();
        _seen.Clear();
        _expanded.UnionWith(expanded ?? []);
        _seen.UnionWith(collapsed ?? []);
        Refresh();
    }

    /// <summary>Reveals <paramref name="id"/> as soon as the node exists (it may come with the next model update).</summary>
    public void RevealWhenAvailable(string id)
    {
        _pendingReveal = id;
        Reveal(id);
    }

    /// <summary>
    /// Selects (and reveals) the node with <paramref name="id"/>. Its collapsed ancestors expand: the topmost one's rows
    /// are inserted below it, with one insertion, instead of the tree being built again and every row compared (M56).
    /// </summary>
    public void Reveal(string id)
    {
        if (PathTo(id) is not { } path)
        {
            // The folder view (M57): a file or folder in a folder not listed yet is revealed once the folders down to it are.
            if (IsFolderView && id.Length > 2 && id[0] is 'f' or 'd' && id[1] == ':' && ListFolderDownTo(id[2..]))
            {
                _pendingReveal = id;
            }

            return;
        }

        _syncWhenListed = null; // a newer reveal (Track, a command, a new file) wins over a Sync still waiting for its listing
        if (_pendingReveal == id)
        {
            _pendingReveal = null;
        }

        ExplorerNode? topmost = null;
        var relisted = new List<string>();
        foreach (var ancestor in path.SkipLast(1))
        {
            if (_expanded.Add(ancestor.Id))
            {
                topmost ??= ancestor;
                if (IsFolderView && ancestor is { Kind: ExplorerNodeKind.Folder, IsUnlisted: false, Path: { } folder })
                {
                    relisted.Add(folder); // expanded again: its last listing shows at once, a new one refreshes it
                }
            }
        }

        QueueFolderListings(relisted);

        var top = topmost is null ? null : Rows.FirstOrDefault(r => r.Id == topmost.Id);
        if (_filter.Trim().Length > 0)
        {
            // The filtered rows show every match with its ancestors; the expansion shows when the filter is cleared.
        }
        else if (top is { IsExpanded: false })
        {
            ExpandRows(top);
        }
        else if (topmost is not null || !Rows.Any(r => r.Id == id))
        {
            Refresh();
        }

        Selected = Rows.FirstOrDefault(r => r.Id == id);
        if (Selected is { } revealed && revealed.Id == id)
        {
            RevealRequested?.Invoke(this, revealed);
        }
    }

    private static List<ExplorerNode>? Path(ExplorerNode node, string id)
    {
        if (node.Id == id)
        {
            return [node];
        }

        foreach (var child in node.Children)
        {
            if (Path(child, id) is { } path)
            {
                path.Insert(0, node);
                return path;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ members under files (deferred from M12a)

    public const string ShowMembersSetting = "solutionExplorer.showMembers";

    private readonly Dictionary<string, IReadOnlyList<ExplorerNode>> _members = new(SolutionService.PathComparer);
    private readonly HashSet<string> _loadingMembers = new(SolutionService.PathComparer);

    /// <summary>A C# or VB file in a project: it expands into its types and members.</summary>
    private bool ShowsMembers(ExplorerNode node) =>
        node is { Kind: ExplorerNodeKind.File, Path: { } path, ProjectPath: not null, IsExcluded: false }
        && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) | path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
        && _shell.Workbench.Settings.GetBoolean(ShowMembersSetting, true);

    /// <summary>
    /// A code file's node with its members after its own children (files nested under it): the cached ones, else a
    /// "loading" row that asks for them when the file is expanded.
    /// </summary>
    private ExplorerNode WithMembers(ExplorerNode node)
    {
        if (IsAnalyzer(node))
        {
            return WithGeneratedFiles(node);
        }

        if (node.IsUnlisted)
        {
            // The folder view (M57): expanded (by the user, a reveal, the expansion restored) and not listed yet, it's
            // listed now. It shows "Loading…" until its listing comes.
            var folder = node.Kind == ExplorerNodeKind.Solution ? _folderRoot : node.Path;
            if (folder is not null && (node.Kind == ExplorerNodeKind.Solution || _expanded.Contains(node.Id)) && !IsFolderListing(folder) && IsShownFolder(folder))
            {
                QueueFolderListings([folder]);
            }

            return With(node, LoadingRow(node, "|listing"));
        }

        if (IsUnlisted(node))
        {
            if (_expanded.Contains(node.Id) && !IsListing(node.ProjectPath!))
            {
                // Expanded with another project's copy of a shared folder (one id for both), or by a reveal: its listing
                // goes into it now, as an expansion of this row would. It showed "Loading…" until something else listed it.
                QueueDiskRefresh([node.ProjectPath!]);
            }

            return With(node, LoadingRow(node, "|listing"));
        }

        if (!ShowsMembers(node))
        {
            return node;
        }

        var path = node.Path!;
        if (!_members.TryGetValue(path, out var members))
        {
            if (_expanded.Contains(node.Id))
            {
                _ = LoadMembersAsync(path);
            }

            members = LoadingRow(node, "|members-loading");
        }

        return members.Count == 0 && node.Children.Count == 0 ? node : With(node, members);
    }

    /// <summary>The node's "Loading…" row, the same one each time.</summary>
    private IReadOnlyList<ExplorerNode> LoadingRow(ExplorerNode node, string suffix)
    {
        if (!_loadingRows.TryGetValue(node.Id, out var row))
        {
            _loadingRows[node.Id] = row = [new ExplorerNode(node.Id + suffix, ExplorerNodeKind.Message, Localizer.Get("msg.solutionExplorer.loadingMembers", "Loading…")) { Icon = "dot", ProjectPath = node.ProjectPath }];
        }

        return row;
    }

    /// <summary>The node with <paramref name="members"/> after its own children.</summary>
    private ExplorerNode With(ExplorerNode node, IReadOnlyList<ExplorerNode> members)
    {
        // The same node and members give the same result, so an unchanged row keeps its node and a refresh raises nothing
        // for it: each refresh gave every code file's row a new node, and its thirteen property changes (M56).
        var path = node.Path!;
        if (_withMembers.TryGetValue(path, out var known) && ReferenceEquals(known.Node, node) && ReferenceEquals(known.Members, members))
        {
            return known.Result;
        }

        var result = node with { Children = [.. node.Children, .. members] };
        _withMembers[path] = (node, members, result);
        return result;
    }

    private readonly Dictionary<string, (ExplorerNode Node, IReadOnlyList<ExplorerNode> Members, ExplorerNode Result)> _withMembers = new(SolutionService.PathComparer);
    private readonly Dictionary<string, IReadOnlyList<ExplorerNode>> _loadingRows = new(StringComparer.Ordinal);

    /// <summary>The file's symbols from its language service, as member nodes; then the tree again.</summary>
    private async Task LoadMembersAsync(string path)
    {
        if (!_loadingMembers.Add(path))
        {
            return;
        }

        try
        {
            var language = path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ? ShellHost.VisualBasicLanguageId : ShellHost.CSharpLanguageId;
            var client = _shell.Languages.RunningClient(language) ?? _shell.Languages.RunningClient(ShellHost.CSharpLanguageId);
            IReadOnlyList<LanguageServices.Protocol.DocumentSymbol> symbols = client is null
                ? []
                : await client.DocumentSymbolsAsync(LanguageServices.Protocol.DocumentUris.FromPath(path), CancellationToken.None).ConfigureAwait(true);
            _members[path] = MemberNodes(path, symbols);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or System.Text.Json.JsonException or OperationCanceledException)
        {
            _members[path] = [];
        }
        finally
        {
            _loadingMembers.Remove(path);
        }

        QueueRefresh();
    }

    /// <summary>Member nodes for a file's symbols: types and their members, namespaces left out (their types take their place).</summary>
    internal static IReadOnlyList<ExplorerNode> MemberNodes(string path, IReadOnlyList<LanguageServices.Protocol.DocumentSymbol> symbols)
    {
        var fileId = "member|" + path;
        IReadOnlyList<ExplorerNode> Nodes(IEnumerable<LanguageServices.Protocol.DocumentSymbol> level, string parent) =>
        [
            .. level.SelectMany(symbol => symbol.Kind is LanguageServices.Protocol.SymbolKind.Namespace or LanguageServices.Protocol.SymbolKind.Package or LanguageServices.Protocol.SymbolKind.Module
                ? Nodes(symbol.Children ?? [], parent)
                : [new ExplorerNode($"{parent}|{symbol.Name}@{symbol.SelectionRange.Start.Line}", ExplorerNodeKind.Member, symbol.Name)
                {
                    Path = path,
                    Icon = SymbolNodeViewModel.IconFor(symbol.Kind),
                    Detail = symbol.Detail,
                    Position = (symbol.SelectionRange.Start.Line, symbol.SelectionRange.Start.Character),
                    Children = Nodes(symbol.Children ?? [], $"{parent}|{symbol.Name}"),
                }]),
        ];

        return Nodes(symbols, fileId);
    }

    /// <summary>
    /// A file changed (an edit, a save, a change on disk): its members, and generated files, are read again when shown,
    /// once typing pauses. Each keystroke cleared them and built the tree again, twice with members or generated files
    /// shown (M56).
    /// </summary>
    public void OnFileChanged(string path)
    {
        _changedFiles.Add(path);
        (_fileChanges ??= new UiDebouncer(ApplyFileChanges, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2))).Request();
    }

    private readonly HashSet<string> _changedFiles = new(SolutionService.PathComparer);
    private UiDebouncer? _fileChanges;

    private void ApplyFileChanges()
    {
        var generated = _generated.Count > 0;
        _generated.Clear();
        var members = false;
        foreach (var path in _changedFiles)
        {
            members |= _members.Remove(path);
        }

        _changedFiles.Clear();
        if (members || generated)
        {
            QueueRefresh();
        }
    }

    // ------------------------------------------------------------------ generated sources under analyzers (deferred from M12a)

    private readonly Dictionary<string, IReadOnlyList<LanguageServices.Protocol.GeneratedDocumentInfo>> _generated = new(SolutionService.PathComparer);
    private readonly HashSet<string> _loadingGenerated = new(SolutionService.PathComparer);

    private static bool IsAnalyzer(ExplorerNode node) =>
        node is { Kind: ExplorerNodeKind.Dependency, ProjectPath: not null } && node.Id.Contains("|" + ReferenceKinds.Analyzer + "|", StringComparison.Ordinal);

    /// <summary>An analyzer with the files its generators made in the project (once known; "Loading…" until then).</summary>
    private ExplorerNode WithGeneratedFiles(ExplorerNode node)
    {
        var project = node.ProjectPath!;
        if (!_generated.TryGetValue(project, out var documents))
        {
            if (_expanded.Contains(node.Id))
            {
                _ = LoadGeneratedAsync(project);
            }

            return node with { Children = [new ExplorerNode(node.Id + "|generated-loading", ExplorerNodeKind.Message, Localizer.Get("msg.solutionExplorer.loadingMembers", "Loading…")) { Icon = "dot", ProjectPath = project }] };
        }

        var files = documents.Where(d => string.Equals(d.Assembly, node.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return files.Count == 0 ? node : node with
        {
            Children = [.. files.Select(d => new ExplorerNode($"{node.Id}|{d.Generator}|{d.HintName}", ExplorerNodeKind.GeneratedFile, d.HintName)
            {
                Path = d.Uri,
                ProjectPath = project,
                Icon = Controls.FileIcons.ForFile(d.HintName),
                Detail = d.Generator.Split('.')[^1],
            })],
        };
    }

    private async Task LoadGeneratedAsync(string project)
    {
        if (!_loadingGenerated.Add(project))
        {
            return;
        }

        try
        {
            _generated[project] = _shell.Languages.RunningClient(ShellHost.CSharpLanguageId) is { } client
                ? (await client.GeneratedDocumentsAsync(project, CancellationToken.None).ConfigureAwait(true)).Documents
                : [];
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or System.Text.Json.JsonException or OperationCanceledException)
        {
            _generated[project] = [];
        }
        finally
        {
            _loadingGenerated.Remove(project);
        }

        QueueRefresh();
    }

    // ------------------------------------------------------------------ analyzers a build made

    // By project: the inputs the build host sent with analyzers marked missing (missingAnalyzers), and the same inputs
    // without the ones a build has made since, which the tree takes while the build host's are the project's.
    private readonly Dictionary<string, (CompilerInputsInfo Sent, CompilerInputsInfo Inputs)> _analyzersFound = new(SolutionService.PathComparer);

    /// <summary>
    /// After a build, the analyzers the build host marked missing are looked for again, off the UI thread and through the
    /// file system of the build host's paths. A build doesn't run design-time builds again, so a source generator built
    /// from the solution (on a fresh clone, an analyzer another project of it outputs) kept its warning until the project's
    /// next one (M56). Each look starts from the build host's list, not the last one's result, so a Clean that deletes what
    /// a build made shows it missing again.
    /// </summary>
    private void FindMissingAnalyzers()
    {
        var marked = _shell.Solutions.CompilerInputs.Values.Where(sent => sent.MissingAnalyzers is { Length: > 0 }).ToList();
        if (marked.Count > 0)
        {
            _ = FindMissingAnalyzersAsync(marked);
        }
    }

    private async Task FindMissingAnalyzersAsync(List<CompilerInputsInfo> marked)
    {
        var found = await Task.Run(async () =>
        {
            var results = new List<(CompilerInputsInfo Sent, string[]? Missing)>();
            foreach (var sent in marked)
            {
                var missing = new List<string>();
                foreach (var analyzer in sent.MissingAnalyzers!)
                {
                    // A relative path (an Analyzer item's own) is the project folder's, as the build host takes it; one that
                    // can't be looked at (a remote window's connection lost) stays missing.
                    try
                    {
                        var path = System.IO.Path.GetFullPath(analyzer, System.IO.Path.GetDirectoryName(sent.Path)!);
                        if (!await Codalus.Workbench.FileSystem.FileSystems.FileExistsAsync(path, CancellationToken.None).ConfigureAwait(false))
                        {
                            missing.Add(analyzer);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
                    {
                        missing.Add(analyzer);
                    }
                }

                results.Add((sent, missing.Count == 0 ? null : [.. missing]));
            }

            return results;
        }).ConfigureAwait(true);

        var changed = false;
        foreach (var (sent, missing) in found)
        {
            if (!ReferenceEquals(_shell.Solutions.CompilerInputs.GetValueOrDefault(sent.Path), sent))
            {
                continue; // the build host sent newer inputs meanwhile: it marked them itself
            }

            if (missing?.Length == sent.MissingAnalyzers!.Length)
            {
                changed |= _analyzersFound.Remove(sent.Path); // all still missing, or again: the build host's marks stand
            }
            else if (!_analyzersFound.TryGetValue(sent.Path, out var known) || !ReferenceEquals(known.Sent, sent)
                     || !(known.Inputs.MissingAnalyzers ?? []).SequenceEqual(missing ?? []))
            {
                _analyzersFound[sent.Path] = (sent, sent with { MissingAnalyzers = missing }); // a new instance only when it changed, for the tree's cache
                changed = true;
            }
        }

        if (changed)
        {
            QueueRefresh();
        }
    }

    /// <summary>The build host's inputs, with the analyzers a build made since unmarked (the same instances each time, for the tree's cache).</summary>
    private IReadOnlyDictionary<string, CompilerInputsInfo> WithAnalyzersFound(IReadOnlyDictionary<string, CompilerInputsInfo> sent)
    {
        if (_analyzersFound.Count == 0)
        {
            return sent;
        }

        var inputs = new Dictionary<string, CompilerInputsInfo>(sent, SolutionService.PathComparer);
        foreach (var (project, found) in _analyzersFound.ToList())
        {
            if (inputs.TryGetValue(project, out var current) && ReferenceEquals(current, found.Sent))
            {
                inputs[project] = found.Inputs;
            }
            else
            {
                _analyzersFound.Remove(project); // newer inputs, which the build host marked itself
            }
        }

        return inputs;
    }

    // ------------------------------------------------------------------ activation

    /// <summary>A single click on a file shows it in the preview tab, keeping focus here.</summary>
    public async Task PreviewAsync(ExplorerRowViewModel row)
    {
        if (row.Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.SolutionItem, Path: { } path } && File.Exists(path))
        {
            await _shell.OpenFileAsync(path, preview: true).ConfigureAwait(true);
        }
        else if (row.Node is { Kind: ExplorerNodeKind.Member, Path: { } member, Position: { } at })
        {
            await _shell.OpenFileAsync(member, preview: true, new LanguageServices.Protocol.Position(at.Line, at.Character)).ConfigureAwait(true);
        }
        else if (row.Node is { Kind: ExplorerNodeKind.GeneratedFile, Path: { } generated })
        {
            await _shell.OpenVirtualAsync(generated, null).ConfigureAwait(true);
        }
    }

    /// <summary>Enter or double-click: files open, containers expand or collapse, project references jump to the project.</summary>
    public async Task ActivateAsync(ExplorerRowViewModel row)
    {
        switch (row.Node)
        {
            case { Kind: ExplorerNodeKind.File or ExplorerNodeKind.SolutionItem, Path: { } path }:
                await _shell.OpenFileAsync(path).ConfigureAwait(true);
                break;
            case { Kind: ExplorerNodeKind.GeneratedFile, Path: { } generated }:
                await _shell.OpenVirtualAsync(generated, null).ConfigureAwait(true);
                break;
            case { Kind: ExplorerNodeKind.Member, Path: { } member, Position: { } at }:
                await _shell.OpenFileAsync(member, preview: false, new LanguageServices.Protocol.Position(at.Line, at.Character)).ConfigureAwait(true);
                break;
            case { Kind: ExplorerNodeKind.Dependency, Path: { } target } when target.EndsWith("proj", StringComparison.OrdinalIgnoreCase):
                Reveal("p:" + target);
                break;
            default:
                Toggle(row);
                break;
        }
    }

    // ------------------------------------------------------------------ operations

    private ExplorerNode? Node => _selected?.Node;

    /// <summary>The folder new items go into: the selected folder or project, or the selected file's folder.</summary>
    private string? TargetDirectory => Node switch
    {
        { Kind: ExplorerNodeKind.Solution, Path: { } root } when _shell.Solutions.Solution?.IsFolder == true => root,
        { Kind: ExplorerNodeKind.Folder, Path: { } folder } => folder,
        { Kind: ExplorerNodeKind.Project, Path: { } project } => System.IO.Path.GetDirectoryName(project),
        { Kind: ExplorerNodeKind.File, Path: { } file } => System.IO.Path.GetDirectoryName(file),
        _ => null,
    };

    public async Task NewFileAsync()
    {
        if (TargetDirectory is not { } directory)
        {
            return;
        }

        var name = await _shell.Dialogs.InputAsync(Localizer.Get("msg.solutionExplorerViewModel.newFile", "New File"), Localizer.Format("msg.solutionExplorerViewModel.createAFileIn", "Create a file in {0}.", Display(directory)), "NewFile.cs", n => ValidateName(directory, n), (0, 7)).ConfigureAwait(true);
        if (name is not null && await TryAsync(() => _shell.Solutions.CreateFileAsync(System.IO.Path.Combine(directory, name), Template(directory, name), CancellationToken.None)).ConfigureAwait(true) is { } result)
        {
            await _shell.OpenTextFileAsync(result.CreatedPaths[0], string.Empty).ConfigureAwait(true);
        }
    }

    public async Task NewFolderAsync()
    {
        if (TargetDirectory is not { } directory)
        {
            return;
        }

        var name = await _shell.Dialogs.InputAsync(Localizer.Get("msg.solutionExplorerViewModel.newFolder", "New Folder"), Localizer.Format("msg.solutionExplorerViewModel.createAFolderIn", "Create a folder in {0}.", Display(directory)), "NewFolder", n => ValidateName(directory, n)).ConfigureAwait(true);
        if (name is not null)
        {
            await TryAsync(() => _shell.Solutions.CreateFolderAsync(System.IO.Path.Combine(directory, name), CancellationToken.None)).ConfigureAwait(true);
        }
    }

    public async Task RenameAsync()
    {
        switch (Node)
        {
            case { Kind: ExplorerNodeKind.SolutionFolder, SolutionFolderId: { } id, Name: var current }:
            {
                var name = await _shell.Dialogs.InputAsync(Localizer.Get("msg.solutionExplorerViewModel.renameSolutionFolder", "Rename Solution Folder"), Localizer.Get("msg.solutionExplorerViewModel.newName", "New name:"), current, n => string.IsNullOrWhiteSpace(n) || n.IndexOfAny(['/', '\\']) >= 0 ? "Enter a name without slashes." : null).ConfigureAwait(true);
                if (name is not null && name != current)
                {
                    await TryAsync(() => _shell.Solutions.RenameSolutionFolderAsync(id, name, CancellationToken.None)).ConfigureAwait(true);
                }

                break;
            }

            case { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder or ExplorerNodeKind.Project, Path: { } path, IsLinked: false }:
            {
                var directory = System.IO.Path.GetDirectoryName(path)!;
                var current = System.IO.Path.GetFileName(path);
                var stem = Node.Kind == ExplorerNodeKind.Folder ? current.Length : System.IO.Path.GetFileNameWithoutExtension(current).Length;
                var name = await _shell.Dialogs.InputAsync(
                    Node.Kind == ExplorerNodeKind.Project ? "Rename Project File" : "Rename",
                    Localizer.Format("msg.solutionExplorerViewModel.newNameFor", "New name for '{0}':", current),
                    current,
                    n => n == current ? null : ValidateName(directory, n, current),
                    (0, stem)).ConfigureAwait(true);
                if (name is not null && name != current)
                {
                    await TryAsync(() => _shell.Solutions.RenameAsync(path, System.IO.Path.Combine(directory, name), CancellationToken.None)).ConfigureAwait(true);
                }

                break;
            }
        }
    }

    public async Task DeleteAsync()
    {
        switch (Node)
        {
            case { Kind: ExplorerNodeKind.SolutionFolder, SolutionFolderId: { } id, Name: var name }:
                if (await _shell.Dialogs.ConfirmAsync(Localizer.Get("msg.solutionExplorerViewModel.removeSolutionFolder", "Remove Solution Folder"), Localizer.Format("msg.solutionExplorerViewModel.removeTheSolutionFolderAndEverything", "Remove the solution folder '{0}' and everything in it from the solution? Projects stay on disk.", name), Localizer.Get("msg.solutionExplorerViewModel.remove", "Remove"), danger: true).ConfigureAwait(true))
                {
                    await TryAsync(() => _shell.Solutions.RemoveSolutionFolderAsync(id, CancellationToken.None)).ConfigureAwait(true);
                }

                break;
            case { Kind: ExplorerNodeKind.Project, Path: { } project }:
                await RemoveProjectAsync(project).ConfigureAwait(true);
                break;
            case { Kind: ExplorerNodeKind.Dependency, ProjectPath: { } owner, Path: { } target } when target.EndsWith("proj", StringComparison.OrdinalIgnoreCase):
                await TryAsync(() => _shell.Solutions.RemoveProjectReferenceAsync(owner, target, CancellationToken.None)).ConfigureAwait(true);
                break;
            case { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, Path: { } path, IsLinked: false, Name: var name }:
                if (await _shell.Dialogs.ConfirmAsync(Localizer.Get("msg.solutionExplorerViewModel.delete", "Delete"), Localizer.Format("msg.solutionExplorerViewModel.moveToThe", "Move '{0}' to the {1}?", name, TrashName), Localizer.Get("msg.solutionExplorerViewModel.delete", "Delete"), danger: true).ConfigureAwait(true))
                {
                    await TryAsync(() => _shell.Solutions.DeleteAsync([path], CancellationToken.None)).ConfigureAwait(true);
                }

                break;
        }
    }

    public async Task RemoveProjectAsync(string project)
    {
        if (await _shell.Dialogs.ConfirmAsync(Localizer.Get("msg.solutionExplorerViewModel.removeProject", "Remove Project"), Localizer.Format("msg.solutionExplorerViewModel.removeFromTheSolutionItsFiles", "Remove '{0}' from the solution? Its files stay on disk.", System.IO.Path.GetFileNameWithoutExtension(project)), Localizer.Get("msg.solutionExplorerViewModel.remove", "Remove")).ConfigureAwait(true))
        {
            await TryAsync(() => _shell.Solutions.RemoveProjectAsync(project, CancellationToken.None)).ConfigureAwait(true);
        }
    }

    private static string TrashName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    public void Cut() => _clipboard = Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, Path: { } path, IsLinked: false } ? ([path], true) : _clipboard;

    public void Copy() => _clipboard = Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, Path: { } path, IsLinked: false } ? ([path], false) : _clipboard;

    public bool CanPaste => _clipboard is not null;

    public async Task PasteAsync()
    {
        if (_clipboard is not { } clip || TargetDirectory is not { } directory)
        {
            return;
        }

        if (await TryAsync(() => _shell.Solutions.MoveAsync(clip.Paths, directory, copy: !clip.Cut, CancellationToken.None)).ConfigureAwait(true) is not null && clip.Cut)
        {
            _clipboard = null;
        }
    }

    /// <summary>The node being dragged (a linked file moves its link, not a file).</summary>
    private ExplorerNode? _dragSource;

    /// <summary>The view started dragging <paramref name="node"/>.</summary>
    public void BeginDrag(ExplorerNode node) => _dragSource = node;

    private static string? DropDirectory(ExplorerNode target) => target switch
    {
        { Kind: ExplorerNodeKind.Folder, Path: { } folder } => folder,
        { Kind: ExplorerNodeKind.Project, Path: { } project } => System.IO.Path.GetDirectoryName(project),
        { Kind: ExplorerNodeKind.File, Path: { } file, IsLinked: false } => System.IO.Path.GetDirectoryName(file),
        { Kind: ExplorerNodeKind.Solution, Path: { } root } when Directory.Exists(root) => root,
        _ => null,
    };

    /// <summary>
    /// Drag and drop: moves (or with Ctrl, copies) <paramref name="paths"/> into the folder or project of
    /// <paramref name="target"/>. Into another project, the files' items go with them (ADR 0033); a linked file moves (or
    /// copies) its link instead.
    /// </summary>
    public async Task DropAsync(IReadOnlyList<string> paths, ExplorerNode target, bool copy)
    {
        var directory = DropDirectory(target);
        var source = _dragSource;
        _dragSource = null;
        if (directory is null)
        {
            return;
        }

        if (source is { IsLinked: true, Path: { } linked, ProjectPath: { } from } && target.ProjectPath is { } to && paths.Count == 1 && SolutionService.PathComparer.Equals(paths[0], linked))
        {
            await TryAsync(async () =>
            {
                if (SolutionService.PathComparer.Equals(from, to))
                {
                    var relative = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(to)!, directory);
                    var link = (relative == "." ? string.Empty : relative.Replace('/', '\\') + "\\") + System.IO.Path.GetFileName(linked);
                    await _shell.ProjectItems.SetAsync(to, linked, null, new Dictionary<string, string?> { [ItemMetadataNames.Link] = link }, CancellationToken.None).ConfigureAwait(true);
                }
                else
                {
                    await _shell.ProjectItems.AddExistingAsync(to, directory, [linked], asLink: true, CancellationToken.None).ConfigureAwait(true);
                    if (!copy)
                    {
                        await _shell.ProjectItems.ExcludeAsync(from, [linked], CancellationToken.None).ConfigureAwait(true);
                    }
                }
            }).ConfigureAwait(true);
            return;
        }

        var moving = paths.Where(p => !SolutionService.PathComparer.Equals(System.IO.Path.GetDirectoryName(p), directory)).ToList();
        if (moving.Count > 0)
        {
            await TryAsync(() => _shell.Solutions.MoveAsync(moving, directory, copy, CancellationToken.None)).ConfigureAwait(true);
        }
    }

    /// <summary>Files dropped from the file manager: copied into the folder (and made items of its project).</summary>
    public async Task DropExternalAsync(IReadOnlyList<string> paths, ExplorerNode target)
    {
        if (DropDirectory(target) is not { } directory)
        {
            return;
        }

        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Where(Directory.Exists).ToList();
        if (target.ProjectPath is { } project && !IsFolderView && _shell.Solutions.Solution is { Trusted: true } && files.Count > 0)
        {
            await TryAsync(() => _shell.ProjectItems.AddExistingAsync(project, directory, files, asLink: false, CancellationToken.None)).ConfigureAwait(true);
            files.Clear();
        }

        if (files.Count + folders.Count > 0)
        {
            await TryAsync(() => _shell.Solutions.MoveAsync([.. files, .. folders], directory, copy: true, CancellationToken.None)).ConfigureAwait(true);
        }
    }

    public async Task AddSolutionFolderAsync()
    {
        var parent = Node is { Kind: ExplorerNodeKind.SolutionFolder, SolutionFolderId: { } id } ? id : null;
        var name = await _shell.Dialogs.InputAsync(Localizer.Get("msg.solutionExplorerViewModel.newSolutionFolder", "New Solution Folder"), Localizer.Get("msg.solutionExplorerViewModel.name", "Name:"), "New Folder", n => string.IsNullOrWhiteSpace(n) || n.IndexOfAny(['/', '\\']) >= 0 ? "Enter a name without slashes." : null).ConfigureAwait(true);
        if (name is not null)
        {
            await TryAsync(() => _shell.Solutions.AddSolutionFolderAsync(name, parent, CancellationToken.None)).ConfigureAwait(true);
        }
    }

    public async Task AddProjectReferenceAsync()
    {
        if (Node?.ProjectPath is not { } project || _shell.Solutions.Solution is not { } solution)
        {
            return;
        }

        var existing = _shell.Solutions.Projects.GetValueOrDefault(project)?.References.Where(r => r.Kind == ReferenceKinds.Project).Select(r => r.Path).ToHashSet(SolutionService.PathComparer) ?? [];
        var candidates = solution.Projects
            .Where(p => !SolutionService.PathComparer.Equals(p.Path, project) && !existing.Contains(p.Path))
            .Select(p => new QuickPickItem(p.Name) { Detail = Display(p.Path), Tag = p.Path })
            .ToList();
        if (candidates.Count == 0)
        {
            _shell.Workbench.Notifications.Info(Localizer.Get("msg.solutionExplorerViewModel.everyOtherProjectOfTheSolution", "Every other project of the solution is already referenced."));
            return;
        }

        if (await _shell.Palette.ShowAsync(candidates, $"Add a project reference to {System.IO.Path.GetFileNameWithoutExtension(project)}").ConfigureAwait(true) is { Tag: string reference })
        {
            await TryAsync(() => _shell.Solutions.AddProjectReferenceAsync(project, reference, CancellationToken.None)).ConfigureAwait(true);
        }
    }

    public async Task CopyPathAsync()
    {
        if (Node?.Path is { } path && _shell.Window?.Clipboard is { } clipboard)
        {
            await Codalus.Shell.Infrastructure.SafeClipboard.TrySetTextAsync(clipboard, path).ConfigureAwait(true);
        }
    }

    public void RevealInFileManager()
    {
        if (Node?.Path is { } path)
        {
            try
            {
                _shell.RevealPath(path);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _shell.Workbench.Notifications.Error(Localizer.Format("msg.solutionExplorerViewModel.couldNotOpenTheFileManager", "Could not open the file manager: {0}", ex.Message));
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private string Display(string path) =>
        _shell.Solutions.Solution is { } solution ? System.IO.Path.GetRelativePath(solution.Directory, path) : path;

    private static string? ValidateName(string directory, string name, string? current = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name.";
        }

        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or "..")
        {
            return "The name contains characters that aren't allowed in file names.";
        }

        var target = System.IO.Path.Combine(directory, name);
        var caseOnlyRename = current is not null && string.Equals(current, name, StringComparison.OrdinalIgnoreCase);
        return !caseOnlyRename && (File.Exists(target) || Directory.Exists(target)) ? $"'{name}' already exists here." : null;
    }

    /// <summary>
    /// A starting point for new C# files: a class named after the file, in the namespace the project name and the
    /// folder imply (as the classic IDE does). Other files start empty.
    /// </summary>
    private string? Template(string directory, string name)
    {
        var type = System.IO.Path.GetFileNameWithoutExtension(name);
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !System.Text.RegularExpressions.Regex.IsMatch(type, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            return null;
        }

        var segments = new List<string>();
        if (Node?.ProjectPath is { } project)
        {
            segments.Add(System.IO.Path.GetFileNameWithoutExtension(project));
            var relative = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(project)!, directory);
            if (relative != ".")
            {
                segments.AddRange(relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
            }
        }

        var ns = string.Join('.', segments.SelectMany(s => s.Split('.')).Select(Identifier));
        return ns.Length == 0
            ? $"public class {type}\n{{\n}}\n"
            : $"namespace {ns};\n\npublic class {type}\n{{\n}}\n";
    }

    private static string Identifier(string segment)
    {
        var chars = segment.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var text = new string(chars);
        return text.Length > 0 && char.IsDigit(text[0]) ? "_" + text : text;
    }

    /// <summary>Runs an operation; failures become a notification with the host's message.</summary>
    private async Task<T?> TryAsync<T>(Func<Task<T>> operation)
        where T : class
    {
        try
        {
            return await operation().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            _shell.Workbench.Notifications.Error(ex.Message);
            return null;
        }
    }

    private async Task TryAsync(Func<Task> operation) => await TryAsync<object>(async () =>
    {
        await operation().ConfigureAwait(true);
        return new object();
    }).ConfigureAwait(true);
}
