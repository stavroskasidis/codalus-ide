using System.Collections.ObjectModel;
using Avalonia;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Projects;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell.ViewModels;

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
public sealed class SolutionExplorerViewModel : ObservableObject
{
    public const string ToolWindowId = "solutionExplorer";

    private readonly ShellHost _shell;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private ExplorerNode? _root;
    private ExplorerRowViewModel? _selected;
    private bool _refreshQueued;
    private string? _pendingReveal;
    private (IReadOnlyList<string> Paths, bool Cut)? _clipboard;
    private string? _startupProject;
    private string _filter = string.Empty;
    private bool _showAllFiles;
    private bool _folderView;
    private readonly Dictionary<string, IReadOnlyList<DirectoryEntry>> _disk = new(SolutionService.PathComparer);
    private IReadOnlyList<DirectoryEntry> _folderEntries = [];
    private int _diskGeneration;
    private bool _diskQueued;

    /// <summary>What the build host knows about a project (its icon: test, web, library…).</summary>
    private ProjectInfo? ProjectInfoOf(string path) => _shell.Solutions?.Projects.GetValueOrDefault(path);

    public SolutionExplorerViewModel(ShellHost shell)
    {
        _shell = shell;
        var solutions = shell.Solutions;
        solutions.SolutionChanged += (_, _) => QueueRefresh();
        solutions.ProjectChanged += (_, _) => QueueRefresh();
        solutions.CompilerInputsChanged += (_, _) => QueueRefresh();
        solutions.SolutionChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(OnSolutionChangedForDisk);
        solutions.ProjectChanged += (_, project) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_showAllFiles && !_disk.ContainsKey(project.Path))
            {
                QueueDiskRefresh();
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

    public ObservableCollection<ExplorerRowViewModel> Rows { get; } = [];

    public RelayCommand OpenSolutionCommand { get; }

    public RelayCommand HomeCommand { get; }

    public RelayCommand SyncCommand { get; }

    public RelayCommand CollapseAllCommand { get; }

    public RelayCommand PropertiesCommand { get; }

    public RelayCommand ShowAllFilesCommand { get; }

    public RelayCommand SwitchViewsCommand { get; }

    /// <summary>Show All Files: files and folders on disk that aren't in their project show dimmed (ADR 0033).</summary>
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
                    QueueDiskRefresh();
                }
                else
                {
                    _disk.Clear();
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
            QueueDiskRefresh();
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
        if (_shell.Solutions.Solution is null)
        {
            _disk.Clear();
            _folderEntries = [];
            _folderView = false;
        }
        else if (_showAllFiles || IsFolderView)
        {
            QueueDiskRefresh();
        }
    }

    /// <summary>Folders changed on disk (the build host's watcher): Show All Files and the folder view list them again.</summary>
    public void OnFilesChanged(IReadOnlyList<string> directories)
    {
        if (_showAllFiles || IsFolderView)
        {
            QueueDiskRefresh();
        }
    }

    /// <summary>Lists the disk again (after a build, a new tasks.json…), when Show All Files or the folder view needs it.</summary>
    public void RefreshDisk()
    {
        if (_showAllFiles || IsFolderView)
        {
            QueueDiskRefresh();
        }
    }

    private void QueueDiskRefresh()
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

    /// <summary>Lists each loaded project's folder (Show All Files) or the workspace folder (folder view) through the build host.</summary>
    public async Task LoadDiskAsync()
    {
        var generation = ++_diskGeneration;
        if (_shell.Solutions.Solution is not { } solution)
        {
            return;
        }

        try
        {
            if (IsFolderView)
            {
                var tree = await _shell.ProjectItems.ListTreeAsync(solution.Directory, CancellationToken.None).ConfigureAwait(true);
                if (generation == _diskGeneration)
                {
                    _folderEntries = tree.Entries;
                }
            }

            if (_showAllFiles)
            {
                foreach (var project in _shell.Solutions.Projects.Values.Where(p => p.State == ProjectStates.Loaded).ToList())
                {
                    var tree = await _shell.ProjectItems.ListTreeAsync(System.IO.Path.GetDirectoryName(project.Path)!, CancellationToken.None).ConfigureAwait(true);
                    if (generation != _diskGeneration)
                    {
                        return;
                    }

                    _disk[project.Path] = tree.Entries;
                }
            }
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            _shell.Workbench.Log.AppendLine($"[explorer] Could not list the files on disk: {ex.Message}");
        }

        if (generation == _diskGeneration)
        {
            DiskLoaded?.Invoke(this, EventArgs.Empty);
            Refresh();
        }
    }

    /// <summary>The disk listing arrived (tests wait for it).</summary>
    public event EventHandler? DiskLoaded;

    /// <summary>Filters the tree to the items whose names contain the text, with their parents (the search box, M29).</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

    /// <summary>Raised to focus the search box (Ctrl+;).</summary>
    public event EventHandler? SearchRequested;

    public void RequestSearch() => SearchRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Home: back to the solution node, collapsed to its projects.</summary>
    public void Home()
    {
        Filter = string.Empty;
        CollapseAll();
        Selected = Rows.FirstOrDefault();
    }

    /// <summary>Sync with Active Document: selects the active document's file in the tree.</summary>
    public void SyncWithActiveDocument()
    {
        if (_shell.ActiveEditor?.Model.Path is not { } path || _root is null || FindByPath(_root, path) is not { } node)
        {
            _shell.StatusBar.ShowMessage(Localizer.Get("msg.solutionExplorerViewModel.theActiveDocumentIsntInThe", "The active document isn't in the solution."), TimeSpan.FromSeconds(4));
            return;
        }

        Filter = string.Empty;
        Reveal(node.Id);
    }

    private static ExplorerNode? FindByPath(ExplorerNode node, string path)
    {
        if (node.Kind is not ExplorerNodeKind.Solution and not ExplorerNodeKind.Project && node.Path is { } nodePath && SolutionService.PathComparer.Equals(nodePath, path))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (FindByPath(child, path) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public bool HasSolution => _shell.Solutions.IsOpen;

    public ExplorerRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                _shell.Workbench.Context.Set("explorerItemKind", value is null ? null : KindKey(value.Node.Kind));
                _shell.Workbench.Context.Set("explorerItemIsProjectReference", value?.Node is { Kind: ExplorerNodeKind.Dependency } n && n.Id.Contains("|project|", StringComparison.Ordinal));
                _shell.Workbench.Context.Set("explorerProjectUnloaded", value?.Node.IsUnloaded == true);
                _shell.Workbench.Context.Set("explorerItemName", value?.Node is { Kind: ExplorerNodeKind.File, Path: { } file } ? System.IO.Path.GetFileName(file) : null);
                _shell.Workbench.Context.Set("explorerItemExcluded", value?.Node.IsExcluded == true);
                _shell.Workbench.Context.Set("explorerItemLinked", value?.Node.IsLinked == true);
                _shell.Workbench.Context.Set("explorerItemInProject", value?.Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, ProjectPath: not null, IsExcluded: false } n2 && !SolutionTree.IsProjectFile(n2.Path ?? string.Empty) && !IsFolderView);
                _shell.Workbench.Context.Set("explorerItemIsProjectFile", value?.Node is { Kind: ExplorerNodeKind.File, Path: { } projectFile } && SolutionTree.IsProjectFile(projectFile));
                _shell.Workbench.Context.Set("explorerProjectLoaded", value?.Node is { Kind: ExplorerNodeKind.File, Path: { } loadedFile } && _shell.Solutions.Solution?.Projects.Any(p => SolutionService.PathComparer.Equals(p.Path, loadedFile)) == true);
            }
        }
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

    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Refresh()
    {
        var solutions = _shell.Solutions;
        _root = solutions.Solution is { } solution
            ? SolutionTree.Build(solution, solutions.Projects, solutions.CompilerInputs, new ExplorerOptions
            {
                DiskEntries = _showAllFiles ? _disk : null,
                FolderEntries = IsFolderView ? _folderEntries : null,
            })
            : null;
        OnPropertyChanged(nameof(HasSolution));
        if (_pendingReveal is { } pending && _root is not null && Path(_root, pending) is not null)
        {
            Reveal(pending);
            return;
        }
        var rows = new List<(ExplorerNode Node, int Depth)>();
        if (_root is not null && _filter.Trim() is { Length: > 0 } filter)
        {
            FlattenMatching(_root, 0, filter, rows);
        }
        else if (_root is not null)
        {
            Flatten(_root, 0, rows);
        }

        Apply(rows);
    }

    private void Flatten(ExplorerNode node, int depth, List<(ExplorerNode, int)> rows)
    {
        if (_seen.Add(node.Id) && node.Kind is ExplorerNodeKind.Solution or ExplorerNodeKind.SolutionFolder)
        {
            _expanded.Add(node.Id);
        }

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
    private static bool FlattenMatching(ExplorerNode node, int depth, string filter, List<(ExplorerNode, int)> rows)
    {
        var at = rows.Count;
        var any = false;
        foreach (var child in node.Children)
        {
            any |= FlattenMatching(child, depth + 1, filter, rows);
        }

        if (any || depth == 0 || node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            rows.Insert(at, (node, depth));
            return true;
        }

        return false;
    }

    /// <summary>Updates <see cref="Rows"/> in place: only the changed middle part is replaced, so scrolling is kept.</summary>
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

        for (var i = Rows.Count - suffix - 1; i >= prefix; i--)
        {
            Rows.RemoveAt(i);
        }

        for (var i = prefix; i < rows.Count - suffix; i++)
        {
            Rows.Insert(i, new ExplorerRowViewModel(rows[i].Node, rows[i].Depth, _expanded.Contains(rows[i].Node.Id), ProjectInfoOf));
        }

        MarkStartupProject();
        Selected = Rows.FirstOrDefault(r => r.Id == selectedId) ?? (selectedId is null ? null : Selected);
        if (Selected is not null && !Rows.Contains(Selected))
        {
            Selected = null;
        }
    }

    public void SetExpanded(ExplorerRowViewModel row, bool expanded)
    {
        if (!row.HasChildren || (expanded ? !_expanded.Add(row.Id) : !_expanded.Remove(row.Id)))
        {
            return;
        }

        Refresh();
    }

    public void Toggle(ExplorerRowViewModel row) => SetExpanded(row, !_expanded.Contains(row.Id));

    public void CollapseAll()
    {
        _expanded.RemoveWhere(id => id != "solution");
        Refresh();
    }

    /// <summary>Reveals <paramref name="id"/> as soon as the node exists (it may come with the next model update).</summary>
    public void RevealWhenAvailable(string id)
    {
        _pendingReveal = id;
        Reveal(id);
    }

    /// <summary>Selects (and reveals) the node with <paramref name="id"/>.</summary>
    public void Reveal(string id)
    {
        if (_root is null || Path(_root, id) is not { } path)
        {
            return;
        }

        if (_pendingReveal == id)
        {
            _pendingReveal = null;
        }

        foreach (var ancestor in path.SkipLast(1))
        {
            _expanded.Add(ancestor.Id);
        }

        Refresh();
        Selected = Rows.FirstOrDefault(r => r.Id == id);
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

    // ------------------------------------------------------------------ activation

    /// <summary>A single click on a file shows it in the preview tab, keeping focus here.</summary>
    public async Task PreviewAsync(ExplorerRowViewModel row)
    {
        if (row.Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.SolutionItem, Path: { } path } && File.Exists(path))
        {
            await _shell.OpenFileAsync(path, preview: true).ConfigureAwait(true);
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
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, path).ConfigureAwait(true);
        }
    }

    public void RevealInFileManager()
    {
        if (Node?.Path is { } path)
        {
            try
            {
                DaedalusStudio.Platform.ShellIntegration.RevealInFileManager(path);
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
    /// folder imply (as Visual Studio does). Other files start empty.
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
