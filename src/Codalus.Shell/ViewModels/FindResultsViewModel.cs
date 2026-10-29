using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A row of the Find Results: a group header (folder or file), or a match (line number and the line's text). Find All
/// References has its own window (<see cref="ReferencesViewModel"/>).
/// </summary>
public sealed record FindResultRow(string Path, int Line, int Column, int Length, string Text, bool IsHeader)
{
    /// <summary>The positions in <see cref="Code"/> that matched (Find in Files), drawn emphasized.</summary>
    public IReadOnlyList<int>? Highlights { get; init; }

    /// <summary>Find in Files: true once Replace changed this match.</summary>
    public bool IsReplaced { get; init; }

    /// <summary>How deep in the groups the row sits (0: the outermost).</summary>
    public int Depth { get; init; }

    /// <summary>A header that collapses: the key its collapsed state is kept under; null for other rows.</summary>
    public string? GroupKey { get; init; }

    public bool IsExpanded { get; init; } = true;

    /// <summary>A match: its index in the page's results (before filters and groups); -1 for headers.</summary>
    public int Source { get; init; } = -1;

    /// <summary>List View: the File column shows the full path.</summary>
    public bool FullPath { get; init; }

    /// <summary>A header: how many matches its group holds (shown as a badge after it); 0 for other rows.</summary>
    public int Count { get; init; }

    public bool HasCount => Count > 0;

    /// <summary>A header: a folder's (its path shown below the search's root), not a file's or a project's.</summary>
    public bool IsFolder { get; init; }

    /// <summary>A header of a file: drawn semibold (a folder's is regular, M34h).</summary>
    public bool IsFileHeader => IsHeader && !IsFolder;

    /// <summary>A header's text as shown: a folder below the search's root, a file's name or path.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>A header's icon: the folder (open or closed) or the file's type.</summary>
    public string? HeaderIcon => !IsHeader ? null
        : IsFolder ? Controls.FileIcons.ForFolder(System.IO.Path.GetFileName(Path.TrimEnd('/', '\\')), IsExpanded)
        : GroupKey is null ? null : Controls.FileIcons.ForFile(System.IO.Path.GetFileName(Path));

    /// <summary>The columns a row's first cell takes: a header the whole row, a match the Code column.</summary>
    public int Span => IsHeader ? 4 : 1;

    /// <summary>
    /// The row's indent: a header by its depth (its chevron, icon and text after it), a match under its group's text
    /// (M34h, the mockup's: folder, file, then the code where the file's name starts).
    /// </summary>
    public Avalonia.Thickness Indent => new(IsHeader ? 4 + (Depth * 18) : Depth == 0 ? 8 : 40 + ((Depth - 1) * 18), 0, 0, 0);

    /// <summary>The Code column: the line's text; a header's text with its count.</summary>
    public string Code => IsHeader && Count > 0 ? $"{Text} ({Count})" : Text;

    public string FileName => IsHeader ? string.Empty : FullPath ? Path : System.IO.Path.GetFileName(Path);

    public string LineLabel => IsHeader ? string.Empty : Line.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string ColumnLabel => IsHeader ? string.Empty : (Column + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool IsCollapsible => GroupKey is not null;

    /// <summary>A collapsible header's chevron: right when collapsed, down when expanded.</summary>
    public string? Chevron => GroupKey is null ? null : IsExpanded ? "chevron-down" : "chevron-right";

    public string Display => IsHeader ? Code : $"{Line,6}:  {Text}";

    public string AccessibleName => IsHeader ? (GroupKey is null ? Code : $"{Code}, {(IsExpanded ? "expanded" : "collapsed")}")
        : $"Line {Line}: {Text}";
}

/// <summary>How Find in Files results are grouped (the Group by drop-down).</summary>
public static class FindResultsGrouping
{
    /// <summary>A header per folder, under it a header per file: a tree that collapses.</summary>
    public const string PathAndFile = "Path and file";

    /// <summary>A header per file (its full path).</summary>
    public const string File = "File";

    /// <summary>The matches only.</summary>
    public const string None = "None";

    public static IReadOnlyList<string> All { get; } = [PathAndFile, File, None];
}

/// <summary>
/// Find Results: what Find and Replace (<see cref="FindReplaceViewModel"/>) found, in numbered pages (Find Results 1, 2…). A page with Keep Results on keeps its results: the next search
/// goes to a page without it, or a new one. The members below the pages act on the current page.
/// </summary>
public sealed class FindResultsViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "findResults";

    private readonly Func<FindInFilesOptions, Task<IReadOnlyList<string>>> _files;
    private readonly Func<string, string?> _openText;
    private readonly Func<string, Position, int, Task> _open;
    private FindResultsPage _currentPage;

    /// <param name="files">The files a Find in Files search looks in (its scope, its file types applied).</param>
    /// <param name="openText">The editor's text of an open file (unsaved changes count), or null.</param>
    /// <param name="open">Opens a file at a position, selecting that many characters.</param>
    public FindResultsViewModel(Func<FindInFilesOptions, Task<IReadOnlyList<string>>> files, Func<string, string?> openText, Func<string, Position, int, Task> open)
    {
        _files = files;
        _openText = openText;
        _open = open;
        _currentPage = new FindResultsPage(this, 1);
        Pages.Add(_currentPage);
        Pages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasManyPages));
    }

    /// <summary>The result pages, by number.</summary>
    public ObservableCollection<FindResultsPage> Pages { get; } = [];

    /// <summary>The page shown, and the one Next, Previous and Replace work on.</summary>
    public FindResultsPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (value is not null && SetField(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(Rows));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(SelectedRow));
                OnPropertyChanged(nameof(IsSearching));
            }
        }
    }

    /// <summary>The page tabs show only once there is more than one page.</summary>
    public bool HasManyPages => Pages.Count > 1;

    /// <summary>The folder headers' paths are shown below (the solution's folder) when a search looked in a scope, not a folder.</summary>
    public Func<string?>? RootFolder { get; set; }

    public static IReadOnlyList<string> FilesGroupByOptions => FindResultsGrouping.All;

    /// <summary>Modify Find: Find and Replace should show again with this page's search.</summary>
    public event EventHandler<FindResultsPage>? ModifyFindRequested;

    /// <summary>A search or a replace started: the window should be shown.</summary>
    public event EventHandler? ShowRequested;

    // ------------------------------------------------------------------ the current page

    public ObservableCollection<FindResultRow> Rows => _currentPage.Rows;

    public string Title => _currentPage.Title;

    public string Summary => _currentPage.Summary;

    public FindInFilesOptions? LastSearch => _currentPage.LastSearch;

    public bool IsSearching => _currentPage.IsSearching;

    public FindResultRow? SelectedRow
    {
        get => _currentPage.SelectedRow;
        set => _currentPage.SelectedRow = value;
    }

    public RelayCommand ModifyFindCommand => _currentPage.ModifyFindCommand;

    public RelayCommand NextCommand => _currentPage.NextCommand;

    public RelayCommand PreviousCommand => _currentPage.PreviousCommand;

    public RelayCommand ClearCommand => _currentPage.ClearCommand;

    public RelayCommand CancelCommand => _currentPage.CancelCommand;

    public void Cancel() => _currentPage.Cancel();

    public void Clear() => _currentPage.Clear();

    public Task OpenAsync(FindResultRow row) => _currentPage.OpenAsync(row);

    public Task GoToAsync(int direction) => _currentPage.GoToAsync(direction);

    public Task GoToNextFileAsync() => _currentPage.GoToNextFileAsync();

    public Task ReplaceRowAsync(FindResultRow row, FindResultRow next, bool open) => _currentPage.ReplaceRowAsync(row, next, open);

    // ------------------------------------------------------------------ new results

    /// <summary>Find All: lists every line that matches <paramref name="options"/>, after the current page's results when <paramref name="append"/>.</summary>
    public Task SearchAsync(FindInFilesOptions options, bool append = false) =>
        (append ? _currentPage : TakePage()).SearchAsync(options, append);

    /// <summary>Replace All's results: the lines that changed (with their new text), under the replace's title.</summary>
    public void ShowReplaced(FindInFilesOptions options, string replaceWith, IReadOnlyList<FindResultRow> rows, int occurrences, int files) =>
        TakePage().ShowReplaced(options, replaceWith, rows, occurrences, files);

    /// <summary>The page new results go to: the current one, unless it keeps its results; then one that doesn't, or a new one.</summary>
    private FindResultsPage TakePage()
    {
        if (!_currentPage.KeepResults)
        {
            return _currentPage;
        }

        if (Pages.FirstOrDefault(p => !p.KeepResults) is not { } page)
        {
            var number = 1;
            while (Pages.Any(p => p.Number == number))
            {
                number++;
            }

            page = new FindResultsPage(this, number);
            var index = 0;
            while (index < Pages.Count && Pages[index].Number < number)
            {
                index++;
            }

            Pages.Insert(index, page);
        }

        CurrentPage = page;
        return page;
    }

    /// <summary>Closes a page; the last one is cleared instead.</summary>
    public void Close(FindResultsPage page)
    {
        var index = Pages.IndexOf(page);
        if (index < 0)
        {
            return;
        }

        if (Pages.Count == 1)
        {
            page.KeepResults = false;
            page.Clear();
            return;
        }

        Pages.RemoveAt(index);
        page.Dispose();
        if (_currentPage == page)
        {
            CurrentPage = Pages[Math.Min(index, Pages.Count - 1)];
        }
    }

    public void Dispose()
    {
        foreach (var page in Pages)
        {
            page.Dispose();
        }
    }

    // ------------------------------------------------------------------ for the pages

    internal Task<IReadOnlyList<string>> FilesAsync(FindInFilesOptions options) => _files(options);

    internal string? OpenText(string path) => _openText(path);

    internal Task Open(string path, Position position, int length) => _open(path, position, length);

    internal void RequestShow() => ShowRequested?.Invoke(this, EventArgs.Empty);

    internal void RequestModifyFind(FindResultsPage page) => ModifyFindRequested?.Invoke(this, page);

    /// <summary>A page's state changed: the forwarding members follow the current page.</summary>
    internal void PageChanged(FindResultsPage page, string? property)
    {
        if (page == _currentPage && property is nameof(Title) or nameof(Summary) or nameof(SelectedRow) or nameof(IsSearching))
        {
            OnPropertyChanged(property);
        }
    }
}

/// <summary>
/// One page of the Find Results (Find Results 1, 2…): a search's results or a Replace All's. Searching
/// runs on the thread pool and streams results in batches; a new search cancels the page's previous one. The rows
/// shown are the results through the Search Find Results filter, grouped by Group by (a tree whose headers collapse)
/// or listed flat (List View). Double-click or Enter opens a match; Next and Previous (and Find and Replace's Find Next)
/// walk them; Modify Find shows Find and Replace again with the page's search.
/// </summary>
public sealed class FindResultsPage : ObservableObject, IDisposable
{
    private const int MaxMatches = 10_000;

    private readonly FindResultsViewModel _owner;

    /// <summary>The results, before the filter and grouping (no headers).</summary>
    private readonly List<FindResultRow> _matches = [];

    /// <summary>The <see cref="FindResultRow.GroupKey"/>s of collapsed headers.</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _search;
    private string _title = string.Empty;
    private string _summary = string.Empty;
    private int _shownMatches;
    private bool _isSearching;
    private FindResultRow? _selectedRow;
    private string _filter = string.Empty;
    private string _filesGroupBy = FindResultsGrouping.File;
    private bool _isListView;
    private bool _keepResults;

    internal FindResultsPage(FindResultsViewModel owner, int number)
    {
        _owner = owner;
        Number = number;
        CancelCommand = new RelayCommand(Cancel);
        ModifyFindCommand = new RelayCommand(() => _owner.RequestModifyFind(this));
        NextCommand = new RelayCommand(() => _ = GoToAsync(1));
        PreviousCommand = new RelayCommand(() => _ = GoToAsync(-1));
        ClearCommand = new RelayCommand(Clear);
        CloseCommand = new RelayCommand(() => _owner.Close(this));
    }

    public int Number { get; }

    /// <summary>The page's tab: Find Results 1, 2…</summary>
    public string Header => Localizer.Format("findResults.pageHeader", "Find Results {0}", Number);

    public ObservableCollection<FindResultRow> Rows { get; } = [];

    public RelayCommand CancelCommand { get; }

    public RelayCommand ModifyFindCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand PreviousCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>The last Find in Files search (null after a Replace All was shown).</summary>
    public FindInFilesOptions? LastSearch { get; private set; }

    /// <summary>The search behind the results (a Find All's or a Replace All's), for Modify Find.</summary>
    public FindInFilesOptions? Options { get; private set; }

    /// <summary>A Replace All's results: what it replaced with.</summary>
    public string? ReplaceWith { get; private set; }

    /// <summary>Keep Results: the next search goes to another page and these stay until the page is closed.</summary>
    public bool KeepResults
    {
        get => _keepResults;
        set => SetField(ref _keepResults, value);
    }

    /// <summary>What was searched for: <c>Find all "x", Entire solution, "*.cs"</c>.</summary>
    public string Title
    {
        get => _title;
        private set
        {
            if (SetField(ref _title, value))
            {
                OnPropertyChanged(nameof(HasTitle));
                OnPropertyChanged(nameof(TitleVerb));
                OnPropertyChanged(nameof(TitleTerm));
                OnPropertyChanged(nameof(TitleRest));
                _owner.PageChanged(this, nameof(Title));
            }
        }
    }

    public bool HasTitle => _title.Length > 0;

    /// <summary>The title's start, before what was searched for: <c>Find all </c>.</summary>
    public string TitleVerb => SplitTitle().Verb;

    /// <summary>What was searched for, in its quotes: <c>"Person"</c> (drawn semibold).</summary>
    public string TitleTerm => SplitTitle().Term;

    /// <summary>The rest: <c>, Match case, Entire solution, "*.cs"</c> (muted).</summary>
    public string TitleRest => SplitTitle().Remainder;

    private (string Verb, string Term, string Remainder) SplitTitle()
    {
        var quote = _title.IndexOf('"', StringComparison.Ordinal);
        var end = Options is { } options && quote >= 0 ? quote + options.Query.Length + 2 : -1;
        return end > quote && end <= _title.Length && _title[end - 1] == '"'
            ? (_title[..quote], _title[quote..end], _title[end..])
            : (_title, string.Empty, string.Empty);
    }

    /// <summary>The results before the filter (the page's tab shows the count).</summary>
    public int MatchCount => _matches.Count;

    public bool HasMatches => _matches.Count > 0;

    /// <summary>The counts; with a filter, how many of the matches it shows.</summary>
    public string Summary => _filter.Length == 0 || _matches.Count == 0 ? _summary
        : $"{_summary}    Shown: {_shownMatches} of {_matches.Count}";

    private string BaseSummary
    {
        set
        {
            _summary = value;
            OnPropertyChanged(nameof(Summary));
            _owner.PageChanged(this, nameof(Summary));
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetField(ref _isSearching, value))
            {
                _owner.PageChanged(this, nameof(IsSearching));
            }
        }
    }

    /// <summary>The current result: the one Next, Previous and Replace start from.</summary>
    public FindResultRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetField(ref _selectedRow, value))
            {
                _owner.PageChanged(this, nameof(SelectedRow));
            }
        }
    }

    /// <summary>Search Find Results: shows the matches whose code or file name contains the text.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Refresh();
                OnPropertyChanged(nameof(Summary));
                _owner.PageChanged(this, nameof(Summary));
            }
        }
    }

    /// <summary>Find in Files results: one of <see cref="FindResultsGrouping"/>.</summary>
    public string FilesGroupBy
    {
        get => _filesGroupBy;
        set
        {
            if (SetField(ref _filesGroupBy, FindResultsGrouping.All.Contains(value) ? value : FindResultsGrouping.File))
            {
                Refresh();
            }
        }
    }

    /// <summary>List View: the matches flat, with their full paths in the File column; Group by doesn't apply.</summary>
    public bool IsListView
    {
        get => _isListView;
        set
        {
            if (SetField(ref _isListView, value))
            {
                OnPropertyChanged(nameof(CanGroupFiles));
                OnPropertyChanged(nameof(ViewIndex));
                Refresh();
            }
        }
    }

    /// <summary>The Tree or List segmented control (M34h, D33: it replaced the List View toggle): 0 grouped, 1 List View.</summary>
    public int ViewIndex
    {
        get => _isListView ? 1 : 0;
        set => IsListView = value == 1;
    }

    public bool CanGroupFiles => !_isListView;

    // ------------------------------------------------------------------ the rows shown

    /// <summary>Starts new results: the earlier ones go, along with what they showed.</summary>
    private void Begin()
    {
        _matches.Clear();
        _collapsed.Clear();
        Rows.Clear();
        SelectedRow = null;
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(HasMatches));
    }

    /// <summary>Collapses or expands a header's group.</summary>
    public void Toggle(FindResultRow header)
    {
        if (header.GroupKey is not { } key)
        {
            return;
        }

        if (!_collapsed.Remove(key))
        {
            _collapsed.Add(key);
        }

        Refresh();
        if (_selectedRow is null || !Rows.Contains(_selectedRow))
        {
            SelectedRow = Rows.FirstOrDefault(r => r.GroupKey == key); // the selection was in the group: the header takes it
        }
    }

    /// <summary>Expands (true) or collapses a header's group; false when it already was.</summary>
    public bool SetExpanded(FindResultRow header, bool expanded)
    {
        if (header.GroupKey is null || header.IsExpanded == expanded)
        {
            return false;
        }

        Toggle(header);
        return true;
    }

    /// <summary>
    /// Shows the results through the filter and grouping. Only the rows from the first one that differs change, so
    /// results streaming in are added at the end and the selection stays.
    /// </summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(HasMatches));
        var rows = Arrange();
        var selected = _selectedRow;
        var index = 0;
        while (index < rows.Count && index < Rows.Count && Rows[index].Equals(rows[index]))
        {
            index++;
        }

        for (var i = index; i < rows.Count && i < Rows.Count; i++)
        {
            Rows[i] = rows[i];
        }

        while (Rows.Count > rows.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        for (var i = Rows.Count; i < rows.Count; i++)
        {
            Rows.Add(rows[i]);
        }

        if (selected is not null && !Rows.Contains(selected))
        {
            SelectedRow = selected.IsHeader
                ? Rows.FirstOrDefault(r => r.IsHeader && r.GroupKey is not null && r.GroupKey == selected.GroupKey)
                : Rows.FirstOrDefault(r => !r.IsHeader && r.Source == selected.Source);
        }
        else
        {
            SelectedRow = selected;
        }
    }

    private List<FindResultRow> Arrange()
    {
        var shown = new List<FindResultRow>();
        for (var i = 0; i < _matches.Count; i++)
        {
            var match = _matches[i];
            if (PassesFilter(match))
            {
                shown.Add(match with { Source = i });
            }
        }

        _shownMatches = shown.Count;
        var rows = new List<FindResultRow>(shown.Count + 16);
        if (_isListView)
        {
            rows.AddRange(shown.Select(m => m with { FullPath = true }));
        }
        else if (_filesGroupBy == FindResultsGrouping.None)
        {
            rows.AddRange(shown);
        }
        else if (_filesGroupBy == FindResultsGrouping.PathAndFile)
        {
            foreach (var folder in shown.GroupBy(m => System.IO.Path.GetDirectoryName(m.Path) ?? string.Empty, SolutionService.PathComparer))
            {
                var folderHeader = Group("d:" + folder.Key, folder.Key, folder.Key, folder.Count(), 0) with { IsFolder = true, Label = Relative(folder.Key) };
                rows.Add(folderHeader);
                if (folderHeader.IsExpanded)
                {
                    foreach (var file in folder.GroupBy(m => m.Path, SolutionService.PathComparer))
                    {
                        AddFile(file.Key, System.IO.Path.GetFileName(file.Key), [.. file], 1, System.IO.Path.GetFileName(file.Key));
                    }
                }
            }
        }
        else
        {
            foreach (var file in shown.GroupBy(m => m.Path, SolutionService.PathComparer))
            {
                AddFile(file.Key, file.Key, [.. file], 0, Relative(file.Key));
            }
        }

        return rows;

        void AddFile(string path, string text, List<FindResultRow> matches, int depth, string label)
        {
            var header = Group("f:" + path, path, text, matches.Count, depth) with { Label = label };
            rows.Add(header);
            if (header.IsExpanded)
            {
                rows.AddRange(matches.Select(m => m with { Depth = depth + 1 }));
            }
        }
    }

    private FindResultRow Group(string key, string path, string text, int count, int depth) =>
        new(path, 0, 0, 0, text, true) { GroupKey = key, IsExpanded = !_collapsed.Contains(key), Depth = depth, Count = count, Label = text };

    /// <summary>
    /// A folder or file as a header shows it (M34h): below the folder searched, or the solution's, when it is in it
    /// (<c>src/Calculator</c>); its full path otherwise.
    /// </summary>
    private string Relative(string path)
    {
        var root = Options is { LookIn: var lookIn } && System.IO.Path.IsPathRooted(lookIn) ? lookIn : _owner.RootFolder?.Invoke();
        if (string.IsNullOrEmpty(root))
        {
            return path;
        }

        var relative = System.IO.Path.GetRelativePath(root, path);
        return relative == "." ? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(root))
            : relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative) ? path
            : relative;
    }

    private bool PassesFilter(FindResultRow row) => _filter.Length == 0
        || row.Code.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || System.IO.Path.GetFileName(row.Path).Contains(_filter, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ walking the results

    public void Cancel()
    {
        _search?.Cancel();
    }

    public void Clear()
    {
        _search?.Cancel();
        Begin();
        LastSearch = null;
        Options = null;
        ReplaceWith = null;
        Title = string.Empty;
        BaseSummary = string.Empty;
    }

    public void Dispose()
    {
        _search?.Cancel();
        _search?.Dispose();
    }

    public Task OpenAsync(FindResultRow row) => row.IsHeader ? Task.CompletedTask : _owner.Open(row.Path, new Position(row.Line - 1, row.Column), row.Length);

    /// <summary>Goes to the next (1) or previous (-1) result after the current one, wrapping around, and opens it.</summary>
    public Task GoToAsync(int direction)
    {
        var count = Rows.Count;
        if (count == 0)
        {
            return Task.CompletedTask;
        }

        var start = _selectedRow is { } current ? Rows.IndexOf(current) : -1;
        if (start < 0)
        {
            start = direction > 0 ? -1 : count;
        }

        for (var step = 1; step <= count; step++)
        {
            var index = (((start + direction * step) % count) + count) % count;
            if (Rows[index] is { IsHeader: false, IsReplaced: false } row)
            {
                SelectedRow = row;
                return OpenAsync(row);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Skip File: the first result in the next file after the current one's, wrapping around.</summary>
    public Task GoToNextFileAsync()
    {
        var start = _selectedRow is { } current ? Rows.IndexOf(current) : -1;
        if (start < 0)
        {
            return GoToAsync(1);
        }

        var path = Rows[start].Path;
        var count = Rows.Count;
        for (var step = 1; step < count; step++)
        {
            var row = Rows[(start + step) % count];
            if (row is { IsHeader: false, IsReplaced: false } && !SolutionService.PathComparer.Equals(row.Path, path))
            {
                SelectedRow = row;
                return OpenAsync(row);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Replace changed <paramref name="row"/>'s match: <paramref name="next"/> takes its place and becomes current (so
    /// Next moves on from it), and is opened when it is the line's next match.
    /// </summary>
    public Task ReplaceRowAsync(FindResultRow row, FindResultRow next, bool open)
    {
        var source = row.Source;
        if (source < 0 || source >= _matches.Count)
        {
            return Task.CompletedTask;
        }

        _matches[source] = next with { Source = -1, Depth = 0, FullPath = false };
        Refresh();
        if (Rows.FirstOrDefault(r => !r.IsHeader && r.Source == source) is not { } shown)
        {
            return Task.CompletedTask;
        }

        SelectedRow = shown;
        return open ? OpenAsync(shown) : Task.CompletedTask;
    }

    // ------------------------------------------------------------------ Find in Files

    /// <summary>Find All: lists every line that matches <paramref name="options"/>, after the earlier results when <paramref name="append"/>.</summary>
    public async Task SearchAsync(FindInFilesOptions options, bool append = false)
    {
        _search?.Cancel();
        _search?.Dispose();
        var cts = _search = new CancellationTokenSource();
        if (!append)
        {
            Begin();
        }

        LastSearch = options;
        Options = options;
        ReplaceWith = null;
        Title = options.Describe("Find all");
        _owner.RequestShow();
        if (options.Query.Length == 0)
        {
            BaseSummary = string.Empty;
            return;
        }

        Regex regex;
        try
        {
            regex = options.CreateRegex();
        }
        catch (ArgumentException ex)
        {
            BaseSummary = "Invalid regular expression: " + ex.Message;
            return;
        }

        IsSearching = true;
        BaseSummary = "Searching…";
        IReadOnlyList<string> files;
        try
        {
            files = await _owner.FilesAsync(options).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsSearching = false;
            BaseSummary = ex.Message;
            return;
        }

        if (cts.IsCancellationRequested)
        {
            if (_search == cts)
            {
                IsSearching = false;
                BaseSummary = "Search cancelled.";
            }

            return;
        }

        // A folder's files together, so grouping by path adds its rows at the end as they stream in.
        files = [.. files
            .OrderBy(f => System.IO.Path.GetDirectoryName(f), StringComparer.OrdinalIgnoreCase)
            .ThenBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase)];
        var openTexts = files.Select(f => (f, _owner.OpenText(f))).Where(x => x.Item2 is not null).ToDictionary(x => x.f, x => x.Item2!, SolutionService.PathComparer);
        IsSearching = true;
        BaseSummary = $"Searching {files.Count} files…";
        var lines = 0;
        var matchedFiles = 0;
        try
        {
            await Task.Run(async () =>
            {
                var batch = new List<FindResultRow>();
                foreach (var file in files)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    string text;
                    try
                    {
                        text = openTexts.TryGetValue(file, out var open) ? open : await File.ReadAllTextAsync(file, cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if (text.Contains('\0', StringComparison.Ordinal))
                    {
                        continue; // binary
                    }

                    var found = false;
                    var lineNumber = 0;
                    foreach (var line in text.Split('\n'))
                    {
                        lineNumber++;
                        if (MatchLine(file, lineNumber, line.TrimEnd('\r'), regex) is { } row)
                        {
                            batch.Add(row);
                            found = true;
                            if (++lines >= MaxMatches)
                            {
                                break;
                            }
                        }
                    }

                    if (found)
                    {
                        matchedFiles++;
                    }

                    if (batch.Count >= 200 || lines >= MaxMatches)
                    {
                        await PostAsync(batch, cts.Token).ConfigureAwait(false);
                        batch = [];
                    }

                    if (lines >= MaxMatches)
                    {
                        break;
                    }
                }

                await PostAsync(batch, cts.Token).ConfigureAwait(false);
            }, cts.Token).ConfigureAwait(true);
            BaseSummary = lines == 0
                ? $"No results. Total files searched: {files.Count}"
                : $"Matching lines: {lines}{(lines >= MaxMatches ? "+" : string.Empty)}    Matching files: {matchedFiles}    Total files searched: {files.Count}";
        }
        catch (OperationCanceledException)
        {
            BaseSummary = "Search cancelled.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Replace All's results: the lines that changed (with their new text), under the replace's title.</summary>
    public void ShowReplaced(FindInFilesOptions options, string replaceWith, IReadOnlyList<FindResultRow> rows, int occurrences, int files)
    {
        _search?.Cancel();
        Begin();
        LastSearch = null;
        Options = options;
        ReplaceWith = replaceWith;
        _matches.AddRange(rows.GroupBy(r => r.Path, SolutionService.PathComparer).SelectMany(g => g));
        Refresh();
        Title = options.Describe("Replace all") + $" with \"{replaceWith}\"";
        _owner.RequestShow();
        BaseSummary = $"Occurrences replaced: {occurrences}    Changed lines: {rows.Count}    Files changed: {files}";
    }

    /// <summary>A row for <paramref name="line"/> when it matches: its trimmed text with every match highlighted.</summary>
    internal static FindResultRow? MatchLine(string path, int lineNumber, string line, Regex regex)
    {
        Match? first = null;
        var highlights = new List<int>();
        const int MaxShown = 400;
        var indent = line.Length - line.TrimStart().Length;
        var shown = line.Trim();
        if (shown.Length > MaxShown)
        {
            shown = shown[..MaxShown] + "…";
        }

        foreach (Match match in regex.Matches(line))
        {
            if (match.Length == 0)
            {
                continue; // an empty match (^, \b…) finds nothing to show
            }

            first ??= match;
            for (var i = Math.Max(0, match.Index - indent); i < Math.Min(match.Index - indent + match.Length, Math.Min(shown.Length, MaxShown)); i++)
            {
                highlights.Add(i);
            }
        }

        return first is null ? null : new FindResultRow(path, lineNumber, first.Index, first.Length, shown, false) { Highlights = highlights };
    }

    /// <summary>Adds a batch of results on the UI thread (awaited, so a finished search shows all of them).</summary>
    private async Task PostAsync(List<FindResultRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var copy = rows.ToList();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _matches.AddRange(copy);
                Refresh();
                if (_filter.Length > 0)
                {
                    BaseSummary = _summary; // the shown count changed
                }
            }
        });
    }
}

/// <summary>Bindings for the Find Results view.</summary>
public static class FindResultsStyles
{
    public static Avalonia.Data.Converters.IValueConverter Weight { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.FontWeight>(header => header ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);

    /// <summary>A row sits under its group's header.</summary>
    public static Avalonia.Data.Converters.IValueConverter Indent { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<int, Avalonia.Thickness>(depth => new Avalonia.Thickness(depth * 16, 0, 0, 0));

    /// <summary>A match that Replace changed is dimmed.</summary>
    public static Avalonia.Data.Converters.IValueConverter ReplacedOpacity { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(replaced => replaced ? 0.5 : 1.0);
}
