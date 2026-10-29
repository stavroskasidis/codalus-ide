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

/// <summary>A repository's pull requests for the tree, or why there are none to show.</summary>
public sealed record GitRepositoryPullRequests(IReadOnlyList<PullRequestDto> PullRequests, string? Message, HostingMatch? Match = null, Func<Task>? SignIn = null);

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
public sealed class GitRepositoryViewModel : ObservableObject, IDisposable
{
    public const string DocumentId = "git-repository";

    private readonly GitRepositoryActions _actions;
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<GitService, RepositoryData> _data = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _selectionCts;
    private GitRepositoryNode? _selected;
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

    /// <summary>What was read of a repository: its branches, tags, remotes, submodules and pull requests.</summary>
    private sealed record RepositoryData(
        IReadOnlyList<GitBranchDetails> Branches,
        IReadOnlyList<GitTag> Tags,
        IReadOnlyList<string> Remotes,
        IReadOnlyList<GitSubmodule> Submodules,
        GitRepositoryPullRequests? PullRequests);

    public GitRepositoryViewModel(GitRepositoryActions actions)
    {
        _actions = actions;
    }

    public string Title => Localizer.Get("msg.gitRepository.title", "Git Repository");

    public ObservableCollection<GitRepositoryNode> Roots { get; } = [];

    /// <summary>Text in a branch's, tag's, pull request's or submodule's name (case-insensitive).</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Rebuild();
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
            // The tree's selection goes while Rebuild replaces the nodes (Rebuild selects the same node again), and as the
            // view attaches; a node stays selected while it's in the tree.
            if (_rebuilding || value is null && _selected is not null && Roots.SelectMany(r => r.Descendants().Prepend(r)).Contains(_selected))
            {
                return;
            }

            if (SetField(ref _selected, value))
            {
                _ = ShowAsync(value);
            }
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

    /// <summary>Reads every repository's branches, tags, remotes, submodules and pull requests, then the tree.</summary>
    public Task LoadAsync() => LoadAsync(pullRequests: true);

    /// <summary>
    /// Reads the repositories again: after git changed them (the status), only the refs; with
    /// <paramref name="pullRequests"/>, the pull requests from their hosts too (on open and Refresh).
    /// </summary>
    public async Task LoadAsync(bool pullRequests)
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
            foreach (var git in repositories)
            {
                var branches = await git.BranchDetailsAsync(token).ConfigureAwait(true);
                var tags = await git.TagsAsync(token).ConfigureAwait(true);
                var remotes = (await git.RemotesAsync(token).ConfigureAwait(true)).Select(r => r.Name).ToList();
                var submodules = File.Exists(Path.Combine(git.Root ?? string.Empty, ".gitmodules")) ? await git.SubmodulesAsync(token).ConfigureAwait(true) : [];
                var prs = pullRequests ? null : _data.GetValueOrDefault(git)?.PullRequests;
                data[git] = new RepositoryData(branches, tags, remotes, submodules, prs);
            }

            if (version != _version)
            {
                return;
            }

            _data.Clear();
            foreach (var (git, each) in data)
            {
                _data[git] = each;
            }

            Rebuild();
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

                Rebuild();
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

    /// <summary>Builds the tree from what was read, with the filter; keeps the expanded nodes and the selection.</summary>
    private void Rebuild()
    {
        var query = _filter.Trim();
        var repositories = _actions.Repositories().Where(_data.ContainsKey).ToList();
        var roots = new List<GitRepositoryNode>();
        if (repositories.Count == 1)
        {
            // As in the classic IDE: Branches / Tags with the repository under it, then Pull Requests and Submodules.
            var git = repositories[0];
            var refs = Group("branches", Localizer.Get("msg.gitRepository.branchesTags", "Branches / Tags"), null);
            if (RepositoryNode(git, query) is { } repository)
            {
                refs.Children.Add(repository);
            }

            roots.Add(refs);
            roots.Add(PullRequestsNode(git, "pullRequests", query));
            roots.Add(SubmodulesNode(git, "submodules", query));
        }
        else
        {
            // Several repositories: each one a root with its own Branches / Tags, Pull Requests and Submodules.
            foreach (var git in repositories)
            {
                var root = new GitRepositoryNode("r:" + git.Root, GitRepositoryNodeKind.Repository, RepositoryText(git), git);
                var refs = Group("r:" + git.Root + ":branches", Localizer.Get("msg.gitRepository.branchesTags", "Branches / Tags"), git);
                AddRefs(refs, git, query);
                root.Children.Add(refs);
                root.Children.Add(PullRequestsNode(git, "r:" + git.Root + ":pullRequests", query));
                root.Children.Add(SubmodulesNode(git, "r:" + git.Root + ":submodules", query));
                roots.Add(root);
            }
        }

        // Expansion: what the user chose, else the default (filtering shows everything that matches).
        foreach (var node in roots.SelectMany(r => r.Descendants().Prepend(r)))
        {
            node.IsExpanded = node.Children.Count > 0 && (query.Length > 0 || (_expanded.TryGetValue(node.Key, out var expanded) ? expanded : ExpandedByDefault(node)));
            node.ExpandedChanged += (n, value) => _expanded[n.Key] = value;
        }

        var selectedKey = _selected?.Key;
        _rebuilding = true;
        try
        {
            Roots.Clear();
            foreach (var root in roots)
            {
                Roots.Add(root);
            }
        }
        finally
        {
            _rebuilding = false;
        }

        var selected = selectedKey is null ? null : roots.SelectMany(r => r.Descendants().Prepend(r)).FirstOrDefault(n => n.Key == selectedKey);
        selected ??= _selected is null ? roots.SelectMany(r => r.Descendants()).FirstOrDefault(n => n is { Kind: GitRepositoryNodeKind.Branch, IsCurrent: true }) : null;
        if (selected is not null)
        {
            selected.IsSelected = true;
        }

        // The same branch at the same commit keeps its history (and its scroll); a moved branch reloads it.
        _selected = selected;
        OnPropertyChanged(nameof(Selected));
        if (HistoryKey(selected) != _historyFor)
        {
            _ = ShowAsync(selected);
        }
    }

    private static bool ExpandedByDefault(GitRepositoryNode node) =>
        node.Kind is GitRepositoryNodeKind.Repository || node.Kind == GitRepositoryNodeKind.Group && !node.Key.EndsWith(":remote", StringComparison.Ordinal) && !node.Key.EndsWith(":tags", StringComparison.Ordinal)
        || node.Kind == GitRepositoryNodeKind.Folder && !node.Key.Contains(":remote:", StringComparison.Ordinal);

    private static GitRepositoryNode Group(string key, string text, GitService? git) => new(key, GitRepositoryNodeKind.Group, text, git);

    private static string RepositoryText(GitService git) =>
        $"{git.Name} ({git.Status?.Branch ?? Localizer.Get("msg.gitBranches.detached", "detached")})";

    private GitRepositoryNode? RepositoryNode(GitService git, string query)
    {
        var node = new GitRepositoryNode("r:" + git.Root, GitRepositoryNodeKind.Repository, RepositoryText(git), git);
        AddRefs(node, git, query);
        return node;
    }

    /// <summary>A repository's local branches (in folders), then each remote's branches and the tags, in folders of their own.</summary>
    private void AddRefs(GitRepositoryNode parent, GitService git, string query)
    {
        var data = _data[git];
        var status = git.Status;
        var prefix = "r:" + git.Root;
        bool Matches(string name) => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);

        var locals = data.Branches.Where(b => !b.IsRemote && Matches(b.Name)).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var branch in locals)
        {
            Place(parent, prefix + ":local", branch.Name, git, leaf => LocalBranch(leaf, branch, git, prefix, status, data.Branches, data.Remotes.Count > 0));
        }

        foreach (var remote in data.Remotes.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            var branches = data.Branches.Where(b => b.IsRemote && b.Remote == remote && b.ShortName != "HEAD" && Matches(b.Name)).OrderBy(b => b.ShortName, StringComparer.OrdinalIgnoreCase).ToList();
            if (branches.Count == 0)
            {
                continue;
            }

            var folder = Group(prefix + ":remote:" + remote + ":remote", "remotes/" + remote, git);
            foreach (var branch in branches)
            {
                Place(folder, prefix + ":remote:" + remote, branch.ShortName, git, leaf => new GitRepositoryNode(prefix + ":ref:" + branch.FullRef, GitRepositoryNodeKind.Branch, leaf, git) { Branch = branch, Siblings = data.Branches, HasRemote = true });
            }

            parent.Children.Add(folder);
        }

        var tags = data.Tags.Where(t => Matches(t.Name)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (tags.Count > 0)
        {
            var folder = Group(prefix + ":tags", Localizer.Get("msg.gitRepository.tags", "tags"), git);
            foreach (var tag in tags)
            {
                folder.Children.Add(new GitRepositoryNode(prefix + ":ref:refs/tags/" + tag.Name, GitRepositoryNodeKind.Tag, tag.Name, git)
                {
                    Tag = tag,
                    HasRemote = data.Remotes.Count > 0,
                    Detail = tag.Message ?? string.Empty,
                });
            }

            parent.Children.Add(folder);
        }
    }

    private static GitRepositoryNode LocalBranch(string leaf, GitBranchDetails branch, GitService git, string prefix, GitStatus? status, IReadOnlyList<GitBranchDetails> siblings, bool hasRemote)
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
            IsCurrent = branch.IsCurrent && status?.Branch is not null,
            Track = track,
            TrackName = track.Length > 0 ? $"{string.Join(", ", parts)} ({branch.Upstream})" : string.Empty,
            Detail = branch.WorktreePath is { } path ? Localizer.Format("msg.gitBranches.checkedOutIn", "Checked out in {0}", path) : string.Empty,
        };
    }

    /// <summary>Puts a branch under the folders its name's <c>/</c> parts make (<c>feature/x</c>: folder feature, branch x).</summary>
    private static void Place(GitRepositoryNode parent, string keyPrefix, string name, GitService git, Func<string, GitRepositoryNode> leaf)
    {
        var parts = name.Split('/');
        var node = parent;
        var path = string.Empty;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            path += parts[i] + "/";
            var key = keyPrefix + ":folder:" + path;
            var folder = node.Children.FirstOrDefault(c => c.Key == key);
            if (folder is null)
            {
                folder = new GitRepositoryNode(key, GitRepositoryNodeKind.Folder, parts[i], git);

                // Folders before branches, as the classic IDE lists them.
                var at = node.Children.TakeWhile(c => c.Kind == GitRepositoryNodeKind.Folder).Count();
                node.Children.Insert(at, folder);
            }

            node = folder;
        }

        node.Children.Add(leaf(parts[^1]));
    }

    private GitRepositoryNode PullRequestsNode(GitService git, string key, string query)
    {
        var node = Group(key, Localizer.Get("msg.gitRepository.pullRequests", "Pull Requests"), git);
        switch (_data[git].PullRequests)
        {
            case null:
                node.Children.Add(MessageNode(key, Localizer.Get("msg.gitRepository.loadingPullRequests", "Loading…"), git));
                break;
            case { Message: { } message } result:
                node.Children.Add(MessageNode(key, message, git, result.SignIn));
                break;
            case { PullRequests: var pullRequests, Match: var match }:
                var shown = pullRequests.Where(p => query.Length == 0 || p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Reference.Contains(query, StringComparison.OrdinalIgnoreCase) || p.SourceBranch.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
                if (pullRequests.Count == 0)
                {
                    node.Children.Add(MessageNode(key, Localizer.Get("msg.gitRepository.noPullRequests", "No open pull requests"), git));
                }

                foreach (var pullRequest in shown)
                {
                    node.Children.Add(new GitRepositoryNode(key + ":" + pullRequest.Number, GitRepositoryNodeKind.PullRequest, $"{pullRequest.Reference} {pullRequest.Title}", git)
                    {
                        PullRequest = pullRequest,
                        Match = match,
                        Detail = $"{pullRequest.SourceBranch} → {pullRequest.TargetBranch} · {pullRequest.Author}{(pullRequest.IsDraft ? " · " + Localizer.Get("msg.gitRepository.draft", "draft") : string.Empty)}",
                    });
                }

                break;
        }

        return node;
    }

    private GitRepositoryNode SubmodulesNode(GitService git, string key, string query)
    {
        var node = Group(key, Localizer.Get("msg.gitRepository.submodules", "Submodules"), git);
        var root = git.Root ?? string.Empty;
        foreach (var submodule in _data[git].Submodules)
        {
            var relative = Path.GetRelativePath(root, submodule.Path).Replace('\\', '/');
            if (query.Length > 0 && !relative.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            node.Children.Add(new GitRepositoryNode(key + ":" + relative, GitRepositoryNodeKind.Submodule, relative, git)
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

    /// <summary>Selects a ref's node in a repository (all of them: null), its folders expanded; false when there's none.</summary>
    public bool Select(string reference, GitService? git = null)
    {
        if (Find(reference, git) is not { } node)
        {
            return false;
        }

        foreach (var ancestor in PathTo(node, Roots) ?? [])
        {
            ancestor.IsExpanded = true;
        }

        node.IsSelected = true;
        Selected = node;
        return true;
    }

    private static List<GitRepositoryNode>? PathTo(GitRepositoryNode target, IEnumerable<GitRepositoryNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Contains(target))
            {
                return [node];
            }

            if (PathTo(target, node.Children) is { } path)
            {
                path.Insert(0, node);
                return path;
            }
        }

        return null;
    }

    /// <summary>Selects the node of a ref (<c>refs/heads/main</c>) in a repository (the smoke test, tests).</summary>
    public GitRepositoryNode? Find(string reference, GitService? git = null) =>
        Roots.SelectMany(r => r.Descendants().Prepend(r)).FirstOrDefault(n => n.Reference == reference && (git is null || n.Repository == git));

    public void Dispose()
    {
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
