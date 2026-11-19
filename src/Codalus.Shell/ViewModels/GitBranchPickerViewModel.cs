using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A row of the branch drop-down: a group's header in the list view (a remote, Common branches, a repository), a folder
/// of branch names or a group in the tree view (it expands and collapses), or a branch.
/// </summary>
public sealed class GitBranchRow : ObservableObject, ITreeRow
{
    private string _busy = string.Empty;

    private GitBranchRow(string key)
    {
        Key = key;
    }

    /// <summary>A header row (the list view): a remote's name, Common branches, or a repository's name and branch.</summary>
    public static GitBranchRow Header(string text, string? key = null, int depth = 0) => new(key ?? "h:" + text) { HeaderText = text, IsHeader = true, Depth = depth };

    /// <summary>
    /// A folder of the tree view: a part of the branches' names before a <c>/</c> (<c>feature</c>), or a group (Common
    /// branches, a repository, a remote) with its <paramref name="icon"/>; <paramref name="count"/> branches under it.
    /// </summary>
    public static GitBranchRow Folder(string key, string name, int depth, bool expanded, int count, IReadOnlyList<int>? highlights = null, string? icon = null) =>
        new(key)
        {
            Name = name,
            IsFolder = true,
            IsExpanded = expanded,
            Count = count,
            Highlights = highlights,
            GroupIcon = icon,
            Depth = depth,
            InTree = true,
        };

    /// <summary>
    /// A branch row; <paramref name="name"/> is what it shows (a remote branch without its remote; in the tree view, the
    /// part after its folders). With all repositories shown (ADR 0085), a row of Common branches (<paramref name="isCommon"/>)
    /// or of one <paramref name="repository"/>.
    /// </summary>
    public static GitBranchRow ForBranch(
        GitBranchDetails branch, string name, bool isCurrent, IReadOnlyList<int>? highlights = null, string detail = "", string track = "", string trackName = "", string busy = "",
        GitService? repository = null, bool isCommon = false, int depth = 0, bool inTree = false) =>
        new((isCommon ? "c:" : string.Empty) + (repository is { Root: { } root } ? root + ":" : string.Empty) + (branch.IsRemote ? "r:" : "l:") + branch.Name)
        {
            Branch = branch,
            Name = name,
            IsCurrent = isCurrent,
            Highlights = highlights,
            Detail = detail,
            Track = track,
            TrackName = trackName,
            _busy = busy,
            Repository = repository,
            IsCommon = isCommon,
            Depth = depth,
            InTree = inTree,
        };

    /// <summary>The repository the branch is in, with all repositories shown (ADR 0085); null otherwise and on Common branches.</summary>
    public GitService? Repository { get; private init; }

    /// <summary>A branch every repository has (Common branches): its actions run in each.</summary>
    public bool IsCommon { get; private init; }

    /// <summary>How far it's indented: its level in the tree, or in the list its place under a header.</summary>
    public int Depth { get; private init; }

    public Avalonia.Thickness Indent => new(Depth * TreeMetrics.IndentPerLevel, 0, 0, 0);

    /// <summary>Identifies the row across reloads: <c>l:main</c>, <c>r:origin/main</c>, <c>h:…</c>, a folder's <c>L:f:feature/</c>.</summary>
    public string Key { get; }

    public bool IsHeader { get; private init; }

    /// <summary>A folder or a group of the tree view: it expands and collapses.</summary>
    public bool IsFolder { get; private init; }

    public bool IsBranch => Branch is not null;

    /// <summary>A branch or a folder: the selection and the keys move over these, never a header.</summary>
    public bool IsSelectable => !IsHeader;

    /// <summary>A row of the tree view: a branch keeps a folder's chevron's place, so its name lines up with theirs.</summary>
    public bool InTree { get; private init; }

    public bool HasChildren => IsFolder;

    public bool IsExpanded { get; private init; }

    public string? Chevron => IsFolder ? (IsExpanded ? "chevron-down" : "chevron-right") : null;

    /// <summary>A group's own icon (Common branches, a repository, a remote), null for a folder of names.</summary>
    public string? GroupIcon { get; private init; }

    public string FolderIcon => GroupIcon ?? (IsExpanded ? "folder-open" : "folder");

    /// <summary>The branches under a folder (those the search shows).</summary>
    public int Count { get; private init; }

    public string CountText => Count.ToString(CultureInfo.CurrentCulture);

    public string HeaderText { get; private init; } = string.Empty;

    public GitBranchDetails? Branch { get; private init; }

    public string Name { get; private init; } = string.Empty;
    /// <summary>The checked-out branch: the check before its name (none with a detached HEAD, B14).</summary>
    public bool IsCurrent { get; private init; }

    /// <summary>The check's opacity: drawn on the current branch only, its place kept on the others so the names line up.</summary>
    public double CheckOpacity => IsCurrent ? 1 : 0;

    /// <summary>What the row says after its name: "No commits yet" (B16).</summary>
    public string Detail { get; private init; } = string.Empty;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>The characters of <see cref="Name"/> that match the search (B2).</summary>
    public IReadOnlyList<int>? Highlights { get; private init; }

    /// <summary>"Updating…" where its chips go while Update runs on it (B23).</summary>
    public string Busy
    {
        get => _busy;
        set
        {
            if (SetField(ref _busy, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(HasTrack));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public bool IsBusy => _busy.Length > 0;

    /// <summary>The worktree chip: it's checked out in another worktree (B12).</summary>
    public bool IsInOtherWorktree => Branch?.WorktreePath is not null;

    public string WorktreeTip => Branch?.WorktreePath is { } path ? Localizer.Format("msg.gitBranches.checkedOutIn", "Checked out in {0}", path) : string.Empty;

    /// <summary>The chips: "↑2 ↓1" (outgoing, incoming), none on a remote branch or when there's nothing to say.</summary>
    public string Track { get; private init; } = string.Empty;

    public bool HasTrack => Track.Length > 0 && !IsBusy;

    /// <summary>"2 outgoing, 1 incoming (origin/feature/sign-in)" (question 14): the chips' tooltip and accessible text.</summary>
    public string TrackName { get; private init; } = string.Empty;

    /// <summary>The row's name for screen readers: the branch (and remote), current, detail, chips, worktree, busy.</summary>
    public string AccessibleName => IsHeader ? HeaderText : IsFolder
        ? Localizer.Format(Count == 1 ? "msg.gitBranches.folderOneBranch" : "msg.gitBranches.folderBranches", Count == 1 ? "{0}, 1 branch" : "{0}, {1} branches", Name, Count)
        : string.Join(", ", new[]
    {
        Branch is { IsRemote: true, Remote: { } remote } ? Localizer.Format("msg.gitBranches.onRemote", "{0} on {1}", Name, remote) : Name,
        IsCurrent ? Localizer.Get("msg.gitBranches.currentBranch", "current branch") : string.Empty,
        Detail,
        HasTrack ? TrackName : string.Empty,
        IsInOtherWorktree ? Localizer.Format("msg.gitBranches.checkedOutInLower", "checked out in {0}", Branch!.WorktreePath!) : string.Empty,
        _busy,
    }.Where(p => p.Length > 0));
}

/// <summary>
/// The Git Changes window's branch drop-down (#23, D38: B1-B26): the branches on two tabs, Local and Remote, each with
/// its count of the branches the search shows; every open starts on Local. The tree view (the default, remembered in
/// <c>git.branchPicker.view</c>) puts the branches in folders by their names' <c>/</c> parts, folders before branches;
/// folders start collapsed but for the current branch's path, and what is expanded is remembered while the IDE runs,
/// for each repository and tab; a search keeps the folders, hides those with no match and expands the paths to the
/// matches, and clearing it puts back what was expanded. The list view has the current branch first, then the others by
/// name. Remote branches show without their remote's prefix (question 4), under their remote's group only when there are
/// several remotes. Also its first open's loading state, git's error with Try Again, no match with the footer's New
/// Branch 'x'…, the banners (detached, an operation in progress, where a branch is checked out, what Update found) and
/// the rows Update is running on. The list is kept between opens, so later opens show it at once while it reloads.
/// </summary>
public sealed class GitBranchPickerViewModel : ObservableObject, ITreeRowsOwner
{
    /// <summary>The <c>git.branchPicker.view</c> setting's values.</summary>
    public const string ViewTree = "tree";

    public const string ViewList = "list";

    private bool _isRemoteTab;
    private bool _isTreeView = true;
    private bool _revealCurrent;
    private int _localMatches;
    private int _remoteMatches;
    private bool _noRemotes;
    private bool _matchesNowhere;

    // What the user expanded or collapsed, by repository and folder (kept while the IDE runs); and during a search, what
    // they changed in its tree (dropped as the search changes, so clearing it puts back the folders as they were).
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _searchExpanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    private readonly GitService _git;
    private IReadOnlyList<GitBranchDetails>? _branches;
    private bool _hasRemote;
    private string _query = string.Empty;
    private bool _isOpen;
    private bool _isLoading;
    private string? _error;
    private string? _errorDetail;
    private GitBranchRow? _selected;
    private string? _banner;
    private bool _bannerWarning;
    private string? _bannerAction;
    private string _bannerActionIcon = "window-new";
    private Func<Task>? _bannerRun;
    private int _loadVersion;
    private Task? _latestLoad;
    private string? _loadedFor;
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private IReadOnlyList<RepositoryBranches>? _repositories;

    /// <summary>A repository's branches, with all repositories shown (ADR 0085).</summary>
    private sealed record RepositoryBranches(GitService Git, IReadOnlyList<GitBranchDetails> Branches, bool HasRemote);

    public GitBranchPickerViewModel(GitService git)
    {
        _git = git;
    }

    /// <summary>The repositories Git Changes shows when it shows them all (ADR 0085), else null; set by the shell.</summary>
    public Func<IReadOnlyList<GitService>?>? AllRepositories { get; set; }

    /// <summary>The list has Common branches and each repository's (ADR 0085).</summary>
    public bool IsAllRepositories => _repositories is not null;

    /// <summary>The row that shows <paramref name="branch"/> (the very object), or null.</summary>
    public GitBranchRow? RowOf(GitBranchDetails branch) => Rows.FirstOrDefault(r => ReferenceEquals(r.Branch, branch));

    /// <summary>The repository a branch of the list is in: its own row's, or the open one (one repository, and Common branches).</summary>
    public GitService RepositoryOf(GitBranchDetails branch) =>
        _repositories?.FirstOrDefault(r => r.Branches.Any(b => ReferenceEquals(b, branch)))?.Git ?? _git;

    /// <summary>A Common branches row (its actions run in every repository).</summary>
    public bool IsCommon(GitBranchDetails branch) => RowOf(branch) is { IsCommon: true };

    /// <summary>Each repository's local branch named <paramref name="name"/>, in the repositories' order.</summary>
    public IReadOnlyList<(GitService Git, GitBranchDetails Branch)> CommonBranchIn(string name) =>
        [.. (_repositories ?? []).SelectMany(r => r.Branches.Where(b => !b.IsRemote && b.Name == name).Take(1).Select(b => (r.Git, b)))];

    /// <summary>The branches of the repository <paramref name="branch"/> is in.</summary>
    private IReadOnlyList<GitBranchDetails> BranchesBeside(GitBranchDetails branch) =>
        _repositories?.FirstOrDefault(r => r.Branches.Any(b => ReferenceEquals(b, branch)))?.Branches ?? Branches;

    /// <summary>Whether the repository <paramref name="branch"/> is in has a remote (any of them, for a Common branches row).</summary>
    public bool HasRemoteFor(GitBranchDetails branch) =>
        _repositories is { } all
            ? all.FirstOrDefault(r => r.Branches.Any(b => ReferenceEquals(b, branch)))?.HasRemote ?? all.Any(r => r.HasRemote)
            : _hasRemote;

    public ObservableCollection<GitBranchRow> Rows { get; } = [];

    /// <summary>Checks a branch out (a click or Enter on its row, B1), set by the shell.</summary>
    public Func<GitBranchDetails, Task>? Checkout { get; set; }

    /// <summary>New Branch… (the footer), with the name to fill in, set by the shell.</summary>
    public Func<string?, Task>? NewBranch { get; set; }

    /// <summary>Manage Branches (the footer's link): the Git Repository document, set by the shell.</summary>
    public Func<Task>? ManageBranches { get; set; }

    /// <summary>A branch's menu (B7), set by the shell.</summary>
    public Func<GitBranchDetails, Avalonia.Controls.ContextMenu?>? MenuFor { get; set; }

    /// <summary>A key the list handles (F2: Rename…, Delete: Delete) on a branch, when its menu has that entry; set by the shell.</summary>
    public Func<string, GitBranchDetails, Task>? RunOnBranch { get; set; }

    /// <summary>The branches as last read (null before the first read).</summary>
    public IReadOnlyList<GitBranchDetails> Branches => _branches ?? [];

    /// <summary>The repository has a remote (B17: no remote group, no chips, no Push).</summary>
    public bool HasRemote => _hasRemote;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetField(ref _isOpen, value))
            {
                if (value)
                {
                    if (ReadView?.Invoke() is { } view)
                    {
                        LoadView(view);
                    }

                    _isRemoteTab = false;
                    _revealCurrent = true;
                    OnPropertyChanged(nameof(IsRemoteTab));
                    OnPropertyChanged(nameof(TabIndex));
                    _searchExpanded.Clear();
                    _query = string.Empty;
                    OnPropertyChanged(nameof(Query));
                    Rebuild();
                    ClearBanner();
                    _ = LoadAsync();
                }

                Opened?.Invoke(this, value);
            }
        }
    }

    /// <summary>Raised when it opens (true) or closes (false); the view shows or hides the flyout.</summary>
    public event EventHandler<bool>? Opened;

    /// <summary>The search box's text: filters the rows as it's typed (B2), even while the first read runs (B4).</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (SetField(ref _query, value ?? string.Empty))
            {
                _searchExpanded.Clear();
                Rebuild();
            }
        }
    }

    /// <summary>The Remote tab is shown (the remote branches); else the Local tab. Every open starts on Local.</summary>
    public bool IsRemoteTab
    {
        get => _isRemoteTab;
        set
        {
            if (SetField(ref _isRemoteTab, value))
            {
                OnPropertyChanged(nameof(TabIndex));
                _selected = null;
                Rebuild();
            }
        }
    }

    /// <summary>The tab strip's selection: 0 Local, 1 Remote.</summary>
    public int TabIndex
    {
        get => _isRemoteTab ? 1 : 0;
        set => IsRemoteTab = value == 1;
    }

    /// <summary>The branches in folders (the default), or a list; the toggle beside the search box switches, and the shell saves it.</summary>
    public bool IsTreeView
    {
        get => _isTreeView;
        set
        {
            if (SetField(ref _isTreeView, value))
            {
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(ViewToggleTip));
                Rebuild();
                SaveView?.Invoke(value ? ViewTree : ViewList);
            }
        }
    }

    /// <summary>The toggle beside the search box: checked in the list view.</summary>
    public bool IsListView
    {
        get => !_isTreeView;
        set => IsTreeView = !value;
    }

    public string ViewToggleTip => _isTreeView
        ? Localizer.Get("msg.gitBranches.showAsList", "Show as List")
        : Localizer.Get("msg.gitBranches.showAsTree", "Show as Tree");

    /// <summary>Reads the view from the settings as the drop-down opens (an edit of settings.json shows at the next open); set by the shell.</summary>
    public Func<string>? ReadView { get; set; }

    /// <summary>Saves the view (<see cref="ViewTree"/> or <see cref="ViewList"/>) when the toggle changes it; set by the shell.</summary>
    public Action<string>? SaveView { get; set; }

    /// <summary>Sets the view from the settings, without saving it back.</summary>
    public void LoadView(string view)
    {
        var tree = view != ViewList;
        if (tree != _isTreeView)
        {
            _isTreeView = tree;
            OnPropertyChanged(nameof(IsTreeView));
            OnPropertyChanged(nameof(IsListView));
            OnPropertyChanged(nameof(ViewToggleTip));
            Rebuild();
        }
    }

    /// <summary>The Local tab's count: the local branches the search shows.</summary>
    public string LocalCount => _localMatches.ToString(CultureInfo.CurrentCulture);

    public string RemoteCount => _remoteMatches.ToString(CultureInfo.CurrentCulture);

    /// <summary>The tabs' names for screen readers: "Local branches, 3".</summary>
    public string LocalTabName => Localizer.Format("msg.gitBranches.localTabName", "Local branches, {0}", _localMatches);

    public string RemoteTabName => Localizer.Format("msg.gitBranches.remoteTabName", "Remote branches, {0}", _remoteMatches);

    /// <summary>The Remote tab of a repository without a remote (B17): it says so.</summary>
    public bool IsNoRemotes => _noRemotes;

    public string NoRemotesText => IsAllRepositories
        ? Localizer.Get("msg.gitBranches.noRepositoryHasRemote", "None of the repositories has a remote.")
        : Localizer.Get("msg.gitBranches.noRemote", "This repository has no remote.");

    /// <summary>
    /// Raised once the rows have the current branch after an open, with its row: the view scrolls it into sight (in the
    /// tree view it can be under many folders).
    /// </summary>
    public event Action<GitBranchRow>? RevealRequested;

    /// <summary>The first read runs and nothing is cached: an indeterminate bar under the search box (B4).</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(LoadingText));
                Rebuild();
            }
        }
    }

    public string LoadingText => Localizer.Get("msg.gitBranches.loadingBranches", "Loading branches…");

    /// <summary>git failed to list the branches (B5): what failed, with git's line in <see cref="ErrorDetail"/>.</summary>
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

    public string? ErrorDetail
    {
        get => _errorDetail;
        private set => SetField(ref _errorDetail, value);
    }

    public GitBranchRow? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value is { IsSelectable: false } ? _selected : value);
    }

    /// <summary>"2 of 7": the branches shown of all.</summary>
    public string CountText { get; private set; } = string.Empty;

    /// <summary>No branch matches the search (B3).</summary>
    public bool IsEmpty { get; private set; }

    public string EmptyText => _isRemoteTab
        ? Localizer.Format("msg.gitBranches.noRemoteBranchesMatch", "No remote branches match “{0}”.", _query.Trim())
        : Localizer.Format("msg.gitBranches.noLocalBranchesMatch", "No local branches match “{0}”.", _query.Trim());

    /// <summary>The footer: New Branch…, or New Branch 'zzz'… when no branch on either tab matches (B3). None in an empty repository (B16).</summary>
    public string FooterText => FooterName is { } name
        ? Localizer.Format("msg.gitBranches.newBranchNamed", "New Branch '{0}'…", name)
        : Localizer.Get("msg.gitBranches.newBranch", "New Branch…");

    public bool HasFooter => _git.Status is not { Branch: not null, Head: null };

    /// <summary>The name the footer's New Branch fills in (the search, when no branch on either tab matches).</summary>
    public string? FooterName => _matchesNowhere && _query.Trim().Length > 0 ? _query.Trim() : null;

    /// <summary>The banner under the search box: a message for a moment (B13, B25, B26), else HEAD detached (B14) or an operation in progress (B15).</summary>
    public string? Banner => _banner ?? StandingBanner;

    public bool HasBanner => Banner is not null;

    public bool IsBannerWarning => _banner is not null && _bannerWarning;

    public string BannerIcon => IsBannerWarning ? "warning" : "info";

    public string? BannerAction => _banner is not null ? _bannerAction : null;

    public bool HasBannerAction => BannerAction is not null;

    public string BannerActionIcon => _bannerActionIcon;

    /// <summary>What the window's state says when no message is shown: HEAD detached, or what an operation in progress hides.</summary>
    private string? StandingBanner => _git.Status switch
    {
        { Branch: null, Head: { } head } => Localizer.Format("msg.gitBranches.headDetachedBanner", "HEAD is detached at {0}. Check out a branch, or use New Branch… to keep commits made here.", head[..Math.Min(7, head.Length)]),
        { Operation: "merge" } status => Localizer.Format("msg.gitBranches.mergeInProgressBanner", "A merge is in progress: until it is committed or aborted, Checkout, Merge, Rebase, Delete and {0}'s Update are hidden, and a new branch isn't checked out.", status.Branch ?? "HEAD"),
        { Operation: "rebase" } => Localizer.Format("msg.gitBranches.rebaseInProgressBanner", "A rebase is in progress: until it is continued or aborted, Checkout, Merge, Rebase, Delete and {0}'s Update are hidden, and a new branch isn't checked out.", Branches.FirstOrDefault(b => b.IsRebasingHere)?.Name ?? "HEAD"),
        { Operation: { } operation } => Localizer.Format("msg.gitBranches.operationInProgressBanner", "A {0} is in progress: until it is continued or aborted, Checkout, Merge, Rebase and Delete are hidden, and a new branch isn't checked out.", operation),
        _ => null,
    };

    /// <summary>Shows a message in the banner, with an action (Open in New Window, Checkout) when given.</summary>
    public void ShowBanner(string message, bool warning, string? action = null, string actionIcon = "window-new", Func<Task>? run = null)
    {
        _banner = message;
        _bannerWarning = warning;
        _bannerAction = action;
        _bannerActionIcon = actionIcon;
        _bannerRun = run;
        NotifyBanner();
    }

    public void ClearBanner()
    {
        _banner = null;
        _bannerAction = null;
        _bannerRun = null;
        NotifyBanner();
    }

    /// <summary>The banner's action button.</summary>
    public Task RunBannerActionAsync() => _bannerRun is { } run ? run() : Task.CompletedTask;

    private void NotifyBanner()
    {
        foreach (var name in new[] { nameof(Banner), nameof(HasBanner), nameof(IsBannerWarning), nameof(BannerIcon), nameof(BannerAction), nameof(HasBannerAction), nameof(BannerActionIcon) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Marks a local branch as being updated (B23): the row says so where its chips go.</summary>
    public void SetBusy(string branch, bool busy)
    {
        if (busy)
        {
            _busy.Add(branch);
        }
        else
        {
            _busy.Remove(branch);
        }

        foreach (var row in Rows.Where(r => r.Branch is { IsRemote: false } b && b.Name == branch))
        {
            row.Busy = busy ? Localizer.Get("msg.gitBranches.updating", "Updating…") : string.Empty;
        }
    }

    /// <summary>
    /// Reads the branches again (on each open, after Update and Try Again); the cached list stays shown meanwhile. When a
    /// newer load replaces this one's result (Update's reload, the repositories' refresh after git changed .git), it
    /// returns once that one has landed: the list it leaves was read after the call, never the one before it (a load
    /// returned with the rows still showing main as current after a switch, macOS runs 40538692111, 40547948961).
    /// </summary>
    public async Task LoadAsync()
    {
        var load = LoadOnceAsync();
        _latestLoad = load;
        await load.ConfigureAwait(true);
        while (_latestLoad is { } newer && newer != load)
        {
            load = newer;
            await load.ConfigureAwait(true);
        }
    }

    private async Task LoadOnceAsync()
    {
        var version = ++_loadVersion;
        if (_loadedFor != _git.Root)
        {
            _branches = null;
        }

        Error = null;
        IsLoading = _branches is null;
        try
        {
            var branches = await _git.BranchDetailsAsync().ConfigureAwait(true);
            var remotes = await _git.RemotesAsync().ConfigureAwait(true);
            List<RepositoryBranches>? repositories = null;
            if (AllRepositories?.Invoke() is { Count: > 1 } services)
            {
                repositories = [];
                foreach (var git in services)
                {
                    repositories.Add(git == _git
                        ? new RepositoryBranches(git, branches, remotes.Count > 0)
                        : new RepositoryBranches(git, await git.BranchDetailsAsync().ConfigureAwait(true), (await git.RemotesAsync().ConfigureAwait(true)).Count > 0));
                }
            }

            if (version != _loadVersion)
            {
                return;
            }

            _branches = branches;
            _repositories = repositories;
            _hasRemote = remotes.Count > 0;
            _loadedFor = _git.Root;
            OnPropertyChanged(nameof(HasRemote));
            OnPropertyChanged(nameof(Branches));
            OnPropertyChanged(nameof(IsAllRepositories));
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            if (version != _loadVersion)
            {
                return;
            }

            ErrorDetail = ex.Message;
            Error = ex is GitException { ExitCode: var code } && code >= 0
                ? Localizer.Format("msg.gitBranches.couldntListBranchesExit", "Couldn't list the branches: git exited with code {0}.", code)
                : Localizer.Get("msg.gitBranches.couldntListBranches", "Couldn't list the branches.");
            _branches ??= [];
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                Rebuild();
                _revealCurrent = false;
                NotifyBanner();
                OnPropertyChanged(nameof(HasFooter));
            }
        }
    }

    /// <summary>
    /// A group of the rows being built (Common branches, a repository, a remote: a folder of the tree view, a header of the
    /// list view) or, in the tree view, a folder of branch names; the branches under it; their count and whether one is
    /// the current branch (its folders start expanded).
    /// </summary>
    private sealed class Node(string key, string name, string? icon, bool isGroup, Node? parent)
    {
        public string Key { get; } = key;

        public string Name { get; } = name;

        public string? Icon { get; } = icon;

        public bool IsGroup { get; } = isGroup;

        public Node? Parent { get; } = parent;

        public List<Node> Children { get; } = [];

        public Dictionary<string, Node> Folders { get; } = new(StringComparer.Ordinal);

        public List<Leaf> Leaves { get; } = [];

        public int Count { get; set; }

        public bool HasCurrent { get; set; }

        public Node Group(string key, string name, string icon)
        {
            var group = new Node(key, name, icon, isGroup: true, this);
            Children.Add(group);
            return group;
        }
    }

    /// <summary>A branch to show: first the current one in the list view (<paramref name="First"/>), then by name; its row at a name and a depth.</summary>
    private sealed record Leaf(string Name, bool First, Func<string, int, GitBranchRow> Make);

    /// <summary>
    /// The rows of the tab for the search. Local: (an empty repository's branch, which git doesn't list, with "No commits
    /// yet") the local branches; Remote: the remote branches without their remote's prefix, under their remote's group
    /// when there are several. With all repositories shown (ADR 0085), Local has Common branches (the local branches every
    /// repository has, checked when every repository is on it) and then each repository's under its name and current
    /// branch; Remote has each repository's remote branches. In the tree view the branches are in folders.
    /// </summary>
    private void Rebuild()
    {
        var selectedKey = _selected?.Key;
        var query = _query.Trim();
        var status = _git.Status;
        var all = _branches ?? [];
        var tab = _isRemoteTab ? "R" : "L";
        var root = new Node(tab, string.Empty, null, isGroup: true, null);
        var localMatches = 0;
        var remoteMatches = 0;
        int total;
        bool anyRemote;

        bool Matches(string name) => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);

        if (_repositories is { } repositories)
        {
            anyRemote = repositories.Any(r => r.HasRemote);
            total = repositories.Sum(r => r.Branches.Count(b => !b.IsRemote || r.HasRemote));
            var common = GitService.CommonBranches([.. repositories.Select(r => r.Branches)]).Where(Matches).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (!_isRemoteTab && common.Count > 0)
            {
                var group = root.Group("L:common", Localizer.Get("msg.gitBranches.commonBranches", "Common branches"), "branch");
                foreach (var name in common)
                {
                    var each = CommonBranchIn(name);
                    var current = each.All(e => e.Branch.IsCurrent && e.Git.Status?.Branch is not null);

                    // A copy of the first repository's: a row's branch identifies the row (RowOf).
                    var branch = each[0].Branch with { };
                    Place(group, name, current, (shown, depth) => GitBranchRow.ForBranch(branch, shown, current, Highlights(shown), isCommon: true, depth: depth, inTree: _isTreeView));
                }
            }

            foreach (var repository in repositories)
            {
                var git = repository.Git;
                var on = git.Status?.Branch;
                var locals = repository.Branches.Where(b => !b.IsRemote && Matches(b.Name)).ToList();
                var remotes = repository.HasRemote ? repository.Branches.Where(b => b.IsRemote && Matches(b.ShortName)).ToList() : [];
                localMatches += locals.Count;
                remoteMatches += remotes.Count;
                var shown = _isRemoteTab ? remotes : locals;
                if (shown.Count == 0)
                {
                    continue;
                }

                var group = root.Group(tab + ":repo:" + git.Root, $"{git.Name} ({on ?? Localizer.Get("msg.gitBranches.detached", "detached")})", "repository");
                if (_isRemoteTab)
                {
                    PlaceRemotes(group, repository.Branches, remotes, git);
                }
                else
                {
                    foreach (var b in locals)
                    {
                        var current = b.IsCurrent && on is not null;
                        Place(group, b.Name, current, (name, depth) => GitBranchRow.ForBranch(b, name, current, Highlights(name), repository: git, depth: depth, inTree: _isTreeView));
                    }
                }
            }
        }
        else
        {
            var locals = all.Where(b => !b.IsRemote).ToList();
            var remotes = _hasRemote ? all.Where(b => b.IsRemote).ToList() : [];
            var unborn = status is { Branch: { } name, Head: null } && locals.All(b => b.Name != name)
                ? new GitBranchDetails("refs/heads/" + name, name, IsRemote: false, IsCurrent: true)
                : null;
            if (unborn is not null)
            {
                locals.Insert(0, unborn);
            }

            anyRemote = _hasRemote;
            total = locals.Count + remotes.Count;
            var shownLocals = locals.Where(b => Matches(b.Name)).ToList();
            var shownRemotes = remotes.Where(b => Matches(b.ShortName)).ToList();
            localMatches = shownLocals.Count;
            remoteMatches = shownRemotes.Count;
            if (_branches is not null || unborn is not null)
            {
                if (_isRemoteTab)
                {
                    PlaceRemotes(root, all, shownRemotes, null);
                }
                else
                {
                    foreach (var b in shownLocals)
                    {
                        var current = b.IsCurrent && status?.Branch is not null;
                        var isUnborn = b == unborn;
                        Place(root, b.Name, current, (name, depth) => LocalRow(b, isUnborn, name, current, depth));
                    }
                }
            }
        }

        _nodes.Clear();
        var rows = new List<GitBranchRow>();
        Emit(root, 0, rows, query.Length > 0);

        var loaded = !_isLoading && _error is null && _branches is not null;
        _localMatches = localMatches;
        _remoteMatches = remoteMatches;
        _noRemotes = loaded && _isRemoteTab && !anyRemote;
        _matchesNowhere = loaded && localMatches + remoteMatches == 0;
        CountText = Localizer.Format("msg.gitBranches.countOfTotal", "{0} of {1}", localMatches + remoteMatches, total);
        IsEmpty = loaded && !_noRemotes && query.Length > 0 && (_isRemoteTab ? remoteMatches : localMatches) == 0;
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        _selected = Rows.FirstOrDefault(r => r.Key == selectedKey);
        foreach (var property in new[]
        {
            nameof(Selected), nameof(CountText), nameof(IsEmpty), nameof(EmptyText), nameof(FooterText), nameof(FooterName), nameof(LocalCount), nameof(RemoteCount),
            nameof(LocalTabName), nameof(RemoteTabName), nameof(IsNoRemotes), nameof(NoRemotesText),
        })
        {
            OnPropertyChanged(property);
        }

        if (_revealCurrent && Rows.FirstOrDefault(r => r.IsCurrent) is { } currentRow)
        {
            RevealRequested?.Invoke(currentRow);
        }

        IReadOnlyList<int>? Highlights(string name)
        {
            var at = query.Length == 0 ? -1 : name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? null : [.. Enumerable.Range(at, query.Length)];
        }

        // A remote's branches (those the search shows of all of them): under the remote's group when there are several.
        void PlaceRemotes(Node parent, IReadOnlyList<GitBranchDetails> branches, List<GitBranchDetails> shown, GitService? repository)
        {
            var several = branches.Where(b => b.IsRemote).Select(b => b.Remote ?? string.Empty).Distinct(StringComparer.Ordinal).Count() > 1;
            var head = (repository ?? _git).Status?.Branch;
            var upstream = head is null ? null : branches.FirstOrDefault(b => !b.IsRemote && b.Name == head)?.Upstream;
            foreach (var group in shown.GroupBy(b => b.Remote ?? string.Empty).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var under = several ? parent.Group(parent.Key + ":remote:" + group.Key, group.Key, "cloud") : parent;
                foreach (var b in group)
                {
                    Place(under, b.ShortName, b.Name == upstream, (name, depth) => GitBranchRow.ForBranch(b, name, isCurrent: false, Highlights(name), repository: repository, depth: depth, inTree: _isTreeView), first: false);
                }
            }
        }

        GitBranchRow LocalRow(GitBranchDetails b, bool isUnborn, string name, bool current, int depth)
        {
            var track = !_hasRemote || b.Upstream is null || b.UpstreamGone ? string.Empty
                : string.Join(' ', new[] { b.Ahead > 0 ? "↑" + b.Ahead.ToString(CultureInfo.CurrentCulture) : string.Empty, b.Behind > 0 ? "↓" + b.Behind.ToString(CultureInfo.CurrentCulture) : string.Empty }.Where(t => t.Length > 0));
            var parts = new[]
            {
                b.Ahead > 0 ? Localizer.Format("msg.gitBranches.outgoing", "{0} outgoing", b.Ahead) : string.Empty,
                b.Behind > 0 ? Localizer.Format("msg.gitBranches.incoming", "{0} incoming", b.Behind) : string.Empty,
            }.Where(p => p.Length > 0);
            return GitBranchRow.ForBranch(
                b,
                name,
                current,
                Highlights(name),
                detail: isUnborn ? Localizer.Get("msg.gitBranches.noCommitsYet", "No commits yet") : string.Empty,
                track: track,
                trackName: track.Length > 0 ? $"{string.Join(", ", parts)} ({b.Upstream})" : string.Empty,
                busy: _busy.Contains(b.Name) ? Localizer.Get("msg.gitBranches.updating", "Updating…") : string.Empty,
                depth: depth,
                inTree: _isTreeView);
        }

        // The rows of a node's groups, folders and branches: groups in the order they came, then folders by name, then
        // branches (the current one first in the list view) by name. In the list view a group is a header over its
        // branches; in the tree view a group or a folder is a row that expands, its rows under it only when it's expanded.
        void Emit(Node node, int depth, List<GitBranchRow> into, bool searching)
        {
            foreach (var child in node.Children.Where(c => c.IsGroup).Concat(node.Children.Where(c => !c.IsGroup).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)))
            {
                _nodes[child.Key] = child;
                if (!_isTreeView)
                {
                    into.Add(GitBranchRow.Header(child.Name, "h:" + child.Key, depth));
                    Emit(child, depth + 1, into, searching);
                    continue;
                }

                var expanded = searching
                    ? !_searchExpanded.TryGetValue(child.Key, out var e) || e
                    : _expanded.TryGetValue(StateKey(child.Key), out var kept) ? kept : child.IsGroup || child.HasCurrent;
                into.Add(GitBranchRow.Folder(child.Key, child.Name, depth, expanded, child.Count, child.IsGroup ? null : Highlights(child.Name), child.Icon));
                if (expanded)
                {
                    Emit(child, depth + 1, into, searching);
                }
            }

            foreach (var leaf in node.Leaves.OrderByDescending(l => !_isTreeView && l.First).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                into.Add(leaf.Make(leaf.Name, depth));
            }
        }
    }

    /// <summary>
    /// Puts a branch under <paramref name="group"/> (<paramref name="isCurrent"/>: its folders start expanded; with <paramref name="first"/>, the list view shows it first): in the tree view under the folders its name's <c>/</c> parts make
    /// (<c>feature/x</c>: folder feature, branch x), in the list view with its whole name. Every node above it counts it.
    /// </summary>
    private void Place(Node group, string name, bool isCurrent, Func<string, int, GitBranchRow> make, bool first = true)
    {
        var node = group;
        var leaf = name;
        if (_isTreeView)
        {
            var parts = name.Split('/');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Length == 0)
                {
                    continue;
                }

                if (!node.Folders.TryGetValue(parts[i], out var folder))
                {
                    folder = new Node(node.Key + "|" + parts[i], parts[i], null, isGroup: false, node);
                    node.Folders[parts[i]] = folder;
                    node.Children.Add(folder);
                }

                node = folder;
            }

            leaf = parts[^1];
        }

        node.Leaves.Add(new Leaf(leaf, first && isCurrent, make));
        for (var up = node; up is not null; up = up.Parent)
        {
            up.Count++;
            up.HasCurrent |= isCurrent;
        }
    }

    /// <summary>Where an expanded or collapsed folder is remembered: by repository, and in the key by tab and group.</summary>
    private string StateKey(string key) => (_git.Root ?? string.Empty) + "\n" + key;

    private void Remember(string key, bool expanded)
    {
        if (_query.Trim().Length > 0)
        {
            _searchExpanded[key] = expanded;
        }
        else
        {
            _expanded[StateKey(key)] = expanded;
        }
    }

    /// <summary>Expands or collapses a folder of the tree view (its chevron, a click, Left and Right).</summary>
    public void SetExpanded(ITreeRow row, bool expanded)
    {
        if (row is GitBranchRow { IsFolder: true } folder)
        {
            Remember(folder.Key, expanded);
            Rebuild();
        }
    }

    /// <summary>A folder and every folder under it (Ctrl+Left, Ctrl+Right).</summary>
    public void SetExpandedUnder(ITreeRow row, bool expanded)
    {
        if (row is not GitBranchRow { IsFolder: true } folder || !_nodes.TryGetValue(folder.Key, out var node))
        {
            return;
        }

        void Visit(Node n)
        {
            Remember(n.Key, expanded);
            foreach (var child in n.Children)
            {
                Visit(child);
            }
        }

        Visit(node);
        Rebuild();
    }

    /// <summary>The selection moves down (+1) or up (-1) over the branch and folder rows, skipping headers (the keyboard, B2).</summary>
    public void MoveSelection(int direction)
    {
        var selectable = Rows.Where(r => r.IsSelectable).ToList();
        if (selectable.Count == 0)
        {
            return;
        }

        var index = _selected is null ? -1 : selectable.IndexOf(_selected);
        Selected = index < 0 ? (direction > 0 ? selectable[0] : selectable[^1]) : selectable[Math.Clamp(index + direction, 0, selectable.Count - 1)];
    }

    /// <summary>The local branch with a remote branch's name, which Checkout on the remote row switches to (question 7).</summary>
    public GitBranchDetails? LocalTwin(GitBranchDetails remote) =>
        remote.IsRemote ? BranchesBeside(remote).FirstOrDefault(b => !b.IsRemote && b.Name == remote.ShortName) : null;
}
