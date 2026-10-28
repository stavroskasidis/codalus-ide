using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>A row of the branch drop-down: a group's header (Local branches, a remote) or a branch.</summary>
public sealed class GitBranchRow : ObservableObject
{
    private string _busy = string.Empty;

    private GitBranchRow(string key)
    {
        Key = key;
    }

    /// <summary>A header row: "Local branches", or a remote's name; with all repositories shown, a repository's (indented under All branches).</summary>
    public static GitBranchRow Header(string text, string? key = null, int depth = 0) => new(key ?? "h:" + text) { HeaderText = text, IsHeader = true, Depth = depth };

    /// <summary>
    /// A branch row; <paramref name="name"/> is what it shows (a remote branch without its remote). With all repositories
    /// shown (ADR 0085), a row of Common branches (<paramref name="isCommon"/>) or of one <paramref name="repository"/>.
    /// </summary>
    public static GitBranchRow ForBranch(
        GitBranchDetails branch, string name, bool isCurrent, IReadOnlyList<int>? highlights = null, string detail = "", string track = "", string trackName = "", string busy = "",
        GitService? repository = null, bool isCommon = false, int depth = 0) =>
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
        };

    /// <summary>The repository the branch is in, with all repositories shown (ADR 0085); null otherwise and on Common branches.</summary>
    public GitService? Repository { get; private init; }

    /// <summary>A branch every repository has (Common branches): its actions run in each.</summary>
    public bool IsCommon { get; private init; }

    /// <summary>How far it's indented: under All branches, a repository's header (1) and its branches (2).</summary>
    public int Depth { get; private init; }

    public Avalonia.Thickness Indent => new(Depth * 12, 0, 0, 0);

    /// <summary>Identifies the row across reloads: <c>l:main</c>, <c>r:origin/main</c>, <c>h:…</c>.</summary>
    public string Key { get; }

    public bool IsHeader { get; private init; }

    public bool IsBranch => !IsHeader;

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
    public string AccessibleName => IsHeader ? HeaderText : string.Join(", ", new[]
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
/// The Git Changes window's branch drop-down (#23, D38: B1-B26): the branches, current first, then by name, remote
/// branches under their remote's header without its prefix (question 4), filtered as the search box is typed; its first
/// open's loading state, git's error with Try Again, no match with the footer's New Branch 'x'…, the banners (detached,
/// an operation in progress, where a branch is checked out, what Update found) and the rows Update is running on. The
/// list is kept between opens, so later opens show it at once while it reloads.
/// </summary>
public sealed class GitBranchPickerViewModel : ObservableObject
{
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
                    Query = string.Empty;
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
                Rebuild();
            }
        }
    }

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
        set => SetField(ref _selected, value is { IsHeader: true } ? _selected : value);
    }

    /// <summary>"2 of 7": the branches shown of all.</summary>
    public string CountText { get; private set; } = string.Empty;

    /// <summary>No branch matches the search (B3).</summary>
    public bool IsEmpty { get; private set; }

    public string EmptyText => Localizer.Format("msg.gitBranches.noBranchesMatch", "No branches match “{0}”.", _query.Trim());

    /// <summary>The footer: New Branch…, or New Branch 'zzz'… when nothing matches (B3). None in an empty repository (B16).</summary>
    public string FooterText => IsEmpty && _query.Trim().Length > 0
        ? Localizer.Format("msg.gitBranches.newBranchNamed", "New Branch '{0}'…", _query.Trim())
        : Localizer.Get("msg.gitBranches.newBranch", "New Branch…");

    public bool HasFooter => _git.Status is not { Branch: not null, Head: null };

    /// <summary>The name the footer's New Branch fills in (the search, when nothing matches).</summary>
    public string? FooterName => IsEmpty && _query.Trim().Length > 0 ? _query.Trim() : null;

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
                NotifyBanner();
                OnPropertyChanged(nameof(HasFooter));
            }
        }
    }

    /// <summary>
    /// The rows for the search: Local branches (the current first, then by name; an empty repository's branch, which git
    /// doesn't list, with "No commits yet"), then each remote's branches without its prefix, by name.
    /// </summary>
    private void Rebuild()
    {
        var selectedKey = _selected?.Key;
        var query = _query.Trim();
        var status = _git.Status;
        var all = _branches ?? [];
        var locals = all.Where(b => !b.IsRemote).ToList();
        var remotes = _hasRemote ? all.Where(b => b.IsRemote).ToList() : [];
        var unborn = status is { Branch: { } name, Head: null } && locals.All(b => b.Name != name)
            ? new GitBranchDetails("refs/heads/" + name, name, IsRemote: false, IsCurrent: true)
            : null;
        if (unborn is not null)
        {
            locals.Insert(0, unborn);
        }

        bool Matches(string name) => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<int>? Highlights(string name)
        {
            var at = query.Length == 0 ? -1 : name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? null : [.. Enumerable.Range(at, query.Length)];
        }

        var rows = new List<GitBranchRow>();
        if (_repositories is { } repositories)
        {
            RebuildAll(repositories, rows, Matches, Highlights);
            Show(rows, rows.Count(r => r.IsBranch && !r.IsCommon), repositories.Sum(r => r.Branches.Count(b => !b.IsRemote || r.HasRemote)));
            return;
        }

        var shownLocals = locals.Where(b => Matches(b.Name))
            .OrderByDescending(b => b.IsCurrent && status?.Branch is not null)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_branches is not null || unborn is not null)
        {
            if (shownLocals.Count > 0)
            {
                rows.Add(GitBranchRow.Header(Localizer.Get("msg.gitBranches.localBranches", "Local branches")));
                rows.AddRange(shownLocals.Select(b => LocalRow(b, b == unborn)));
            }

            foreach (var group in remotes.GroupBy(b => b.Remote ?? string.Empty).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var shown = group.Where(b => Matches(b.ShortName)).OrderBy(b => b.ShortName, StringComparer.OrdinalIgnoreCase).ToList();
                if (shown.Count == 0)
                {
                    continue;
                }

                rows.Add(GitBranchRow.Header(group.Key));
                rows.AddRange(shown.Select(b => GitBranchRow.ForBranch(b, b.ShortName, isCurrent: false, Highlights(b.ShortName))));
            }
        }

        Show(rows, rows.Count(r => r.IsBranch), locals.Count + remotes.Count);

        void Show(List<GitBranchRow> shown, int count, int total)
        {
            CountText = Localizer.Format("msg.gitBranches.countOfTotal", "{0} of {1}", count, total);
            IsEmpty = !_isLoading && _error is null && count == 0 && query.Length > 0;
            Rows.Clear();
            foreach (var row in shown)
            {
                Rows.Add(row);
            }

            _selected = Rows.FirstOrDefault(r => r.Key == selectedKey);
            foreach (var property in new[] { nameof(Selected), nameof(CountText), nameof(IsEmpty), nameof(EmptyText), nameof(FooterText), nameof(FooterName) })
            {
                OnPropertyChanged(property);
            }
        }

        GitBranchRow LocalRow(GitBranchDetails b, bool isUnborn)
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
                b.Name,
                b.IsCurrent && status?.Branch is not null,
                Highlights(b.Name),
                detail: isUnborn ? Localizer.Get("msg.gitBranches.noCommitsYet", "No commits yet") : string.Empty,
                track: track,
                trackName: track.Length > 0 ? $"{string.Join(", ", parts)} ({b.Upstream})" : string.Empty,
                busy: _busy.Contains(b.Name) ? Localizer.Get("msg.gitBranches.updating", "Updating…") : string.Empty);
        }
    }

    /// <summary>
    /// The rows with all repositories shown (ADR 0085): Common branches (the local branches every repository has, checked
    /// when every repository is on it), then All branches: each repository's header with its current branch, its local
    /// branches (current first) and, when it has a remote, its remote branches with their remote's name.
    /// </summary>
    private void RebuildAll(IReadOnlyList<RepositoryBranches> repositories, List<GitBranchRow> rows, Func<string, bool> matches, Func<string, IReadOnlyList<int>?> highlights)
    {
        var common = GitService.CommonBranches([.. repositories.Select(r => r.Branches)]).Where(matches).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (common.Count > 0)
        {
            rows.Add(GitBranchRow.Header(Localizer.Get("msg.gitBranches.commonBranches", "Common branches")));
            foreach (var name in common)
            {
                var each = CommonBranchIn(name);
                var current = each.All(e => e.Branch.IsCurrent && e.Git.Status?.Branch is not null);

                // A copy of the first repository's: a row's branch identifies the row (RowOf).
                rows.Add(GitBranchRow.ForBranch(each[0].Branch with { }, name, current, highlights(name), isCommon: true));
            }
        }

        rows.Add(GitBranchRow.Header(Localizer.Get("msg.gitBranches.allBranches", "All branches"), "h:all"));
        foreach (var repository in repositories)
        {
            var git = repository.Git;
            var status = git.Status;
            var locals = repository.Branches.Where(b => !b.IsRemote && matches(b.Name))
                .OrderByDescending(b => b.IsCurrent && status?.Branch is not null)
                .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase);
            var remotes = repository.HasRemote ? repository.Branches.Where(b => b.IsRemote && matches(b.Name)).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<GitBranchDetails>();
            var shown = locals.Concat(remotes).ToList();
            if (shown.Count == 0)
            {
                continue;
            }

            var on = status?.Branch ?? Localizer.Get("msg.gitBranches.detached", "detached");
            rows.Add(GitBranchRow.Header($"{git.Name} ({on})", "h:" + git.Root, depth: 1));
            rows.AddRange(shown.Select(b => GitBranchRow.ForBranch(b, b.Name, !b.IsRemote && b.IsCurrent && status?.Branch is not null, highlights(b.Name), repository: git, depth: 2)));
        }
    }

    /// <summary>The selection moves down (+1) or up (-1) over the branch rows, skipping headers (the keyboard, B2).</summary>
    public void MoveSelection(int direction)
    {
        var branches = Rows.Where(r => r.IsBranch).ToList();
        if (branches.Count == 0)
        {
            return;
        }

        var index = _selected is null ? -1 : branches.IndexOf(_selected);
        Selected = index < 0 ? (direction > 0 ? branches[0] : branches[^1]) : branches[Math.Clamp(index + direction, 0, branches.Count - 1)];
    }

    /// <summary>The local branch with a remote branch's name, which Checkout on the remote row switches to (question 7).</summary>
    public GitBranchDetails? LocalTwin(GitBranchDetails remote) =>
        remote.IsRemote ? BranchesBeside(remote).FirstOrDefault(b => !b.IsRemote && b.Name == remote.ShortName) : null;
}
