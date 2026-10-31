using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Hosting;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>What a node of the Git Repository document's tree is.</summary>
public enum GitRepositoryNodeKind
{
    /// <summary>Branches / Tags, Pull Requests, Submodules, a remote's or the tags' folder.</summary>
    Group,

    /// <summary>A repository, "WPoseidon (develop)".</summary>
    Repository,

    /// <summary>A part of branch names before a <c>/</c> (<c>feature</c>).</summary>
    Folder,

    Branch,
    Tag,
    PullRequest,
    Submodule,

    /// <summary>A line that says why a group is empty (sign in, not on a known host, git failed).</summary>
    Message,
}

/// <summary>
/// A node of the Git Repository document's tree (the classic IDE's Git Repository window): a group, a repository, a
/// folder of branch names, a branch, a tag, a pull request or a submodule. <see cref="Key"/> stays the same across
/// reloads, so the expanded folders and the selection survive them.
/// </summary>
public sealed class GitRepositoryNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public GitRepositoryNode(string key, GitRepositoryNodeKind kind, string text, GitService? repository)
    {
        Key = key;
        Kind = kind;
        Text = text;
        Repository = repository;
    }

    public string Key { get; }

    public GitRepositoryNodeKind Kind { get; }

    public string Text { get; }

    /// <summary>The repository it belongs to (null for a group above every repository).</summary>
    public GitService? Repository { get; }

    public ObservableCollection<GitRepositoryNode> Children { get; } = [];

    public GitBranchDetails? Branch { get; init; }

    public GitTag? Tag { get; init; }

    public PullRequestDto? PullRequest { get; init; }

    public GitSubmodule? Submodule { get; init; }

    /// <summary>A branch's or tag's repository's branches as read with it (a remote branch's menu names its local one).</summary>
    public IReadOnlyList<GitBranchDetails>? Siblings { get; init; }

    /// <summary>Its repository has a remote (Push).</summary>
    public bool HasRemote { get; init; }

    /// <summary>A pull request's host (Check Out).</summary>
    public HostingMatch? Match { get; init; }

    /// <summary>The ref a branch or tag node names (<c>refs/heads/main</c>, <c>refs/tags/v1</c>), else null.</summary>
    public string? Reference => Branch?.FullRef ?? (Tag is { } tag ? "refs/tags/" + tag.Name : null);

    /// <summary>The current branch (bold, as in the classic IDE).</summary>
    public bool IsCurrent { get; init; }

    /// <summary>A local branch's outgoing and incoming commits (↑1 ↓2), as the branch drop-down shows them.</summary>
    public string Track { get; init; } = string.Empty;

    public bool HasTrack => Track.Length > 0;

    public string TrackName { get; init; } = string.Empty;

    /// <summary>Muted text after the name: a tag's message, a pull request's author, a submodule's state.</summary>
    public string Detail { get; init; } = string.Empty;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>The message node's action (Sign In), run by a double-click or Enter.</summary>
    public Func<Task>? Action { get; init; }

    public string Icon => Kind switch
    {
        GitRepositoryNodeKind.Repository => "repository",
        GitRepositoryNodeKind.Folder => _isExpanded ? "folder-open" : "folder",
        GitRepositoryNodeKind.Branch => "branch",
        GitRepositoryNodeKind.Tag => "tag",
        GitRepositoryNodeKind.PullRequest => "pull-request",
        GitRepositoryNodeKind.Submodule => "repository",
        GitRepositoryNodeKind.Message => "info",
        _ when Key.EndsWith(":remote", StringComparison.Ordinal) => "cloud",
        _ when Key.EndsWith(":tags", StringComparison.Ordinal) => "tag",
        _ when Key.EndsWith("branches", StringComparison.Ordinal) => "branch",
        _ when Key.EndsWith("pullRequests", StringComparison.Ordinal) => "pull-request",
        _ when Key.EndsWith("submodules", StringComparison.Ordinal) => "repositories",
        _ => _isExpanded ? "folder-open" : "folder",
    };

    public bool IsMessage => Kind == GitRepositoryNodeKind.Message;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Icon));
                ExpandedChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>Expands or collapses it without telling its owner, which does (its rows, M57).</summary>
    internal void SetExpanded(bool expanded)
    {
        if (SetField(ref _isExpanded, expanded, nameof(IsExpanded)))
        {
            OnPropertyChanged(nameof(Icon));
        }
    }

    /// <summary>The node it's under, or null for a root (set as the tree is built).</summary>
    public GitRepositoryNode? Parent { get; internal set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>Raised when the user expands or collapses it (the document remembers it across reloads).</summary>
    internal event Action<GitRepositoryNode, bool>? ExpandedChanged;

    public string AccessibleName => string.Join(", ", new[]
    {
        Text,
        IsCurrent ? Localizer.Get("msg.gitRepository.current", "current branch") : string.Empty,
        TrackName,
        Detail,
    }.Where(p => p.Length > 0));

    public IEnumerable<GitRepositoryNode> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
}

/// <summary>
/// One visible line of the Git Repository tree, as Git Changes' rows (M57): the tree is flattened so the list can
/// virtualize (a tree view made an item for every branch and tag, and the tree was made again at each filter keystroke and
/// status read). A tree built again keeps the row of a node that's still in its place, with its new node, so its control,
/// the selection and the scroll position stay; it raises only what changed.
/// </summary>
public sealed class GitRepositoryRow(GitRepositoryNode node, int depth) : ObservableObject, ITreeRow
{
    private GitRepositoryNode _node = node;

    public GitRepositoryNode Node
    {
        get => _node;
        set
        {
            var old = _node;
            _node = value;
            if (ReferenceEquals(old, value))
            {
                return;
            }

            var changed = false;
            void Changed(bool differs, string name)
            {
                if (differs)
                {
                    changed = true;
                    OnPropertyChanged(name);
                }
            }

            Changed(!string.Equals(old.Text, value.Text, StringComparison.Ordinal), nameof(Text));
            Changed(!string.Equals(old.Icon, value.Icon, StringComparison.Ordinal), nameof(Icon));
            Changed(old.IsCurrent != value.IsCurrent, nameof(IsCurrent));
            Changed(old.IsMessage != value.IsMessage, nameof(IsMessage));
            Changed(!string.Equals(old.Track, value.Track, StringComparison.Ordinal), nameof(Track));
            Changed(old.HasTrack != value.HasTrack, nameof(HasTrack));
            Changed(!string.Equals(old.TrackName, value.TrackName, StringComparison.Ordinal), nameof(TrackName));
            Changed(!string.Equals(old.Detail, value.Detail, StringComparison.Ordinal), nameof(Detail));
            Changed(old.HasDetail != value.HasDetail, nameof(HasDetail));
            Changed(old.Children.Count == 0 != (value.Children.Count == 0), nameof(HasChildren));
            Changed(old.IsExpanded != value.IsExpanded, nameof(IsExpanded));
            if (changed)
            {
                OnPropertyChanged(nameof(Chevron));
                OnPropertyChanged(nameof(Tip));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>The node's key, which stays the same across reloads.</summary>
    public string Key => _node.Key;

    public int Depth { get; } = depth;

    /// <summary>The indentation of its level, 16 px each, as the tree view's.</summary>
    public Avalonia.Thickness Indent => new(Depth * 16, 0, 0, 0);

    /// <summary>A group, a repository or a folder with something in it: it expands and collapses.</summary>
    public bool HasChildren => _node.Children.Count > 0;

    public bool IsExpanded => _node.IsExpanded;

    public string? Chevron => HasChildren ? (_node.IsExpanded ? "chevron-down" : "chevron-right") : null;

    public string Icon => _node.Icon;

    public string Text => _node.Text;

    public bool IsCurrent => _node.IsCurrent;

    public bool IsMessage => _node.IsMessage;

    public string Track => _node.Track;

    public bool HasTrack => _node.HasTrack;

    public string TrackName => _node.TrackName;

    public string Detail => _node.Detail;

    public bool HasDetail => _node.HasDetail;

    /// <summary>The tooltip: the node's name, the current branch, its tracking and its detail.</summary>
    public string Tip => _node.AccessibleName;

    /// <summary>The node's name, and a folder's state: a list's row has no expander for a screen reader to read.</summary>
    public string AccessibleName => !HasChildren ? _node.AccessibleName
        : _node.IsExpanded ? Localizer.Format("msg.gitChanges.expandedAccessibleName", "{0}, expanded", _node.AccessibleName)
        : Localizer.Format("msg.gitChanges.collapsedAccessibleName", "{0}, collapsed", _node.AccessibleName);

    /// <summary>Its node was expanded or collapsed (its owner shows or hides the rows under it).</summary>
    internal void ExpandedChanged()
    {
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(Chevron));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(AccessibleName));
    }
}

/// <summary>
/// A repository's open pull requests for the tree (all of them), or why there are none to show; with the Pull Requests
/// window's other filters, the user's own and those that ask for the user's review (null: the host couldn't say).
/// </summary>
public sealed record GitRepositoryPullRequests(IReadOnlyList<PullRequestDto> PullRequests, string? Message, HostingMatch? Match = null, Func<Task>? SignIn = null)
{
    public IReadOnlyList<PullRequestDto>? Mine { get; init; }

    public IReadOnlyList<PullRequestDto>? ReviewRequested { get; init; }
}

/// <summary>What the Git Repository document asks of the shell.</summary>
public sealed class GitRepositoryActions
{
    /// <summary>The repositories to show: the solution's (all of them with All Repositories), the open one first.</summary>
    public required Func<IReadOnlyList<GitService>> Repositories { get; init; }

    /// <summary>A repository's open pull requests (nothing reaches a host the user isn't signed in to).</summary>
    public required Func<GitService, CancellationToken, Task<GitRepositoryPullRequests>> PullRequests { get; init; }

    /// <summary>The history of a ref (its commit menu runs in its repository), and whether it offers Show All Branches.</summary>
    public required Func<GitService, GitLogQuery, string, bool, GitHistoryViewModel> CreateHistory { get; init; }

    /// <summary>A command run in a repository with a ref (or none): Fetch, Pull, Update, Push, Sync, Checkout…</summary>
    public required Func<string, GitService, string?, Task> Run { get; init; }

    /// <summary>A node's menu, built by the shell (a branch's is <c>gitChanges/branch</c>).</summary>
    public Func<GitRepositoryNode, Avalonia.Controls.ContextMenu?>? MenuFor { get; init; }

    /// <summary>A double-click or Enter on a node: a branch checks out, a pull request opens, a message's action runs.</summary>
    public Func<GitRepositoryNode, Task>? Activate { get; init; }
}

/// <summary>
/// The Git Repository document (the classic IDE's Git Repository window, Git → Manage Branches): a tree of each
/// repository's local branches (in folders by their <c>/</c>), its remotes' branches, its tags, its open pull requests
/// and its submodules, with a filter; and on the right the selected branch's or tag's history (the History window's
/// graph, details and commit menu), with a local branch's incoming commits above it and its outgoing count, with Fetch,
/// Pull (Update for a branch that isn't checked out), Push and Sync. Several repositories are each a root of their own.
/// </summary>
public sealed class GitRepositoryViewModel : ObservableObject, IDisposable, IShownAware, ITreeRowsOwner
{
    public const string DocumentId = "git-repository";

    /// <summary>How long the filter waits after a keystroke before it filters (M57): the tree is filtered once per pause.</summary>
    internal static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(150);

    private readonly GitRepositoryActions _actions;
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<GitService, RepositoryData> _data = [];
    private readonly ShownViews _views = new();
    private Dictionary<string, GitRepositoryNode> _byKey = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _repositoryKeys = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _selectionCts;
    private GitRepositoryNode? _selected;
    private GitRepositoryRow? _selectedRow;
    private string _filter = string.Empty;
    private bool _isLoading;
    private string? _error;
    private GitHistoryViewModel? _history;
    private GitService? _submoduleService;
    private string? _historyFor;
    private string _header = string.Empty;
    private string? _message;
    private int _version;
    private bool _rebuilding;
    private bool _stale;
    private Task? _loading;
    private Task? _building;
    private bool _buildAgain;
    private Avalonia.Threading.DispatcherTimer? _filterTimer;

    /// <summary>What was read of a repository: its branches, tags, remotes, submodules and pull requests, and HEAD's branch.</summary>
    private sealed record RepositoryData(
        IReadOnlyList<GitBranchDetails> Branches,
        IReadOnlyList<GitTag> Tags,
        IReadOnlyList<string> Remotes,
        IReadOnlyList<GitSubmodule> Submodules,
        GitRepositoryPullRequests? PullRequests,
        string? Head = null)
    {
        /// <summary>The same refs (a status read that changed none builds nothing, M57).</summary>
        public bool SameAs(RepositoryData other) =>
            Branches.SequenceEqual(other.Branches) && Tags.SequenceEqual(other.Tags) && Remotes.SequenceEqual(other.Remotes, StringComparer.Ordinal)
            && Submodules.SequenceEqual(other.Submodules) && ReferenceEquals(PullRequests, other.PullRequests) && string.Equals(Head, other.Head, StringComparison.Ordinal);
    }

    /// <summary>A repository as the tree is built of it, read on the UI thread so the build can run on any.</summary>
    private sealed record RepositoryState(GitService Git, string Root, string Name, RepositoryData Data);

    /// <summary>What the tree is built of: the repositories, the filter and the expansion the user chose.</summary>
    private sealed record TreeQuery(IReadOnlyList<RepositoryState> Repositories, string Filter, IReadOnlyDictionary<string, bool> Expanded, Action<GitRepositoryNode, bool> ExpandedChanged);

    /// <summary>A built tree: its roots, its rows and its nodes by key.</summary>
    private sealed record BuiltTree(IReadOnlyList<GitRepositoryNode> Roots, IReadOnlyList<(GitRepositoryNode Node, int Depth)> Rows, Dictionary<string, GitRepositoryNode> ByKey, IReadOnlyList<string> RepositoryKeys);

    public GitRepositoryViewModel(GitRepositoryActions actions)
    {
        _actions = actions;
    }

    public string Title => Localizer.Get("msg.gitRepository.title", "Git Repository");

    /// <summary>The tree's top nodes (the view shows <see cref="Rows"/>).</summary>
    public ResettableCollection<GitRepositoryNode> Roots { get; } = [];

    /// <summary>The visible rows: each root, then the rows under it when it's expanded (M57: a virtualized list, as Git Changes').</summary>
    public ResettableCollection<GitRepositoryRow> Rows { get; } = [];

    /// <summary>How many times the tree was built (tests: none while hidden, none for refs that didn't change, one per filter pause).</summary>
    internal int TreesBuilt { get; private set; }

    /// <summary>How many times the repositories' refs were read (tests: none while hidden).</summary>
    internal int RefsReads { get; private set; }

    /// <summary>Whether a build of the tree waits or runs: a filter in its pause, or a build off the UI thread (tests).</summary>
    internal bool IsRebuilding => _building is { IsCompleted: false } || _filterTimer?.IsEnabled == true;

    /// <summary>Whether a read of the refs, or the tree built after it, is under way (tests).</summary>
    internal bool IsReading => _loading is { IsCompleted: false } || IsRebuilding;

    /// <summary>Whether a view shows the document (tests).</summary>
    internal bool IsViewShown => _views.Any;

    /// <summary>Raised to bring a row into view (the list doesn't scroll to its selection by itself, M56).</summary>
    public event Action<GitRepositoryRow>? RevealRequested;

    /// <summary>Text in a branch's, tag's, pull request's or submodule's name (case-insensitive): filtered after a pause, off the UI thread.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                if (_filterTimer is null)
                {
                    _filterTimer = new Avalonia.Threading.DispatcherTimer { Interval = FilterDelay };
                    _filterTimer.Tick += (_, _) =>
                    {
                        _filterTimer.Stop();
                        _ = RebuildAsync();
                    };
                }

                _filterTimer.Stop();
                _filterTimer.Start();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
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

    public GitRepositoryNode? Selected
    {
        get => _selected;
        set
        {
            // The list's selection goes while a build replaces the rows (the build selects the same node again), and as a
            // folder collapses over it; a node stays selected while it's in the tree.
            if (_rebuilding || value is null && _selected is not null && _byKey.TryGetValue(_selected.Key, out var kept) && ReferenceEquals(kept, _selected))
            {
                return;
            }

            if (SetField(ref _selected, value))
            {
                SyncSelectedRow();
                _ = ShowAsync(value);
            }
        }
    }

    /// <summary>The list's selection: the row of <see cref="Selected"/>, or null while that's under a collapsed folder.</summary>
    public GitRepositoryRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (_rebuilding || ReferenceEquals(value, _selectedRow))
            {
                return;
            }

            if (value is null)
            {
                // Its row went: a folder collapsed over it (the node stays selected), or the node is gone.
                _selectedRow = null;
                Selected = null;
                return;
            }

            _selectedRow = value;
            Selected = value.Node;
        }
    }

    /// <summary>The selected ref's history, or null (a group, a message, a pull request whose branch isn't here).</summary>
    public GitHistoryViewModel? History
    {
        get => _history;
        private set
        {
            var old = _history;
            if (SetField(ref _history, value))
            {
                old?.Dispose();
                OnPropertyChanged(nameof(HasHistory));
            }
        }
    }

    public bool HasHistory => _history is not null;

    /// <summary>"Branch / Tag: develop", "Pull Request: #12 Title", "Submodule: libs/core".</summary>
    public string Header
    {
        get => _header;
        private set => SetField(ref _header, value);
    }

    /// <summary>Why the right side has no history (nothing selected, a pull request's branch not fetched…).</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    /// <summary>
    /// A view shows the document, or no longer does (a tab behind another, closed): while none does, git's changes read
    /// nothing (<see cref="RequestReload"/>), and showing it reads the refs once (M57). Each read was 4-5 git processes and
    /// a tree of every branch and tag, at each status read with the tab in the background.
    /// </summary>
    public void SetViewShown(object view, bool shown)
    {
        if (_views.Set(view, shown) && shown && _stale)
        {
            _ = LoadAsync(pullRequests: false);
        }
    }

    /// <summary>After git changed a repository (its status): the refs again now while the document is shown, else once it is.</summary>
    public void RequestReload()
    {
        if (_views.Any)
        {
            _ = LoadAsync(pullRequests: false);
        }
        else
        {
            _stale = true;
        }
    }

    /// <summary>Reads every repository's branches, tags, remotes, submodules and pull requests, then the tree.</summary>
    public Task LoadAsync() => LoadAsync(pullRequests: true);

    /// <summary>
    /// Reads the repositories again: after git changed them (the status), only the refs; with
    /// <paramref name="pullRequests"/>, the pull requests from their hosts too (on open and Refresh). Refs the same as
    /// the last read's build nothing; others build the tree off the UI thread and show it in place (M57).
    /// </summary>
    public Task LoadAsync(bool pullRequests)
    {
        _stale = false;
        return _loading = LoadCoreAsync(pullRequests);
    }

    private async Task LoadCoreAsync(bool pullRequests)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var token = cts.Token;
        var version = ++_version;
        IsLoading = Roots.Count == 0;
        Error = null;
        try
        {
            var repositories = _actions.Repositories();
            var data = new Dictionary<GitService, RepositoryData>();
            RefsReads++;
            foreach (var git in repositories)
            {
                var branches = await git.BranchDetailsAsync(token).ConfigureAwait(true);
                var tags = await git.TagsAsync(token).ConfigureAwait(true);
                var remotes = (await git.RemotesAsync(token).ConfigureAwait(true)).Select(r => r.Name).ToList();
                var submodules = File.Exists(Path.Combine(git.Root ?? string.Empty, ".gitmodules")) ? await git.SubmodulesAsync(token).ConfigureAwait(true) : [];
                var prs = pullRequests ? null : _data.GetValueOrDefault(git)?.PullRequests;
                data[git] = new RepositoryData(branches, tags, remotes, submodules, prs, git.Status?.Branch);
            }

            if (version != _version)
            {
                return;
            }

            var same = TreesBuilt > 0 && data.Count == _data.Count && data.All(d => _data.TryGetValue(d.Key, out var old) && old.SameAs(d.Value));
            _data.Clear();
            foreach (var (git, each) in data)
            {
                _data[git] = each;
            }

            if (!same)
            {
                await RebuildAsync().ConfigureAwait(true);
            }

            if (pullRequests)
            {
                foreach (var git in repositories)
                {
                    GitRepositoryPullRequests result;
                    try
                    {
                        result = await _actions.PullRequests(git, token).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (ex is HostingRequestException or HostingSignInRequiredException or GitException or InvalidOperationException)
                    {
                        result = new GitRepositoryPullRequests([], ex.Message);
                    }

                    if (version != _version || !_data.TryGetValue(git, out var current))
                    {
                        return;
                    }

                    _data[git] = current with { PullRequests = result };
                }

                await RebuildAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            if (version == _version)
            {
                Error = Localizer.Format("msg.gitRepository.couldntRead", "Couldn't read the repository: {0}", ex.Message);
            }
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Builds the tree from what was read, with the filter, off the UI thread, and shows its rows in place; keeps the
    /// expanded nodes and the selection (M57). A build asked for while one runs runs after it, and the older one isn't
    /// shown; the task ends with the newest shown. On the UI thread, a tree view item per branch and tag at each filter
    /// keystroke and status read took seconds with thousands of them.
    /// </summary>
    private Task RebuildAsync()
    {
        _buildAgain = true;
        return _building is { IsCompleted: false } running ? running : _building = BuildAsync();
    }

    private async Task BuildAsync()
    {
        while (_buildAgain)
        {
            _buildAgain = false;
            var query = Query();
            var tree = await Task.Run(() => Build(query)).ConfigureAwait(true);
            if (!_buildAgain)
            {
                Apply(tree);
            }
        }
    }

    private TreeQuery Query() => new(
        [.. _actions.Repositories().Where(_data.ContainsKey).Select(git => new RepositoryState(git, git.Root ?? string.Empty, git.Name, _data[git]))],
        _filter.Trim(),
        new Dictionary<string, bool>(_expanded, StringComparer.Ordinal),
        OnExpandedChanged);

    private static BuiltTree Build(TreeQuery query)
    {
        var filter = query.Filter;
        var roots = new List<GitRepositoryNode>();
        var repositories = query.Repositories;
        if (repositories.Count == 1)
        {
            // As in the classic IDE: Branches / Tags with the repository under it, then Pull Requests and Submodules.
            var repository = repositories[0];
            var refs = Group("branches", Localizer.Get("msg.gitRepository.branchesTags", "Branches / Tags"), null);
            var node = new GitRepositoryNode("r:" + repository.Root, GitRepositoryNodeKind.Repository, RepositoryText(repository), repository.Git);
            AddRefs(node, repository, filter);
            Add(refs, node);
            roots.Add(refs);
            roots.Add(PullRequestsNode(repository, "pullRequests", filter));
            roots.Add(SubmodulesNode(repository, "submodules", filter));
        }
        else
        {
            // Several repositories: each one a root with its own Branches / Tags, Pull Requests and Submodules.
            foreach (var repository in repositories)
            {
                var root = new GitRepositoryNode("r:" + repository.Root, GitRepositoryNodeKind.Repository, RepositoryText(repository), repository.Git);
                var refs = Group("r:" + repository.Root + ":branches", Localizer.Get("msg.gitRepository.branchesTags", "Branches / Tags"), repository.Git);
                AddRefs(refs, repository, filter);
                Add(root, refs);
                Add(root, PullRequestsNode(repository, "r:" + repository.Root + ":pullRequests", filter));
                Add(root, SubmodulesNode(repository, "r:" + repository.Root + ":submodules", filter));
                roots.Add(root);
            }
        }

        // Expansion: what the user chose, else the default (filtering shows everything that matches); the nodes by key.
        var byKey = new Dictionary<string, GitRepositoryNode>(StringComparer.Ordinal);
        var rows = new List<(GitRepositoryNode Node, int Depth)>();
        foreach (var root in roots)
        {
            Visit(root, 0, shown: true);
        }

        return new BuiltTree(roots, rows, byKey, [.. repositories.Select(r => "r:" + r.Root)]);

        void Visit(GitRepositoryNode node, int depth, bool shown)
        {
            node.SetExpanded(node.Children.Count > 0 && (filter.Length > 0 || (query.Expanded.TryGetValue(node.Key, out var expanded) ? expanded : ExpandedByDefault(node))));
            node.ExpandedChanged += query.ExpandedChanged;
            byKey.TryAdd(node.Key, node);
            if (shown)
            {
                rows.Add((node, depth));
            }

            foreach (var child in node.Children)
            {
                Visit(child, depth + 1, shown && node.IsExpanded);
            }
        }
    }

    /// <summary>Adds <paramref name="child"/> under <paramref name="parent"/>.</summary>
    private static void Add(GitRepositoryNode parent, GitRepositoryNode child)
    {
        child.Parent = parent;
        parent.Children.Add(child);
    }

    /// <summary>Shows a built tree: the rows of the same nodes in the same places stay, with their new nodes; the selection stays.</summary>
    private void Apply(BuiltTree tree)
    {
        TreesBuilt++;
        var selectedKey = _selected?.Key;
        _rebuilding = true;
        try
        {
            _byKey = tree.ByKey;
            _repositoryKeys = tree.RepositoryKeys;
            Roots.Reset(tree.Roots);
            Rows.Patch(tree.Rows, (row, r) => row.Depth == r.Depth && string.Equals(row.Key, r.Node.Key, StringComparison.Ordinal), r => new GitRepositoryRow(r.Node, r.Depth), (row, r) => row.Node = r.Node);
        }
        finally
        {
            _rebuilding = false;
        }

        // The same node selected again; the current branch at first; nothing for one that's gone (a deleted branch).
        var selected = selectedKey is null ? null : _byKey.GetValueOrDefault(selectedKey);
        selected ??= _selected is null ? _byKey.Values.FirstOrDefault(n => n is { Kind: GitRepositoryNodeKind.Branch, IsCurrent: true }) : null;

        // The same branch at the same commit keeps its history (and its scroll); a moved branch reloads it.
        _selected = selected;
        OnPropertyChanged(nameof(Selected));
        SyncSelectedRow(force: true);
        if (HistoryKey(selected) != _historyFor)
        {
            _ = ShowAsync(selected);
        }
    }

    /// <summary>The list selects the row of <see cref="Selected"/>, or none while that's under a collapsed folder.</summary>
    private void SyncSelectedRow(bool force = false)
    {
        var row = _selected is null ? null
            : !force && _selectedRow is { } kept && ReferenceEquals(kept.Node, _selected) && Rows.Contains(kept) ? kept
            : Rows.FirstOrDefault(r => ReferenceEquals(r.Node, _selected));
        if (force || !ReferenceEquals(row, _selectedRow))
        {
            _selectedRow = row;
            OnPropertyChanged(nameof(SelectedRow));
        }
    }

    /// <summary>Expands or collapses a row (the chevron, Left and Right, double-click): only the rows under it change.</summary>
    public void SetExpanded(GitRepositoryRow row, bool expanded)
    {
        if (!row.HasChildren || row.IsExpanded == expanded)
        {
            return;
        }

        _expanded[row.Key] = expanded;
        _buildAgain |= _building is { IsCompleted: false }; // a build with the expansion before it would undo it
        row.Node.SetExpanded(expanded);
        row.ExpandedChanged();
        var index = Rows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        if (expanded)
        {
            var below = new List<(GitRepositoryNode Node, int Depth)>();
            Flatten(row.Node.Children, row.Depth + 1, below);
            Rows.InsertRange(index + 1, [.. below.Select(r => new GitRepositoryRow(r.Node, r.Depth))]);
        }
        else
        {
            var end = index + 1;
            while (end < Rows.Count && Rows[end].Depth > row.Depth)
            {
                end++;
            }

            Rows.RemoveRange(index + 1, end - index - 1);
        }

        SyncSelectedRow();
    }

    /// <summary>Expand All and Collapse All on a row: it and every group and folder under it, the rows shown again at once.</summary>
    public void SetExpandedUnder(GitRepositoryRow row, bool expanded)
    {
        Apply(row.Node);
        _buildAgain |= _building is { IsCompleted: false };
        ShowRows();

        void Apply(GitRepositoryNode node)
        {
            if (node.Children.Count == 0)
            {
                return;
            }

            _expanded[node.Key] = expanded;
            node.SetExpanded(expanded);
            foreach (var child in node.Children)
            {
                Apply(child);
            }
        }
    }

    void ITreeRowsOwner.SetExpanded(ITreeRow row, bool expanded)
    {
        if (row is GitRepositoryRow node)
        {
            SetExpanded(node, expanded);
        }
    }

    void ITreeRowsOwner.SetExpandedUnder(ITreeRow row, bool expanded)
    {
        if (row is GitRepositoryRow node)
        {
            SetExpandedUnder(node, expanded);
        }
    }

    /// <summary>A node's IsExpanded was set from outside the rows: they follow.</summary>
    private void OnExpandedChanged(GitRepositoryNode node, bool expanded)
    {
        if (Rows.FirstOrDefault(r => ReferenceEquals(r.Node, node)) is { } row)
        {
            node.SetExpanded(!expanded);
            SetExpanded(row, expanded);
            return;
        }

        _expanded[node.Key] = expanded;
        _buildAgain |= _building is { IsCompleted: false };
        ShowRows();
    }

    /// <summary>The rows of the tree as it's expanded now, shown in place: the rows that stay say their node's state.</summary>
    private void ShowRows()
    {
        var rows = new List<(GitRepositoryNode Node, int Depth)>();
        Flatten(Roots, 0, rows);
        Rows.Patch(rows, (row, r) => row.Depth == r.Depth && ReferenceEquals(row.Node, r.Node), r => new GitRepositoryRow(r.Node, r.Depth), (row, _) => row.ExpandedChanged());
        SyncSelectedRow();
    }

    /// <summary>The rows of <paramref name="nodes"/> at <paramref name="depth"/>: each node, then its children's rows when it's expanded.</summary>
    private static void Flatten(IEnumerable<GitRepositoryNode> nodes, int depth, List<(GitRepositoryNode Node, int Depth)> rows)
    {
        foreach (var node in nodes)
        {
            rows.Add((node, depth));
            if (node.IsExpanded)
            {
                Flatten(node.Children, depth + 1, rows);
            }
        }
    }

    private static bool ExpandedByDefault(GitRepositoryNode node) =>
        node.Kind is GitRepositoryNodeKind.Repository || node.Kind == GitRepositoryNodeKind.Group && !node.Key.EndsWith(":remote", StringComparison.Ordinal) && !node.Key.EndsWith(":tags", StringComparison.Ordinal)
        || node.Kind == GitRepositoryNodeKind.Folder && !node.Key.Contains(":remote:", StringComparison.Ordinal) && !node.Key.EndsWith("pullRequests:all", StringComparison.Ordinal);

    private static GitRepositoryNode Group(string key, string text, GitService? git) => new(key, GitRepositoryNodeKind.Group, text, git);

    private static string RepositoryText(RepositoryState repository) =>
        $"{repository.Name} ({repository.Data.Head ?? Localizer.Get("msg.gitBranches.detached", "detached")})";

    /// <summary>A repository's local branches (in folders), then each remote's branches and the tags, in folders of their own.</summary>
    private static void AddRefs(GitRepositoryNode parent, RepositoryState repository, string query)
    {
        var data = repository.Data;
        var git = repository.Git;
        var prefix = "r:" + repository.Root;
        var folders = new Dictionary<string, GitRepositoryNode>(StringComparer.Ordinal);
        var counts = new Dictionary<GitRepositoryNode, int>(ReferenceEqualityComparer.Instance);
        bool Matches(string name) => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);

        var locals = data.Branches.Where(b => !b.IsRemote && Matches(b.Name)).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var branch in locals)
        {
            Place(parent, prefix + ":local", branch.Name, git, folders, counts, leaf => LocalBranch(leaf, branch, git, prefix, data.Head, data.Branches, data.Remotes.Count > 0));
        }

        var byRemote = data.Branches.Where(b => b.IsRemote && b.ShortName != "HEAD" && Matches(b.Name)).ToLookup(b => b.Remote ?? string.Empty, StringComparer.Ordinal);
        foreach (var remote in data.Remotes.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            var branches = byRemote[remote].OrderBy(b => b.ShortName, StringComparer.OrdinalIgnoreCase).ToList();
            if (branches.Count == 0)
            {
                continue;
            }

            var folder = Group(prefix + ":remote:" + remote + ":remote", "remotes/" + remote, git);
            foreach (var branch in branches)
            {
                Place(folder, prefix + ":remote:" + remote, branch.ShortName, git, folders, counts, leaf => new GitRepositoryNode(prefix + ":ref:" + branch.FullRef, GitRepositoryNodeKind.Branch, leaf, git) { Branch = branch, Siblings = data.Branches, HasRemote = true });
            }

            Add(parent, folder);
        }

        var tags = data.Tags.Where(t => Matches(t.Name)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (tags.Count > 0)
        {
            var folder = Group(prefix + ":tags", Localizer.Get("msg.gitRepository.tags", "tags"), git);
            foreach (var tag in tags)
            {
                Add(folder, new GitRepositoryNode(prefix + ":ref:refs/tags/" + tag.Name, GitRepositoryNodeKind.Tag, tag.Name, git)
                {
                    Tag = tag,
                    HasRemote = data.Remotes.Count > 0,
                    Detail = tag.Message ?? string.Empty,
                });
            }

            Add(parent, folder);
        }
    }

    private static GitRepositoryNode LocalBranch(string leaf, GitBranchDetails branch, GitService git, string prefix, string? head, IReadOnlyList<GitBranchDetails> siblings, bool hasRemote)
    {
        var track = branch.Upstream is null || branch.UpstreamGone ? string.Empty
            : string.Join(' ', new[] { branch.Ahead > 0 ? "↑" + branch.Ahead.ToString(CultureInfo.CurrentCulture) : string.Empty, branch.Behind > 0 ? "↓" + branch.Behind.ToString(CultureInfo.CurrentCulture) : string.Empty }.Where(t => t.Length > 0));
        var parts = new[]
        {
            branch.Ahead > 0 ? Localizer.Format("msg.gitBranches.outgoing", "{0} outgoing", branch.Ahead) : string.Empty,
            branch.Behind > 0 ? Localizer.Format("msg.gitBranches.incoming", "{0} incoming", branch.Behind) : string.Empty,
        }.Where(p => p.Length > 0);
        return new GitRepositoryNode(prefix + ":ref:" + branch.FullRef, GitRepositoryNodeKind.Branch, leaf, git)
        {
            Branch = branch,
            Siblings = siblings,
            HasRemote = hasRemote,
            IsCurrent = branch.IsCurrent && head is not null,
            Track = track,
            TrackName = track.Length > 0 ? $"{string.Join(", ", parts)} ({branch.Upstream})" : string.Empty,
            Detail = branch.WorktreePath is { } path ? Localizer.Format("msg.gitBranches.checkedOutIn", "Checked out in {0}", path) : string.Empty,
        };
    }

    /// <summary>
    /// Puts a branch under the folders its name's <c>/</c> parts make (<c>feature/x</c>: folder feature, branch x), folders
    /// before branches as the classic IDE lists them. Folders are found by key and each node counts its folders, so a
    /// branch costs the same with thousands beside it (M57: a look through the parent's children for each was folders × branches).
    /// </summary>
    private static void Place(GitRepositoryNode parent, string keyPrefix, string name, GitService git, Dictionary<string, GitRepositoryNode> folders, Dictionary<GitRepositoryNode, int> counts, Func<string, GitRepositoryNode> leaf)
    {
        var parts = name.Split('/');
        var node = parent;
        var path = string.Empty;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            path += parts[i] + "/";
            var key = keyPrefix + ":folder:" + path;
            if (!folders.TryGetValue(key, out var folder))
            {
                folders[key] = folder = new GitRepositoryNode(key, GitRepositoryNodeKind.Folder, parts[i], git) { Parent = node };
                ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(counts, node, out _);
                node.Children.Insert(count++, folder);
            }

            node = folder;
        }

        Add(node, leaf(parts[^1]));
    }

    /// <summary>
    /// Pull Requests: the Pull Requests window's filters as folders, Mine, Review requested and All open, each with its
    /// count; or the one line that says why there are none (signing in, not on a host, git or the host failing).
    /// </summary>
    private static GitRepositoryNode PullRequestsNode(RepositoryState repository, string key, string query)
    {
        var git = repository.Git;
        var node = Group(key, Localizer.Get("msg.gitRepository.pullRequests", "Pull Requests"), git);
        switch (repository.Data.PullRequests)
        {
            case null:
                Add(node, MessageNode(key, Localizer.Get("msg.gitRepository.loadingPullRequests", "Loading…"), git));
                break;
            case { Message: { } message } result:
                Add(node, MessageNode(key, message, git, result.SignIn));
                break;
            case { } result:
                Folder("mine", Localizer.Get("msg.gitRepository.pullRequestsMine", "Mine"), result.Mine, result.Match);
                Folder("reviewRequested", Localizer.Get("msg.gitRepository.pullRequestsReviewRequested", "Review requested"), result.ReviewRequested, result.Match);
                Folder("all", Localizer.Get("msg.gitRepository.pullRequestsAll", "All open"), result.PullRequests, result.Match);
                break;
        }

        return node;

        void Folder(string filter, string title, IReadOnlyList<PullRequestDto>? pullRequests, HostingMatch? match)
        {
            var folderKey = key + ":" + filter;
            var shown = (pullRequests ?? []).Where(p => query.Length == 0 || p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Reference.Contains(query, StringComparison.OrdinalIgnoreCase) || p.SourceBranch.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (query.Length > 0 && shown.Count == 0)
            {
                return; // filtering: only the folders with a match
            }

            var folder = new GitRepositoryNode(folderKey, GitRepositoryNodeKind.Folder, pullRequests is null ? title : $"{title} ({pullRequests.Count.ToString(CultureInfo.CurrentCulture)})", git);
            if (pullRequests is null)
            {
                Add(folder, MessageNode(folderKey, Localizer.Get("msg.gitRepository.pullRequestsUnavailable", "The host didn't say"), git));
            }
            else if (pullRequests.Count == 0)
            {
                Add(folder, MessageNode(folderKey, Localizer.Get("msg.gitRepository.noPullRequests", "No open pull requests"), git));
            }

            foreach (var pullRequest in shown)
            {
                Add(folder, new GitRepositoryNode(folderKey + ":" + pullRequest.Number, GitRepositoryNodeKind.PullRequest, $"{pullRequest.Reference} {pullRequest.Title}", git)
                {
                    PullRequest = pullRequest,
                    Match = match,
                    Detail = $"{pullRequest.SourceBranch} → {pullRequest.TargetBranch} · {pullRequest.Author}{(pullRequest.IsDraft ? " · " + Localizer.Get("msg.gitRepository.draft", "draft") : string.Empty)}",
                });
            }

            Add(node, folder);
        }
    }

    private static GitRepositoryNode SubmodulesNode(RepositoryState repository, string key, string query)
    {
        var node = Group(key, Localizer.Get("msg.gitRepository.submodules", "Submodules"), repository.Git);
        foreach (var submodule in repository.Data.Submodules)
        {
            var relative = Path.GetRelativePath(repository.Root, submodule.Path).Replace('\\', '/');
            if (query.Length > 0 && !relative.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Add(node, new GitRepositoryNode(key + ":" + relative, GitRepositoryNodeKind.Submodule, relative, repository.Git)
            {
                Submodule = submodule,
                Detail = submodule.State switch
                {
                    '-' => Localizer.Get("msg.gitRepository.submoduleNotInitialized", "not initialized"),
                    '+' => Localizer.Get("msg.gitRepository.submoduleOtherCommit", "at another commit"),
                    'U' => Localizer.Get("msg.gitRepository.submoduleConflicts", "conflicts"),
                    _ => submodule.Commit[..Math.Min(8, submodule.Commit.Length)],
                },
            });
        }

        return node;
    }

    private static GitRepositoryNode MessageNode(string key, string text, GitService git, Func<Task>? action = null) =>
        new(key + ":message", GitRepositoryNodeKind.Message, text, git) { Action = action };

    /// <summary>What the history on the right is of: a node's ref and commit (a moved branch reloads it).</summary>
    private static string? HistoryKey(GitRepositoryNode? node) => node switch
    {
        { Kind: GitRepositoryNodeKind.Branch or GitRepositoryNodeKind.Tag } => $"{node.Key}@{node.Branch?.Commit ?? node.Tag?.Commit}|{node.Branch?.Ahead}|{node.Branch?.Behind}|{node.IsCurrent}",
        { Kind: GitRepositoryNodeKind.PullRequest or GitRepositoryNodeKind.Submodule } => node.Key + "@" + (node.PullRequest?.SourceCommit ?? node.Submodule?.Commit),
        _ => node?.Key,
    };

    /// <summary>The right side for a node: a branch's or tag's history (and a local branch's incoming commits), a pull request's commits, a submodule's history.</summary>
    private async Task ShowAsync(GitRepositoryNode? node)
    {
        _selectionCts?.Cancel();
        var cts = _selectionCts = new CancellationTokenSource();
        _historyFor = HistoryKey(node);
        var submoduleService = _submoduleService;
        _submoduleService = null;
        try
        {
            switch (node)
            {
                case { Kind: GitRepositoryNodeKind.Branch or GitRepositoryNodeKind.Tag, Repository: { } git, Reference: { } reference }:
                    Header = Localizer.Format("msg.gitRepository.branchTagHeader", "Branch / Tag: {0}", node.Branch?.Name ?? node.Tag!.Name);
                    ShowHistory(git, new GitLogQuery { Revision = reference }, node.Text, reference, SectionsFor(node, git, reference));
                    break;
                case { Kind: GitRepositoryNodeKind.PullRequest, Repository: { } git, PullRequest: { } pullRequest }:
                    Header = Localizer.Format("msg.gitRepository.pullRequestHeader", "Pull Request: {0} {1} ({2} → {3})", pullRequest.Reference, pullRequest.Title, pullRequest.SourceBranch, pullRequest.TargetBranch);
                    ShowPullRequest(git, pullRequest);
                    break;
                case { Kind: GitRepositoryNodeKind.Submodule, Submodule: { } submodule }:
                    Header = Localizer.Format("msg.gitRepository.submoduleHeader", "Submodule: {0}", node.Text);
                    if (!submodule.IsInitialized)
                    {
                        History = null;
                        Message = Localizer.Get("msg.gitRepository.submoduleNotInitializedMessage", "The submodule isn't initialized. Update Submodules (its menu) checks it out.");
                        break;
                    }

                    // Its own repository, read (not watched) while it's selected.
                    var service = new GitService(watch: false);
                    if (!await service.OpenAsync(submodule.Path, cts.Token).ConfigureAwait(true) || cts.IsCancellationRequested)
                    {
                        service.Dispose();
                        break;
                    }

                    _submoduleService = service;
                    ShowHistory(service, new GitLogQuery(), node.Text, null, null, canShowAllBranches: false);
                    break;
                default:
                    Header = string.Empty;
                    History = null;
                    Message = Localizer.Get("msg.gitRepository.selectABranch", "Select a branch or tag to see its history.");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (GitException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            submoduleService?.Dispose();
        }
    }

    /// <summary>
    /// A local branch with an upstream: Incoming (its upstream's commits it doesn't have) with Fetch and Pull (Update when
    /// it isn't checked out), and Local History with its outgoing count, Push (with a remote) and Sync (checked out).
    /// </summary>
    private GitHistorySections? SectionsFor(GitRepositoryNode node, GitService git, string reference)
    {
        if (node.Branch is not { IsRemote: false, Upstream: not null, UpstreamGone: false, UpstreamRef: { } upstream } branch)
        {
            return null;
        }

        var fetch = new GitHistoryLink(Localizer.Get("msg.gitRepository.fetch", "Fetch"), "git.fetch");
        var pull = node.IsCurrent
            ? new GitHistoryLink(Localizer.Get("msg.gitRepository.pull", "Pull"), "git.pull")
            : new GitHistoryLink(Localizer.Get("msg.gitRepository.update", "Update"), "git.updateBranch");
        var local = new List<GitHistoryLink>();
        if (_data.TryGetValue(git, out var data) && data.Remotes.Count > 0)
        {
            local.Add(new GitHistoryLink(Localizer.Get("msg.gitRepository.push", "Push"), "git.push"));
        }

        if (node.IsCurrent)
        {
            local.Add(new GitHistoryLink(Localizer.Get("msg.gitRepository.sync", "Sync"), "git.sync"));
        }

        return new GitHistorySections($"{reference}..{upstream}", branch.Behind, branch.Ahead, [fetch, pull], local);
    }

    private void ShowHistory(GitService git, GitLogQuery query, string title, string? reference, GitHistorySections? sections, bool canShowAllBranches = true)
    {
        Message = null;
        var history = _actions.CreateHistory(git, query, title, canShowAllBranches);
        history.Sections = sections;
        history.RunLink = command => _actions.Run(command, git, reference);
        History = history;
        _ = history.LoadAsync();
    }

    /// <summary>A pull request's commits: its source branch's that its target doesn't have, once both are fetched.</summary>
    private void ShowPullRequest(GitService git, PullRequestDto pullRequest)
    {
        var branches = _data.TryGetValue(git, out var data) ? data.Branches : [];
        var remotes = data?.Remotes ?? [];
        GitBranchDetails? Remote(string name) =>
            branches.Where(b => b.IsRemote && b.ShortName == name).OrderByDescending(b => b.Remote == "origin").ThenBy(b => remotes.ToList().IndexOf(b.Remote ?? string.Empty)).FirstOrDefault();
        var source = pullRequest.SourceRepository is null ? Remote(pullRequest.SourceBranch) : null;
        if (source is null)
        {
            History = null;
            Message = Localizer.Format("msg.gitRepository.pullRequestNotFetched", "{0} isn't here: Fetch, or check the pull request out (its menu), to see its commits.", pullRequest.SourceBranch);
            return;
        }

        var target = branches.FirstOrDefault(b => b.IsRemote && b.Remote == source.Remote && b.ShortName == pullRequest.TargetBranch);
        ShowHistory(git, new GitLogQuery { Revision = target is null ? source.FullRef : $"{target.FullRef}..{source.FullRef}" }, pullRequest.Reference, null, null, canShowAllBranches: false);
    }

    /// <summary>A command on a node with its ref (F2, Delete): run in its repository, only where its menu has it.</summary>
    public Task RunOnNodeAsync(string command, GitRepositoryNode node) =>
        node.Repository is { } git ? _actions.Run(command, git, node.Reference) : Task.CompletedTask;

    /// <summary>A double-click or Enter on a node.</summary>
    public Task ActivateAsync(GitRepositoryNode node) => node.Action is { } action ? action() : _actions.Activate?.Invoke(node) ?? Task.CompletedTask;

    public Avalonia.Controls.ContextMenu? MenuFor(GitRepositoryNode node) => _actions.MenuFor?.Invoke(node);

    /// <summary>
    /// Selects a ref's node in a repository (all of them: null), its folders expanded, and brings its row into view; false
    /// when there's none. On the tree as it is: menus and the smoke test use <see cref="SelectAsync"/>.
    /// </summary>
    public bool Select(string reference, GitService? git = null)
    {
        if (Find(reference, git) is not { } node)
        {
            return false;
        }

        var expanded = false;
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!ancestor.IsExpanded)
            {
                _expanded[ancestor.Key] = true;
                ancestor.SetExpanded(true);
                expanded = true;
            }
        }

        if (expanded)
        {
            _buildAgain |= _building is { IsCompleted: false };
            ShowRows();
        }

        node.IsSelected = true;
        Selected = node;
        if (_selectedRow is { } row)
        {
            RevealRequested?.Invoke(row);
        }

        return true;
    }

    /// <summary>
    /// <see cref="Select"/> on a current tree (M57): the refs are read first when git changed them while the document was
    /// hidden, and a read or a build under way is waited for.
    /// </summary>
    public async Task<bool> SelectAsync(string reference, GitService? git = null)
    {
        if (_stale)
        {
            await LoadAsync(pullRequests: false).ConfigureAwait(true);
        }
        else if (_loading is { IsCompleted: false } loading)
        {
            await loading.ConfigureAwait(true);
        }

        if (_building is { IsCompleted: false } building)
        {
            await building.ConfigureAwait(true);
        }

        return Select(reference, git);
    }

    /// <summary>The node of a ref (<c>refs/heads/main</c>) in a repository (all of them: null), or null (the smoke test, tests).</summary>
    public GitRepositoryNode? Find(string reference, GitService? git = null)
    {
        if (git is not null)
        {
            return _byKey.GetValueOrDefault("r:" + git.Root + ":ref:" + reference);
        }

        foreach (var repository in _repositoryKeys)
        {
            if (_byKey.TryGetValue(repository + ":ref:" + reference, out var node))
            {
                return node;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _filterTimer?.Stop();
        _loadCts?.Cancel();
        _selectionCts?.Cancel();
        History = null;
        _submoduleService?.Dispose();
        _submoduleService = null;
    }
}

/// <summary>The Git Repository document tab.</summary>
public sealed class GitRepositoryDocument : ShellDocument
{
    public GitRepositoryDocument(GitRepositoryViewModel viewModel)
    {
        ViewModel = viewModel;
        Id = GitRepositoryViewModel.DocumentId;
        Title = viewModel.Title;
    }

    public override string Kind => "gitRepository";

    public GitRepositoryViewModel ViewModel { get; }
}
