using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>A folder or a file of a commit's Changes tree (a folder holding a single folder is shown with it, as <c>src\Data</c>).</summary>
public sealed class GitCommitTreeNode : ObservableObject
{
    private bool _isExpanded = true;
    private bool _isSelected;

    public GitCommitTreeNode(string name, GitCommitFileRow? file)
    {
        Name = name;
        File = file;
    }

    public string Name { get; private set; }

    /// <summary>The file; null for a folder.</summary>
    public GitCommitFileRow? File { get; }

    public bool IsFolder => File is null;

    public ObservableCollection<GitCommitTreeNode> Children { get; } = [];

    public string Icon => File is null ? (_isExpanded ? "folder-open" : "folder") : "file";

    /// <summary>A file's status letter (A, M, D, R).</summary>
    public string Status => File is { } file ? file.Status.ToString() : string.Empty;

    public string Tip => File?.RelativePath ?? Name;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Icon));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string AccessibleName => File is { } file ? file.AccessibleName : Name;

    /// <summary>The files' tree: folders first, by name; single-child folders joined with their child (<c>src\Data</c>).</summary>
    public static IReadOnlyList<GitCommitTreeNode> Build(IEnumerable<GitCommitFileRow> files)
    {
        var root = new GitCommitTreeNode(string.Empty, null);
        foreach (var file in files)
        {
            var parts = file.RelativePath.Split('/');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var folder = node.Children.FirstOrDefault(c => c.IsFolder && c.Name == parts[i]);
                if (folder is null)
                {
                    folder = new GitCommitTreeNode(parts[i], null);
                    node.Children.Add(folder);
                }

                node = folder;
            }

            node.Children.Add(new GitCommitTreeNode(parts[^1], file));
        }

        Sort(root);
        foreach (var child in root.Children)
        {
            Compact(child);
        }

        return [.. root.Children];
    }

    private static void Sort(GitCommitTreeNode node)
    {
        var sorted = node.Children.OrderByDescending(c => c.IsFolder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        node.Children.Clear();
        foreach (var child in sorted)
        {
            Sort(child);
            node.Children.Add(child);
        }
    }

    private static void Compact(GitCommitTreeNode node)
    {
        while (node.IsFolder && node.Children is [{ IsFolder: true } only])
        {
            node.Name = node.Name + System.IO.Path.DirectorySeparatorChar + only.Name;
            node.Children.Clear();
            foreach (var child in only.Children)
            {
                node.Children.Add(child);
            }
        }

        foreach (var child in node.Children)
        {
            Compact(child);
        }
    }

    public IEnumerable<GitCommitTreeNode> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
}

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
    private GitCommitTreeNode? _selectedNode;
    private DiffViewModel? _diff;
    private bool _isLoading = true;
    private string? _error;

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
    /// The changed files' tree, every folder expanded. TODO(issue): built on the UI thread into a tree view that isn't
    /// virtualized, an item per file: seconds for a commit of 10,000 files (M56; Git Changes' flattened rows are the way).
    /// </summary>
    public ObservableCollection<GitCommitTreeNode> Tree { get; } = [];

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

    /// <summary>The Changes tree's selection: a file shows its diff on the left.</summary>
    public GitCommitTreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetField(ref _selectedNode, value) && value is { File: { } file } && _details is { } details)
            {
                Diff = _actions.CreateDiff(details.Commit, file);
            }
        }
    }

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

    /// <summary>The files of the commit, flat (tests).</summary>
    public IReadOnlyList<GitCommitFileRow> Files => [.. Tree.SelectMany(n => n.Descendants().Prepend(n)).Select(n => n.File).OfType<GitCommitFileRow>()];

    /// <summary>Reads the commit, and selects its first file.</summary>
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
            Tree.Clear();
            foreach (var node in GitCommitTreeNode.Build(details.Files.Select(f => new GitCommitFileRow(f.Status, f.Path, f.OldPath, Path.GetRelativePath(root, f.Path).Replace('\\', '/')))))
            {
                Tree.Add(node);
            }

            Details = details;
            if (Tree.SelectMany(n => n.Descendants().Prepend(n)).FirstOrDefault(n => n.File is not null) is { } first)
            {
                first.IsSelected = true;
                SelectedNode = first;
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

    /// <summary>Revert, Reset (soft, mixed, hard) and the ⋯ menu's commands.</summary>
    public Task RunAsync(string action) => _details is { } details ? _actions.Run(action, details.Commit) : Task.CompletedTask;

    /// <summary>Selects a file of the commit by its path relative to the repository (tests).</summary>
    public bool SelectFile(string relativePath)
    {
        if (Tree.SelectMany(n => n.Descendants().Prepend(n)).FirstOrDefault(n => n.File?.RelativePath == relativePath) is not { } node)
        {
            return false;
        }

        node.IsSelected = true;
        SelectedNode = node;
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
