using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>What a branch or tag at a commit is: a local branch, a remote branch or a tag.</summary>
public enum GitRefKind
{
    Local,
    Remote,
    Tag,
}

/// <summary>A branch or tag at a commit of the history, in its Branch / Tag column.</summary>
public sealed record GitRefChip(string Name, GitRefKind Kind, bool IsCurrent)
{
    public string Icon => Kind == GitRefKind.Tag ? "tag" : "branch";

    /// <summary>
    /// The chips of a commit's refs as <c>git log --decorate=full</c> writes them (<c>HEAD -&gt; refs/heads/main</c>,
    /// <c>refs/remotes/origin/main</c>, <c>tag: refs/tags/v1</c>), or shortened (the line history's); HEAD alone, a
    /// remote's HEAD and other refs (the stash) are left out.
    /// </summary>
    public static IReadOnlyList<GitRefChip> Parse(IEnumerable<string> refs)
    {
        var chips = new List<GitRefChip>();
        foreach (var raw in refs)
        {
            if (raw == "HEAD")
            {
                continue;
            }

            var current = raw.StartsWith("HEAD -> ", StringComparison.Ordinal);
            var name = current ? raw[8..] : raw;
            if (name.StartsWith("tag: ", StringComparison.Ordinal))
            {
                var tag = name[5..];
                chips.Add(new GitRefChip(tag.StartsWith("refs/tags/", StringComparison.Ordinal) ? tag[10..] : tag, GitRefKind.Tag, false));
            }
            else if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                chips.Add(new GitRefChip(name[11..], GitRefKind.Local, current));
            }
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                if (!name.EndsWith("/HEAD", StringComparison.Ordinal))
                {
                    chips.Add(new GitRefChip(name[13..], GitRefKind.Remote, false));
                }
            }
            else if (!name.StartsWith("refs/", StringComparison.Ordinal))
            {
                chips.Add(new GitRefChip(name, GitRefKind.Local, current));
            }
        }

        return chips;
    }
}

/// <summary>A row of the history: a commit, with its place in the graph and its branches and tags.</summary>
public sealed class GitCommitRow : ObservableObject
{
    /// <summary>
    /// The most chips the Branch / Tag column shows (M57): it's 130 px wide and a chip is at least 30, so more never show,
    /// and every name is in its tooltip. A commit with many refs at it made controls for each, each time a branch at it was
    /// selected in the Git Repository document: 0.5 to 1 s of UI thread for 320 refs, 4.9 s for 7,000.
    /// </summary>
    internal const int MaxShownRefs = 8;

    private IReadOnlyList<GitRefChip> _shownRefs;

    public GitCommitRow(GitCommit commit, bool isIncoming = false)
    {
        Commit = commit;
        IsIncoming = isIncoming;
        RefChips = GitRefChip.Parse(commit.Refs);
        _shownRefs = RefChips.Count > MaxShownRefs ? [.. RefChips.Take(MaxShownRefs)] : RefChips;
    }

    public GitCommit Commit { get; }

    /// <summary>A commit of the Incoming section (the upstream's, not yet on the branch).</summary>
    public bool IsIncoming { get; }

    public string ShortHash => Commit.ShortHash;

    public string Subject => Commit.Subject;

    public string Author => Commit.Author;

    public string Date => Commit.Date.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Every branch and tag at the commit.</summary>
    public IReadOnlyList<GitRefChip> RefChips { get; }

    /// <summary>Their names (<c>main</c>, <c>origin/main</c>, <c>v1.0</c>).</summary>
    public IReadOnlyList<string> Refs => [.. RefChips.Select(r => r.Name)];

    /// <summary>The ones the toolbar's toggles show (remote branches and tags can be hidden), at most <see cref="MaxShownRefs"/>.</summary>
    public IReadOnlyList<GitRefChip> ShownRefs
    {
        get => _shownRefs;
        private set
        {
            if (SetField(ref _shownRefs, value))
            {
                OnPropertyChanged(nameof(HasRefs));
            }
        }
    }

    public bool HasRefs => _shownRefs.Count > 0;

    public string RefsTip => string.Join(", ", Refs);

    public bool IsHead => Commit.Refs.Any(r => r == "HEAD" || r.StartsWith("HEAD -> ", StringComparison.Ordinal));

    public GraphRow? Graph => Commit.Graph;

    public string AccessibleName => $"{Subject}, {Author}, {Date}, {ShortHash}{(RefChips.Count > 0 ? ", " + RefsTip : string.Empty)}";

    internal void ShowRefs(bool remote, bool tags) =>
        ShownRefs = remote && tags && RefChips.Count <= MaxShownRefs ? RefChips
            : [.. RefChips.Where(r => r.Kind == GitRefKind.Local || remote && r.Kind == GitRefKind.Remote || tags && r.Kind == GitRefKind.Tag).Take(MaxShownRefs)];
}

/// <summary>A file changed by a commit.</summary>
public sealed record GitCommitFileRow(char Status, string Path, string? OldPath, string RelativePath)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string StatusText => Status switch
    {
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        _ => "modified",
    };

    public string AccessibleName => $"{Name}, {StatusText}";
}

/// <summary>A link of a section's header: Fetch, Pull, Update, Push, Sync.</summary>
public sealed record GitHistoryLink(string Text, string Command);

/// <summary>
/// A section header of the history, as the classic IDE's Git Repository window has them: <b>Incoming (n)</b> with Fetch
/// and Pull, <b>Local History (n Outgoing)</b> with Push and Sync. It collapses and expands its commits.
/// </summary>
public sealed class GitHistorySection : ObservableObject
{
    private bool _isExpanded;

    public GitHistorySection(string kind, string title, IReadOnlyList<GitHistoryLink> links, bool expanded)
    {
        Kind = kind;
        Title = title;
        Links = links;
        _isExpanded = expanded;
    }

    /// <summary><c>incoming</c> or <c>local</c>.</summary>
    public string Kind { get; }

    public string Title { get; }

    public IReadOnlyList<GitHistoryLink> Links { get; }

    public bool HasLinks => Links.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ChevronAngle));
            }
        }
    }

    /// <summary>The chevron points right (collapsed) or down (expanded).</summary>
    public double ChevronAngle => _isExpanded ? 90 : 0;

    public string AccessibleName => Title + ", " + (_isExpanded ? Localizer.Get("msg.gitHistory.expanded", "expanded") : Localizer.Get("msg.gitHistory.collapsed", "collapsed"));
}

/// <summary>
/// What the Git Repository document gives a local branch's history (with an upstream): the range of incoming commits
/// (<c>main..origin/main</c>), the counts, and the links of each section.
/// </summary>
public sealed record GitHistorySections(string IncomingRevision, int Incoming, int Outgoing, IReadOnlyList<GitHistoryLink> IncomingLinks, IReadOnlyList<GitHistoryLink> LocalLinks);

/// <summary>
/// A history (M13; the Git Repository document's right side, and the File History tab): the commit graph of a branch,
/// every branch, a file, a folder or a range of lines, loaded in pages as it scrolls, in the classic IDE's columns
/// (Branch / Tag, Graph, Message, Author, Date, ID), with one Filter History box (a message, an author or a commit id). A
/// local branch with an upstream has an Incoming section above its Local History. Double-click or Enter opens a commit's
/// details (a tab); commands on a commit go to the shell. Ctrl+click and Shift+click select several commits: Compare
/// Commits compares two of them, the older on the left; Compare with Previous compares one with its first parent.
/// </summary>
public sealed class GitHistoryViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 200;

    /// <summary>The incoming commits listed, at most.</summary>
    public const int IncomingLimit = 100;

    /// <summary>A filter's matches, at most.</summary>
    public const int FilterLimit = 500;

    private readonly GitService _git;
    private readonly Func<string, GitCommit, Task> _commitAction;
    private GitGraph _graph = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _filterCts;
    private Task? _loading;
    private bool _hasMore = true;
    private bool _isLoading;
    private GitCommitRow? _selected;
    private IReadOnlyList<GitCommitRow> _selectedRows = [];
    private string _filter = string.Empty;
    private bool _allBranches;
    private bool _showRemoteRefs = true;
    private bool _showTags = true;
    private GitHistorySections? _sections;
    private GitHistorySection? _incomingSection;
    private GitHistorySection? _localSection;
    private int _lanes = 1;

    public GitHistoryViewModel(GitService git, GitLogQuery query, string title, Func<string, GitCommit, Task> commitAction, (string Path, int Start, int End)? lines = null)
    {
        _git = git;
        Query = query;
        Title = title;
        Lines = lines;
        _commitAction = commitAction;
        _allBranches = query.AllBranches;
    }

    public string Title { get; }

    /// <summary>The base query (path, revision).</summary>
    public GitLogQuery Query { get; }

    /// <summary>Line history: the file and its lines (1-based) instead of a log query.</summary>
    public (string Path, int Start, int End)? Lines { get; }

    /// <summary>The history's commits (not the incoming ones), as loaded.</summary>
    public ObservableCollection<GitCommitRow> Rows { get; } = [];

    /// <summary>The Incoming section's commits.</summary>
    public ObservableCollection<GitCommitRow> IncomingRows { get; } = [];

    /// <summary>What the list shows: the section headers and the commits of the expanded sections.</summary>
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>The repository it reads (the commands on a commit run there).</summary>
    public GitService Repository => _git;

    /// <summary>Runs a section's link (Fetch, Pull…), set by its owner.</summary>
    public Func<string, Task>? RunLink { get; set; }

    /// <summary>Compares two commits, the older first (Compare Commits, Compare with Previous), set by its owner.</summary>
    public Func<GitCommit, GitCommit, Task>? CompareCommits { get; set; }

    /// <summary>Saves a toolbar toggle (<c>allBranches</c>, <c>remoteBranches</c>, <c>tags</c>), set by its owner.</summary>
    public Action<string, bool>? SaveToggle { get; set; }

    /// <summary>Show All Branches is offered (the Git Repository document; not a file's or lines' history).</summary>
    public bool CanShowAllBranches { get; set; }

    /// <summary>The Incoming and Local History sections of a local branch with an upstream; null: one list.</summary>
    public GitHistorySections? Sections
    {
        get => _sections;
        set
        {
            _sections = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Text a commit's message or author contains, or a commit id (a prefix of one).</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(EmptyText));
                _ = FilterSoonAsync();
            }
        }
    }

    /// <summary>The graph of every branch instead of the selected one's.</summary>
    public bool AllBranches
    {
        get => _allBranches;
        set
        {
            if (SetField(ref _allBranches, value))
            {
                SaveToggle?.Invoke("allBranches", value);
                _ = ReloadAsync();
            }
        }
    }

    /// <summary>Remote branches in the Branch / Tag column.</summary>
    public bool ShowRemoteRefs
    {
        get => _showRemoteRefs;
        set
        {
            if (SetField(ref _showRemoteRefs, value))
            {
                SaveToggle?.Invoke("remoteBranches", value);
                ApplyRefToggles();
            }
        }
    }

    /// <summary>Tags in the Branch / Tag column.</summary>
    public bool ShowTags
    {
        get => _showTags;
        set
        {
            if (SetField(ref _showTags, value))
            {
                SaveToggle?.Invoke("tags", value);
                ApplyRefToggles();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public bool IsEmpty => Rows.Count == 0 && IncomingRows.Count == 0 && !_isLoading;

    public string EmptyText => _filter.Trim().Length > 0
        ? Localizer.Format("msg.gitHistory.noCommitsMatch", "No commits match “{0}”.", _filter.Trim())
        : Localizer.Get("msg.gitHistory.noCommits", "No commits.");

    /// <summary>The widest graph so far, in lanes.</summary>
    public int Lanes
    {
        get => _lanes;
        private set
        {
            if (SetField(ref _lanes, value))
            {
                OnPropertyChanged(nameof(GraphWidth));
            }
        }
    }

    /// <summary>The Graph column's width, as <see cref="Controls.GitGraphCell"/> measures it.</summary>
    public double GraphWidth => (Math.Max(1, Math.Min(_lanes, 24)) * Controls.GitGraphCell.LaneWidth) + 4;

    /// <summary>The selected commit (a section's header can't be selected).</summary>
    public GitCommitRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(SelectedItem));
                if (value is null || !_selectedRows.Contains(value))
                {
                    SelectedRows = value is null ? [] : [value];
                }

                OnSelectionChanged();
            }
        }
    }

    /// <summary>Every selected commit (Ctrl+click, Shift+click), in the list's order; <see cref="Selected"/> is one of them.</summary>
    public IReadOnlyList<GitCommitRow> SelectedRows
    {
        get => _selectedRows;
        set
        {
            // Each row's place found once: a search of the loaded rows for each selected one was 200 million comparisons
            // for Select All with 20,000 commits loaded.
            var order = new Dictionary<GitCommitRow, int>();
            foreach (var row in IncomingRows.Concat(Rows))
            {
                order.TryAdd(row, order.Count);
            }

            IReadOnlyList<GitCommitRow> rows = [.. value.Distinct().OrderBy(r => order.GetValueOrDefault(r, -1))];
            if (_selectedRows.SequenceEqual(rows))
            {
                return;
            }

            _selectedRows = rows;
            OnPropertyChanged();

            // Not through SelectedItem: setting the list's selected item would drop the rest of its selection.
            if (rows.Count > 0 && (_selected is null || !rows.Contains(_selected)))
            {
                SetField(ref _selected, rows[0], nameof(Selected));
            }

            OnSelectionChanged();
        }
    }

    /// <summary>One commit is selected: the commands on a commit apply.</summary>
    public bool IsSingleSelection => _selected is not null && _selectedRows.Count <= 1;

    /// <summary>Compare Commits: two commits are selected.</summary>
    public bool CanCompareCommits => _selectedRows.Count == 2;

    /// <summary>Compare with Previous: one commit, with a parent, is selected.</summary>
    public bool CanCompareWithPrevious => IsSingleSelection && _selected!.Commit.Parents.Count > 0;

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(IsSingleSelection));
        OnPropertyChanged(nameof(CanCompareCommits));
        OnPropertyChanged(nameof(CanCompareWithPrevious));
    }

    /// <summary>The list's selection: a commit; choosing a header keeps the commit selected before it.</summary>
    public object? SelectedItem
    {
        get => _selected;
        set
        {
            if (value is GitCommitRow row)
            {
                Selected = row;
            }
            else
            {
                OnPropertyChanged();
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _filterCts?.Cancel();
    }

    /// <summary>The toolbar's toggles as saved, without reloading or saving them again.</summary>
    public void InitializeToggles(bool allBranches, bool remoteBranches, bool tags)
    {
        _allBranches = allBranches;
        _showRemoteRefs = remoteBranches;
        _showTags = tags;
    }

    /// <summary>Loads the first page (and the incoming commits).</summary>
    public Task LoadAsync() => ReloadAsync();

    private async Task FilterSoonAsync()
    {
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Starts again with the current filter and toggles.</summary>
    public async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        // A load still running (cancelled now) must finish first: it holds the loading flag.
        if (_loading is { } running)
        {
            await running.ConfigureAwait(true);
        }

        if (cts != _cts)
        {
            return; // reloaded again meanwhile
        }

        _graph = new GitGraph();
        _hasMore = true;
        var selectedHash = _selected?.Commit.Hash;
        Rows.Clear();
        IncomingRows.Clear();
        Lanes = 1;
        var sectioned = _sections is not null && !_allBranches && _filter.Trim().Length == 0 && Lines is null;
        _incomingSection = sectioned
            ? new GitHistorySection("incoming", Localizer.Format("msg.gitHistory.incoming", "Incoming ({0})", _sections!.Incoming), _sections.IncomingLinks, _incomingSection?.IsExpanded ?? true)
            : null;
        _localSection = sectioned
            ? new GitHistorySection("local", Localizer.Format("msg.gitHistory.localHistory", "Local History ({0} Outgoing)", _sections!.Outgoing), _sections.LocalLinks, _localSection?.IsExpanded ?? true)
            : null;
        RebuildItems();
        if (sectioned && _sections!.Incoming > 0)
        {
            await LoadIncomingAsync(_sections.IncomingRevision, cts.Token).ConfigureAwait(true);
        }

        await LoadMoreAsync().ConfigureAwait(true);
        if (selectedHash is not null && cts == _cts && Rows.Concat(IncomingRows).FirstOrDefault(r => r.Commit.Hash == selectedHash) is { } again)
        {
            Selected = again;
        }
    }

    private async Task LoadIncomingAsync(string revision, CancellationToken token)
    {
        try
        {
            var commits = await _git.LogAsync(new GitLogQuery { Revision = revision }, 0, IncomingLimit, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            new GitGraph().Add(commits);
            foreach (var commit in commits)
            {
                var row = new GitCommitRow(commit, isIncoming: true);
                row.ShowRefs(_showRemoteRefs, _showTags);
                IncomingRows.Add(row);
                Lanes = Math.Max(Lanes, commit.Graph?.LaneCount ?? 1);
            }

            RebuildItems();
        }
        catch (Exception ex) when (ex is GitException or OperationCanceledException or InvalidOperationException)
        {
        }
    }

    /// <summary>The next page (when the list is scrolled near its end).</summary>
    public Task LoadMoreAsync()
    {
        if (!_hasMore || _isLoading)
        {
            return _loading ?? Task.CompletedTask;
        }

        return _loading = LoadPageAsync((_cts ??= new CancellationTokenSource()).Token);
    }

    private async Task LoadPageAsync(CancellationToken token)
    {
        IsLoading = true;
        try
        {
            IReadOnlyList<GitCommit> page;
            var filter = _filter.Trim();
            var query = Query with { AllBranches = _allBranches };
            if (Lines is { } lines)
            {
                page = await _git.LineHistoryAsync(lines.Path, lines.Start, lines.End, token).ConfigureAwait(true);
                if (filter.Length > 0)
                {
                    page = [.. page.Where(c => Matches(c, filter))];
                }

                _hasMore = false;
            }
            else if (filter.Length > 0)
            {
                page = await _git.SearchLogAsync(query, filter, FilterLimit, token).ConfigureAwait(true);
                _hasMore = false;
            }
            else
            {
                page = await _git.LogAsync(query, Rows.Count, PageSize, token).ConfigureAwait(true);
                _hasMore = page.Count == PageSize;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (filter.Length > 0)
            {
                // The matches aren't one history: each is a dot of its own.
                foreach (var commit in page)
                {
                    commit.Graph = new GraphRow(0, 1, [], [], []);
                }
            }
            else
            {
                _graph.Add(page);
            }

            var shown = _localSection is not { IsExpanded: false };
            foreach (var commit in page)
            {
                var row = new GitCommitRow(commit);
                row.ShowRefs(_showRemoteRefs, _showTags);
                Rows.Add(row);
                if (shown)
                {
                    Items.Add(row);
                }

                Lanes = Math.Max(Lanes, commit.Graph?.LaneCount ?? 1);
            }

            if (Selected is null && Rows.Count > 0 && IncomingRows.Count == 0)
            {
                Selected = Rows[0];
            }
        }
        catch (Exception ex) when (ex is GitException or OperationCanceledException or InvalidOperationException)
        {
            _hasMore = false;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private static bool Matches(GitCommit commit, string filter) =>
        commit.Subject.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || commit.Author.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || commit.Hash.StartsWith(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>The list: each section's header and, when it's expanded, its commits.</summary>
    private void RebuildItems()
    {
        Items.Clear();
        if (_incomingSection is { } incoming)
        {
            Items.Add(incoming);
            if (incoming.IsExpanded)
            {
                foreach (var row in IncomingRows)
                {
                    Items.Add(row);
                }
            }
        }

        if (_localSection is { } local)
        {
            Items.Add(local);
            if (!local.IsExpanded)
            {
                return;
            }
        }

        foreach (var row in Rows)
        {
            Items.Add(row);
        }
    }

    /// <summary>Collapses or expands a section (its chevron).</summary>
    public void Toggle(GitHistorySection section)
    {
        section.IsExpanded = !section.IsExpanded;
        RebuildItems();
        OnPropertyChanged(nameof(SelectedItem));
    }

    /// <summary>The headers of the sections shown, if any (tests).</summary>
    public IReadOnlyList<GitHistorySection> SectionHeaders => [.. Items.OfType<GitHistorySection>()];

    private void ApplyRefToggles()
    {
        foreach (var row in Rows.Concat(IncomingRows))
        {
            row.ShowRefs(_showRemoteRefs, _showTags);
        }
    }

    /// <summary>
    /// A command on the selected commit: details, checkout, branch, cherryPick, revert, reset-soft|mixed|hard, tag,
    /// rebaseInteractive, copyHash.
    /// </summary>
    public Task RunAsync(string action) => _selected is { } row ? _commitAction(action, row.Commit) : Task.CompletedTask;

    /// <summary>Compare Commits: the two selected commits, the older (lower in the list) on the left.</summary>
    public Task CompareSelectedAsync() =>
        CanCompareCommits && CompareCommits is { } compare ? compare(_selectedRows[1].Commit, _selectedRows[0].Commit) : Task.CompletedTask;

    /// <summary>Compare with Previous: the selected commit against its first parent.</summary>
    public Task CompareWithPreviousAsync()
    {
        if (!CanCompareWithPrevious || CompareCommits is not { } compare)
        {
            return Task.CompletedTask;
        }

        var commit = _selected!.Commit;
        var parent = IncomingRows.Concat(Rows).FirstOrDefault(r => r.Commit.Hash == commit.Parents[0])?.Commit
            ?? new GitCommit(commit.Parents[0], [], string.Empty, string.Empty, default, [], string.Empty);
        return compare(parent, commit);
    }

    /// <summary>Double-click or Enter on a commit: its details, in a tab.</summary>
    public Task OpenSelectedAsync() => RunAsync("details");

    /// <summary>A section's link.</summary>
    public Task RunLinkAsync(GitHistoryLink link) => RunLink?.Invoke(link.Command) ?? Task.CompletedTask;
}

/// <summary>A history document tab (a file's, a folder's or lines').</summary>
public sealed class GitHistoryDocument : ShellDocument
{
    public GitHistoryDocument(GitHistoryViewModel viewModel, string id)
    {
        ViewModel = viewModel;
        Id = id;
        Title = viewModel.Title;
    }

    public override string Kind => "gitHistory";

    public GitHistoryViewModel ViewModel { get; }
}
