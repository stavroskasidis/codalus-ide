using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Infrastructure;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>
/// A row of the Find Results: a group header (file or project), or a match (line number and the line's text). For
/// references (M12a), <see cref="Kind"/> is read, write, declaration or reference and <see cref="Container"/> the member
/// containing it.
/// </summary>
public sealed record FindResultRow(string Path, int Line, int Column, int Length, string Text, bool IsHeader)
{
    public string? Kind { get; init; }

    public string? Container { get; init; }

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
/// Find Results: Find in Files (the search bar: match case, whole word, regular expression, file filter) and the
/// results of Find All References. Searching runs on the thread pool and streams results in batches; a new search
/// cancels the previous one. Double-click or Enter opens a match.
/// </summary>
public sealed partial class FindResultsViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "findResults";
    private const int MaxMatches = 10_000;

    private readonly Func<IReadOnlyList<string>> _files;
    private readonly Func<string, string?> _openText;
    private readonly Func<string, Position, Task> _open;
    private CancellationTokenSource? _search;
    private string _query = string.Empty;
    private string _include = string.Empty;
    private bool _matchCase;
    private bool _wholeWord;
    private bool _useRegex;
    private string _summary = string.Empty;
    private bool _isSearching;

    /// <param name="files">The files Find in Files searches (the solution's).</param>
    /// <param name="openText">The editor's text of an open file (unsaved changes count), or null.</param>
    /// <param name="open">Opens a file at a position.</param>
    public FindResultsViewModel(Func<IReadOnlyList<string>> files, Func<string, string?> openText, Func<string, Position, Task> open)
    {
        _files = files;
        _openText = openText;
        _open = open;
        SearchCommand = new RelayCommand(() => _ = SearchAsync());
        CancelCommand = new RelayCommand(Cancel);
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

    /// <summary>Shows classified references (<c>daedalus/references</c>) and keeps them in <see cref="ReferenceSets"/>.</summary>
    public async Task ShowReferencesAsync(string title, IReadOnlyList<ReferenceItem> items)
    {
        _search?.Cancel();
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
        Rows.Clear();
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
            _ => rows.GroupBy(r => r.Path, DaedalusStudio.Workbench.Projects.SolutionService.PathComparer),
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

    public RelayCommand SearchCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string Query
    {
        get => _query;
        set => SetField(ref _query, value ?? string.Empty);
    }

    /// <summary>File name patterns, separated by <c>;</c> or <c>,</c> (e.g. <c>*.cs; *.json</c>); empty: every file.</summary>
    public string Include
    {
        get => _include;
        set => SetField(ref _include, value ?? string.Empty);
    }

    public bool MatchCase
    {
        get => _matchCase;
        set => SetField(ref _matchCase, value);
    }

    public bool WholeWord
    {
        get => _wholeWord;
        set => SetField(ref _wholeWord, value);
    }

    public bool UseRegex
    {
        get => _useRegex;
        set => SetField(ref _useRegex, value);
    }

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

    /// <summary>Raised when the search box should get focus (Ctrl+Shift+F).</summary>
    public event EventHandler? FocusRequested;

    public void RequestFocus(string? query)
    {
        if (!string.IsNullOrEmpty(query))
        {
            Query = query;
        }

        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        _search?.Cancel();
    }

    public void Dispose()
    {
        _search?.Cancel();
        _search?.Dispose();
    }

    public Task OpenAsync(FindResultRow row) => row.IsHeader ? Task.CompletedTask : _open(row.Path, new Position(row.Line - 1, row.Column));

    // ------------------------------------------------------------------ Find in Files

    public async Task SearchAsync()
    {
        _search?.Cancel();
        _search?.Dispose();
        var cts = _search = new CancellationTokenSource();
        _currentSet = null;
        OnPropertyChanged(nameof(CurrentSet));
        OnPropertyChanged(nameof(IsReferences));
        Rows.Clear();
        if (_query.Length == 0)
        {
            Summary = string.Empty;
            return;
        }

        Regex regex;
        try
        {
            var pattern = _useRegex ? _query : Regex.Escape(_query);
            if (_wholeWord)
            {
                pattern = $@"\b(?:{pattern})\b";
            }

            regex = new Regex(pattern, (_matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            Summary = "Invalid regular expression: " + ex.Message;
            return;
        }

        var patterns = _include.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Glob).ToList();
        var files = _files().Where(f => patterns.Count == 0 || patterns.Any(p => p.IsMatch(System.IO.Path.GetFileName(f)))).ToList();
        var openTexts = files.Select(f => (f, _openText(f))).Where(x => x.Item2 is not null).ToDictionary(x => x.f, x => x.Item2!, DaedalusStudio.Workbench.Projects.SolutionService.PathComparer);
        IsSearching = true;
        Summary = $"Searching {files.Count} files…";
        var matches = 0;
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
                        foreach (Match match in regex.Matches(line))
                        {
                            fileRows.Add(new FindResultRow(file, lineNumber, match.Index, match.Length, line.TrimEnd('\r').Trim(), false));
                            if (++matches >= MaxMatches)
                            {
                                break;
                            }
                        }

                        if (matches >= MaxMatches)
                        {
                            break;
                        }
                    }

                    if (fileRows.Count > 0)
                    {
                        matchedFiles++;
                        batch.Add(Header(file, fileRows.Count));
                        batch.AddRange(fileRows);
                    }

                    if (batch.Count >= 200 || matches >= MaxMatches)
                    {
                        await PostAsync(batch, cts.Token).ConfigureAwait(false);
                        batch = [];
                    }

                    if (matches >= MaxMatches)
                    {
                        break;
                    }
                }

                await PostAsync(batch, cts.Token).ConfigureAwait(false);
            }, cts.Token).ConfigureAwait(true);
            Summary = matches == 0
                ? $"No results for \"{_query}\" in {files.Count} files."
                : $"{matches}{(matches >= MaxMatches ? "+" : string.Empty)} matches in {matchedFiles} files for \"{_query}\".";
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
        _currentSet = null;
        OnPropertyChanged(nameof(CurrentSet));
        OnPropertyChanged(nameof(IsReferences));
        Rows.Clear();
        var byFile = locations
            .Select(l => (Path: DocumentUris.ToPath(l.Uri), Location: l))
            .Where(x => x.Path is not null)
            .GroupBy(x => x.Path!, DaedalusStudio.Workbench.Projects.SolutionService.PathComparer)
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

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>Bindings for the Find Results view.</summary>
public static class FindResultsStyles
{
    public static Avalonia.Data.Converters.IValueConverter Weight { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.FontWeight>(header => header ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);
}
