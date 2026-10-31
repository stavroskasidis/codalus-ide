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
/// no differences, a sentence and View History. The history's Compare Commits and Compare with Previous use it for two
/// commits, "1a2b3c4d → 5e6f7a8b", the older on the left.
/// </summary>
public sealed class GitCompareViewModel : ObservableObject
{
    private readonly GitService _git;
    private bool _isLoading;
    private bool _isTreeView = true;
    private IReadOnlyList<GitFileRow> _rows = [];
    private bool _treeStale = true;
    private bool _listStale = true;
    private int _version;
    private bool _showing;
    private GitChangeRow? _selectedRow;
    private GitFileRow? _selectedItem;
    private string? _selectedKey;
    private string? _selectedPath;

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

    /// <summary>Two commits (the history's Compare Commits), not two branches' tips.</summary>
    public bool ComparesCommits { get; init; }

    /// <summary>"main → feature/sign-in": the tab, the header and the accessible name.</summary>
    public string Title => Localizer.Format("msg.gitCompare.title", "{0} → {1}", FromName, ToName);

    /// <summary>
    /// The compared files' tree, as Git Changes shows its sections (M57): built off the UI thread, only while the Tree view
    /// is shown, into a virtualized list of rows updated in place, so Refresh keeps the collapsed folders and the selection.
    /// A tree view built on the UI thread, with the list beside it, took seconds for two branches 10,000 files apart.
    /// </summary>
    public GitChangeTree Changes { get; } = new();

    /// <summary>The rows of <see cref="Changes"/>.</summary>
    public ResettableCollection<GitChangeRow> Rows => Changes.Rows;

    /// <summary>The List view's files by path, filled (with one reset) only while it's shown.</summary>
    public ResettableCollection<GitFileRow> Items { get; } = [];

    /// <summary>Every compared file, by path, whichever view is shown.</summary>
    public IReadOnlyList<GitFileRow> Files => _rows;

    /// <summary>How many times the tree and the list were built (tests: only the shown view's).</summary>
    internal (int Tree, int List) ViewsBuilt { get; private set; }

    /// <summary>The Tree view's selection; Refresh selects the same node again.</summary>
    public GitChangeRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!_showing && SetField(ref _selectedRow, value))
            {
                _selectedKey = value?.Key;
            }
        }
    }

    /// <summary>The List view's selection; Refresh selects the same file again.</summary>
    public GitFileRow? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!_showing && SetField(ref _selectedItem, value))
            {
                _selectedPath = value?.Status.Path;
            }
        }
    }

    /// <summary>Opens a file's diff (set by the shell).</summary>
    public Func<GitChangedFile, Task>? OpenFile { get; set; }

    /// <summary>View History (set by the shell): the next step when the files are the same (C3).</summary>
    public Func<Task>? ShowHistory { get; set; }

    public bool CanShowHistory => ShowHistory is not null;

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

    /// <summary>The Tree view (or the List view): the one shown is built when it's out of date.</summary>
    public bool IsTreeView
    {
        get => _isTreeView;
        set
        {
            if (SetField(ref _isTreeView, value))
            {
                OnPropertyChanged(nameof(IsListView));
                if (!_isLoading)
                {
                    _ = ShowViewAsync(_version);
                }
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

    public string EmptyText => ComparesCommits
        ? Localizer.Format("msg.gitCompare.sameFilesCommits", "No differences: {0} and {1} have the same files.", FromName, ToName)
        : Localizer.Format("msg.gitCompare.sameFiles", "No differences: {0} and {1} have the same files. Their commits can still differ; View History lists them.", FromName, ToName);

    public string Hint => ComparesCommits
        ? Localizer.Format("msg.gitCompare.hintCommits", "Letters say what {1} changes from {0}. Enter or double-click opens a file's diff: {0} on the left, {1} on the right.", FromName, ToName)
        : Localizer.Format("msg.gitCompare.hint", "Letters say what {1} changes from {0}, tip to tip. Enter or double-click opens a file's diff: {0} on the left, {1} on the right.", FromName, ToName);

    /// <summary>Reads the files again (on open and Refresh), off the UI thread, then builds the view shown.</summary>
    public async Task LoadAsync()
    {
        var version = ++_version;
        IsLoading = true;
        try
        {
            var files = await _git.CompareBranchesAsync(From, To).ConfigureAwait(true);
            var root = _git.Root ?? string.Empty;
            var rows = await Task.Run(() => (IReadOnlyList<GitFileRow>)[.. files.Select(f => new GitFileRow(
                new GitFileStatus(f.Path, System.IO.Path.GetRelativePath(root, f.Path).Replace('\\', '/'), f.Status, '.', f.OldPath),
                staged: true)
            {
                Committed = true,
                Location = System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(root, f.Path)) ?? string.Empty,
            }).OrderBy(r => r.Status.RelativePath, StringComparer.OrdinalIgnoreCase)]).ConfigureAwait(true);
            if (version == _version)
            {
                _rows = rows;
            }
        }
        finally
        {
            if (version == _version)
            {
                (_treeStale, _listStale) = (true, true);
                await ShowViewAsync(version).ConfigureAwait(true);
                IsLoading = false;
                Notify();
            }
        }
    }

    /// <summary>
    /// Builds the view shown when it's out of date: the tree off the UI thread, shown in place with the folders the user
    /// collapsed still collapsed, or the list with one reset; then the same selection.
    /// </summary>
    private async Task ShowViewAsync(int version)
    {
        if (_isTreeView && _treeStale)
        {
            var rows = _rows;
            var root = _git.Root ?? string.Empty;
            var collapsed = Changes.Collapsed;
            var expandedChanged = Changes.ExpandedChanged;
            var (tree, flat) = await Task.Run(() =>
            {
                var built = GitChangesViewModel.BuildTree(root, rows, "compare", staged: true, collapsed, expandedChanged);
                return (built, GitChangeTree.Flatten(built));
            }).ConfigureAwait(true);
            if (version != _version || !_isTreeView || !ReferenceEquals(rows, _rows))
            {
                return;
            }

            _treeStale = false;
            ViewsBuilt = (ViewsBuilt.Tree + 1, ViewsBuilt.List);
            Shown(() => Changes.Show(tree, flat));
            _selectedRow = _selectedKey is null ? null : Changes.RowOf(_selectedKey);
            OnPropertyChanged(nameof(SelectedRow));
        }
        else if (!_isTreeView && _listStale)
        {
            _listStale = false;
            ViewsBuilt = (ViewsBuilt.Tree, ViewsBuilt.List + 1);
            Shown(() => Items.Reset(_rows));
            _selectedItem = _selectedPath is null ? null : _rows.FirstOrDefault(r => string.Equals(r.Status.Path, _selectedPath, StringComparison.Ordinal));
            OnPropertyChanged(nameof(SelectedItem));
        }
    }

    /// <summary>Shows new rows: the list's selection, which goes as they're replaced, is set again after.</summary>
    private void Shown(Action show)
    {
        _showing = true;
        try
        {
            show();
        }
        finally
        {
            _showing = false;
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
