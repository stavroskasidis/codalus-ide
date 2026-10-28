using System.Collections.ObjectModel;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>A file in the Changes view.</summary>
public sealed class GitFileRow(GitFileStatus status, bool staged, GitLfsStatus? lfs = null)
{
    public GitFileStatus Status { get; } = status;

    public bool Staged { get; } = staged;

    private readonly GitLfsFile? _lfsFile = lfs?.FileOf(status.Path);
    private readonly GitLfsLock? _lock = lfs?.LockOf(status.Path);

    /// <summary>Git LFS (M13b): "LFS", "LFS pointer" (not downloaded), and who holds a lock.</summary>
    public string? Badge => _lock is { } held ? "locked: " + held.Owner : _lfsFile is { IsPointer: true } ? "LFS pointer" : _lfsFile is not null ? "LFS" : null;

    public bool HasBadge => Badge is not null;

    public string? BadgeTooltip => _lock is { } held
        ? $"Locked in Git LFS by {held.Owner}{(held.LockedAt is { } at ? " since " + at.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) : string.Empty)}"
        : _lfsFile is { IsPointer: true } ? "Tracked by Git LFS; only its pointer is here (git-lfs isn't installed, or the content wasn't downloaded)."
        : _lfsFile is not null ? "Tracked by Git LFS." : null;

    public string Name => System.IO.Path.GetFileName(Status.Path);

    /// <summary>The file's icon, as the Solution Explorer shows it.</summary>
    public string Icon => Controls.FileIcons.ForFile(Name);

    /// <summary>The list view's Path column: the repository's folder name and the file's folder (<c>repo\src\Data</c>).</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>The repository's folder: one of several while Git Changes shows them all (ADR 0085).</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>Its repository's place in the solution's order, which the sections keep (all repositories, ADR 0085).</summary>
    public int RepositoryOrder { get; init; }

    /// <summary>Deleted (in the index or the working tree): there's no file to open.</summary>
    public bool IsDeleted => (Staged ? Status.Index : Status.WorkTree) == 'D';

    public string Folder => System.IO.Path.GetDirectoryName(Status.RelativePath)?.Replace('\\', '/') ?? string.Empty;

    /// <summary>One letter: M(odified), A(dded), D(eleted), R(enamed), U(ntracked or conflicted).</summary>
    public string Letter => Status.IsConflict ? "!" : Status.IsUntracked ? "U" : (Staged ? Status.Index : Status.WorkTree).ToString();

    public string Description => Status.IsConflict ? "conflict" : Status.IsUntracked ? "untracked" : (Staged ? Status.Index : Status.WorkTree) switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'T' => "type changed",
        _ => "changed",
    };

    public bool CanStage => !Staged;

    public bool CanUnstage => Staged;

    /// <summary>A rename's old name ("← Tokens.cs"), or null.</summary>
    public string? OldName => Status.OriginalPath is { } old ? "← " + System.IO.Path.GetFileName(old) : null;

    public bool HasOldName => Status.OriginalPath is not null;

    public string AccessibleName => $"{Name} in {Folder}, {Description}{(Staged ? ", staged" : string.Empty)}{(Badge is { } badge ? ", " + badge : string.Empty)}";
}

/// <summary>
/// A node of the Changes tree: the repository, a folder (chains of single folders compacted, as <c>src\Data</c>), or a
/// file. Folders remember being collapsed across refreshes through <paramref name="expandedChanged"/>.
/// </summary>
public sealed class GitChangeNode(string key, string text, GitFileRow? row, bool isRepository, bool staged, bool expanded, Action<GitChangeNode, bool>? expandedChanged = null)
    : ObservableObject
{
    private bool _isExpanded = expanded;

    /// <summary>The section and path that identify the node across refreshes.</summary>
    public string Key { get; } = key;

    public string Text { get; } = text;

    public GitFileRow? Row { get; } = row;

    public bool IsRepository { get; } = isRepository;

    public bool IsFile => Row is not null;

    public bool Staged { get; } = staged;

    public ObservableCollection<GitChangeNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Icon));
                expandedChanged?.Invoke(this, value);
            }
        }
    }

    public string Icon => Row is { } file ? file.Icon
        : IsRepository ? Controls.FileIcons.ForFile(".gitignore")
        : Controls.FileIcons.ForFolder(System.IO.Path.GetFileName(Text), _isExpanded);

    /// <summary>The files at and under this node.</summary>
    public IEnumerable<GitFileRow> Rows => Row is { } file ? [file] : Children.SelectMany(c => c.Rows);

    public string? Letter => Row?.Letter;

    public string? Description => Row?.Description;

    public bool HasBadge => Row?.HasBadge ?? false;

    public string? Badge => Row?.Badge;

    public string? BadgeTooltip => Row?.BadgeTooltip;

    public bool CanStage => !Staged;

    public bool CanUnstage => Staged;

    /// <summary>A file with unstaged changes, or a folder (or the repository) of the Changes section (T1: Discard on a folder's hover buttons).</summary>
    public bool CanDiscard => Row is { CanStage: true } || (Row is null && Section == "changes");

    /// <summary>The node's path: the repository's folder, a folder's or a file's (with the OS's separators).</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Its path from the repository's folder, with '/' (empty for the repository).</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>The section it's in: <c>changes</c>, <c>staged</c> or <c>conflicts</c>.</summary>
    public string Section { get; init; } = string.Empty;

    /// <summary>The changed files at and under it that the window shows (a filter can hide some).</summary>
    public int ShownFiles => Rows.Count();

    /// <summary>The changed files at and under it in its section, shown or not.</summary>
    public int TotalFiles { get; set; }

    /// <summary>"2 of 5 files" on a folder while a filter hides some of its files (T6, question 15); null otherwise.</summary>
    public string? CountText => Row is null && ShownFiles < TotalFiles
        ? Localizer.Format("msg.gitChanges.filesOfTotal", "{0} of {1} files", ShownFiles, TotalFiles)
        : null;

    public bool HasCountText => CountText is not null;

    /// <summary>A file's description; a folder's name and its count (every folder's, question 15).</summary>
    public string AccessibleName => Row?.AccessibleName ?? (IsRepository
        ? Localizer.Format("msg.gitChanges.repositoryAccessibleName", "{0}, repository, {1} changed files", Text, TotalFiles)
        : TotalFiles == 1
            ? Localizer.Format("msg.gitChanges.folderAccessibleNameOne", "{0}, folder, 1 changed file", Text)
            : Localizer.Format("msg.gitChanges.folderAccessibleName", "{0}, folder, {1} changed files", Text, TotalFiles));
}

/// <summary>
/// A choice of the repository picker (several repositories, M13b): a repository of the solution, or (ADR 0085) All
/// Repositories, whose <see cref="Count"/> is how many there are.
/// </summary>
public sealed record GitRepositoryChoice(string Path, bool IsAll = false, int Count = 0)
{
    public static GitRepositoryChoice All(int count) => new(string.Empty, IsAll: true, count);

    public string Name => IsAll
        ? Localizer.Format("msg.gitChanges.allRepositoriesCount", "All Repositories ({0})", Count)
        : System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>The repository's folder, shown after its name; none for All Repositories.</summary>
    public string Detail => IsAll ? string.Empty : Path;

    public string Icon => IsAll ? "repositories" : "repository";

    /// <summary>The user added it to the solution's repositories, or it was found and can be left out (not the solution's own).</summary>
    public bool CanRemove { get; init; }

    public bool IsSelected { get; init; }

    public double CheckOpacity => IsSelected ? 1 : 0;

    public override string ToString() => Name;
}

/// <summary>A completion in the commit message: an issue or work item (M13b).</summary>
public sealed record CommitCompletion(string Reference, string Title, string? Detail)
{
    public string Label => $"{Reference}  {Title}";
}

/// <summary>An issue or work item the commit message mentions (<c>#12</c>), with its title when completion gave it.</summary>
public sealed record RelatedItem(string Reference, string? Title);

/// <summary>The Git Changes tool window (ADR 0008).</summary>
/// <summary>A fetch, pull, push or sync that Git Changes shows while it runs (<see cref="GitChangesViewModel.BeginProgress"/>); dispose it when it ends.</summary>
public sealed class GitProgress : IDisposable
{
    private readonly GitChangesViewModel _owner;
    private readonly CancellationTokenSource _cancellation = new();

    internal GitProgress(GitChangesViewModel owner, string text)
    {
        _owner = owner;
        Text = text;
    }

    public string Text { get; }

    /// <summary>Canceled by the window's Cancel, or the status bar's.</summary>
    public CancellationToken Token => _cancellation.Token;

    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    public void Cancel()
    {
        if (!_cancellation.IsCancellationRequested)
        {
            _cancellation.Cancel();
            _owner.ProgressChanged();
        }
    }

    public void Dispose()
    {
        _owner.EndProgress(this);
        _cancellation.Dispose();
    }
}

public sealed partial class GitChangesViewModel : ObservableObject
{
    public const string ToolWindowId = "git.changes";

    private readonly GitService _git;
    private readonly Func<string, bool, Task> _openDiff;
    private readonly Func<string, Task> _run;
    private string _message = string.Empty;
    private bool _amend;
    private string? _messageBeforeAmend;

    public GitChangesViewModel(GitService git, Func<string, bool, Task> openDiff, Func<string, Task> run)
    {
        _git = git;
        _openDiff = openDiff;
        _run = run;
        git.StatusChanged += (_, _) => Ui.Post(Refresh);
        git.SiblingStatusChanged += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    /// <summary>The repositories the window shows: every one of the solution's in All Repositories (ADR 0085), else the open one.</summary>
    public IReadOnlyList<GitService> Services => _isAllRepositories ? _git.AllServices : [_git];

    public ObservableCollection<GitFileRow> Staged { get; } = [];

    public ObservableCollection<GitFileRow> Changes { get; } = [];

    public ObservableCollection<GitFileRow> Conflicts { get; } = [];

    // ------------------------------------------------------------------ tree and list views

    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private bool _isTreeView = true;
    private bool _openFileAsDefault;
    private bool _showFilter;
    private string _filterText = string.Empty;
    private bool _sortByPath;
    private bool _sortDescending;

    /// <summary>The staged changes as a tree: the repository, its folders, their files.</summary>
    public ObservableCollection<GitChangeNode> StagedTree { get; } = [];

    public ObservableCollection<GitChangeNode> ChangesTree { get; } = [];

    public ObservableCollection<GitChangeNode> ConflictsTree { get; } = [];

    /// <summary>The staged changes as a list (filtered and sorted).</summary>
    public ObservableCollection<GitFileRow> StagedItems { get; } = [];

    public ObservableCollection<GitFileRow> ChangesItems { get; } = [];

    public ObservableCollection<GitFileRow> ConflictsItems { get; } = [];

    /// <summary>Files as a tree of folders (the default), or as a list with Name and Path columns.</summary>
    public bool IsTreeView
    {
        get => _isTreeView;
        set
        {
            if (SetField(ref _isTreeView, value))
            {
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(SwitchViewText));
            }
        }
    }

    public bool IsListView => !_isTreeView;

    public string SwitchViewText => _isTreeView
        ? Localizer.Get("msg.gitChanges.switchToListView", "Switch to List View")
        : Localizer.Get("msg.gitChanges.switchToTreeView", "Switch to Tree View");

    /// <summary>Double-click (Enter) opens the file itself instead of its changes.</summary>
    public bool OpenFileAsDefault
    {
        get => _openFileAsDefault;
        set => SetField(ref _openFileAsDefault, value);
    }

    /// <summary>Shows the filter box; hiding it clears the filter.</summary>
    public bool ShowFilter
    {
        get => _showFilter;
        set
        {
            if (SetField(ref _showFilter, value))
            {
                OnPropertyChanged(nameof(ShowFilterText));
                if (!value)
                {
                    FilterText = string.Empty;
                }
            }
        }
    }

    public string ShowFilterText => _showFilter
        ? Localizer.Get("msg.gitChanges.hideFilter", "Hide Filter")
        : Localizer.Get("msg.gitChanges.showFilter", "Show Filter");

    /// <summary>Shows only the files whose path contains this text.</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetField(ref _filterText, value ?? string.Empty))
            {
                RebuildViews();
            }
        }
    }

    /// <summary>The list view's order: by Path (then name) or by Name.</summary>
    public bool SortByPath => _sortByPath;

    public bool SortDescending => _sortDescending;

    public string NameSortGlyph => _sortByPath ? string.Empty : _sortDescending ? "▼" : "▲";

    public string PathSortGlyph => _sortByPath ? _sortDescending ? "▼" : "▲" : string.Empty;

    /// <summary>Sorts the list by a column (<c>name</c> or <c>path</c>); the same column again reverses the order.</summary>
    public void SortBy(string column)
    {
        var byPath = column == "path";
        _sortDescending = byPath == _sortByPath && !_sortDescending;
        _sortByPath = byPath;
        foreach (var name in new[] { nameof(SortByPath), nameof(SortDescending), nameof(NameSortGlyph), nameof(PathSortGlyph) })
        {
            OnPropertyChanged(name);
        }

        RebuildViews();
    }

    /// <summary>Opens a file (set by the shell), for <see cref="OpenFileAsDefault"/>.</summary>
    public Func<string, Task>? OpenFile { get; set; }

    /// <summary>The default action of a file: its changes, or the file itself with <see cref="OpenFileAsDefault"/>.</summary>
    public Task OpenAsync(GitFileRow row) =>
        OpenFileAsDefault && !row.IsDeleted && OpenFile is { } open ? open(row.Status.Path) : OpenDiffAsync(row);

    public Task OpenFileAsync(GitFileRow row) => OpenFile is { } open && !row.IsDeleted ? open(row.Status.Path) : Task.CompletedTask;

    public Task StageAsync(GitChangeNode node) => EachRepositoryAsync(node.Rows, (git, paths) => git.StageAsync(paths));

    public Task UnstageAsync(GitChangeNode node) => EachRepositoryAsync(node.Rows, (git, paths) => git.UnstageAsync(paths));

    /// <summary>Runs <paramref name="action"/> on the rows' files in each one's repository (one, unless all are shown).</summary>
    private async Task EachRepositoryAsync(IEnumerable<GitFileRow> rows, Func<GitService, IReadOnlyList<string>, Task> action)
    {
        foreach (var (git, paths) in _git.GroupByRepository(rows.Select(r => r.Status.Path)))
        {
            await action(git, paths).ConfigureAwait(true);
        }
    }

    private bool Matches(GitFileRow row) =>
        _filterText.Length == 0 || row.Status.RelativePath.Contains(_filterText.Trim(), StringComparison.OrdinalIgnoreCase);

    private void RebuildViews()
    {
        Fill(StagedTree, Forest(Staged, "staged", staged: true));
        Fill(ChangesTree, Forest(Changes, "changes", staged: false));
        Fill(ConflictsTree, Forest(Conflicts, "conflicts", staged: false));
        Fill(StagedItems, Sorted(Staged.Where(Matches)));
        Fill(ChangesItems, Sorted(Changes.Where(Matches)));
        Fill(ConflictsItems, Sorted(Conflicts.Where(Matches)));
        foreach (var name in new[] { nameof(StagedHeader), nameof(ChangesHeader), nameof(ConflictsHeader), nameof(IsFiltered), nameof(StagedFilteredOut), nameof(ChangesFilteredOut), nameof(FilteredOutText) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>A section's tree: one repository node, or with all repositories shown one per repository with changes, in order.</summary>
    private IEnumerable<GitChangeNode> Forest(IReadOnlyList<GitFileRow> rows, string section, bool staged) => !_isAllRepositories
        ? BuildTree(_git.Root ?? string.Empty, rows.Where(Matches), section, staged, _collapsed, OnExpandedChanged, rows)
        : rows.GroupBy(r => r.Repository, StringComparer.Ordinal).SelectMany(g =>
            BuildTree(g.Key, g.Where(Matches), section, staged, _collapsed, OnExpandedChanged, [.. g], key: section + ":" + g.Key, text: g.First().Location.Split(System.IO.Path.DirectorySeparatorChar)[0]));

    /// <summary>A filter is hiding files (the menu's folder actions then act on the files shown, question 2).</summary>
    public bool IsFiltered => _filterText.Trim().Length > 0;

    /// <summary>The section's files: <c>changes</c>, <c>staged</c> or <c>conflicts</c>.</summary>
    public IReadOnlyList<GitFileRow> SectionRows(string section) => section switch
    {
        "staged" => Staged,
        "conflicts" => Conflicts,
        _ => Changes,
    };

    /// <summary>
    /// The section's files at or under <paramref name="path"/> (a file, a folder or the repository's folder), every one
    /// or (<paramref name="shownOnly"/>) only those the filter shows.
    /// </summary>
    public IReadOnlyList<GitFileRow> RowsUnder(string path, string section, bool shownOnly)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(path);
        return [.. SectionRows(section).Where(r => (!shownOnly || Matches(r)) && IsUnder(r.Status.Path, full))];

        static bool IsUnder(string file, string folder) =>
            Workbench.Projects.SolutionService.PathComparer.Equals(file, folder)
            || file.StartsWith(folder + System.IO.Path.DirectorySeparatorChar, Workbench.Projects.SolutionService.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tree's node for <paramref name="path"/> in <paramref name="section"/>, or null.</summary>
    public GitChangeNode? FindNode(string section, string path)
    {
        var tree = section switch { "staged" => StagedTree, "conflicts" => ConflictsTree, _ => ChangesTree };
        return tree.SelectMany(Flatten).FirstOrDefault(n => Workbench.Projects.SolutionService.PathComparer.Equals(n.FullPath, System.IO.Path.TrimEndingDirectorySeparator(path)));

        static IEnumerable<GitChangeNode> Flatten(GitChangeNode node) => node.Children.SelectMany(Flatten).Prepend(node);
    }

    /// <summary>Expand All and Collapse All on a folder or the repository node: it and every folder under it.</summary>
    public void SetExpandedUnder(string section, string path, bool expanded)
    {
        if (FindNode(section, path) is not { } node)
        {
            return;
        }

        Apply(node);

        void Apply(GitChangeNode n)
        {
            if (n.IsFile)
            {
                return;
            }

            n.IsExpanded = expanded;
            foreach (var child in n.Children)
            {
                Apply(child);
            }
        }
    }

    /// <summary>The staged section has files, but the filter shows none of them.</summary>
    public bool StagedFilteredOut => IsFiltered && Staged.Count > 0 && !Staged.Any(Matches);

    public bool ChangesFilteredOut => IsFiltered && Changes.Count > 0 && !Changes.Any(Matches);

    /// <summary>"No files match “Greeter”." in a section the filter empties (T6).</summary>
    public string FilteredOutText => Localizer.Format("msg.gitChanges.noFilesMatch", "No files match “{0}”.", _filterText.Trim());

    private static string Header(string title, int shown, int total) => shown < total
        ? Localizer.Format("msg.gitChanges.sectionOfTotal", "{0} ({1} of {2})", title, shown, total)
        : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{title} ({total})");

    private void OnExpandedChanged(GitChangeNode node, bool expanded)
    {
        if (expanded)
        {
            _collapsed.Remove(node.Key);
        }
        else
        {
            _collapsed.Add(node.Key);
        }
    }

    private IEnumerable<GitFileRow> Sorted(IEnumerable<GitFileRow> rows)
    {
        var ordered = _sortByPath
            ? rows.OrderBy(r => r.Location, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Location, StringComparer.OrdinalIgnoreCase);
        return _sortDescending ? ordered.Reverse() : ordered;
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <summary>
    /// The tree of <paramref name="rows"/>: one repository node (none without rows), folders first then files, each by
    /// name; a folder with nothing but one folder in it is shown with it (<c>src\Data</c>). Keys in
    /// <paramref name="collapsed"/> start collapsed; the repository node's is <paramref name="key"/> (<c>changes:</c> by
    /// default; with all repositories shown, one per repository), its text <paramref name="text"/> (the folder by default).
    /// </summary>
    public static IReadOnlyList<GitChangeNode> BuildTree(string root, IEnumerable<GitFileRow> rows, string section, bool staged, IReadOnlySet<string> collapsed, Action<GitChangeNode, bool>? expandedChanged = null, IReadOnlyList<GitFileRow>? all = null, string? key = null, string? text = null)
    {
        var top = new TreeFolder();
        foreach (var row in rows)
        {
            var folder = top;
            var segments = row.Status.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments[..^1])
            {
                if (!folder.Folders.TryGetValue(segment, out var child))
                {
                    folder.Folders[segment] = child = new TreeFolder();
                }

                folder = child;
            }

            folder.Files.Add(row);
        }

        if (top.Folders.Count == 0 && top.Files.Count == 0)
        {
            return [];
        }

        key ??= section + ":";
        var rootPath = root.Replace('/', System.IO.Path.DirectorySeparatorChar);
        var repository = new GitChangeNode(key, text ?? rootPath, null, isRepository: true, staged, !collapsed.Contains(key), expandedChanged) { FullPath = rootPath, Section = section };
        Add(repository, top, key, string.Empty);
        var everything = (all ?? []).Select(r => r.Status.RelativePath.Replace('\\', '/')).ToList();
        Count(repository);
        return [repository];

        // Each folder's files in the section, shown or not (all: the section's rows before the filter).
        void Count(GitChangeNode node)
        {
            if (node.IsFile)
            {
                return;
            }

            var prefix = node.IsRepository ? string.Empty : node.RelativePath + "/";
            node.TotalFiles = all is null ? node.ShownFiles : everything.Count(p => p.StartsWith(prefix, StringComparison.Ordinal));
            foreach (var child in node.Children)
            {
                Count(child);
            }
        }

        void Add(GitChangeNode parent, TreeFolder folder, string parentKey, string parentRelative)
        {
            foreach (var (name, child) in folder.Folders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                var names = new List<string> { name };
                var contents = child;
                while (contents.Files.Count == 0 && contents.Folders.Count == 1)
                {
                    var only = contents.Folders.Single();
                    names.Add(only.Key);
                    contents = only.Value;
                }

                var folderKey = parentKey + "/" + string.Join('/', names);
                var relative = (parentRelative.Length > 0 ? parentRelative + "/" : string.Empty) + string.Join('/', names);
                var node = new GitChangeNode(folderKey, string.Join(System.IO.Path.DirectorySeparatorChar, names), null, isRepository: false, staged, !collapsed.Contains(folderKey), expandedChanged)
                {
                    FullPath = System.IO.Path.Combine(rootPath, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                    RelativePath = relative,
                    Section = section,
                };
                parent.Children.Add(node);
                Add(node, contents, folderKey, relative);
            }

            foreach (var file in folder.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                parent.Children.Add(new GitChangeNode(parentKey + "/" + file.Name, file.Name, file, isRepository: false, staged, expanded: false)
                {
                    FullPath = file.Status.Path,
                    RelativePath = file.Status.RelativePath.Replace('\\', '/'),
                    Section = section,
                });
            }
        }
    }

    private sealed class TreeFolder
    {
        public Dictionary<string, TreeFolder> Folders { get; } = new(StringComparer.Ordinal);

        public List<GitFileRow> Files { get; } = [];
    }

    public bool HasRepository => _git.Status is not null;

    public bool HasNoRepository => !HasRepository;

    /// <summary>
    /// The current branch, or the commit HEAD is detached at. With all repositories shown, their branch when they're all on
    /// the same one, else "3 branches".
    /// </summary>
    public string BranchName
    {
        get
        {
            if (!_isAllRepositories)
            {
                return BranchOf(_git.Status);
            }

            var branches = Services.Select(s => BranchOf(s.Status)).Distinct(StringComparer.Ordinal).ToList();
            return branches.Count == 1 ? branches[0] : Localizer.Format("msg.gitChanges.branchCount", "{0} branches", branches.Count);
        }
    }

    private static string BranchOf(GitStatus? status) => status is null
        ? string.Empty
        : status.Branch ?? Localizer.Format("msg.gitChanges.detachedAt", "(detached at {0})", status.Head?[..Math.Min(7, status.Head.Length)]);

    /// <summary>The branch button's tooltip: the branch, or each repository's ("WPylon: develop") with all shown.</summary>
    public string BranchTooltip => _isAllRepositories
        ? string.Join(Environment.NewLine, Services.Select(s => $"{s.Name}: {BranchOf(s.Status)}"))
        : BranchName;

    /// <summary>The branch button's accessible name (B20): "Branch: main".</summary>
    public string BranchAccessibleName => Localizer.Format("msg.gitChanges.branchAccessibleName", "Branch: {0}", BranchName);

    /// <summary>Whether the branch tracks a remote branch (otherwise outgoing and incoming commits are unknown).</summary>
    public bool IsPublished => _git.Status?.Upstream is not null;

    public bool IsNotPublished => HasRepository && !IsPublished;

    private bool _hasRemote = true;

    /// <summary>The repository has a remote (set by the shell from <c>git remote</c>): without one, the link publishes to a Git host (B18).</summary>
    public bool HasRemote
    {
        get => _hasRemote;
        set
        {
            if (SetField(ref _hasRemote, value))
            {
                OnPropertyChanged(nameof(ShowPublishBranch));
                OnPropertyChanged(nameof(ShowPublishToHost));
                OnPropertyChanged(nameof(OutgoingIncomingTooltip));
            }
        }
    }

    /// <summary>HEAD is detached: no branch to push or pull (B14).</summary>
    public bool IsDetached => _git.Status is { Branch: null };

    /// <summary>The repository has no commits yet (B16).</summary>
    public bool IsUnborn => _git.Status is { Branch: not null, Head: null };

    /// <summary>Publish branch: a branch with no upstream in a repository with a remote (question 19); one repository's only.</summary>
    public bool ShowPublishBranch => !_isAllRepositories && IsNotPublished && _hasRemote && !IsDetached && !IsUnborn;

    /// <summary>Publish to a Git Host…: a repository with no remote (B18), not detached and with commits; one repository's only.</summary>
    public bool ShowPublishToHost => !_isAllRepositories && HasRepository && !_hasRemote && !IsDetached && !IsUnborn;

    /// <summary>"2 / 1": the commits to push (outgoing) and to pull (incoming); with all repositories shown, theirs added up.</summary>
    public string OutgoingIncomingText => _isAllRepositories
        ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{Services.Sum(s => s.Status?.Ahead ?? 0)} / {Services.Sum(s => s.Status?.Behind ?? 0)}")
        : _git.Status is { } status
            ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{status.Ahead} / {status.Behind}")
            : string.Empty;

    /// <summary>What the counts mean: one repository's, or with all shown a line for each ("WPylon: 1 outgoing / 0 incoming (tracking origin/develop)").</summary>
    public string OutgoingIncomingTooltip => _isAllRepositories
        ? string.Join(Environment.NewLine, Services.Select(s => s.Name + ": " + TrackingOf(s.Status)))
        : TrackingOf(_git.Status);

    private string TrackingOf(GitStatus? gitStatus) => gitStatus switch
    {
        { Upstream: { } upstream } status => Localizer.Format("msg.gitChanges.outgoingIncoming", "{0} outgoing / {1} incoming (tracking {2})", status.Ahead, status.Behind, upstream),
        { Branch: null } status => Localizer.Format("msg.gitChanges.headIsDetached", "HEAD is detached at {0}: there is no branch to push or pull.", status.Head?[..Math.Min(7, status.Head.Length)]),
        { Head: null } => Localizer.Get("msg.gitChanges.noCommitsYet", "No commits yet: there is nothing to push."),
        not null when !_hasRemote => Localizer.Get("msg.gitChanges.noRemote", "The repository has no remote: publish it to a Git host, or add one with Manage Remotes… in the ⋯ menu."),
        _ => Localizer.Get("msg.gitChanges.notPublished", "The branch isn't published: push it to create its remote branch."),
    };

    public bool IsMerging => _git.Status?.IsMerging ?? false;

    private string? _operation;
    private GitService? _operationRepository;

    /// <summary>
    /// The repository whose operation the banner shows, set by the shell with <see cref="Operation"/>: with all
    /// repositories shown, the first one that has one (its Continue, Skip and Abort act there); else the open one.
    /// </summary>
    public GitService? OperationRepository
    {
        get => _operationRepository;
        set
        {
            if (SetField(ref _operationRepository, value))
            {
                OnPropertyChanged(nameof(OperationText));
            }
        }
    }

    /// <summary>The merge, rebase, cherry-pick or revert in progress (M13); the banner offers Continue, Skip and Abort.</summary>
    public string? Operation
    {
        get => _operation;
        set
        {
            if (SetField(ref _operation, value))
            {
                OnPropertyChanged(nameof(HasOperation));
                OnPropertyChanged(nameof(OperationText));
                OnPropertyChanged(nameof(CanSkip));
            }
        }
    }

    public bool HasOperation => _operation is not null;

    public bool CanSkip => _operation is "rebase" or "cherry-pick" or "revert";

    public string OperationText => (_isAllRepositories && _operationRepository is { } repository ? repository.Name + ": " : string.Empty) + _operation switch
    {
        "merge" => "A merge is in progress.",
        "rebase" => "A rebase is in progress.",
        "cherry-pick" => "A cherry-pick is in progress.",
        "revert" => "A revert is in progress.",
        _ => string.Empty,
    } + (HasConflicts ? " Resolve the conflicts, then Continue." : " Continue when ready.");

    public bool HasStaged => Staged.Count > 0;

    public bool HasChanges => Changes.Count > 0;

    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>"Staged Changes (2)", or "(0 of 2)" while a filter hides some (T6).</summary>
    public string StagedHeader => Header(Localizer.Get("msg.gitChanges.stagedChanges", "Staged Changes"), Staged.Count(Matches), Staged.Count);

    public string ChangesHeader => Changes.Count > 0 ? Header(Localizer.Get("msg.gitChanges.changes", "Changes"), Changes.Count(Matches), Changes.Count) : Localizer.Get("msg.gitChanges.changes", "Changes");

    /// <summary>
    /// The commit button commits the staged changes when there are some, otherwise every change; with all repositories
    /// shown, in each repository that has some (Commit All Repos).
    /// </summary>
    public string CommitText => HasStaged
        ? Localizer.Get("msg.gitChanges.commitStaged", "Commit Staged")
        : _isAllRepositories
            ? Localizer.Get("msg.gitChanges.commitAllRepos", "Commit All Repos")
            : Localizer.Get("msg.gitChanges.commitAll", "Commit All");

    public string CommitAndPushText => HasStaged
        ? Localizer.Get("msg.gitChanges.commitStagedAndPush", "Commit Staged and Push")
        : _isAllRepositories
            ? Localizer.Get("msg.gitChanges.commitAllReposAndPush", "Commit All Repos and Push")
            : Localizer.Get("msg.gitChanges.commitAllAndPush", "Commit All and Push");

    public string CommitAndSyncText => HasStaged
        ? Localizer.Get("msg.gitChanges.commitStagedAndSync", "Commit Staged and Sync")
        : _isAllRepositories
            ? Localizer.Get("msg.gitChanges.commitAllReposAndSync", "Commit All Repos and Sync")
            : Localizer.Get("msg.gitChanges.commitAllAndSync", "Commit All and Sync");

    /// <summary>A message is required, and something to commit (or the last commit to amend).</summary>
    public bool CanCommit => !string.IsNullOrWhiteSpace(_message) && (HasStaged || HasChanges || Amend);

    /// <summary>Amend is one repository's: hidden while all are shown.</summary>
    public bool CanAmend => !_isAllRepositories;

    public string ConflictsHeader => Header(Localizer.Get("msg.gitChanges.mergeConflicts", "Merge Conflicts"), Conflicts.Count(Matches), Conflicts.Count);

    public string Message
    {
        get => _message;
        set
        {
            if (SetField(ref _message, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanCommit));
                UpdateRelatedItems();
            }
        }
    }

    // ------------------------------------------------------------------ related items

    private readonly Dictionary<string, string> _knownTitles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The issues and work items the message mentions (<c>#12</c>), in order.</summary>
    public ObservableCollection<RelatedItem> RelatedItems { get; } = [];

    public bool HasRelatedItems => RelatedItems.Count > 0;

    public bool HasNoRelatedItems => RelatedItems.Count == 0;

    /// <summary>Remembers a completion's title, which the related items then show.</summary>
    public void RememberItem(CommitCompletion item)
    {
        _knownTitles[item.Reference] = item.Title;
        UpdateRelatedItems(force: true);
    }

    /// <summary>The <c>#123</c> references in <paramref name="message"/>, each once, in order.</summary>
    public static IReadOnlyList<string> MentionedItems(string message) =>
        [.. MentionRegex().Matches(message).Select(m => m.Value).Distinct(StringComparer.Ordinal)];

    private void UpdateRelatedItems(bool force = false)
    {
        var items = MentionedItems(_message).Select(r => new RelatedItem(r, _knownTitles.GetValueOrDefault(r))).ToList();
        if (!force && items.SequenceEqual(RelatedItems))
        {
            return;
        }

        RelatedItems.Clear();
        foreach (var item in items)
        {
            RelatedItems.Add(item);
        }

        OnPropertyChanged(nameof(HasRelatedItems));
        OnPropertyChanged(nameof(HasNoRelatedItems));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w#])#\d+\b")]
    private static partial System.Text.RegularExpressions.Regex MentionRegex();

    // ------------------------------------------------------------------ stashes

    private int _stashVersion;

    /// <summary>The repository's stashes, newest first.</summary>
    public ObservableCollection<GitStash> Stashes { get; } = [];

    public bool HasStashes => Stashes.Count > 0;

    public bool HasNoStashes => Stashes.Count == 0;

    public string StashesHeader => Stashes.Count > 0 ? $"Stashes ({Stashes.Count})" : "Stashes";

    /// <summary>Reads the stashes again (after every status change: stashing changes the working tree).</summary>
    public async Task LoadStashesAsync()
    {
        var version = ++_stashVersion;
        var stashes = new List<GitStash>();
        var all = _isAllRepositories;
        foreach (var git in Services.Where(s => s.Root is not null))
        {
            try
            {
                stashes.AddRange((await git.StashesAsync().ConfigureAwait(true)).Select(s => all ? s with { Repository = git.Root } : s));
            }
            catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
            {
                return;
            }
        }

        if (version != _stashVersion || stashes.SequenceEqual(Stashes))
        {
            return;
        }

        Stashes.Clear();
        foreach (var stash in stashes)
        {
            Stashes.Add(stash);
        }

        OnPropertyChanged(nameof(HasStashes));
        OnPropertyChanged(nameof(HasNoStashes));
        OnPropertyChanged(nameof(StashesHeader));
    }

    public Task ApplyStashAsync(GitStash stash, bool pop) => StashRepository(stash).ApplyStashAsync(stash, pop);

    /// <summary>Drops a stash, after the shell asks (in its repository, with all shown: <see cref="DropStash"/>).</summary>
    public Task DropStashAsync(GitStash stash) => DropStash is { } drop ? drop(stash) : _run("git.dropStash:" + stash.Name);

    /// <summary>Drops a stash of a repository after asking, set by the shell.</summary>
    public Func<GitStash, Task>? DropStash { get; set; }

    /// <summary>The repository a stash is in.</summary>
    public GitService StashRepository(GitStash stash) => stash.Repository is { } root ? _git.ServiceAt(root) ?? _git : _git;

    /// <summary>Amend the last commit: fills in its message (and restores the draft when unchecked).</summary>
    public bool Amend
    {
        get => _amend;
        set
        {
            if (!SetField(ref _amend, value))
            {
                return;
            }

            if (value)
            {
                _messageBeforeAmend = Message;
                _ = FillLastMessageAsync();
            }
            else
            {
                Message = _messageBeforeAmend ?? string.Empty;
            }

            OnPropertyChanged(nameof(CanCommit));
        }
    }

    private async Task FillLastMessageAsync()
    {
        if (await _git.LastCommitMessageAsync().ConfigureAwait(true) is { } last && Amend && Message == (_messageBeforeAmend ?? string.Empty))
        {
            Message = last;
        }
    }

    // ------------------------------------------------------------------ M13b: repositories, CI status, pull request, LFS, completion

    private GitLfsStatus _lfs = GitLfsStatus.None;
    private bool _isAllRepositories;
    private bool _updatingRepositories;
    private GitRepositoryChoice? _selectedRepository;
    private string? _ciState;
    private string _ciText = string.Empty;
    private string? _ciTooltip;
    private string? _pullRequestText;

    /// <summary>The repository's Git LFS files and locks: the rows' badges.</summary>
    public GitLfsStatus Lfs
    {
        get => _lfs;
        set
        {
            _lfs = value;
            Refresh();
        }
    }

    /// <summary>
    /// The repository picker's choices when the solution spans several repositories: All Repositories (ADR 0085), then
    /// each repository; none with one.
    /// </summary>
    public ObservableCollection<GitRepositoryChoice> Repositories { get; } = [];

    /// <summary>The choices the picker's filter box shows.</summary>
    public ObservableCollection<GitRepositoryChoice> ShownRepositories { get; } = [];

    public bool HasSeveralRepositories => Repositories.Count > 1;

    /// <summary>Every repository of the solution is shown (All Repositories, ADR 0085), not only the open one.</summary>
    public bool IsAllRepositories => _isAllRepositories;

    /// <summary>The picker's button: "2 Repositories" with all shown, else the open repository's name.</summary>
    public string RepositoryButtonText => _isAllRepositories
        ? Localizer.Format("msg.gitChanges.repositoriesCount", "{0} Repositories", Repositories.Count(r => !r.IsAll))
        : _selectedRepository?.Name ?? _git.Name;

    /// <summary>Opens another of the solution's repositories (set by the shell).</summary>
    public Func<string, Task>? SwitchRepository { get; set; }

    /// <summary>Shows all the solution's repositories (set by the shell).</summary>
    public Func<Task>? ShowAllRepositories { get; set; }

    /// <summary>Add Repository… (the picker's footer), set by the shell.</summary>
    public Func<Task>? AddRepository { get; set; }

    /// <summary>Leaves a repository out of the solution's repositories (a choice's Remove), set by the shell.</summary>
    public Func<string, Task>? RemoveRepository { get; set; }

    public GitRepositoryChoice? SelectedRepository
    {
        get => _selectedRepository;
        set
        {
            if (!SetField(ref _selectedRepository, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RepositoryButtonText));
            if (_updatingRepositories || value is null)
            {
                return;
            }

            if (value.IsAll)
            {
                _ = ShowAllRepositories?.Invoke();
            }
            else if (SwitchRepository is { } switchTo)
            {
                _ = switchTo(value.Path);
            }
        }
    }

    private string _repositoryFilter = string.Empty;

    /// <summary>The picker's filter box: shows the repositories whose name or folder contains it.</summary>
    public string RepositoryFilter
    {
        get => _repositoryFilter;
        set
        {
            if (SetField(ref _repositoryFilter, value ?? string.Empty))
            {
                FilterRepositories();
            }
        }
    }

    private void FilterRepositories()
    {
        var filter = _repositoryFilter.Trim();
        Fill(ShownRepositories, Repositories.Where(r => filter.Length == 0 || (!r.IsAll && (r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)))));
    }

    /// <summary>
    /// The solution's repositories (its own first), <paramref name="active"/> the open one, and whether all are shown
    /// (<paramref name="all"/>, ADR 0085); <paramref name="removable"/> are the ones the picker can leave out.
    /// </summary>
    public void SetRepositories(IReadOnlyList<string> roots, string? active, bool all = false, IReadOnlyCollection<string>? removable = null)
    {
        _updatingRepositories = true;
        try
        {
            var several = roots.Count > 1;
            _isAllRepositories = all && several;
            Repositories.Clear();
            if (several)
            {
                Repositories.Add(GitRepositoryChoice.All(roots.Count) with { IsSelected = _isAllRepositories });
                foreach (var root in roots)
                {
                    var isActive = !_isAllRepositories && active is not null && Workbench.Projects.SolutionService.PathComparer.Equals(root, active);
                    Repositories.Add(new GitRepositoryChoice(root)
                    {
                        CanRemove = removable?.Contains(root, Workbench.Projects.SolutionService.PathComparer) == true,
                        IsSelected = isActive,
                    });
                }
            }

            SelectedRepository = Repositories.FirstOrDefault(r => r.IsSelected);
            FilterRepositories();
            OnPropertyChanged(nameof(HasSeveralRepositories));
            OnPropertyChanged(nameof(IsAllRepositories));
            OnPropertyChanged(nameof(RepositoryButtonText));
            OnPropertyChanged(nameof(CanAmend));
            if (!CanAmend && _amend)
            {
                Amend = false;
            }
        }
        finally
        {
            _updatingRepositories = false;
        }

        Refresh();
    }

    /// <summary>The branch's CI status: <c>success</c>, <c>failure</c>, <c>pending</c>, or null when unknown.</summary>
    public string? CiState => _ciState;

    public string CiText => _ciText;

    public string? CiTooltip => _ciTooltip;

    public bool HasCi => _ciState is not null and not "none";

    public string CiIcon => _ciState switch { "success" => "test-passed", "failure" => "test-failed", _ => "test-running" };

    public bool IsCiPassed => _ciState == "success";

    public bool IsCiFailed => _ciState == "failure";

    public bool IsCiPending => _ciState == "pending";

    public void SetCi(string? state, string text, string? tooltip)
    {
        _ciState = state;
        _ciText = text;
        _ciTooltip = tooltip;
        foreach (var name in new[] { nameof(CiState), nameof(CiText), nameof(CiTooltip), nameof(HasCi), nameof(CiIcon), nameof(IsCiPassed), nameof(IsCiFailed), nameof(IsCiPending) })
        {
            OnPropertyChanged(name);
        }
    }

    // ------------------------------------------------------------------ fetch, pull, push and sync while they run

    private readonly List<GitProgress> _progress = [];

    /// <summary>"Fetching…", "Pulling…": the latest fetch, pull, push or sync running, shown under the branch as the classic IDE does; null when none runs.</summary>
    public string? ProgressText => _progress.Count == 0 ? null
        : _progress[^1].IsCancellationRequested ? Localizer.Get("msg.gitChanges.cancelling", "Cancelling…")
        : _progress[^1].Text;

    /// <summary>A fetch, pull, push or sync is running: the window shows it over a running bar, with Cancel in place of Fetch, Pull, Push and Sync.</summary>
    public bool IsBusy => _progress.Count > 0;

    public bool IsIdle => _progress.Count == 0;

    /// <summary>Shows <paramref name="text"/> until the returned progress is disposed; Cancel cancels its token.</summary>
    public GitProgress BeginProgress(string text)
    {
        var progress = new GitProgress(this, text);
        _progress.Add(progress);
        ProgressChanged();
        return progress;
    }

    /// <summary>Cancel: stops every fetch, pull, push and sync running (git is killed through each one's token).</summary>
    public void CancelProgress()
    {
        foreach (var progress in _progress.ToList())
        {
            progress.Cancel();
        }
    }

    internal void EndProgress(GitProgress progress)
    {
        if (_progress.Remove(progress))
        {
            ProgressChanged();
        }
    }

    internal void ProgressChanged()
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsIdle));
    }

    /// <summary>"#1 Add the greeter": the open pull request of the current branch.</summary>
    public string? PullRequestText
    {
        get => _pullRequestText;
        set
        {
            if (SetField(ref _pullRequestText, value))
            {
                OnPropertyChanged(nameof(HasPullRequest));
            }
        }
    }

    public bool HasPullRequest => _pullRequestText is not null;

    /// <summary>Searches issues and work items for <c>#</c> completion in the message (set by the shell when signed in).</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<CommitCompletion>>>? CompletionSource { get; set; }

    /// <summary>The <c>#123</c> or <c>#words</c> being typed before <paramref name="caret"/>: where its <c>#</c> is, and what follows it.</summary>
    public static (int Start, string Query)? CompletionQuery(string text, int caret)
    {
        if (caret < 1 || caret > text.Length)
        {
            return null;
        }

        var start = caret - 1;
        while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] is '_' or '-'))
        {
            start--;
        }

        return start >= 0 && text[start] == '#' && (start == 0 || char.IsWhiteSpace(text[start - 1]) || text[start - 1] is '(' or '[')
            ? (start, text[(start + 1)..caret])
            : null;
    }

    /// <summary>The completions at the caret, or none when no <c>#</c> is being typed (or nobody is signed in).</summary>
    public async Task<IReadOnlyList<CommitCompletion>> CompleteAsync(string text, int caret, CancellationToken cancellationToken)
    {
        if (CompletionSource is not { } source || CompletionQuery(text, caret) is not { } query)
        {
            return [];
        }

        return await source(query.Query, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Replaces the <c>#…</c> before the caret with the reference and a space; returns the text and the new caret.</summary>
    public static (string Text, int Caret) ApplyCompletion(string text, int caret, string reference)
    {
        if (CompletionQuery(text, caret) is not { } query)
        {
            return (text, caret);
        }

        // A space after it, unless one follows already; the caret goes past it.
        var rest = text[caret..];
        var inserted = rest.Length > 0 && char.IsWhiteSpace(rest[0]) ? reference : reference + " ";
        return (text[..query.Start] + inserted + rest, query.Start + reference.Length + 1);
    }

    public void Refresh()
    {
        var services = Services.Select((git, order) => (git, order)).Where(s => s.git.Status is not null).ToList();
        Replace(Staged, services.SelectMany(s => s.git.Status!.Staged.Select(f => Row(s.git, s.order, f, staged: true))));
        Replace(Changes, services.SelectMany(s => s.git.Status!.Unstaged.Select(f => Row(s.git, s.order, f, staged: false))));
        Replace(Conflicts, services.SelectMany(s => s.git.Status!.Conflicts.Select(f => Row(s.git, s.order, f, staged: false))));
        RebuildViews();
        foreach (var name in new[]
        {
            nameof(HasRepository), nameof(HasNoRepository), nameof(BranchName), nameof(BranchTooltip), nameof(BranchAccessibleName), nameof(IsPublished), nameof(IsNotPublished), nameof(OutgoingIncomingText),
            nameof(IsDetached), nameof(IsUnborn), nameof(ShowPublishBranch), nameof(ShowPublishToHost),
            nameof(OutgoingIncomingTooltip), nameof(IsMerging), nameof(HasStaged), nameof(HasChanges), nameof(HasConflicts), nameof(StagedHeader),
            nameof(ChangesHeader), nameof(ConflictsHeader), nameof(OperationText), nameof(CommitText), nameof(CommitAndPushText), nameof(CommitAndSyncText),
            nameof(CanCommit),
        })
        {
            OnPropertyChanged(name);
        }

        _ = LoadStashesAsync();

        GitFileRow Row(GitService git, int order, GitFileStatus file, bool staged)
        {
            var repository = git.Name;
            var folder = System.IO.Path.GetDirectoryName(file.RelativePath.Replace('\\', '/'))?.Replace('/', System.IO.Path.DirectorySeparatorChar) ?? string.Empty;
            return new GitFileRow(file, staged, _lfs)
            {
                Location = folder.Length > 0 ? repository + System.IO.Path.DirectorySeparatorChar + folder : repository,
                Repository = git.Root ?? string.Empty,
                RepositoryOrder = order,
            };
        }
    }

    private static void Replace(ObservableCollection<GitFileRow> target, IEnumerable<GitFileRow>? rows)
    {
        target.Clear();
        foreach (var row in (rows ?? []).OrderBy(r => r.RepositoryOrder).ThenBy(r => r.Status.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(row);
        }
    }

    public Task StageAsync(GitFileRow row) => EachRepositoryAsync([row], (git, paths) => git.StageAsync(paths));

    public Task UnstageAsync(GitFileRow row) => EachRepositoryAsync([row], (git, paths) => git.UnstageAsync(paths));

    public Task StageAllAsync() => Changes.Count == 0 ? Task.CompletedTask : EachRepositoryAsync(Changes, (git, paths) => git.StageAsync(paths));

    public Task UnstageAllAsync() => Staged.Count == 0 ? Task.CompletedTask : EachRepositoryAsync(Staged, (git, paths) => git.UnstageAsync(paths));

    public Task OpenDiffAsync(GitFileRow row) => _openDiff(row.Status.Path, row.Staged);

    /// <summary>
    /// Commits the staged changes, or with <paramref name="all"/> every change (untracked files too: they're staged
    /// first); clears the message on success.
    /// </summary>
    public async Task CommitAsync(bool all)
    {
        if (string.IsNullOrWhiteSpace(Message))
        {
            throw new GitException("Enter a commit message.", -1);
        }

        if (_isAllRepositories)
        {
            await CommitAllRepositoriesAsync(all).ConfigureAwait(true);
        }
        else
        {
            if (all && Changes.Any(r => r.Status.IsUntracked))
            {
                await StageAllAsync().ConfigureAwait(true);
            }

            await _git.CommitAsync(Message, Amend, all).ConfigureAwait(true);
        }

        _messageBeforeAmend = null;
        _amend = false;
        OnPropertyChanged(nameof(Amend));
        Message = string.Empty;
    }

    /// <summary>
    /// Commit All Repos (ADR 0085): the same message in each repository that has something to commit (its staged changes,
    /// or with <paramref name="all"/> every change), carrying on past a repository that fails. When one fails, the message
    /// stays and the exception lists each failure; the others' commits stay.
    /// </summary>
    private async Task CommitAllRepositoriesAsync(bool all)
    {
        var message = Message;
        var targets = Services.Where(s => s.Status is { } status && (all ? status.Files.Any(f => !f.IsConflict) : status.Staged.Any())).ToList();
        if (targets.Count == 0)
        {
            throw new GitException(Localizer.Get("msg.gitChanges.nothingToCommit", "There is nothing to commit."), -1);
        }

        var failures = await GitService.ForEachAsync(targets, async git =>
        {
            if (all && git.Status?.Unstaged.Where(f => f.IsUntracked).Select(f => f.Path).ToList() is { Count: > 0 })
            {
                await git.StageAsync([.. git.Status!.Unstaged.Select(f => f.Path)]).ConfigureAwait(false);
            }

            await git.CommitAsync(message, amend: false, all).ConfigureAwait(false);
        }).ConfigureAwait(true);
        if (failures.Count > 0)
        {
            throw new GitException(FailureSummary(failures, targets.Count), -1);
        }
    }

    /// <summary>"1 of 2 repositories failed:" and a line for each ("WPylon: git's message").</summary>
    public static string FailureSummary(IReadOnlyList<GitRepositoryFailure> failures, int total) =>
        Localizer.Format("msg.gitChanges.repositoriesFailed", "{0} of {1} repositories failed:", failures.Count, total)
        + Environment.NewLine + string.Join(Environment.NewLine, failures);

    /// <summary>Commits (staged changes when there are some, otherwise all), then pushes, or with <paramref name="sync"/> pulls and pushes.</summary>
    public async Task CommitAndPushAsync(bool sync)
    {
        await CommitAsync(all: !HasStaged).ConfigureAwait(true);
        await _run(sync ? "git.sync" : "git.push").ConfigureAwait(true);
    }

    /// <summary>Runs a shell command (fetch, pull, push, discard…), which reports its own errors.</summary>
    public Task RunAsync(string command) => _run(command);

    /// <summary>The branch drop-down's list (D38), set by the shell.</summary>
    public GitBranchPickerViewModel? Branches { get; set; }

    /// <summary>A node's menu from gitChanges/context: its path, section, kind (file, folder, repository) and the files shown; set by the shell.</summary>
    public Func<string, string, string, IReadOnlyList<string>, Avalonia.Controls.ContextMenu?>? MenuFor { get; set; }

    /// <summary>The menu of a tree node (null when no entry applies).</summary>
    public Avalonia.Controls.ContextMenu? MenuOf(GitChangeNode node) =>
        MenuFor?.Invoke(node.FullPath, node.Section, node.IsRepository ? "repository" : node.IsFile ? "file" : "folder", [.. node.Rows.Select(r => r.Status.Path)]);

    /// <summary>The menu of a list row (a file).</summary>
    public Avalonia.Controls.ContextMenu? MenuOf(GitFileRow row) =>
        MenuFor?.Invoke(row.Status.Path, SectionOf(row), "file", [row.Status.Path]);

    /// <summary>Runs a command with arguments (the hover buttons run the menu's commands on a node: D38's T1), set by the shell.</summary>
    public Func<string, IReadOnlyList<System.Text.Json.JsonElement>, Task>? RunWithArguments { get; set; }

    /// <summary>
    /// The arguments of <c>gitChanges/context</c>'s commands for a node: its path, its section and the files the window
    /// shows under it (question 2).
    /// </summary>
    public static IReadOnlyList<System.Text.Json.JsonElement> ArgumentsFor(string path, string section, IEnumerable<string> shown) =>
    [
        System.Text.Json.JsonSerializer.SerializeToElement(path, Docking.ShellJsonContext.Default.String),
        System.Text.Json.JsonSerializer.SerializeToElement(section, Docking.ShellJsonContext.Default.String),
        System.Text.Json.JsonSerializer.SerializeToElement(shown.ToArray(), Docking.ShellJsonContext.Default.StringArray),
    ];

    /// <summary>Runs <paramref name="command"/> on a tree node, with the files shown under it.</summary>
    public Task RunOnAsync(string command, GitChangeNode node) =>
        RunWithArguments is { } run ? run(command, ArgumentsFor(node.FullPath, node.Section, node.Rows.Select(r => r.Status.Path))) : Task.CompletedTask;

    /// <summary>Runs <paramref name="command"/> on a list row (a file).</summary>
    public Task RunOnAsync(string command, GitFileRow row) =>
        RunWithArguments is { } run ? run(command, ArgumentsFor(row.Status.Path, SectionOf(row), [row.Status.Path])) : Task.CompletedTask;

    /// <summary>The section a file row is in.</summary>
    public string SectionOf(GitFileRow row) => row.Staged ? "staged" : Conflicts.Contains(row) ? "conflicts" : "changes";
}
