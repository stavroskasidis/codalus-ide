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

    /// <summary>The same file shown the same way: its status, section, repository and LFS badge (a refresh keeps such a row).</summary>
    internal bool IsSameAs(GitFileRow other) =>
        Status == other.Status && Staged == other.Staged && RepositoryOrder == other.RepositoryOrder
        && string.Equals(Repository, other.Repository, StringComparison.Ordinal) && string.Equals(Location, other.Location, StringComparison.Ordinal)
        && Equals(_lfsFile, other._lfsFile) && Equals(_lock, other._lock);
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

    /// <summary>Expands or collapses it without telling its owner, which does (Git Changes' rows).</summary>
    internal void SetExpanded(bool expanded)
    {
        if (SetField(ref _isExpanded, expanded, nameof(IsExpanded)))
        {
            OnPropertyChanged(nameof(Icon));
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

    /// <summary>The changed files at and under it that the window shows (a filter can hide some), counted as the tree is built.</summary>
    public int ShownFiles { get; init; } = row is null ? 0 : 1;

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
/// One visible line of a Git Changes section's tree, as the Solution Explorer's rows: the tree is flattened so the list
/// can virtualize (M56: a tree view made controls for every file, seconds for 10,000). A refresh keeps the row of a node
/// that's still in its place, with its new node, so its controls, the selection and the scroll position stay.
/// </summary>
public sealed class GitChangeRow(GitChangeNode node, int depth) : ObservableObject
{
    private GitChangeNode _node = node;

    public GitChangeNode Node
    {
        get => _node;
        set
        {
            var old = _node;
            if (ReferenceEquals(old, value))
            {
                return;
            }

            _node = value;

            // Only what differs: unchanged rows raise nothing, and formatting every row's texts again took the UI thread's time.
            var file = old.Row is { } before && value.Row is { } after ? !before.IsSameAs(after) : !ReferenceEquals(old.Row, value.Row);
            var counts = old.TotalFiles != value.TotalFiles || old.ShownFiles != value.ShownFiles;
            var expanded = old.IsExpanded != value.IsExpanded;
            if (!string.Equals(old.Text, value.Text, StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(Text));
            }

            if (file || expanded || old.Children.Count == 0 != (value.Children.Count == 0))
            {
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(HasChildren));
                OnPropertyChanged(nameof(Chevron));
                OnPropertyChanged(nameof(IsExpanded));
            }

            if (file)
            {
                foreach (var name in new[] { nameof(Letter), nameof(Description), nameof(Badge), nameof(HasBadge), nameof(BadgeTooltip), nameof(CanDiscard) })
                {
                    OnPropertyChanged(name);
                }
            }

            if (counts)
            {
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(HasCountText));
            }

            if (file || counts || expanded || !string.Equals(old.Text, value.Text, StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>The section and path that identify it across refreshes.</summary>
    public string Key => _node.Key;

    public int Depth { get; } = depth;

    /// <summary>The indentation of its level, 16 px each, as the tree view's.</summary>
    public Avalonia.Thickness Indent => new(Depth * 16, 0, 0, 0);

    public bool IsFile => _node.IsFile;

    /// <summary>The repository or a folder: it expands and collapses.</summary>
    public bool HasChildren => _node.Children.Count > 0;

    public bool IsExpanded => _node.IsExpanded;

    public string? Chevron => HasChildren ? (_node.IsExpanded ? "chevron-down" : "chevron-right") : null;

    public string Icon => _node.Icon;

    public string Text => _node.Text;

    public string? CountText => _node.CountText;

    public bool HasCountText => _node.HasCountText;

    public string? Letter => _node.Letter;

    public string? Description => _node.Description;

    public bool HasBadge => _node.HasBadge;

    public string? Badge => _node.Badge;

    public string? BadgeTooltip => _node.BadgeTooltip;

    public bool CanStage => _node.CanStage;

    public bool CanUnstage => _node.CanUnstage;

    public bool CanDiscard => _node.CanDiscard;

    /// <summary>The node's name, and a folder's state: a list's row has no expander for a screen reader to read.</summary>
    public string AccessibleName => !HasChildren ? _node.AccessibleName
        : _node.IsExpanded ? Localizer.Format("msg.gitChanges.expandedAccessibleName", "{0}, expanded", _node.AccessibleName)
        : Localizer.Format("msg.gitChanges.collapsedAccessibleName", "{0}, collapsed", _node.AccessibleName);

    /// <summary>Expands or collapses its node (its owner then shows or hides the rows under it).</summary>
    internal void SetExpanded(bool expanded)
    {
        if (_node.IsExpanded == expanded)
        {
            return;
        }

        _node.SetExpanded(expanded);
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(Chevron));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(AccessibleName));
    }
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

        // One refresh for a burst, the repository's and its siblings' (each read posted its own, and each rebuilt everything).
        git.StatusChanged += (_, _) => RequestRefresh(stashes: true);
        git.SiblingStatusChanged += (_, _) => RequestRefresh(stashes: true);
        Refresh();
    }

    /// <summary>The repositories the window shows: every one of the solution's in All Repositories (ADR 0085), else the open one.</summary>
    public IReadOnlyList<GitService> Services => _isAllRepositories ? _git.AllServices : [_git];

    /// <summary>
    /// The files with staged changes, kept current while the window is hidden (the commands read them); replaced at once,
    /// and only when a file changed.
    /// </summary>
    public ResettableCollection<GitFileRow> Staged { get; } = [];

    public ResettableCollection<GitFileRow> Changes { get; } = [];

    public ResettableCollection<GitFileRow> Conflicts { get; } = [];

    // ------------------------------------------------------------------ tree and list views

    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private bool _isTreeView = true;
    private bool _openFileAsDefault;
    private bool _showFilter;
    private string _filterText = string.Empty;
    private bool _sortByPath;
    private bool _sortDescending;
    private IReadOnlyList<GitChangeNode> _stagedTree = [];
    private IReadOnlyList<GitChangeNode> _changesTree = [];
    private IReadOnlyList<GitChangeNode> _conflictsTree = [];

    /// <summary>The staged changes as a tree: the repository, its folders, their files (built while the window is shown, in the tree view).</summary>
    public IReadOnlyList<GitChangeNode> StagedTree => _stagedTree;

    public IReadOnlyList<GitChangeNode> ChangesTree => _changesTree;

    public IReadOnlyList<GitChangeNode> ConflictsTree => _conflictsTree;

    /// <summary>The staged changes' tree as the list shows it: a row for each node of an expanded folder (or the repository).</summary>
    public ResettableCollection<GitChangeRow> StagedRows { get; } = [];

    public ResettableCollection<GitChangeRow> ChangesRows { get; } = [];

    public ResettableCollection<GitChangeRow> ConflictsRows { get; } = [];

    /// <summary>The staged changes as a list (filtered and sorted), built while the window is shown, in the list view.</summary>
    public ResettableCollection<GitFileRow> StagedItems { get; } = [];

    public ResettableCollection<GitFileRow> ChangesItems { get; } = [];

    public ResettableCollection<GitFileRow> ConflictsItems { get; } = [];

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
                Refresh();
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

    /// <summary>Shows only the files whose path contains this text, once typing pauses (the views are built off the UI thread).</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetField(ref _filterText, value ?? string.Empty))
            {
                (_filterRefresh ??= new UiDebouncer(Refresh, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(600))).Request();
            }
        }
    }

    private UiDebouncer? _filterRefresh;

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

        Refresh();
    }

    /// <summary>Opens a file (set by the shell), for <see cref="OpenFileAsDefault"/>.</summary>
    public Func<string, Task>? OpenFile { get; set; }

    /// <summary>The default action of a file: its changes, or the file itself with <see cref="OpenFileAsDefault"/>.</summary>
    public Task OpenAsync(GitFileRow row) =>
        OpenFileAsDefault && !row.IsDeleted && OpenFile is { } open ? open(row.Status.Path) : OpenDiffAsync(row);

    public Task OpenFileAsync(GitFileRow row) => OpenFile is { } open && !row.IsDeleted ? open(row.Status.Path) : Task.CompletedTask;

    public Task StageAsync(GitChangeNode node) => StageAsync(node.Rows);

    public Task UnstageAsync(GitChangeNode node) => UnstageAsync(node.Rows);

    /// <summary>Stages files of the window, in each one's repository.</summary>
    public Task StageAsync(IEnumerable<GitFileRow> rows) => EachRepositoryAsync(rows, (git, files) => git.StageAsync(files));

    /// <summary>Unstages files of the window, in each one's repository.</summary>
    public Task UnstageAsync(IEnumerable<GitFileRow> rows) => EachRepositoryAsync(rows, (git, files) => git.UnstageAsync(files));

    /// <summary>Discards the unstaged changes of files of the window (deletes the untracked ones), in each one's repository.</summary>
    public Task DiscardAsync(IEnumerable<GitFileRow> rows) => EachRepositoryAsync(rows, (git, files) => git.DiscardAsync(files));

    /// <summary>
    /// Runs <paramref name="action"/> on the rows' files in each one's repository (one, unless all are shown), by the
    /// paths git gave them: the status's paths are real already, and resolving each again held the UI thread for a
    /// second with 10,000 files (two stats per folder of each path on Linux and macOS).
    /// </summary>
    private async Task EachRepositoryAsync(IEnumerable<GitFileRow> rows, Func<GitService, IReadOnlyList<GitFileStatus>, Task> action)
    {
        var services = Services.Append(_git).Where(s => s.Root is not null).Distinct().ToDictionary(s => s.Root!, StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(r => r.Repository, StringComparer.Ordinal).OrderBy(g => g.First().RepositoryOrder))
        {
            // A row of a repository closed since (its paths are relative to it) waits for the refresh that removes it.
            if (services.GetValueOrDefault(group.Key) is { } git)
            {
                await action(git, [.. group.Select(r => r.Status)]).ConfigureAwait(true);
            }
        }
    }

    private bool Matches(GitFileRow row) => Matches(row, _filterText.Trim());

    private static bool Matches(GitFileRow row, string filter) =>
        filter.Length == 0 || row.Status.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase);

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

    private IReadOnlyList<GitChangeNode> TreeOf(string section) => section switch { "staged" => _stagedTree, "conflicts" => _conflictsTree, _ => _changesTree };

    /// <summary>The rows of a section's tree.</summary>
    public ResettableCollection<GitChangeRow> RowsOf(string section) => section switch { "staged" => StagedRows, "conflicts" => ConflictsRows, _ => ChangesRows };

    /// <summary>The tree's node for <paramref name="path"/> in <paramref name="section"/>, or null (the trees are built while the window is shown).</summary>
    public GitChangeNode? FindNode(string section, string path)
    {
        return TreeOf(section).SelectMany(Flatten).FirstOrDefault(n => Workbench.Projects.SolutionService.PathComparer.Equals(n.FullPath, System.IO.Path.TrimEndingDirectorySeparator(path)));

        static IEnumerable<GitChangeNode> Flatten(GitChangeNode node) => node.Children.SelectMany(Flatten).Prepend(node);
    }

    /// <summary>Expand All and Collapse All on a folder or the repository node: it and every folder under it, its rows shown again at once.</summary>
    public void SetExpandedUnder(string section, string path, bool expanded)
    {
        if (FindNode(section, path) is not { } node)
        {
            return;
        }

        Apply(node);
        KeepExpansion();
        var rows = new List<(GitChangeNode Node, int Depth)>();
        Flatten(TreeOf(section), 0, rows);
        PatchRows(RowsOf(section), rows);

        void Apply(GitChangeNode n)
        {
            if (n.IsFile)
            {
                return;
            }

            Remember(n.Key, expanded);
            n.SetExpanded(expanded);
            foreach (var child in n.Children)
            {
                Apply(child);
            }
        }
    }

    /// <summary>Expands or collapses a row (the chevron, Left and Right, double-click): only the rows under it change.</summary>
    public void SetExpanded(GitChangeRow row, bool expanded)
    {
        if (!row.HasChildren || row.IsExpanded == expanded)
        {
            return;
        }

        Remember(row.Key, expanded);
        KeepExpansion();
        row.SetExpanded(expanded);
        var rows = RowsOf(row.Node.Section);
        var index = rows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        if (expanded)
        {
            var below = new List<(GitChangeNode Node, int Depth)>();
            Flatten(row.Node.Children, row.Depth + 1, below);
            rows.InsertRange(index + 1, [.. below.Select(r => new GitChangeRow(r.Node, r.Depth))]);
            return;
        }

        var end = index + 1;
        while (end < rows.Count && rows[end].Depth > row.Depth)
        {
            end++;
        }

        rows.RemoveRange(index + 1, end - index - 1);
    }

    public void Toggle(GitChangeRow row) => SetExpanded(row, !row.IsExpanded);

    /// <summary>The staged section has files, but the filter shows none of them.</summary>
    public bool StagedFilteredOut => IsFiltered && Staged.Count > 0 && _stagedShown == 0;

    public bool ChangesFilteredOut => IsFiltered && Changes.Count > 0 && _changesShown == 0;

    /// <summary>"No files match “Greeter”." in a section the filter empties (T6).</summary>
    public string FilteredOutText => Localizer.Format("msg.gitChanges.noFilesMatch", "No files match “{0}”.", _filterText.Trim());

    private static string Header(string title, int shown, int total) => shown < total
        ? Localizer.Format("msg.gitChanges.sectionOfTotal", "{0} ({1} of {2})", title, shown, total)
        : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{title} ({total})");

    /// <summary>A node's IsExpanded was set: its rows follow.</summary>
    private void OnExpandedChanged(GitChangeNode node, bool expanded)
    {
        if (RowsOf(node.Section).FirstOrDefault(r => ReferenceEquals(r.Node, node)) is { } row)
        {
            node.SetExpanded(!expanded);
            SetExpanded(row, expanded);
            return;
        }

        Remember(node.Key, expanded);
        KeepExpansion();
    }

    private void Remember(string key, bool expanded)
    {
        if (expanded)
        {
            _collapsed.Remove(key);
        }
        else
        {
            _collapsed.Add(key);
        }
    }

    /// <summary>Views being built with the expansion before a change are built again (they'd undo it).</summary>
    private void KeepExpansion()
    {
        _generation++;
        _computeAgain |= _computing;
    }

    private static IEnumerable<GitFileRow> Sorted(IEnumerable<GitFileRow> rows, bool byPath, bool descending)
    {
        var ordered = byPath
            ? rows.OrderBy(r => r.Location, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Location, StringComparer.OrdinalIgnoreCase);
        return descending ? ordered.Reverse() : ordered;
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <summary>The rows of <paramref name="nodes"/> at <paramref name="depth"/>: each node, then its children's rows when it's expanded.</summary>
    internal static void Flatten(IEnumerable<GitChangeNode> nodes, int depth, List<(GitChangeNode Node, int Depth)> rows)
    {
        foreach (var node in nodes)
        {
            rows.Add((node, depth));
            if (!node.IsFile && node.IsExpanded)
            {
                Flatten(node.Children, depth + 1, rows);
            }
        }
    }

    /// <summary>Shows a section's new rows: the rows of the same nodes in the same places stay, with their new nodes.</summary>
    private static void PatchRows(ResettableCollection<GitChangeRow> target, IReadOnlyList<(GitChangeNode Node, int Depth)> rows) =>
        Patch(target, rows, (row, r) => row.Depth == r.Depth && string.Equals(row.Key, r.Node.Key, StringComparison.Ordinal), r => new GitChangeRow(r.Node, r.Depth), (row, r) => row.Node = r.Node);

    /// <summary>
    /// Updates <paramref name="target"/> to <paramref name="items"/> in place: the rows alike at its start and at its end
    /// stay (updated), and the rest is replaced with one removal and one insertion, or one reset when none stays. A
    /// collection refilled a row at a time raised a change for each, and new rows lost their controls and the selection.
    /// </summary>
    private static void Patch<TRow, TItem>(ResettableCollection<TRow> target, IReadOnlyList<TItem> items, Func<TRow, TItem, bool> alike, Func<TItem, TRow> create, Action<TRow, TItem>? update = null)
    {
        var prefix = 0;
        while (prefix < items.Count && prefix < target.Count && alike(target[prefix], items[prefix]))
        {
            update?.Invoke(target[prefix], items[prefix]);
            prefix++;
        }

        var suffix = 0;
        while (suffix < items.Count - prefix && suffix < target.Count - prefix && alike(target[target.Count - 1 - suffix], items[items.Count - 1 - suffix]))
        {
            update?.Invoke(target[target.Count - 1 - suffix], items[items.Count - 1 - suffix]);
            suffix++;
        }

        if (prefix == 0 && suffix == 0)
        {
            if (target.Count > 0 || items.Count > 0)
            {
                target.Reset(items.Select(create));
            }

            return;
        }

        target.RemoveRange(prefix, target.Count - suffix - prefix);
        target.InsertRange(prefix, [.. items.Skip(prefix).Take(items.Count - suffix - prefix).Select(create)]);
    }

    /// <summary>
    /// The tree of <paramref name="rows"/>: one repository node (none without rows), folders first then files, each by
    /// name; a folder with nothing but one folder in it is shown with it (<c>src\Data</c>). Keys in
    /// <paramref name="collapsed"/> start collapsed; the repository node's is <paramref name="key"/> (<c>changes:</c> by
    /// default; with all repositories shown, one per repository), its text <paramref name="text"/> (the folder by default).
    /// Each folder counts its files as they're added, shown (<see cref="GitChangeNode.ShownFiles"/>) and in all
    /// (<paramref name="all"/>, the section's rows before the filter): a count over every file for each folder was folders ×
    /// files, seconds for 50,000 files in 5,000 folders.
    /// </summary>
    public static IReadOnlyList<GitChangeNode> BuildTree(string root, IEnumerable<GitFileRow> rows, string section, bool staged, IReadOnlySet<string> collapsed, Action<GitChangeNode, bool>? expandedChanged = null, IReadOnlyList<GitFileRow>? all = null, string? key = null, string? text = null)
    {
        var top = new TreeFolder();
        foreach (var row in rows)
        {
            var folder = top;
            folder.Shown++;
            var segments = row.Status.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments[..^1])
            {
                if (!folder.Folders.TryGetValue(segment, out var child))
                {
                    folder.Folders[segment] = child = new TreeFolder();
                }

                folder = child;
                folder.Shown++;
            }

            folder.Files.Add(row);
        }

        if (top.Folders.Count == 0 && top.Files.Count == 0)
        {
            return [];
        }

        // Each folder's files in the section, shown or not: one for each folder on each file's way.
        Dictionary<string, int>? totals = null;
        if (all is not null)
        {
            totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in all)
            {
                var path = row.Status.RelativePath.Replace('\\', '/');
                for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = path.IndexOf('/', slash + 1))
                {
                    System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(totals, path[..slash], out _)++;
                }
            }
        }

        key ??= section + ":";
        var rootPath = root.Replace('/', System.IO.Path.DirectorySeparatorChar);
        var repository = new GitChangeNode(key, text ?? rootPath, null, isRepository: true, staged, !collapsed.Contains(key), expandedChanged)
        {
            FullPath = rootPath,
            Section = section,
            ShownFiles = top.Shown,
            TotalFiles = all?.Count ?? top.Shown,
        };
        Add(repository, top, key, string.Empty);
        return [repository];

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
                    ShownFiles = contents.Shown,
                    TotalFiles = totals?.GetValueOrDefault(relative) ?? contents.Shown,
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

        /// <summary>The files at and under it.</summary>
        public int Shown { get; set; }
    }

    // ------------------------------------------------------------------ refreshing, off the UI thread

    private int _generation;
    private bool _computing;
    private bool _computeAgain;
    private int _refreshPosted;
    private bool _stashesStale;
    private bool _viewShown;
    private bool _viewsStale = true;
    private int _stagedShown;
    private int _changesShown;
    private int _conflictsShown;

    /// <summary>How many times the trees and lists were built (tests: none while the window is hidden).</summary>
    internal int ViewsBuilt { get; private set; }

    /// <summary>Whether a refresh is waiting or running (tests).</summary>
    internal bool IsRefreshing => _computing || Volatile.Read(ref _refreshPosted) == 1 || _filterRefresh?.IsPending == true;

    /// <summary>Whether the view said the window is shown (tests).</summary>
    internal bool IsViewShown => _viewShown;

    /// <summary>
    /// The view tells whether the window is shown (attached and visible). While it isn't, refreshes keep the files current
    /// (<see cref="Staged"/>, <see cref="Changes"/>, <see cref="Conflicts"/>: the commands read them) but build no tree or
    /// list; showing it builds them once. The window lives for the whole session, and every status read rebuilt them all.
    /// </summary>
    public void SetViewShown(bool shown)
    {
        _viewShown = shown;
        if (shown && _viewsStale)
        {
            Refresh();
        }
    }

    /// <summary>A refresh for a status read, from any thread: one for a burst, after the input waiting (at background priority).</summary>
    private void RequestRefresh(bool stashes)
    {
        if (stashes)
        {
            Volatile.Write(ref _stashesRequested, 1);
        }

        if (Interlocked.Exchange(ref _refreshPosted, 1) == 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _refreshPosted, 0);
                _stashesStale |= Interlocked.Exchange(ref _stashesRequested, 0) == 1;
                Refresh();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private int _stashesRequested;

    /// <summary>
    /// Shows the status again: the files, and while the window is shown its trees and lists, built on the thread pool and
    /// applied in place (a view built for an older status, filter or expansion is dropped and built again). On the UI
    /// thread this took 0.2-3 s per status read with 10,000-50,000 changed files, shown or not.
    /// </summary>
    public void Refresh()
    {
        _generation++;
        _ = RefreshInBackgroundAsync();
    }

    private async Task RefreshInBackgroundAsync()
    {
        if (_computing)
        {
            _computeAgain = true;
            return;
        }

        _computing = true;
        try
        {
            do
            {
                _computeAgain = false;
                var generation = _generation;
                var query = Query();
                var view = await Task.Run(() => Compute(query)).ConfigureAwait(true);
                if (generation == _generation)
                {
                    Apply(view);
                }
            }
            while (_computeAgain);
        }
        finally
        {
            _computing = false;
        }
    }

    /// <summary>A repository the window shows, as <see cref="Compute"/> reads it: its status (a new one at each read) and names.</summary>
    private sealed record RepositoryState(GitStatus Status, string Name, string Root, int Order);

    /// <summary>What the window shows, read on the UI thread so that <see cref="Compute"/> can run on any.</summary>
    private sealed record ChangesQuery(
        IReadOnlyList<RepositoryState> Repositories,
        GitLfsStatus Lfs,
        string Filter,
        bool SortByPath,
        bool SortDescending,
        bool AllRepositories,
        string Root,
        IReadOnlySet<string> Collapsed,
        bool Views,
        bool TreeView,
        IReadOnlyList<GitFileRow> Staged,
        IReadOnlyList<GitFileRow> Changes,
        IReadOnlyList<GitFileRow> Conflicts,
        Action<GitChangeNode, bool> ExpandedChanged);

    /// <summary>A section as the window shows it: its tree and the tree's rows, or its list.</summary>
    private sealed record SectionView(IReadOnlyList<GitChangeNode> Tree, IReadOnlyList<(GitChangeNode Node, int Depth)> Rows, IReadOnlyList<GitFileRow> Items);

    /// <summary>The files of each section (the same list when nothing changed), how many the filter shows, and the views when built.</summary>
    private sealed record ChangesView(
        IReadOnlyList<GitFileRow> Staged,
        IReadOnlyList<GitFileRow> Changes,
        IReadOnlyList<GitFileRow> Conflicts,
        int StagedShown,
        int ChangesShown,
        int ConflictsShown,
        (SectionView Staged, SectionView Changes, SectionView Conflicts)? Views);

    private ChangesQuery Query() => new(
        [.. Services.Select((git, order) => (git, order)).Where(s => s.git.Status is not null).Select(s => new RepositoryState(s.git.Status!, s.git.Name, s.git.Root ?? string.Empty, s.order))],
        _lfs,
        _filterText.Trim(),
        _sortByPath,
        _sortDescending,
        _isAllRepositories,
        _git.Root ?? string.Empty,
        _collapsed.ToHashSet(StringComparer.Ordinal),
        _viewShown,
        _isTreeView,
        [.. Staged],
        [.. Changes],
        [.. Conflicts],
        OnExpandedChanged);

    private static ChangesView Compute(ChangesQuery query)
    {
        var staged = Rows(query, s => s.Staged, staged: true, query.Staged);
        var changes = Rows(query, s => s.Unstaged, staged: false, query.Changes);
        var conflicts = Rows(query, s => s.Conflicts, staged: false, query.Conflicts);
        return new ChangesView(
            staged,
            changes,
            conflicts,
            staged.Count(r => Matches(r, query.Filter)),
            changes.Count(r => Matches(r, query.Filter)),
            conflicts.Count(r => Matches(r, query.Filter)),
            query.Views ? (Section(staged, "staged", true), Section(changes, "changes", false), Section(conflicts, "conflicts", false)) : null);

        SectionView Section(IReadOnlyList<GitFileRow> rows, string section, bool isStaged)
        {
            if (!query.TreeView)
            {
                return new SectionView([], [], [.. Sorted(rows.Where(r => Matches(r, query.Filter)), query.SortByPath, query.SortDescending)]);
            }

            IReadOnlyList<GitChangeNode> tree = !query.AllRepositories
                ? BuildTree(query.Root, rows.Where(r => Matches(r, query.Filter)), section, isStaged, query.Collapsed, query.ExpandedChanged, rows)
                : [.. rows.GroupBy(r => r.Repository, StringComparer.Ordinal).SelectMany(g =>
                    BuildTree(g.Key, g.Where(r => Matches(r, query.Filter)), section, isStaged, query.Collapsed, query.ExpandedChanged, [.. g], key: section + ":" + g.Key, text: g.First().Location.Split(System.IO.Path.DirectorySeparatorChar)[0]))];
            var flat = new List<(GitChangeNode Node, int Depth)>();
            Flatten(tree, 0, flat);
            return new SectionView(tree, flat, []);
        }
    }

    /// <summary>A section's files, in the repositories' order then by path; the list shown now when each file is the same.</summary>
    private static IReadOnlyList<GitFileRow> Rows(ChangesQuery query, Func<GitStatus, IEnumerable<GitFileStatus>> files, bool staged, IReadOnlyList<GitFileRow> shown)
    {
        var rows = query.Repositories
            .SelectMany(r => files(r.Status).Select(f => Row(r, f)))
            .OrderBy(r => r.RepositoryOrder)
            .ThenBy(r => r.Status.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return rows.Count == shown.Count && rows.Zip(shown).All(p => p.First.IsSameAs(p.Second)) ? shown : rows;

        GitFileRow Row(RepositoryState repository, GitFileStatus file)
        {
            var folder = System.IO.Path.GetDirectoryName(file.RelativePath.Replace('\\', '/'))?.Replace('/', System.IO.Path.DirectorySeparatorChar) ?? string.Empty;
            return new GitFileRow(file, staged, query.Lfs)
            {
                Location = folder.Length > 0 ? repository.Name + System.IO.Path.DirectorySeparatorChar + folder : repository.Name,
                Repository = repository.Root,
                RepositoryOrder = repository.Order,
            };
        }
    }

    /// <summary>Shows a computed view: the files that changed (one reset each), the views in place when built, the headers.</summary>
    private void Apply(ChangesView view)
    {
        if (!SameRows(Staged, view.Staged))
        {
            Staged.Reset(view.Staged);
        }

        if (!SameRows(Changes, view.Changes))
        {
            Changes.Reset(view.Changes);
        }

        if (!SameRows(Conflicts, view.Conflicts))
        {
            Conflicts.Reset(view.Conflicts);
        }

        (_stagedShown, _changesShown, _conflictsShown) = (view.StagedShown, view.ChangesShown, view.ConflictsShown);
        if (view.Views is var (staged, changes, conflicts))
        {
            ViewsBuilt++;
            _viewsStale = false;
            if (_isTreeView)
            {
                (_stagedTree, _changesTree, _conflictsTree) = (staged.Tree, changes.Tree, conflicts.Tree);
                PatchRows(StagedRows, staged.Rows);
                PatchRows(ChangesRows, changes.Rows);
                PatchRows(ConflictsRows, conflicts.Rows);
            }
            else
            {
                Patch(StagedItems, staged.Items, (row, item) => ReferenceEquals(row, item) || row.IsSameAs(item), item => item);
                Patch(ChangesItems, changes.Items, (row, item) => ReferenceEquals(row, item) || row.IsSameAs(item), item => item);
                Patch(ConflictsItems, conflicts.Items, (row, item) => ReferenceEquals(row, item) || row.IsSameAs(item), item => item);
            }
        }
        else
        {
            _viewsStale = true;
        }

        foreach (var name in new[]
        {
            nameof(HasRepository), nameof(HasNoRepository), nameof(BranchName), nameof(BranchTooltip), nameof(BranchAccessibleName), nameof(IsPublished), nameof(IsNotPublished), nameof(OutgoingIncomingText),
            nameof(IsDetached), nameof(IsUnborn), nameof(ShowPublishBranch), nameof(ShowPublishToHost),
            nameof(OutgoingIncomingTooltip), nameof(IsMerging), nameof(HasStaged), nameof(HasChanges), nameof(HasConflicts), nameof(StagedHeader),
            nameof(ChangesHeader), nameof(ConflictsHeader), nameof(OperationText), nameof(CommitText), nameof(CommitAndPushText), nameof(CommitAndSyncText),
            nameof(CanCommit), nameof(IsFiltered), nameof(StagedFilteredOut), nameof(ChangesFilteredOut), nameof(FilteredOutText),
        })
        {
            OnPropertyChanged(name);
        }

        if (_stashesStale)
        {
            _stashesStale = false;
            _ = LoadStashesAsync();
        }

        static bool SameRows(IReadOnlyList<GitFileRow> shown, IReadOnlyList<GitFileRow> rows) => ReferenceEquals(shown, rows) || (shown.Count == rows.Count && shown.Zip(rows).All(p => ReferenceEquals(p.First, p.Second)));
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

    /// <summary>The panel that suggests signing in to the repository's host, and signs in (ADR 0088); set by the shell.</summary>
    public HostingSignInViewModel? HostingSignIn { get; set; }

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
    public string StagedHeader => Header(Localizer.Get("msg.gitChanges.stagedChanges", "Staged Changes"), _stagedShown, Staged.Count);

    public string ChangesHeader => Changes.Count > 0 ? Header(Localizer.Get("msg.gitChanges.changes", "Changes"), _changesShown, Changes.Count) : Localizer.Get("msg.gitChanges.changes", "Changes");

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

    public string ConflictsHeader => Header(Localizer.Get("msg.gitChanges.mergeConflicts", "Merge Conflicts"), _conflictsShown, Conflicts.Count);

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

    /// <summary>
    /// The repository's Git LFS files and locks: the rows' badges. The same files and locks again refresh nothing (they're
    /// read after every status read, which refreshed the window a second time).
    /// </summary>
    public GitLfsStatus Lfs
    {
        get => _lfs;
        set
        {
            var same = _lfs.IsSameAs(value);
            _lfs = value;
            if (!same)
            {
                Refresh();
            }
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

        _stashesStale = true;
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

    public Task StageAsync(GitFileRow row) => StageAsync([row]);

    public Task UnstageAsync(GitFileRow row) => UnstageAsync([row]);

    /// <summary>Stage All: every file of Changes (conflicted files aren't there: staging one marks it resolved).</summary>
    public Task StageAllAsync() => Changes.Count == 0 ? Task.CompletedTask : StageAsync([.. Changes]);

    public Task UnstageAllAsync() => Staged.Count == 0 ? Task.CompletedTask : UnstageAsync([.. Staged]);

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
            if (all && git.Status is { } status && status.Unstaged.Any(f => f.IsUntracked))
            {
                await git.StageAsync([.. status.Unstaged]).ConfigureAwait(false);
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
    public string SectionOf(GitFileRow row) => row.Staged ? "staged" : row.Status.IsConflict ? "conflicts" : "changes";
}
