using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;

namespace Codalus.Shell.ViewModels;

/// <summary>A row of the history: a commit, with its place in the graph.</summary>
public sealed class GitCommitRow(GitCommit commit)
{
    public GitCommit Commit { get; } = commit;

    public string ShortHash => Commit.ShortHash;

    public string Subject => Commit.Subject;

    public string Author => Commit.Author;

    public string Date => Commit.Date.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Branches and tags at the commit (<c>HEAD -&gt; main</c> shows as <c>main</c>).</summary>
    public IReadOnlyList<string> Refs { get; } = [.. commit.Refs.Select(r => r.StartsWith("HEAD -> ", StringComparison.Ordinal) ? r[8..] : r.StartsWith("tag: ", StringComparison.Ordinal) ? r[5..] : r).Where(r => r != "HEAD")];

    public bool HasRefs => Refs.Count > 0;

    public bool IsHead => Commit.Refs.Any(r => r == "HEAD" || r.StartsWith("HEAD -> ", StringComparison.Ordinal));

    public GraphRow? Graph => Commit.Graph;

    public string AccessibleName => $"{Subject}, {Author}, {Date}, {ShortHash}{(HasRefs ? ", " + string.Join(", ", Refs) : string.Empty)}";
}

/// <summary>A file changed by the selected commit.</summary>
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

/// <summary>
/// The history window (M13): the commit graph of the repository, a branch, a file or a range of lines, with filters,
/// loaded in pages as it scrolls, and the selected commit's message and files. Commands on a commit go to the shell.
/// </summary>
public sealed class GitHistoryViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 200;

    private readonly GitService _git;
    private readonly Func<string, GitCommit, Task> _commitAction;
    private readonly Func<GitCommit, GitCommitFileRow, Task> _openFile;
    private GitGraph _graph = new();
    private CancellationTokenSource? _cts;
    private Task? _loading;
    private bool _hasMore = true;
    private bool _isLoading;
    private GitCommitRow? _selected;
    private GitCommitDetails? _details;
    private string _author = string.Empty;
    private string _message = string.Empty;
    private string _branch = AllBranches;

    public const string AllBranches = "All branches";

    public GitHistoryViewModel(GitService git, GitLogQuery query, string title, Func<string, GitCommit, Task> commitAction, Func<GitCommit, GitCommitFileRow, Task> openFile, (string Path, int Start, int End)? lines = null)
    {
        _git = git;
        Query = query;
        Title = title;
        Lines = lines;
        _commitAction = commitAction;
        _openFile = openFile;
        _branch = query.AllBranches ? AllBranches : query.Revision ?? "HEAD";
    }

    public string Title { get; }

    /// <summary>The base query (path, revision); the filters below narrow it.</summary>
    public GitLogQuery Query { get; }

    /// <summary>Line history: the file and its lines (1-based) instead of a log query.</summary>
    public (string Path, int Start, int End)? Lines { get; }

    public bool CanFilter => Lines is null;

    /// <summary>The branch box among the filters; the Git Repository document's tree picks the branch instead.</summary>
    public bool ShowBranchFilter { get; set; } = true;

    public ObservableCollection<GitCommitRow> Rows { get; } = [];

    public ObservableCollection<string> Branches { get; } = [AllBranches, "HEAD"];

    public ObservableCollection<GitCommitFileRow> Files { get; } = [];

    public string Branch
    {
        get => _branch;
        set
        {
            if (SetField(ref _branch, value ?? AllBranches))
            {
                _ = ReloadAsync();
            }
        }
    }

    public string Author
    {
        get => _author;
        set => SetField(ref _author, value ?? string.Empty);
    }

    public string MessageFilter
    {
        get => _message;
        set => SetField(ref _message, value ?? string.Empty);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public bool IsEmpty => Rows.Count == 0 && !_isLoading;

    /// <summary>The widest graph so far, in lanes (the graph column's width).</summary>
    public int Lanes { get; private set; } = 1;

    public GitCommitRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                _ = LoadDetailsAsync(value);
            }
        }
    }

    public GitCommitDetails? Details
    {
        get => _details;
        private set
        {
            if (SetField(ref _details, value))
            {
                OnPropertyChanged(nameof(DetailsText));
                OnPropertyChanged(nameof(HasDetails));
            }
        }
    }

    public bool HasDetails => _details is not null;

    public string DetailsText => _details is not { } details
        ? string.Empty
        : $"{details.Commit.Hash}\n{details.Commit.Author} <{details.Commit.AuthorEmail}>, {details.Commit.Date.LocalDateTime.ToString("F", CultureInfo.CurrentCulture)}\n\n{details.Message}";

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    /// <summary>Loads the first page and fills the branch list.</summary>
    public async Task LoadAsync()
    {
        // The first page starts before git lists the branches: the view asks for more rows as soon as it is laid out,
        // and a reload once the branches arrived (git is slow to start on Windows) cleared the page that request had
        // loaded, with the commit selected in it meanwhile.
        var firstPage = ReloadAsync();
        try
        {
            foreach (var branch in (await _git.BranchesAsync().ConfigureAwait(true)).Select(b => b.Name))
            {
                if (!Branches.Contains(branch))
                {
                    Branches.Add(branch);
                }
            }
        }
        catch (GitException)
        {
        }

        await firstPage.ConfigureAwait(true);
    }

    /// <summary>Starts again with the current filters.</summary>
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
        Rows.Clear();
        Files.Clear();
        Details = null;
        Lanes = 1;
        await LoadMoreAsync().ConfigureAwait(true);
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
            if (Lines is { } lines)
            {
                page = await _git.LineHistoryAsync(lines.Path, lines.Start, lines.End, token).ConfigureAwait(true);
                _hasMore = false;
            }
            else
            {
                var query = Query with
                {
                    AllBranches = _branch == AllBranches,
                    Revision = _branch is AllBranches or "HEAD" ? Query.Revision : _branch,
                    Author = _author.Length > 0 ? _author : Query.Author,
                    Message = _message.Length > 0 ? _message : Query.Message,
                };
                page = await _git.LogAsync(query, Rows.Count, PageSize, token).ConfigureAwait(true);
                _hasMore = page.Count == PageSize;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            _graph.Add(page);
            foreach (var commit in page)
            {
                Rows.Add(new GitCommitRow(commit));
                Lanes = Math.Max(Lanes, commit.Graph?.LaneCount ?? 1);
            }

            OnPropertyChanged(nameof(Lanes));
            if (Selected is null && Rows.Count > 0)
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

    private async Task LoadDetailsAsync(GitCommitRow? row)
    {
        Files.Clear();
        if (row is null)
        {
            Details = null;
            return;
        }

        GitCommitDetails? details;
        try
        {
            details = await _git.CommitDetailsAsync(row.Commit.Hash).ConfigureAwait(true);
        }
        catch (GitException)
        {
            details = null;
        }

        if (row != _selected)
        {
            return;
        }

        Details = details;
        var root = _git.Root ?? string.Empty;
        foreach (var (status, path, oldPath) in details?.Files ?? [])
        {
            Files.Add(new GitCommitFileRow(status, path, oldPath, System.IO.Path.GetRelativePath(root, path).Replace('\\', '/')));
        }
    }

    /// <summary>A command on the selected commit: checkout, branch, cherryPick, revert, reset-soft|mixed|hard, tag, rebaseInteractive, copyHash.</summary>
    public Task RunAsync(string action) => _selected is { } row ? _commitAction(action, row.Commit) : Task.CompletedTask;

    public Task OpenFileAsync(GitCommitFileRow file) => _selected is { } row ? _openFile(row.Commit, file) : Task.CompletedTask;
}

/// <summary>A history document tab.</summary>
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
