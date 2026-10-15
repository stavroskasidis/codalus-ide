using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A row of the Find Results: a group header (file or project), or a match (line number and the line's text). For
/// references (M12a), <see cref="Kind"/> is read, write, declaration or reference and <see cref="Container"/> the member
/// containing it.
/// </summary>
public sealed record FindResultRow(string Path, int Line, int Column, int Length, string Text, bool IsHeader)
{
    public string? Kind { get; init; }

    public string? Container { get; init; }

    /// <summary>The positions in <see cref="Code"/> that matched (Find in Files), drawn emphasized.</summary>
    public IReadOnlyList<int>? Highlights { get; init; }

    /// <summary>Find in Files: true once Replace changed this match.</summary>
    public bool IsReplaced { get; init; }

    /// <summary>The Code column: the line's text, with the kind of reference after it.</summary>
    public string Code => IsHeader || Kind is null ? Text
        : $"{Text}    [{KindTitle(Kind)}{(Container is null ? string.Empty : " in " + Container)}]";

    public string FileName => IsHeader ? string.Empty : System.IO.Path.GetFileName(Path);

    public string LineLabel => IsHeader ? string.Empty : Line.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string ColumnLabel => IsHeader ? string.Empty : (Column + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Display => IsHeader ? Text
        : Kind is null ? $"{Line,6}:  {Text}"
        : $"{Line,6}:  {Text}    [{KindTitle(Kind)}{(Container is null ? string.Empty : " in " + Container)}]";

    public string AccessibleName => IsHeader ? Text : Kind is null ? $"Line {Line}: {Text}" : $"Line {Line}, {KindTitle(Kind)}: {Text}";

    public static string KindTitle(string kind) => kind switch
    {
        ReferenceItemKinds.Declaration => "declaration",
        ReferenceItemKinds.Write => "write",
        ReferenceItemKinds.Read => "read",
        _ => "reference",
    };
}

/// <summary>One Find All References result, kept so earlier searches can be shown again.</summary>
public sealed record ReferenceSet(string Title, IReadOnlyList<ReferenceItem> Items)
{
    public override string ToString() => Title;
}

/// <summary>
/// Find Results: what Find and Replace (<see cref="FindReplaceViewModel"/>) found, a row per matching line, and the
/// results of Find All References. Searching runs on the thread pool and streams results in batches; a new search
/// cancels the previous one. Double-click or Enter opens a match; Next and Previous (and the window's Find Next) walk
/// them; Modify Find shows Find and Replace again.
/// </summary>
public sealed partial class FindResultsViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "findResults";
    private const int MaxMatches = 10_000;

    private readonly Func<FindInFilesOptions, Task<IReadOnlyList<string>>> _files;
    private readonly Func<string, string?> _openText;
    private readonly Func<string, Position, int, Task> _open;
    private CancellationTokenSource? _search;
    private string _title = string.Empty;
    private string _summary = string.Empty;
    private bool _isSearching;
    private FindResultRow? _selectedRow;

    /// <param name="files">The files a Find in Files search looks in (its scope, its file types applied).</param>
    /// <param name="openText">The editor's text of an open file (unsaved changes count), or null.</param>
    /// <param name="open">Opens a file at a position, selecting that many characters.</param>
    public FindResultsViewModel(Func<FindInFilesOptions, Task<IReadOnlyList<string>>> files, Func<string, string?> openText, Func<string, Position, int, Task> open)
    {
        _files = files;
        _openText = openText;
        _open = open;
        CancelCommand = new RelayCommand(Cancel);
        ModifyFindCommand = new RelayCommand(() => ModifyFindRequested?.Invoke(this, EventArgs.Empty));
        NextCommand = new RelayCommand(() => _ = GoToAsync(1));
        PreviousCommand = new RelayCommand(() => _ = GoToAsync(-1));
        ClearCommand = new RelayCommand(Clear);
    }

    public ObservableCollection<FindResultRow> Rows { get; } = [];

    // ------------------------------------------------------------------ references: grouping, filters, kept results (M12a)

    private const int MaxReferenceSets = 10;
    private ReferenceSet? _currentSet;
    private string _groupBy = "File";
    private string _kindFilter = "All";

    /// <summary>Earlier Find All References results, newest first.</summary>
    public ObservableCollection<ReferenceSet> ReferenceSets { get; } = [];

    public static IReadOnlyList<string> GroupByOptions { get; } = ["File", "Project", "None"];

    public static IReadOnlyList<string> KindFilterOptions { get; } = ["All", "Read", "Write", "Declaration"];

    /// <summary>True while references (not Find in Files results) are shown: the grouping and filter apply.</summary>
    public bool IsReferences => _currentSet is not null;

    public ReferenceSet? CurrentSet
    {
        get => _currentSet;
        set
        {
            if (SetField(ref _currentSet, value))
            {
                OnPropertyChanged(nameof(IsReferences));
                if (value is not null)
                {
                    _ = ShowSetAsync(value);
                }
            }
        }
    }

    public string GroupBy
    {
        get => _groupBy;
        set
        {
            if (SetField(ref _groupBy, value ?? "File") && _currentSet is { } set)
            {
                _ = ShowSetAsync(set);
            }
        }
    }

    public string KindFilter
    {
        get => _kindFilter;
        set
        {
            if (SetField(ref _kindFilter, value ?? "All") && _currentSet is { } set)
            {
                _ = ShowSetAsync(set);
            }
        }
    }

    /// <summary>Shows classified references (<c>codalus/references</c>) and keeps them in <see cref="ReferenceSets"/>.</summary>
    public async Task ShowReferencesAsync(string title, IReadOnlyList<ReferenceItem> items)
    {
        _search?.Cancel();
        LastSearch = null;
        Title = string.Empty;
        var set = new ReferenceSet(title, items);
        ReferenceSets.Insert(0, set);
        while (ReferenceSets.Count > MaxReferenceSets)
        {
            ReferenceSets.RemoveAt(ReferenceSets.Count - 1);
        }

        _currentSet = set;
        OnPropertyChanged(nameof(CurrentSet));
        OnPropertyChanged(nameof(IsReferences));
        await ShowSetAsync(set).ConfigureAwait(true);
    }

    private async Task ShowSetAsync(ReferenceSet set)
    {
        _search?.Cancel();
        LastSearch = null;
        Title = string.Empty;
        Rows.Clear();
        SelectedRow = null;
        var items = set.Items.Where(i => _kindFilter switch
        {
            "Read" => i.Kind == ReferenceItemKinds.Read,
            "Write" => i.Kind == ReferenceItemKinds.Write,
            "Declaration" => i.Kind == ReferenceItemKinds.Declaration,
            _ => true,
        }).ToList();
        var rows = items.Select(i => (Path: DocumentUris.ToPath(i.Location.Uri) ?? DocumentUris.GeneratedPath(i.Location.Uri) ?? i.Location.Uri, Item: i)).ToList();
        IEnumerable<IGrouping<string, (string Path, ReferenceItem Item)>> groups = _groupBy switch
        {
            "Project" => rows.GroupBy(r => r.Item.Project ?? "(no project)", StringComparer.Ordinal),
            "None" => rows.GroupBy(_ => string.Empty, StringComparer.Ordinal),
            _ => rows.GroupBy(r => r.Path, Codalus.Workbench.Projects.SolutionService.PathComparer),
        };
        foreach (var group in groups)
        {
            if (_groupBy != "None")
            {
                Rows.Add(new FindResultRow(group.First().Path, 0, 0, 0, $"{group.Key} ({group.Count()})", true));
            }

            foreach (var (path, item) in group.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Item.Location.Range.Start.Line))
            {
                var start = item.Location.Range.Start;
                var text = _groupBy == "File" ? item.LineText : $"{System.IO.Path.GetFileName(path)}: {item.LineText}";
                Rows.Add(new FindResultRow(path, start.Line + 1, start.Character, 0, text, false) { Kind = item.Kind, Container = item.ContainingMember });
            }
        }

        var counts = string.Join(", ", set.Items.GroupBy(i => i.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {FindResultRow.KindTitle(g.Key)}"));
        Summary = $"{set.Title}: {set.Items.Count} result(s) ({counts}){(items.Count < set.Items.Count ? $", {items.Count} shown" : string.Empty)}.";
        await Task.CompletedTask.ConfigureAwait(true);
    }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ModifyFindCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand PreviousCommand { get; }

    public RelayCommand ClearCommand { get; }

    /// <summary>Modify Find: Find and Replace should show again with the last search.</summary>
    public event EventHandler? ModifyFindRequested;

    /// <summary>A search or a replace started: the window should be shown.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>The last Find in Files search (null after references were shown).</summary>
    public FindInFilesOptions? LastSearch { get; private set; }

    /// <summary>What was searched for: <c>Find all "x", Entire solution, "*.cs"</c>.</summary>
    public string Title
    {
        get => _title;
        private set
        {
            if (SetField(ref _title, value))
            {
                OnPropertyChanged(nameof(HasTitle));
            }
        }
    }

    public bool HasTitle => _title.Length > 0;

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetField(ref _isSearching, value);
    }

    /// <summary>The current result: the one Next, Previous and Replace start from.</summary>
    public FindResultRow? SelectedRow
    {
        get => _selectedRow;
        set => SetField(ref _selectedRow, value);
    }

    public void Cancel()
    {
        _search?.Cancel();
    }

    public void Clear()
    {
        _search?.Cancel();
        _currentSet = null;
        OnPropertyChanged(nameof(CurrentSet));
        OnPropertyChanged(nameof(IsReferences));
        LastSearch = null;
        Rows.Clear();
        SelectedRow = null;
        Title = string.Empty;
        Summary = string.Empty;
    }

    public void Dispose()
    {
        _search?.Cancel();
        _search?.Dispose();
    }

    public Task OpenAsync(FindResultRow row) => row.IsHeader ? Task.CompletedTask : _open(row.Path, new Position(row.Line - 1, row.Column), row.Length);

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
            if (row is { IsHeader: false, IsReplaced: false } && !Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(row.Path, path))
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
        var index = Rows.IndexOf(row);
        if (index < 0)
        {
            return Task.CompletedTask;
        }

        Rows[index] = next;
        SelectedRow = next;
        return open ? OpenAsync(next) : Task.CompletedTask;
    }

    // ------------------------------------------------------------------ Find in Files

    /// <summary>Find All: lists every line that matches <paramref name="options"/>, after the earlier results when <paramref name="append"/>.</summary>
    public async Task SearchAsync(FindInFilesOptions options, bool append = false)
    {
        _search?.Cancel();
        _search?.Dispose();
        var cts = _search = new CancellationTokenSource();
        _currentSet = null;
        OnPropertyChanged(nameof(CurrentSet));
        OnPropertyChanged(nameof(IsReferences));
        LastSearch = options;
        if (!append)
        {
            Rows.Clear();
            SelectedRow = null;
        }

        Title = options.Describe("Find all");
        ShowRequested?.Invoke(this, EventArgs.Empty);
        if (options.Query.Length == 0)
        {
            Summary = string.Empty;
            return;
        }

        Regex regex;
        try
        {
            regex = options.CreateRegex();
        }
        catch (ArgumentException ex)
        {
            Summary = "Invalid regular expression: " + ex.Message;
            return;
        }

        IsSearching = true;
        Summary = "Searching…";
        IReadOnlyList<string> files;
        try
        {
            files = await _files(options).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsSearching = false;
            Summary = ex.Message;
            return;
        }

        if (cts.IsCancellationRequested)
        {
            if (_search == cts)
            {
                IsSearching = false;
                Summary = "Search cancelled.";
            }

            return;
        }

        var openTexts = files.Select(f => (f, _openText(f))).Where(x => x.Item2 is not null).ToDictionary(x => x.f, x => x.Item2!, Codalus.Workbench.Projects.SolutionService.PathComparer);
        IsSearching = true;
        Summary = $"Searching {files.Count} files…";
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

                    var fileRows = new List<FindResultRow>();
                    var lineNumber = 0;
                    foreach (var line in text.Split('\n'))
                    {
                        lineNumber++;
                        if (MatchLine(file, lineNumber, line.TrimEnd('\r'), regex) is { } row)
                        {
                            fileRows.Add(row);
                            if (++lines >= MaxMatches)
                            {
                                break;
                            }
                        }
                    }

                    if (fileRows.Count > 0)
                    {
                        matchedFiles++;
                        batch.Add(Header(file, fileRows.Count));
                        batch.AddRange(fileRows);
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
            Summary = lines == 0
                ? $"No results. Total files searched: {files.Count}"
                : $"Matching lines: {lines}{(lines >= MaxMatches ? "+" : string.Empty)}    Matching files: {matchedFiles}    Total files searched: {files.Count}";
        }
        catch (OperationCanceledException)
        {
            Summary = "Search cancelled.";
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
        _currentSet = null;
        OnPropertyChanged(nameof(CurrentSet));
        OnPropertyChanged(nameof(IsReferences));
        LastSearch = null;
        Rows.Clear();
        SelectedRow = null;
        foreach (var group in rows.GroupBy(r => r.Path, Codalus.Workbench.Projects.SolutionService.PathComparer))
        {
            Rows.Add(Header(group.Key, group.Count()));
            foreach (var row in group)
            {
                Rows.Add(row);
            }
        }

        Title = options.Describe("Replace all") + $" with \"{replaceWith}\"";
        ShowRequested?.Invoke(this, EventArgs.Empty);
        Summary = $"Occurrences replaced: {occurrences}    Changed lines: {rows.Count}    Files changed: {files}";
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
                foreach (var row in copy)
                {
                    Rows.Add(row);
                }
            }
        });
    }

    // ------------------------------------------------------------------ Find All References

    /// <summary>Shows <paramref name="locations"/> (files on disk) under <paramref name="title"/>.</summary>
    public async Task ShowLocationsAsync(string title, IReadOnlyList<Location> locations)
    {
        _search?.Cancel();
        LastSearch = null;
        Title = string.Empty;
        SelectedRow = null;
        _currentSet = null;
        OnPropertyChanged(nameof(CurrentSet));
        OnPropertyChanged(nameof(IsReferences));
        Rows.Clear();
        var byFile = locations
            .Select(l => (Path: DocumentUris.ToPath(l.Uri), Location: l))
            .Where(x => x.Path is not null)
            .GroupBy(x => x.Path!, Codalus.Workbench.Projects.SolutionService.PathComparer)
            .ToList();
        foreach (var file in byFile)
        {
            var text = _openText(file.Key) ?? await ReadAsync(file.Key).ConfigureAwait(true);
            var lines = text.Split('\n');
            Rows.Add(Header(file.Key, file.Count()));
            foreach (var (_, location) in file.OrderBy(x => x.Location.Range.Start.Line))
            {
                var line = location.Range.Start.Line;
                var preview = line < lines.Length ? lines[line].TrimEnd('\r').Trim() : string.Empty;
                Rows.Add(new FindResultRow(file.Key, line + 1, location.Range.Start.Character, 0, preview, false));
            }
        }

        Summary = $"{title}: {locations.Count} result(s) in {byFile.Count} file(s).";
    }

    private static async Task<string> ReadAsync(string path)
    {
        try
        {
            return await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static FindResultRow Header(string path, int count) => new(path, 0, 0, 0, $"{path} ({count})", true);
}

/// <summary>Bindings for the Find Results view.</summary>
public static class FindResultsStyles
{
    public static Avalonia.Data.Converters.IValueConverter Weight { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.FontWeight>(header => header ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);

    /// <summary>A match sits under its file's header.</summary>
    public static Avalonia.Data.Converters.IValueConverter Indent { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Thickness>(header => header ? default : new Avalonia.Thickness(20, 0, 0, 0));

    /// <summary>A match that Replace changed is dimmed.</summary>
    public static Avalonia.Data.Converters.IValueConverter ReplacedOpacity { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(replaced => replaced ? 0.5 : 1.0);
}
