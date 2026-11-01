using System.Globalization;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>What a commit tab asks of the shell.</summary>
public sealed class GitCommitActions
{
    /// <summary>A command on the commit (revert, reset-soft|mixed|hard, cherryPick, checkout, branch, tag, copyHash), in its repository.</summary>
    public required Func<string, GitCommit, Task> Run { get; init; }

    /// <summary>The diff of one of its files against its first parent.</summary>
    public required Func<GitCommit, GitCommitFileRow, DiffViewModel> CreateDiff { get; init; }

    /// <summary>Asks AI Chat to explain the commit; null while AI is off.</summary>
    public Func<GitCommitDetails, Task>? Explain { get; init; }
}

/// <summary>
/// A commit's tab (the classic IDE's commit details, double-click in a history or View Commit Details): the selected
/// file's diff against the commit's first parent on the left; on the right the id with Revert, Reset and Explain (when AI
/// is on), the message, the author and the committer with their dates, and the changed files as a tree.
/// </summary>
public sealed class GitCommitViewModel : ObservableObject
{
    private readonly GitService _git;
    private readonly GitCommitActions _actions;
    private GitCommitDetails? _details;
    private GitChangeRow? _selectedRow;
    private DiffViewModel? _diff;
    private bool _isLoading = true;
    private string? _error;
    private IReadOnlyList<GitCommitFileRow> _files = [];

    public GitCommitViewModel(GitService git, string hash, GitCommitActions actions)
    {
        _git = git;
        Hash = hash;
        _actions = actions;
    }

    public string Hash { get; }

    public string ShortHash => Hash.Length > 8 ? Hash[..8] : Hash;

    public string Title => Localizer.Format("msg.gitCommit.title", "Commit {0}", ShortHash);

    /// <summary>The repository it's in (the tab's commands run there).</summary>
    public GitService Repository => _git;

    public GitCommitDetails? Details
    {
        get => _details;
        private set
        {
            if (SetField(ref _details, value))
            {
                foreach (var property in new[] { nameof(HasDetails), nameof(Message), nameof(Author), nameof(AuthorDate), nameof(Committer), nameof(CommitDate), nameof(ChangesHeader), nameof(IdText) })
                {
                    OnPropertyChanged(property);
                }
            }
        }
    }

    public bool HasDetails => _details is not null;

    public string IdText => Localizer.Format("msg.gitCommit.id", "ID: {0}", ShortHash);

    public string Message => _details?.Message ?? string.Empty;

    public string Author => _details is { } d ? Localizer.Format("msg.gitCommit.author", "{0} (Author)", d.Commit.Author) : string.Empty;

    public string AuthorDate => _details?.Commit.Date.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

    public string Committer => _details is { } d ? Localizer.Format("msg.gitCommit.committer", "{0} (Committer)", d.Committer) : string.Empty;

    public string CommitDate => _details?.CommitDate.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

    public string ChangesHeader => Localizer.Format("msg.gitCommit.changes", "Changes ({0})", _details?.Files.Count ?? 0);

    /// <summary>
    /// The changed files' tree, every folder expanded, as Git Changes shows its sections (M57): built off the UI thread and
    /// shown as a virtualized list of rows that stay with the tab, so a tab switch keeps the expansion and the selection.
    /// A tree view built on the UI thread took seconds for a commit of 10,000 files.
    /// </summary>
    public GitChangeTree Changes { get; } = new();

    /// <summary>The rows of <see cref="Changes"/>.</summary>
    public ResettableCollection<GitChangeRow> Rows => Changes.Rows;

    public bool CanExplain => _actions.Explain is not null;

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

    /// <summary>The Changes tree's selection: a file shows its diff on the left (a folder keeps the diff shown).</summary>
    public GitChangeRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetField(ref _selectedRow, value) && value?.Node.Row is { } file && _details is { } details)
            {
                Diff = _actions.CreateDiff(details.Commit, CommitFile(file));
            }
        }
    }

    /// <summary>Raised to bring a row into view (the list doesn't scroll to its selection by itself, M56).</summary>
    public event Action<GitChangeRow>? RevealRequested;

    /// <summary>The selected file's diff, or null (no file selected yet, or none changed).</summary>
    public DiffViewModel? Diff
    {
        get => _diff;
        private set
        {
            if (SetField(ref _diff, value))
            {
                OnPropertyChanged(nameof(HasDiff));
                _ = value?.LoadAsync();
            }
        }
    }

    public bool HasDiff => _diff is not null;

    /// <summary>The files of the commit in the tree's order (tests).</summary>
    public IReadOnlyList<GitCommitFileRow> Files => _files;

    /// <summary>Reads the commit, builds its tree off the UI thread, and selects its first file.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var details = await _git.CommitDetailsAsync(Hash).ConfigureAwait(true);
            if (details is null)
            {
                Error = Localizer.Format("msg.gitCommit.notFound", "{0} isn't a commit of this repository.", ShortHash);
                return;
            }

            var root = _git.Root ?? string.Empty;
            var collapsed = Changes.Collapsed;
            var expandedChanged = Changes.ExpandedChanged;
            var (roots, rows, files) = await Task.Run(() => Build(root, details, collapsed, expandedChanged)).ConfigureAwait(true);
            _files = files;
            Changes.Show(roots, rows);
            Details = details;
            if (Rows.FirstOrDefault(r => r.IsFile) is { } first)
            {
                SelectedRow = first;
            }
        }
        catch (GitException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// The commit's tree: Git Changes' (<see cref="GitChangesViewModel.BuildTree"/>: folders first, by name, a folder with
    /// one folder in it shown with it), without its repository node; its rows; and its files in the tree's order.
    /// </summary>
    private static (IReadOnlyList<GitChangeNode> Roots, IReadOnlyList<(GitChangeNode Node, int Depth)> Rows, IReadOnlyList<GitCommitFileRow> Files) Build(
        string root, GitCommitDetails details, IReadOnlySet<string> collapsed, Action<GitChangeNode, bool> expandedChanged)
    {
        var rows = details.Files.Select(f =>
        {
            var relative = Path.GetRelativePath(root, f.Path).Replace('\\', '/');
            return new GitFileRow(new GitFileStatus(f.Path, relative, f.Status, '.', f.OldPath), staged: true)
            {
                Committed = true,
                Location = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty,
            };
        });
        IReadOnlyList<GitChangeNode> roots = GitChangesViewModel.BuildTree(root, rows, "commit", staged: true, collapsed, expandedChanged) is [var repository] ? [.. repository.Children] : [];
        var files = new List<GitCommitFileRow>();
        Collect(roots);
        return (roots, GitChangeTree.Flatten(roots), files);

        void Collect(IEnumerable<GitChangeNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Row is { } file)
                {
                    files.Add(CommitFile(file));
                }

                Collect(node.Children);
            }
        }
    }

    private static GitCommitFileRow CommitFile(GitFileRow file) =>
        new(file.Status.Index, file.Status.Path, file.Status.OriginalPath, file.Status.RelativePath);

    /// <summary>Revert, Reset (soft, mixed, hard) and the ⋯ menu's commands.</summary>
    public Task RunAsync(string action) => _details is { } details ? _actions.Run(action, details.Commit) : Task.CompletedTask;

    /// <summary>Selects a file of the commit by its path relative to the repository, its folders expanded and its row brought into view (tests).</summary>
    public bool SelectFile(string relativePath)
    {
        if (Changes.Reveal(n => n.Row?.Status.RelativePath == relativePath) is not { } row)
        {
            return false;
        }

        SelectedRow = row;
        RevealRequested?.Invoke(row);
        return true;
    }

    public Task ExplainAsync() => _actions.Explain is { } explain && _details is { } details ? explain(details) : Task.CompletedTask;
}

/// <summary>A commit's tab, "Commit 98aa44a1".</summary>
public sealed class GitCommitDocument : ShellDocument
{
    public GitCommitDocument(GitCommitViewModel viewModel, EditorFont font, string id)
    {
        ViewModel = viewModel;
        Font = font;
        Id = id;
        Title = viewModel.Title;
    }

    public override string Kind => "gitCommit";

    public GitCommitViewModel ViewModel { get; }

    /// <summary>The diff's font.</summary>
    public EditorFont Font { get; }

    public static string IdFor(string hash, string? root) => "git-commit:" + hash + (root is null ? string.Empty : "@" + root);
}
