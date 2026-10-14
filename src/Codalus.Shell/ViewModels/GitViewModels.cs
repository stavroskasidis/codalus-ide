using System.Collections.ObjectModel;
using System.Text;
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

    public bool CanDiscard => Row is { CanStage: true };

    public string AccessibleName => Row?.AccessibleName ?? Text;
}

/// <summary>A repository of the solution (several repositories, M13b).</summary>
public sealed record GitRepositoryChoice(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

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
        Refresh();
    }

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

    public Task StageAsync(GitChangeNode node) => _git.StageAsync([.. node.Rows.Select(r => r.Status.Path)]);

    public Task UnstageAsync(GitChangeNode node) => _git.UnstageAsync([.. node.Rows.Select(r => r.Status.Path)]);

    private bool Matches(GitFileRow row) =>
        _filterText.Length == 0 || row.Status.RelativePath.Contains(_filterText.Trim(), StringComparison.OrdinalIgnoreCase);

    private void RebuildViews()
    {
        var root = _git.Root ?? string.Empty;
        Fill(StagedTree, BuildTree(root, Staged.Where(Matches), "staged", staged: true, _collapsed, OnExpandedChanged));
        Fill(ChangesTree, BuildTree(root, Changes.Where(Matches), "changes", staged: false, _collapsed, OnExpandedChanged));
        Fill(ConflictsTree, BuildTree(root, Conflicts.Where(Matches), "conflicts", staged: false, _collapsed, OnExpandedChanged));
        Fill(StagedItems, Sorted(Staged.Where(Matches)));
        Fill(ChangesItems, Sorted(Changes.Where(Matches)));
        Fill(ConflictsItems, Sorted(Conflicts.Where(Matches)));
    }

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
    /// <paramref name="collapsed"/> start collapsed.
    /// </summary>
    public static IReadOnlyList<GitChangeNode> BuildTree(string root, IEnumerable<GitFileRow> rows, string section, bool staged, IReadOnlySet<string> collapsed, Action<GitChangeNode, bool>? expandedChanged = null)
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

        var key = section + ":";
        var repository = new GitChangeNode(key, root.Replace('/', System.IO.Path.DirectorySeparatorChar), null, isRepository: true, staged, !collapsed.Contains(key), expandedChanged);
        Add(repository, top, key);
        return [repository];

        void Add(GitChangeNode parent, TreeFolder folder, string parentKey)
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
                var node = new GitChangeNode(folderKey, string.Join(System.IO.Path.DirectorySeparatorChar, names), null, isRepository: false, staged, !collapsed.Contains(folderKey), expandedChanged);
                parent.Children.Add(node);
                Add(node, contents, folderKey);
            }

            foreach (var file in folder.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                parent.Children.Add(new GitChangeNode(parentKey + "/" + file.Name, file.Name, file, isRepository: false, staged, expanded: false));
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

    /// <summary>The current branch, or the commit HEAD is detached at.</summary>
    public string BranchName => _git.Status is not { } status
        ? string.Empty
        : status.Branch ?? Localizer.Format("msg.gitChanges.detachedAt", "(detached at {0})", status.Head?[..Math.Min(7, status.Head.Length)]);

    /// <summary>Whether the branch tracks a remote branch (otherwise outgoing and incoming commits are unknown).</summary>
    public bool IsPublished => _git.Status?.Upstream is not null;

    public bool IsNotPublished => HasRepository && !IsPublished;

    /// <summary>"2 / 1": the commits to push (outgoing) and to pull (incoming).</summary>
    public string OutgoingIncomingText => _git.Status is { } status
        ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{status.Ahead} / {status.Behind}")
        : string.Empty;

    public string OutgoingIncomingTooltip => _git.Status is { Upstream: { } upstream } status
        ? Localizer.Format("msg.gitChanges.outgoingIncoming", "{0} outgoing / {1} incoming (tracking {2})", status.Ahead, status.Behind, upstream)
        : Localizer.Get("msg.gitChanges.notPublished", "The branch isn't published: push it to create its remote branch.");

    public bool IsMerging => _git.Status?.IsMerging ?? false;

    private string? _operation;

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

    public string OperationText => _operation switch
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

    public string StagedHeader => $"Staged Changes ({Staged.Count})";

    public string ChangesHeader => Changes.Count > 0 ? $"Changes ({Changes.Count})" : "Changes";

    /// <summary>The commit button commits the staged changes when there are some, otherwise every change.</summary>
    public string CommitText => HasStaged
        ? Localizer.Get("msg.gitChanges.commitStaged", "Commit Staged")
        : Localizer.Get("msg.gitChanges.commitAll", "Commit All");

    public string CommitAndPushText => HasStaged
        ? Localizer.Get("msg.gitChanges.commitStagedAndPush", "Commit Staged and Push")
        : Localizer.Get("msg.gitChanges.commitAllAndPush", "Commit All and Push");

    public string CommitAndSyncText => HasStaged
        ? Localizer.Get("msg.gitChanges.commitStagedAndSync", "Commit Staged and Sync")
        : Localizer.Get("msg.gitChanges.commitAllAndSync", "Commit All and Sync");

    /// <summary>A message is required, and something to commit (or the last commit to amend).</summary>
    public bool CanCommit => !string.IsNullOrWhiteSpace(_message) && (HasStaged || HasChanges || Amend);

    public string ConflictsHeader => $"Merge Conflicts ({Conflicts.Count})";

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
        IReadOnlyList<GitStash> stashes = [];
        if (_git.Root is not null)
        {
            try
            {
                stashes = await _git.StashesAsync().ConfigureAwait(true);
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

    public Task ApplyStashAsync(GitStash stash, bool pop) => _git.ApplyStashAsync(stash, pop);

    /// <summary>Drops a stash, after the shell asks.</summary>
    public Task DropStashAsync(GitStash stash) => _run("git.dropStash:" + stash.Name);

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

    /// <summary>The solution's repositories, when it spans several.</summary>
    public ObservableCollection<GitRepositoryChoice> Repositories { get; } = [];

    public bool HasSeveralRepositories => Repositories.Count > 1;

    /// <summary>Opens another of the solution's repositories (set by the shell).</summary>
    public Func<string, Task>? SwitchRepository { get; set; }

    public GitRepositoryChoice? SelectedRepository
    {
        get => _selectedRepository;
        set
        {
            if (SetField(ref _selectedRepository, value) && !_updatingRepositories && value is not null && SwitchRepository is { } switchTo)
            {
                _ = switchTo(value.Path);
            }
        }
    }

    public void SetRepositories(IReadOnlyList<string> roots, string? active)
    {
        _updatingRepositories = true;
        try
        {
            Repositories.Clear();
            foreach (var root in roots)
            {
                Repositories.Add(new GitRepositoryChoice(root));
            }

            SelectedRepository = Repositories.FirstOrDefault(r => active is not null && Workbench.Projects.SolutionService.PathComparer.Equals(r.Path, active));
            OnPropertyChanged(nameof(HasSeveralRepositories));
        }
        finally
        {
            _updatingRepositories = false;
        }
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
        var status = _git.Status;
        var repository = _git.Root is { } root ? System.IO.Path.GetFileName(root.TrimEnd('/', '\\')) : string.Empty;
        Replace(Staged, status?.Staged.Select(f => Row(f, staged: true)));
        Replace(Changes, status?.Unstaged.Select(f => Row(f, staged: false)));
        Replace(Conflicts, status?.Conflicts.Select(f => Row(f, staged: false)));
        RebuildViews();
        foreach (var name in new[]
        {
            nameof(HasRepository), nameof(HasNoRepository), nameof(BranchName), nameof(IsPublished), nameof(IsNotPublished), nameof(OutgoingIncomingText),
            nameof(OutgoingIncomingTooltip), nameof(IsMerging), nameof(HasStaged), nameof(HasChanges), nameof(HasConflicts), nameof(StagedHeader),
            nameof(ChangesHeader), nameof(ConflictsHeader), nameof(OperationText), nameof(CommitText), nameof(CommitAndPushText), nameof(CommitAndSyncText),
            nameof(CanCommit),
        })
        {
            OnPropertyChanged(name);
        }

        _ = LoadStashesAsync();

        GitFileRow Row(GitFileStatus file, bool staged)
        {
            var folder = System.IO.Path.GetDirectoryName(file.RelativePath.Replace('\\', '/'))?.Replace('/', System.IO.Path.DirectorySeparatorChar) ?? string.Empty;
            return new GitFileRow(file, staged, _lfs) { Location = folder.Length > 0 ? repository + System.IO.Path.DirectorySeparatorChar + folder : repository };
        }
    }

    private static void Replace(ObservableCollection<GitFileRow> target, IEnumerable<GitFileRow>? rows)
    {
        target.Clear();
        foreach (var row in (rows ?? []).OrderBy(r => r.Status.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(row);
        }
    }

    public Task StageAsync(GitFileRow row) => _git.StageAsync([row.Status.Path]);

    public Task UnstageAsync(GitFileRow row) => _git.UnstageAsync([row.Status.Path]);

    public Task StageAllAsync() => Changes.Count == 0 ? Task.CompletedTask : _git.StageAsync([.. Changes.Select(r => r.Status.Path)]);

    public Task UnstageAllAsync() => Staged.Count == 0 ? Task.CompletedTask : _git.UnstageAsync([.. Staged.Select(r => r.Status.Path)]);

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

        if (all && Changes.Any(r => r.Status.IsUntracked))
        {
            await StageAllAsync().ConfigureAwait(true);
        }

        await _git.CommitAsync(Message, Amend, all).ConfigureAwait(true);
        _messageBeforeAmend = null;
        _amend = false;
        OnPropertyChanged(nameof(Amend));
        Message = string.Empty;
    }

    /// <summary>Commits (staged changes when there are some, otherwise all), then pushes, or with <paramref name="sync"/> pulls and pushes.</summary>
    public async Task CommitAndPushAsync(bool sync)
    {
        await CommitAsync(all: !HasStaged).ConfigureAwait(true);
        await _run(sync ? "git.sync" : "git.push").ConfigureAwait(true);
    }

    /// <summary>Runs a shell command (fetch, pull, push, discard…), which reports its own errors.</summary>
    public Task RunAsync(string command) => _run(command);
}

/// <summary>A row of a side-by-side diff: a line on either side (null = filler), and its change kind.</summary>
public sealed record DiffRow(int? OldLine, int? NewLine, char Kind);

/// <summary>A file's diff (work tree against the index, or the index against HEAD), inline or side by side.</summary>
public sealed class DiffViewModel : ObservableObject
{
    private readonly GitService? _git;
    private bool _sideBySide = true;
    private string _summary = string.Empty;

    public DiffViewModel(GitService git, string path, bool staged)
    {
        _git = git;
        Path = path;
        Staged = staged;
    }

    /// <summary>Two files, side by side (<c>codalus diff left right</c>): no Git, no staging.</summary>
    public DiffViewModel(string leftPath, string rightPath)
    {
        LeftPath = leftPath;
        Path = rightPath;
    }

    /// <summary>
    /// Two versions of a file read by <paramref name="oldText"/> and <paramref name="newText"/> (revisions, M13: a commit
    /// against its parent, a branch against the working tree…). <paramref name="key"/> identifies the document.
    /// </summary>
    public DiffViewModel(string path, string key, string oldTitle, Func<Task<string>> oldText, string newTitle, Func<Task<string>> newText)
    {
        Path = path;
        Key = key;
        _oldTitle = oldTitle;
        _newTitle = newTitle;
        _oldProvider = oldText;
        _newProvider = newText;
    }

    private readonly string? _oldTitle;
    private readonly string? _newTitle;
    private readonly Func<Task<string>>? _oldProvider;
    private readonly Func<Task<string>>? _newProvider;

    /// <summary>A revision comparison's document key; null otherwise.</summary>
    public string? Key { get; }

    public bool IsRevisionComparison => _oldProvider is not null;

    public string Path { get; }

    /// <summary>The left file of a file comparison; null for Git diffs.</summary>
    public string? LeftPath { get; }

    public bool IsFileComparison => LeftPath is not null;

    public bool Staged { get; }

    public FileDiff? Diff { get; private set; }

    public string OldText { get; private set; } = string.Empty;

    public string NewText { get; private set; } = string.Empty;

    public IReadOnlyList<string> OldLines { get; private set; } = [];

    public IReadOnlyList<string> NewLines { get; private set; } = [];

    /// <summary>Aligned rows of both texts (side by side, and in that order for the inline view).</summary>
    public IReadOnlyList<DiffRow> Rows { get; private set; } = [];

    public bool SideBySide
    {
        get => _sideBySide;
        set
        {
            if (SetField(ref _sideBySide, value))
            {
                OnPropertyChanged(nameof(Inline));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool Inline
    {
        get => !_sideBySide;
        set => SideBySide = !value;
    }

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public string OldTitle => _oldTitle ?? LeftPath ?? (Staged ? "HEAD" : "Index");

    public string NewTitle => _newTitle ?? (IsFileComparison ? Path : Staged ? "Index (staged)" : "Working tree");

    public bool CanStage => !Staged && !IsFileComparison && !IsRevisionComparison;

    public bool CanUnstage => Staged;

    /// <summary>The texts or rows changed; views rebuild.</summary>
    public event EventHandler? Changed;

    public async Task LoadAsync()
    {
        if (_oldProvider is not null && _newProvider is not null)
        {
            await LoadTextsAsync(await _oldProvider().ConfigureAwait(true), await _newProvider().ConfigureAwait(true)).ConfigureAwait(true);
            return;
        }

        if (_git is null)
        {
            await LoadFilesAsync().ConfigureAwait(true);
            return;
        }

        var oldText = await _git.ShowAsync(Path, Staged ? "HEAD" : ":").ConfigureAwait(true) ?? string.Empty;
        string newText;
        if (Staged)
        {
            newText = await _git.ShowAsync(Path, ":").ConfigureAwait(true) ?? string.Empty;
        }
        else
        {
            try
            {
                newText = File.Exists(Path) ? await File.ReadAllTextAsync(Path).ConfigureAwait(true) : string.Empty;
            }
            catch (IOException)
            {
                newText = string.Empty;
            }
        }

        Diff = await _git.DiffAsync(Path, Staged).ConfigureAwait(true);
        OldText = oldText;
        NewText = newText;
        OldLines = LineDiff.Lines(oldText);
        NewLines = LineDiff.Lines(newText);
        var ops = await Task.Run(() => LineDiff.Diff(OldLines, NewLines)).ConfigureAwait(true);
        Rows = Align(ops);
        var hunks = Diff?.Hunks.Count ?? 0;
        Summary = Diff is null ? "No changes." : Diff.IsBinary ? "Binary file." : $"{hunks} change{(hunks == 1 ? string.Empty : "s")}: +{Diff.Hunks.Sum(h => h.Added)} −{Diff.Hunks.Sum(h => h.Removed)}";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadFilesAsync()
    {
        static async Task<string> ReadAsync(string path)
        {
            try
            {
                return File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }

        await LoadTextsAsync(await ReadAsync(LeftPath!).ConfigureAwait(true), await ReadAsync(Path).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async Task LoadTextsAsync(string oldText, string newText)
    {
        OldText = oldText;
        NewText = newText;
        OldLines = LineDiff.Lines(OldText);
        NewLines = LineDiff.Lines(NewText);
        var ops = await Task.Run(() => LineDiff.Diff(OldLines, NewLines)).ConfigureAwait(true);
        Rows = Align(ops);
        var added = ops.Count(o => o.Kind == '+');
        var removed = ops.Count(o => o.Kind == '-');
        Summary = added + removed == 0 ? "The files are identical." : $"+{added} −{removed}";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pairs removed and added runs line by line ('~' rows), so side-by-side views align.</summary>
    private static List<DiffRow> Align(IReadOnlyList<DiffOp> ops)
    {
        var rows = new List<DiffRow>(ops.Count);
        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == ' ')
            {
                rows.Add(new DiffRow(ops[i].OldIndex + 1, ops[i].NewIndex + 1, ' '));
                i++;
                continue;
            }

            var removed = new List<int>();
            var added = new List<int>();
            while (i < ops.Count && ops[i].Kind != ' ')
            {
                if (ops[i].Kind == '-')
                {
                    removed.Add(ops[i].OldIndex + 1);
                }
                else
                {
                    added.Add(ops[i].NewIndex + 1);
                }

                i++;
            }

            for (var k = 0; k < Math.Max(removed.Count, added.Count); k++)
            {
                int? oldLine = k < removed.Count ? removed[k] : null;
                int? newLine = k < added.Count ? added[k] : null;
                rows.Add(new DiffRow(oldLine, newLine, oldLine is null ? '+' : newLine is null ? '-' : '~'));
            }
        }

        return rows;
    }

    /// <summary>The inline text: removed lines (old) and added lines (new) interleaved, with each row's kind.</summary>
    public (string Text, IReadOnlyList<char> Kinds) InlineText()
    {
        var text = new StringBuilder();
        var kinds = new List<char>();
        foreach (var row in Rows)
        {
            if (row.Kind == ' ')
            {
                text.Append(NewLines[row.NewLine!.Value - 1]).Append('\n');
                kinds.Add(' ');
                continue;
            }

            if (row.OldLine is { } oldLine)
            {
                text.Append(OldLines[oldLine - 1]).Append('\n');
                kinds.Add('-');
            }

            if (row.NewLine is { } newLine)
            {
                text.Append(NewLines[newLine - 1]).Append('\n');
                kinds.Add('+');
            }
        }

        return (text.ToString(), kinds);
    }

    /// <summary>The git hunk that covers a new-side (or, for removals, old-side) line.</summary>
    public DiffHunk? HunkAt(int? newLine, int? oldLine)
    {
        if (Diff is null)
        {
            return null;
        }

        foreach (var hunk in Diff.Hunks)
        {
            if (newLine is { } n && n >= hunk.NewStart && n < hunk.NewStart + Math.Max(1, hunk.NewCount))
            {
                return hunk;
            }

            if (oldLine is { } o && o >= hunk.OldStart && o < hunk.OldStart + Math.Max(1, hunk.OldCount))
            {
                return hunk;
            }
        }

        return null;
    }

    /// <summary>The first changed row after (or before) <paramref name="row"/>, for next/previous change.</summary>
    public int? NextChange(int row, int direction)
    {
        for (var i = row + direction; i >= 0 && i < Rows.Count; i += direction)
        {
            if (Rows[i].Kind != ' ' && (i - direction < 0 || i - direction >= Rows.Count || Rows[i - direction].Kind == ' '))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Stages, unstages or discards only the changed lines in the given rows (per-line staging, M13): a removal is
    /// chosen by its old line, an addition by its new line.
    /// </summary>
    public async Task ApplyToLinesAsync(IReadOnlyCollection<int> rows, string action)
    {
        if (Diff is null || _git is null)
        {
            return;
        }

        var oldLines = rows.Where(r => r >= 0 && r < Rows.Count).Select(r => Rows[r].OldLine).OfType<int>().ToHashSet();
        var newLines = rows.Where(r => r >= 0 && r < Rows.Count).Select(r => Rows[r].NewLine).OfType<int>().ToHashSet();
        foreach (var hunk in Diff.Hunks)
        {
            var chosen = new HashSet<int>();
            int oldNo = hunk.OldStart, newNo = hunk.NewStart;
            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                switch (hunk.Lines[i].Kind)
                {
                    case ' ':
                        oldNo++;
                        newNo++;
                        break;
                    case '-':
                        if (oldLines.Contains(oldNo++))
                        {
                            chosen.Add(i);
                        }

                        break;
                    case '+':
                        if (newLines.Contains(newNo++))
                        {
                            chosen.Add(i);
                        }

                        break;
                }
            }

            if (chosen.Count == 0)
            {
                continue;
            }

            switch (action)
            {
                case "stage":
                    await _git.StageLinesAsync(Diff, hunk, chosen).ConfigureAwait(true);
                    break;
                case "unstage":
                    await _git.UnstageLinesAsync(Diff, hunk, chosen).ConfigureAwait(true);
                    break;
                case "discard":
                    await _git.DiscardLinesAsync(Diff, hunk, chosen).ConfigureAwait(true);
                    break;
            }

            break; // the diff changed: one hunk per action
        }

        await LoadAsync().ConfigureAwait(true);
    }

    public async Task ApplyToHunkAsync(DiffHunk hunk, string action)
    {
        if (Diff is null || _git is null)
        {
            return;
        }

        switch (action)
        {
            case "stage":
                await _git.StageHunkAsync(Diff, hunk).ConfigureAwait(true);
                break;
            case "unstage":
                await _git.UnstageHunkAsync(Diff, hunk).ConfigureAwait(true);
                break;
            case "discard":
                await _git.DiscardHunkAsync(Diff, hunk).ConfigureAwait(true);
                break;
        }

        await LoadAsync().ConfigureAwait(true);
    }
}

/// <summary>A diff document tab.</summary>
public sealed class DiffDocument : ShellDocument
{
    public DiffDocument(DiffViewModel viewModel, EditorFont font)
    {
        ViewModel = viewModel;
        Font = font;
        Id = viewModel.Key is { } key ? "diff:revisions:" + key : viewModel.LeftPath is { } left ? IdFor(left, viewModel.Path) : IdFor(viewModel.Path, viewModel.Staged);
        Title = viewModel.IsRevisionComparison
            ? $"{System.IO.Path.GetFileName(viewModel.Path)} ({viewModel.OldTitle} ↔ {viewModel.NewTitle})"
            : viewModel.LeftPath is { } leftPath
                ? $"{System.IO.Path.GetFileName(leftPath)} ↔ {System.IO.Path.GetFileName(viewModel.Path)}"
                : $"{System.IO.Path.GetFileName(viewModel.Path)} ({(viewModel.Staged ? "Staged" : "Working Tree")})";
    }

    public static string IdFor(string path, bool staged) => $"diff:{(staged ? "staged" : "worktree")}:{path}";

    public static string IdFor(string left, string right) => $"diff:files:{left}|{right}";

    public override string Kind => "diff";

    public DiffViewModel ViewModel { get; }

    public EditorFont Font { get; }
}
