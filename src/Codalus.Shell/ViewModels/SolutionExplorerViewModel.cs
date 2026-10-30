using System.Collections.ObjectModel;
using Avalonia;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>One visible line of the Solution Explorer (the tree is flattened so the list can virtualize).</summary>
public sealed class ExplorerRowViewModel(ExplorerNode node, int depth, bool expanded, Func<string, ProjectInfo?>? project = null) : ObservableObject
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

    /// <summary>Shown dimmed: an unloaded project, or a file that isn't in the project.</summary>
    public bool IsDimmed => _node.IsUnloaded || _node.IsExcluded;

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
                OnPropertyChanged(nameof(AccessibleName));
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

    public string AccessibleName
    {
        get
        {
            var state = HasChildren ? (_isExpanded ? ", expanded" : ", collapsed") : string.Empty;
            var detail = _node.Detail is { } d ? " " + d : string.Empty;
            var warning = _node.IsWarning ? ", warning" : string.Empty;
            var excluded = _node.IsExcluded ? ", not in the project" : string.Empty;
            return $"{_node.Name}{detail}{warning}{excluded}{state}";
        }
    }
}

/// <summary>
/// The Solution Explorer. The tree is rebuilt from the build host's model (<see cref="SolutionTree"/>) and shown as
/// a flattened list; expansion and selection survive rebuilds by node id. Operations act on the selected row.
/// Show All Files adds the files on disk that aren't items (ADR 0033), and in folder mode the folder view shows the
/// folder as it is on disk. TODO(issue): multi-selection. (Open documents whose files were deleted are flagged in their tabs.)
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

    // Show All Files: each loaded project's files on disk, down to the folders its listing went into (see LoadProjectsAsync).
    private readonly Dictionary<string, IReadOnlyList<DirectoryEntry>> _disk = new(SolutionService.PathComparer);
    private readonly Dictionary<string, IReadOnlySet<string>> _diskFolders = new(SolutionService.PathComparer);
    private readonly Dictionary<string, int> _diskListings = new(SolutionService.PathComparer); // each project's newest listing
    private readonly HashSet<string> _diskQueue = new(SolutionService.PathComparer);
    private bool _diskQueueAll;
    private int _diskListing;
    private CancellationTokenSource _diskCancellation = new();

    // The folder view: the workspace folder's files on disk.
    private IReadOnlyList<DirectoryEntry> _folderEntries = [];
    private bool _folderQueued;
    private int _folderGeneration;
    private bool _rootIsFolderView;
    private bool _diskQueued;

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
            QueueFolderRefresh();
        }
        else
        {
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
            _folderCancellation.Cancel();
            _folderEntries = [];
            _folderView = false;
            return;
        }

        // The projects the solution lost lose their listings; the ones it gained are listed once they've loaded.
        var projects = solution.Projects.Select(p => p.Path).ToHashSet(SolutionService.PathComparer);
        foreach (var gone in _diskListings.Keys.Where(p => !projects.Contains(p)).ToList())
        {
            _disk.Remove(gone);
            _diskFolders.Remove(gone);
            _diskListings.Remove(gone);
        }

        if (IsFolderView)
        {
            QueueFolderRefresh();
        }
    }

    /// <summary>
    /// Folders changed on disk (the build host's watcher, which leaves out bin and obj): the folder view lists the folder
    /// again, and Show All Files the projects whose folders hold them.
    /// </summary>
    public void OnFilesChanged(IReadOnlyList<string> directories)
    {
        if (IsFolderView)
        {
            QueueFolderRefresh();
        }

        if (_showAllFiles)
        {
            QueueDiskRefresh(ProjectsHolding(_shell.Solutions.Projects.Keys, directories));
        }
    }

    /// <summary>
    /// Lists the disk again, when Show All Files or the folder view needs it: after a build of <paramref name="project"/>
    /// (with the projects it references, built first; null: of every project), a new tasks.json…
    /// </summary>
    public void RefreshDisk(string? project = null)
    {
        if (IsFolderView)
        {
            QueueFolderRefresh();
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

    private void QueueFolderRefresh()
    {
        _folderQueued = true;
        QueueDiskLoad();
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

    /// <summary>Lists what is queued, through the build host: the workspace folder (the folder view) and projects' folders (Show All Files).</summary>
    public Task LoadDiskAsync() => Task.WhenAll(LoadFolderAsync(), LoadProjectsAsync());

    /// <summary>Drops Show All Files' listings, and stops the ones on their way.</summary>
    private void ForgetListings()
    {
        _diskCancellation.Cancel();
        _diskCancellation.Dispose();
        _diskCancellation = new CancellationTokenSource();
        _disk.Clear();
        _diskFolders.Clear();
        _diskListings.Clear();
        _diskQueue.Clear();
        _diskQueueAll = false;
    }

    private CancellationTokenSource _folderCancellation = new();

    /// <summary>The folder view's listing of the workspace folder; the tree is built again when it changed.</summary>
    private async Task LoadFolderAsync()
    {
        if (!_folderQueued)
        {
            return;
        }

        _folderQueued = false;
        if (!IsFolderView || _shell.Solutions.Solution is not { } solution)
        {
            return;
        }

        var generation = ++_folderGeneration;
        _folderCancellation.Cancel();
        _folderCancellation.Dispose();
        _folderCancellation = new CancellationTokenSource();
        var cancellationToken = _folderCancellation.Token;
        var old = _folderEntries;
        IReadOnlyList<DirectoryEntry> entries;
        try
        {
            // Compared off the UI thread: an equal listing keeps the old one, and the tree isn't built again.
            entries = await Task.Run(async () =>
            {
                var tree = await _shell.ProjectItems.ListTreeAsync(solution.Directory, cancellationToken).ConfigureAwait(false);
                return old.SequenceEqual(tree.Entries) ? old : tree.Entries;
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _shell.Workbench.Log.AppendLine($"[explorer] Could not list the files on disk: {ex.Message}");
            }

            return;
        }

        if (generation != _folderGeneration || !IsFolderView)
        {
            return;
        }

        DiskLoaded?.Invoke(this, EventArgs.Empty);
        if (!ReferenceEquals(entries, _folderEntries) || !_rootIsFolderView)
        {
            _folderEntries = entries;
            Refresh();
        }
    }

    /// <summary>
    /// Show All Files' listings of the queued projects' folders, through the build host and off the UI thread, four at a
    /// time. Each goes into the folders that hold the project's items and the folders expanded: another folder (bin, obj)
    /// shows, without what is in it until it's expanded. A listing equal to the project's last keeps its instance, so the
    /// tree keeps the project's subtree; the tree is built again once, if any changed. Every project's whole folder, bin
    /// and obj with hundreds of files after a build, was listed again one project at a time for each change of any folder
    /// and each build, and every project's subtree built again on the UI thread (M56).
    /// </summary>
    private async Task LoadProjectsAsync()
    {
        var queued = _diskQueueAll ? [.. _shell.Solutions.Projects.Keys] : _diskQueue.ToList();
        _diskQueue.Clear();
        _diskQueueAll = false;
        var requests = new List<(string Project, ProjectInfo Info, IReadOnlyList<DirectoryEntry>? Old, int Number)>();
        foreach (var project in _showAllFiles ? queued : [])
        {
            if (_shell.Solutions.Projects.TryGetValue(project, out var info) && info.State == ProjectStates.Loaded)
            {
                requests.Add((project, info, _disk.GetValueOrDefault(project), _diskListings[project] = ++_diskListing));
            }
        }

        if (requests.Count == 0)
        {
            return;
        }

        var expanded = _expanded.Where(id => id.StartsWith("d:", StringComparison.Ordinal)).Select(id => id[2..]).ToList();
        var listings = new System.Collections.Concurrent.ConcurrentBag<ProjectListing>();
        try
        {
            await Parallel.ForEachAsync(requests, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _diskCancellation.Token }, async (r, cancellationToken) =>
                listings.Add(await ListProjectAsync(r.Project, r.Info, r.Old, r.Number, expanded, cancellationToken).ConfigureAwait(false))).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // Show All Files went off, or the solution closed
        }

        var changed = false;
        foreach (var listing in listings)
        {
            if (!_diskListings.TryGetValue(listing.Project, out var newest) || newest != listing.Number)
            {
                continue; // a newer listing of the project is on its way
            }

            if (listing.Error is { } error)
            {
                _shell.Workbench.Log.AppendLine($"[explorer] Could not list the files on disk: {error}");
                continue;
            }

            // The folders listed decide which folders have a "Loading…" row, so they count too.
            var folders = _diskFolders.GetValueOrDefault(listing.Project);
            changed |= !ReferenceEquals(_disk.GetValueOrDefault(listing.Project), listing.Entries) || folders is null || !folders.SetEquals(listing.Folders);
            _disk[listing.Project] = listing.Entries;
            _diskFolders[listing.Project] = listing.Folders;
        }

        Listings += listings.Count;
        DiskLoaded?.Invoke(this, EventArgs.Empty);
        if (changed)
        {
            Refresh();
        }
    }

    private sealed record ProjectListing(string Project, int Number, IReadOnlyList<DirectoryEntry> Entries, IReadOnlySet<string> Folders, string? Error = null);

    /// <summary>A project's folder, going into the folders that hold its items and the expanded ones (off the UI thread).</summary>
    private async Task<ProjectListing> ListProjectAsync(string project, ProjectInfo info, IReadOnlyList<DirectoryEntry>? old, int number, IReadOnlyList<string> expanded, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(project)!;
        var below = System.IO.Path.TrimEndingDirectorySeparator(directory) + System.IO.Path.DirectorySeparatorChar;
        var comparison = SolutionService.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var folders = new HashSet<string>(SolutionTree.ItemFolders(project, info), SolutionService.PathComparer);
        folders.UnionWith(expanded.Where(f => f.StartsWith(below, comparison)));
        try
        {
            var tree = await _shell.ProjectItems.ListTreeAsync(directory, cancellationToken, folders: folders).ConfigureAwait(false);
            return new ProjectListing(project, number, old is not null && old.SequenceEqual(tree.Entries) ? old : tree.Entries, folders);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new ProjectListing(project, number, [], folders, ex.Message);
        }
    }

    /// <summary>The projects' folders listed so far (tests).</summary>
    internal int Listings { get; private set; }

    /// <summary>
    /// Show All Files: a folder that isn't in its project, whose contents aren't listed until it's expanded (it has a
    /// "Loading…" row, so it can be).
    /// </summary>
    private bool IsUnlisted(ExplorerNode node) =>
        node is { Kind: ExplorerNodeKind.Folder, IsExcluded: true, Path: { } path, ProjectPath: { } project }
        && _diskFolders.TryGetValue(project, out var folders) && !folders.Contains(path);

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

    /// <summary>Whether a filter's matches are still being found (tests).</summary>
    internal bool IsFiltering => _filterDebounce?.IsPending == true || _filtering;

    private void CancelFilter()
    {
        Interlocked.Increment(ref _filterGeneration);
        _filtering = false;
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
        RefreshIfQueued(); // a project that just loaded is in the tree to find
        if (_shell.ActiveEditor?.Model.Path is not { } path || FindByPath(path) is not { } node)
        {
            if (_shell.ActiveEditor?.Model.Path is { } unlisted && ListDownTo(unlisted))
            {
                ClearFilter();
                _pendingReveal = "f:" + unlisted; // once its folders are listed
                return;
            }

            _shell.StatusBar.ShowMessage(Localizer.Get("msg.solutionExplorerViewModel.theActiveDocumentIsntInThe", "The active document isn't in the solution."), TimeSpan.FromSeconds(4));
            return;
        }

        ClearFilter();
        Reveal(node.Id);
    }

    /// <summary>
    /// Show All Files: a file in a folder that isn't in its project shows once the folder is expanded and listed. This
    /// expands the folders down to <paramref name="path"/> in the project whose folder holds it, and lists them; true when
    /// it did (the file is in the tree after the listing's refresh), false when they were expanded already.
    /// </summary>
    private bool ListDownTo(string path)
    {
        if (!_showAllFiles || System.IO.Path.GetDirectoryName(path) is not { } folder
            || ProjectsHolding(_diskFolders.Keys, [folder]).MaxBy(p => System.IO.Path.GetDirectoryName(p)!.Length) is not { } project)
        {
            return false;
        }

        var directory = System.IO.Path.GetDirectoryName(project)!;
        var expanded = false;
        for (var down = folder; down.Length > directory.Length; down = System.IO.Path.GetDirectoryName(down)!)
        {
            expanded |= _expanded.Add("d:" + down);
        }

        if (expanded)
        {
            QueueDiskRefresh([project]);
        }

        return expanded;
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
        _root = solutions.Solution is { } solution
            ? SolutionTree.Build(solution, solutions.Projects, solutions.CompilerInputs, new ExplorerOptions
            {
                DiskEntries = _showAllFiles ? _disk : null,
                FolderEntries = IsFolderView ? _folderEntries : null,
            }, _treeCache)
            : null;
        _rootIsFolderView = _root is not null && IsFolderView;
        Builds++;
        _byId = null;
        _byPath = null;
        if (_root is null)
        {
            _withMembers.Clear();
            _loadingRows.Clear();
        }
        OnPropertyChanged(nameof(HasSolution));

        // A reveal that waited for its node: its ancestors expand in this build's rows.
        string? reveal = null;
        if (_pendingReveal is { } pending && PathTo(pending) is { } path)
        {
            _pendingReveal = null;
            reveal = pending;
            foreach (var ancestor in path.SkipLast(1))
            {
                _expanded.Add(ancestor.Id);
            }
        }

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
    }

    /// <summary>A filter's matches, found off the UI thread and shown with one reset, unless the filter or the tree changed meanwhile.</summary>
    private async Task FilterAsync(ExplorerNode root, string filter, string? reveal)
    {
        var generation = Interlocked.Increment(ref _filterGeneration);
        _filtering = true;
        bool Stale() => Volatile.Read(ref _filterGeneration) != generation;
        List<(ExplorerNode Node, int Depth)> rows;
        try
        {
            rows = await Task.Run(() =>
            {
                var matches = new List<(ExplorerNode, int)>();
                FlattenMatching(root, 0, filter, matches, Stale);
                return matches;
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Stale() || !ReferenceEquals(_root, root) || _filter.Trim() != filter)
        {
            return;
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

        if (any || depth == 0 || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            rows.Insert(at, (node, depth));
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
            QueueDiskRefresh([row.Node.ProjectPath!]); // its project's listing goes into it now (it's in the expanded ones by then)
        }

        if (!row.HasChildren || (expanded ? !_expanded.Add(row.Id) : !_expanded.Remove(row.Id)))
        {
            return;
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
            return;
        }

        if (_pendingReveal == id)
        {
            _pendingReveal = null;
        }

        ExplorerNode? topmost = null;
        foreach (var ancestor in path.SkipLast(1))
        {
            if (_expanded.Add(ancestor.Id))
            {
                topmost ??= ancestor;
            }
        }

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

        if (IsUnlisted(node))
        {
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
