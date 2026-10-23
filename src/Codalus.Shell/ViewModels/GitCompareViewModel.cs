using System.Collections.ObjectModel;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// Compare with Current Branch (#23, D38: C1-C3, question 9): the files the other branch's tip differs in from the
/// current branch's, tip to tip, as a tree or a list with name-status letters (a file new on the other branch is A, a
/// rename shows its old name). Named in diff order, "main → feature/sign-in", in the tab, the header and the status line;
/// a file's diff shows the current branch on the left. While it compares, an indeterminate bar and a status line; with
/// no differences, a sentence and View History.
/// </summary>
public sealed class GitCompareViewModel : ObservableObject
{
    private readonly GitService _git;
    private bool _isLoading;
    private bool _isTreeView = true;
    private IReadOnlyList<GitFileRow> _rows = [];

    public GitCompareViewModel(GitService git, string from, string fromName, string to, string toName)
    {
        _git = git;
        From = from;
        FromName = fromName;
        To = to;
        ToName = toName;
    }

    public string From { get; }

    public string FromName { get; }

    public string To { get; }

    public string ToName { get; }

    /// <summary>"main → feature/sign-in": the tab, the header and the accessible name.</summary>
    public string Title => Localizer.Format("msg.gitCompare.title", "{0} → {1}", FromName, ToName);

    public ObservableCollection<GitChangeNode> Tree { get; } = [];

    public ObservableCollection<GitFileRow> Items { get; } = [];

    /// <summary>Opens a file's diff (set by the shell).</summary>
    public Func<GitChangedFile, Task>? OpenFile { get; set; }

    /// <summary>View History (set by the shell): the next step when the files are the same (C3).</summary>
    public Func<Task>? ShowHistory { get; set; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                Notify();
            }
        }
    }

    public bool IsTreeView
    {
        get => _isTreeView;
        set
        {
            if (SetField(ref _isTreeView, value))
            {
                OnPropertyChanged(nameof(IsListView));
            }
        }
    }

    public bool IsListView => !_isTreeView;

    /// <summary>The Tree and List switch: 0 tree, 1 list.</summary>
    public int ViewIndex
    {
        get => _isTreeView ? 0 : 1;
        set
        {
            IsTreeView = value == 0;
            OnPropertyChanged();
        }
    }

    /// <summary>"Comparing…", "5 files", "1 file", "No differences".</summary>
    public string CountText => _isLoading ? Localizer.Get("msg.gitCompare.comparing", "Comparing…")
        : _rows.Count == 0 ? Localizer.Get("msg.gitCompare.noDifferences", "No differences")
        : _rows.Count == 1 ? Localizer.Get("msg.gitCompare.oneFile", "1 file")
        : Localizer.Format("msg.gitCompare.files", "{0} files", _rows.Count);

    /// <summary>The status line while it compares (C2).</summary>
    public string StatusText => Localizer.Format("msg.gitCompare.comparingWith", "Comparing {0} with {1}…", FromName, ToName);

    public bool IsEmpty => !_isLoading && _rows.Count == 0;

    public bool HasFiles => !_isLoading && _rows.Count > 0;

    public string EmptyText => Localizer.Format("msg.gitCompare.sameFiles", "No differences: {0} and {1} have the same files. Their commits can still differ; View History lists them.", FromName, ToName);

    public string Hint => Localizer.Format("msg.gitCompare.hint", "Letters say what {1} changes from {0}, tip to tip. Enter or double-click opens a file's diff: {0} on the left, {1} on the right.", FromName, ToName);

    /// <summary>Reads the files again (on open and Refresh), off the UI thread.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var files = await _git.CompareBranchesAsync(From, To).ConfigureAwait(true);
            var root = _git.Root ?? string.Empty;
            _rows = [.. files.Select(f => new GitFileRow(
                new GitFileStatus(f.Path, System.IO.Path.GetRelativePath(root, f.Path).Replace('\\', '/'), f.Status, '.', f.OldPath),
                staged: true)
            {
                Location = System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(root, f.Path)) ?? string.Empty,
            })];
        }
        finally
        {
            Tree.Clear();
            foreach (var node in GitChangesViewModel.BuildTree(_git.Root ?? string.Empty, _rows, "compare", staged: true, new HashSet<string>()))
            {
                Tree.Add(node);
            }

            Items.Clear();
            foreach (var row in _rows.OrderBy(r => r.Status.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                Items.Add(row);
            }

            IsLoading = false;
            Notify();
        }
    }

    /// <summary>A file's diff: the current branch's version (the old path of a rename) on the left, the other's on the right.</summary>
    public Task OpenAsync(GitFileRow row) =>
        OpenFile is { } open ? open(new GitChangedFile(row.Status.Index, row.Status.Path, row.Status.OriginalPath)) : Task.CompletedTask;

    private void Notify()
    {
        foreach (var name in new[] { nameof(CountText), nameof(IsEmpty), nameof(HasFiles), nameof(EmptyText) })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>The Compare document (C1): "main → feature/sign-in" in the tab.</summary>
public sealed class GitCompareDocument : ShellDocument
{
    public GitCompareDocument(GitCompareViewModel viewModel, string id)
    {
        ViewModel = viewModel;
        Id = id;
        Title = viewModel.Title;
    }

    public override string Kind => "gitCompare";

    public override string? TabIcon => "compare";

    public GitCompareViewModel ViewModel { get; }

    public static string IdFor(string from, string to) => $"git-compare:{from}→{to}";
}
