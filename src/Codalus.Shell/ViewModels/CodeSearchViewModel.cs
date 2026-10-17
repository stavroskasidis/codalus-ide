using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Search;

namespace Codalus.Shell.ViewModels;

/// <summary>A Feature Search entry: a command, a setting or an options page (M47).</summary>
public sealed record FeatureSearchItem(FeatureSearchKind Kind, string Title, string Id)
{
    public string? Detail { get; init; }

    /// <summary>A command's key binding.</summary>
    public string? KeyLabel { get; init; }

    public string? Icon { get; init; }
}

public enum FeatureSearchKind
{
    Command,
    Setting,
    OptionsPage,
}

/// <summary>One batch of Code Search results: all so far, ranked; <see cref="Incomplete"/> while projects load or the index builds.</summary>
public sealed record CodeSearchBatch(IReadOnlyList<CodeSearchResult> Results, bool Incomplete);

/// <summary>What Code Search needs from the shell: the searches, the preview, opening a result, running a feature.</summary>
public interface ICodeSearchHost
{
    /// <summary>Results as they come: the index and the files at once, then live symbols, external items and text.</summary>
    IAsyncEnumerable<CodeSearchBatch> SearchCodeAsync(CodeSearchQuery query, CodeSearchScope scope, bool includeExternal, CancellationToken cancellationToken);

    /// <summary>Commands, settings and options pages matching <paramref name="query"/>, best first.</summary>
    IReadOnlyList<FeatureSearchItem> SearchFeatures(string query);

    /// <summary>Shows a result in the preview, or nothing.</summary>
    void ShowPreview(CodeSearchResult? result);

    /// <summary>Opens a result's document at it.</summary>
    Task OpenAsync(CodeSearchResult result);

    /// <summary>Ctrl+S in the preview: saves the previewed document.</summary>
    Task SavePreviewAsync();

    /// <summary>Runs a command, opens a setting or an options page.</summary>
    void RunFeature(FeatureSearchItem item);

    /// <summary>The window closed (or docked away): the preview lets go of its document; the state is saved.</summary>
    void OnClosed();

    /// <summary>The scope, external items or the window's state changed (saved with the settings or the shell's state).</summary>
    void OnOptionsChanged();

    /// <summary>The window moves between floating and docked as a tool window.</summary>
    void SetDocked(bool docked);
}

/// <summary>A row: a group's header, a code result or a feature.</summary>
public sealed class CodeSearchRow
{
    private CodeSearchRow(string name)
    {
        Name = name;
    }

    public static CodeSearchRow ForHeader(string header) => new(header) { IsHeader = true };

    public static CodeSearchRow ForResult(CodeSearchResult result) => new(result.Kind == CodeSearchKind.Text ? result.LineText ?? result.Name : result.Name)
    {
        Result = result,
        Highlights = result.Highlights,
        Icon = IconOf(result),
        Detail = DetailOf(result),
        FileLabel = FileLabelOf(result),
    };

    public static CodeSearchRow ForFeature(FeatureSearchItem item, IReadOnlyList<int> highlights) => new(item.Title)
    {
        Feature = item,
        Highlights = highlights,
        Icon = item.Icon,
        Detail = item.Detail,
        KeyLabel = item.KeyLabel,
    };

    public CodeSearchResult? Result { get; private init; }

    public FeatureSearchItem? Feature { get; private init; }

    public bool IsHeader { get; private init; }

    public bool IsItem => !IsHeader;

    /// <summary>The first line: the name with the matched characters in bold (a text result's line).</summary>
    public string Name { get; }

    public IReadOnlyList<int> Highlights { get; private init; } = [];

    /// <summary>The second line: where it is ("in Circle (project Lib)", a file's folder).</summary>
    public string? Detail { get; private init; }

    /// <summary>The file name on the right.</summary>
    public string? FileLabel { get; private init; }

    public string? KeyLabel { get; private init; }

    public string? Icon { get; private init; }

    public bool HasIcon => Icon is not null;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool HasFileLabel => !string.IsNullOrEmpty(FileLabel);

    public bool HasKeyLabel => !string.IsNullOrEmpty(KeyLabel);

    /// <summary>What screen readers announce: the name, the kind, where it is and its key binding.</summary>
    public string AccessibleName => IsHeader
        ? Name
        : string.Join(", ", new[] { Name, KindName, Detail, FileLabel, KeyLabel }.Where(s => !string.IsNullOrEmpty(s)));

    private string? KindName => Result?.Kind switch
    {
        CodeSearchKind.File => Localizer.Get("msg.codeSearch.kindFile", "file"),
        CodeSearchKind.Type => Localizer.Get("msg.codeSearch.kindType", "type"),
        CodeSearchKind.Member => Localizer.Get("msg.codeSearch.kindMember", "member"),
        CodeSearchKind.Text => Localizer.Get("msg.codeSearch.kindText", "text"),
        _ => null,
    };

    private static string IconOf(CodeSearchResult result) => result.Kind switch
    {
        CodeSearchKind.File => Controls.FileIcons.ForFile(result.Name),
        CodeSearchKind.Text => "find-in-files",
        CodeSearchKind.Line => "go-to-line",
        _ => SymbolNodeViewModel.IconFor(result.SymbolKind),
    };

    private static string? DetailOf(CodeSearchResult result) => result switch
    {
        { Kind: CodeSearchKind.Line } => result.Container,
        { IsExternal: true } => result.Container is { } external ? Localizer.Format("msg.codeSearch.inContainer", "in {0}", external) : null,
        { Kind: CodeSearchKind.Text } => result.Project is { } project
            ? Localizer.Format("msg.codeSearch.inFileProject", "in {0} (project {1})", System.IO.Path.GetFileName(result.Path) ?? string.Empty, project)
            : Localizer.Format("msg.codeSearch.inContainer", "in {0}", System.IO.Path.GetFileName(result.Path) ?? string.Empty),
        { Container: { } container, Project: { } project } => Localizer.Format("msg.codeSearch.inContainerProject", "in {0} (project {1})", container, project),
        { Container: { } container } => Localizer.Format("msg.codeSearch.inContainer", "in {0}", container),
        { Project: { } project } => Localizer.Format("msg.codeSearch.inProject", "(project {0})", project),
        _ => null,
    };

    private static string? FileLabelOf(CodeSearchResult result) => result.Kind switch
    {
        CodeSearchKind.Text => $"{System.IO.Path.GetFileName(result.Path)}:{result.Line + 1}",
        CodeSearchKind.Type or CodeSearchKind.Member when !result.IsExternal => System.IO.Path.GetFileName(result.Path),
        _ => null,
    };
}

/// <summary>A filter chip under the box: <c>files (f:)</c>, Alt and its letter.</summary>
public sealed class CodeSearchChip(CodeSearchFilter filter, string label, char accessKey, Action<CodeSearchFilter> select) : ObservableObject
{
    private bool _isSelected;

    public CodeSearchFilter Filter { get; } = filter;

    public string Label { get; } = label;

    public char AccessKey { get; } = accessKey;

    public string ToolTip => Localizer.Format("msg.codeSearch.chipTip", "{0} (Alt+{1})", Label, char.ToUpperInvariant(AccessKey));

    /// <summary>What screen readers announce: the chip and whether it is on.</summary>
    public string AccessibleName => IsSelected ? Localizer.Format("msg.codeSearch.chipSelected", "{0}, selected", Label) : Label;

    /// <summary>One chip is always on; choosing another turns this one off.</summary>
    public bool IsSelected => _isSelected;

    public ICommand SelectCommand { get; } = new RelayCommand(() => select(filter));

    internal void Show(bool selected)
    {
        if (_isSelected != selected)
        {
            _isSelected = selected;
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(AccessibleName));
        }
    }
}

/// <summary>A choice of the scope picker.</summary>
public sealed record CodeSearchScopeOption(CodeSearchScope Scope, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Where the floating window was and how it was set up (the shell's state.json).</summary>
public sealed record CodeSearchWindowState
{
    public double Width { get; init; } = 760;

    public double Height { get; init; } = 600;

    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    public bool Pinned { get; init; }

    public bool ShowPreview { get; init; } = true;

    public bool Docked { get; init; }
}

/// <summary>
/// Code Search (M47): Visual Studio's Code Search window in place of Go To All's list. Two tabs, Code Search and
/// Feature Search; the filter chips (and their prefixes); two-line results that arrive in batches (marked incomplete
/// while projects load or the index builds); the scope; and a live, editable preview of the selected result that the
/// shell provides. Searches are debounced and the outdated ones cancelled.
/// </summary>
public sealed class CodeSearchViewModel : ObservableObject, IDisposable
{
    public const int CodeTab = 0;
    public const int FeatureTab = 1;

    private readonly ICodeSearchHost _host;
    private CancellationTokenSource? _search;
    private string _query = string.Empty;
    private int _tabIndex;
    private CodeSearchFilter _filter;
    private CodeSearchScopeOption _scope;
    private bool _includeExternal;
    private bool _grouped;
    private bool _isOpen;
    private bool _isIncomplete;
    private bool _isSearching;
    private string _status = string.Empty;
    private int _selectedIndex = -1;
    private bool _showPreview = true;
    private bool _isPinned;
    private bool _isDocked;
    private object? _previewContent;
    private string? _previewStatus;
    private string? _previewTitle;
    private double _width = 760;
    private double _height = 600;
    private double _offsetX;
    private double _offsetY;

    public CodeSearchViewModel(ICodeSearchHost host)
    {
        _host = host;
        Chips =
        [
            new(CodeSearchFilter.All, Localizer.Get("msg.codeSearch.chipAll", "all"), 'a', SelectFilter),
            new(CodeSearchFilter.Files, Localizer.Get("msg.codeSearch.chipFiles", "files (f:)"), 'f', SelectFilter),
            new(CodeSearchFilter.Types, Localizer.Get("msg.codeSearch.chipTypes", "types (t:)"), 't', SelectFilter),
            new(CodeSearchFilter.Members, Localizer.Get("msg.codeSearch.chipMembers", "members (m:)"), 'm', SelectFilter),
            new(CodeSearchFilter.Text, Localizer.Get("msg.codeSearch.chipText", "text (x:)"), 'x', SelectFilter),
        ];
        Chips[0].Show(true);
        Scopes =
        [
            new(CodeSearchScope.EntireSolution, Localizer.Get("msg.codeSearch.scopeSolution", "Entire solution")),
            new(CodeSearchScope.CurrentProject, Localizer.Get("msg.codeSearch.scopeProject", "Current project")),
            new(CodeSearchScope.CurrentDocument, Localizer.Get("msg.codeSearch.scopeDocument", "Current document")),
        ];
        _scope = Scopes[0];
        CloseCommand = new RelayCommand(Close);
        ToggleDockCommand = new RelayCommand(() => _host.SetDocked(!IsDocked));
        CodeTabCommand = new RelayCommand(() => TabIndex = CodeTab);
        FeatureTabCommand = new RelayCommand(() => TabIndex = FeatureTab);
    }

    public ObservableCollection<CodeSearchRow> Rows { get; } = [];

    public IReadOnlyList<CodeSearchChip> Chips { get; }

    public IReadOnlyList<CodeSearchScopeOption> Scopes { get; }

    public ICommand CloseCommand { get; }

    public ICommand ToggleDockCommand { get; }

    public ICommand CodeTabCommand { get; }

    public ICommand FeatureTabCommand { get; }

    /// <summary>Raised when the box should take the focus (opened, a tab or chip chosen).</summary>
    public event EventHandler? FocusRequested;

    /// <summary>Raised when the preview's content changed (the views host it).</summary>
    public event EventHandler? PreviewChanged;

    public event EventHandler? Opened;

    public event EventHandler? Closed;

    public string Query
    {
        get => _query;
        set
        {
            if (SetField(ref _query, value ?? string.Empty))
            {
                Search();
            }
        }
    }

    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (SetField(ref _tabIndex, Math.Clamp(value, CodeTab, FeatureTab)))
            {
                OnPropertyChanged(nameof(IsCodeTab));
                OnPropertyChanged(nameof(IsFeatureTab));
                OnPropertyChanged(nameof(Placeholder));
                OnPropertyChanged(nameof(HasPreview));
                Search();
                FocusRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsCodeTab => _tabIndex == CodeTab;

    public bool IsFeatureTab => _tabIndex == FeatureTab;

    public string Placeholder => IsCodeTab
        ? Localizer.Get("msg.codeSearch.placeholderCode", "Search files, types, members and text (f: t: m: x:, : for a line)")
        : Localizer.Get("msg.codeSearch.placeholderFeature", "Search commands, settings and options pages");

    /// <summary>The selected chip's filter (a typed prefix overrides it for that query).</summary>
    public CodeSearchFilter Filter
    {
        get => _filter;
        set => SelectFilter(value);
    }

    public CodeSearchScopeOption SelectedScope
    {
        get => _scope;
        set
        {
            if (value is not null && SetField(ref _scope, value))
            {
                _host.OnOptionsChanged();
                Search();
            }
        }
    }

    public CodeSearchScope Scope
    {
        get => _scope.Scope;
        set => SelectedScope = Scopes.First(s => s.Scope == value);
    }

    /// <summary>Types of the referenced assemblies, in decompiled documents ("include external items").</summary>
    public bool IncludeExternal
    {
        get => _includeExternal;
        set
        {
            if (SetField(ref _includeExternal, value))
            {
                _host.OnOptionsChanged();
                Search();
            }
        }
    }

    /// <summary>Results grouped by kind under headers (else interleaved by rank, Visual Studio's default).</summary>
    public bool Grouped
    {
        get => _grouped;
        set
        {
            if (SetField(ref _grouped, value))
            {
                Search();
            }
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetField(ref _isOpen, value);
    }

    /// <summary>Shown while projects load or the index builds; the results refresh when it completes.</summary>
    public bool IsIncomplete
    {
        get => _isIncomplete;
        private set => SetField(ref _isIncomplete, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetField(ref _isSearching, value);
    }

    /// <summary>The line under the results: their count, nothing found, or what to type.</summary>
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value >= 0 && value < Rows.Count && Rows[value].IsHeader)
            {
                value = NextItem(value, value > _selectedIndex ? 1 : -1);
            }

            if (SetField(ref _selectedIndex, value))
            {
                OnPropertyChanged(nameof(Selected));
                _host.ShowPreview(IsCodeTab && ShowPreview ? Selected?.Result : null);
            }
        }
    }

    public CodeSearchRow? Selected => _selectedIndex >= 0 && _selectedIndex < Rows.Count ? Rows[_selectedIndex] : null;

    public bool IsEmpty => Rows.Count == 0;

    public bool ShowPreview
    {
        get => _showPreview;
        set
        {
            if (SetField(ref _showPreview, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                _host.ShowPreview(value && IsCodeTab ? Selected?.Result : null);
                _host.OnOptionsChanged();
            }
        }
    }

    /// <summary>Whether the preview pane shows: on the Code Search tab with the preview turned on.</summary>
    public bool HasPreview => _showPreview && IsCodeTab;

    /// <summary>Kept open after a result opens (the pin).</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetField(ref _isPinned, value))
            {
                _host.OnOptionsChanged();
            }
        }
    }

    /// <summary>Docked as a tool window instead of floating over the editor.</summary>
    public bool IsDocked
    {
        get => _isDocked;
        set
        {
            if (SetField(ref _isDocked, value))
            {
                OnPropertyChanged(nameof(IsFloatingOpen));
                PreviewChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The floating window shows (open and not docked).</summary>
    public bool IsFloatingOpen => _isOpen && !_isDocked;

    /// <summary>The preview's editor, made by the shell (a view hosts it).</summary>
    public object? PreviewContent
    {
        get => _previewContent;
        set
        {
            if (SetField(ref _previewContent, value))
            {
                OnPropertyChanged(nameof(HasPreviewContent));
                PreviewChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasPreviewContent => _previewContent is not null;

    /// <summary>The preview's status line: line, column and selection.</summary>
    public string? PreviewStatus
    {
        get => _previewStatus;
        set => SetField(ref _previewStatus, value);
    }

    /// <summary>The previewed file's path (and "modified").</summary>
    public string? PreviewTitle
    {
        get => _previewTitle;
        set => SetField(ref _previewTitle, value);
    }

    public double Width
    {
        get => _width;
        set => SetField(ref _width, Math.Max(420, value));
    }

    public double Height
    {
        get => _height;
        set => SetField(ref _height, Math.Max(240, value));
    }

    public double OffsetX
    {
        get => _offsetX;
        set => SetField(ref _offsetX, value);
    }

    public double OffsetY
    {
        get => _offsetY;
        set => SetField(ref _offsetY, value);
    }

    public CodeSearchWindowState CaptureState() => new()
    {
        Width = Width,
        Height = Height,
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        Pinned = IsPinned,
        ShowPreview = ShowPreview,
        Docked = IsDocked,
    };

    public void RestoreState(CodeSearchWindowState? state)
    {
        if (state is null)
        {
            return;
        }

        Width = state.Width;
        Height = state.Height;
        OffsetX = state.OffsetX;
        OffsetY = state.OffsetY;
        _isPinned = state.Pinned;
        _showPreview = state.ShowPreview;
        IsDocked = state.Docked;
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(HasPreview));
    }

    /// <summary>
    /// Opens on a tab, with <paramref name="query"/> (else the last one, selected) and <paramref name="filter"/> as the chip.
    /// </summary>
    public void Open(int tab, string? query = null, CodeSearchFilter? filter = null)
    {
        var wasOpen = IsOpen;
        IsOpen = true;
        OnPropertyChanged(nameof(IsFloatingOpen));
        if (filter is { } chip)
        {
            _filter = chip;
            UpdateChips(CodeSearchQuery.Parse(_query, chip));
        }

        if (tab != _tabIndex)
        {
            _tabIndex = tab;
            OnPropertyChanged(nameof(TabIndex));
            OnPropertyChanged(nameof(IsCodeTab));
            OnPropertyChanged(nameof(IsFeatureTab));
            OnPropertyChanged(nameof(Placeholder));
            OnPropertyChanged(nameof(HasPreview));
        }

        if (query is not null && query != _query)
        {
            _query = query;
            OnPropertyChanged(nameof(Query));
        }

        Search();
        if (!wasOpen)
        {
            Opened?.Invoke(this, EventArgs.Empty);
        }

        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Esc: closes the floating window (a docked one stays, and gives the focus back).</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        _search?.Cancel();
        IsOpen = false;
        OnPropertyChanged(nameof(IsFloatingOpen));
        _host.OnClosed();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs the current query again (the index completed, or changed), keeping the selection.</summary>
    public void Refresh()
    {
        if (IsOpen)
        {
            Search(debounce: false);
        }
    }

    public void Move(int delta)
    {
        if (Rows.Count == 0)
        {
            return;
        }

        var start = _selectedIndex < 0 ? (delta > 0 ? -1 : Rows.Count) : _selectedIndex;
        var target = Math.Clamp(start + delta, 0, Rows.Count - 1);
        if (Rows[target].IsHeader)
        {
            target = NextItem(target, delta > 0 ? 1 : -1);
        }

        SelectedIndex = target;
    }

    public void MoveToEdge(bool last) => Move(last ? Rows.Count : -Rows.Count);

    /// <summary>
    /// Enter (or a double click): opens the selected result and closes the window unless it is pinned or
    /// <paramref name="keepOpen"/> (Ctrl+Enter); a feature runs.
    /// </summary>
    public async Task AcceptAsync(bool keepOpen = false)
    {
        switch (Selected)
        {
            case { Result: { } result }:
                if (!keepOpen && !IsPinned)
                {
                    Close();
                }

                await _host.OpenAsync(result).ConfigureAwait(true);
                break;
            case { Feature: { } feature }:
                if (!IsPinned)
                {
                    Close();
                }

                _host.RunFeature(feature);
                break;
        }
    }

    /// <summary>Ctrl+S in the preview's editor.</summary>
    public Task SavePreviewAsync() => _host.SavePreviewAsync();

    /// <summary>Alt and a chip's letter.</summary>
    public bool SelectChipByKey(char key)
    {
        if (!IsCodeTab || Chips.FirstOrDefault(c => c.AccessKey == char.ToLowerInvariant(key)) is not { } chip)
        {
            return false;
        }

        SelectFilter(chip.Filter);
        FocusRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void SelectFilter(CodeSearchFilter filter)
    {
        // A chip replaces a typed prefix (the chip and the prefix say the same thing).
        var parsed = CodeSearchQuery.Parse(_query, _filter);
        if (parsed is { FromPrefix: true, IsLine: false })
        {
            _query = parsed.Text;
            OnPropertyChanged(nameof(Query));
        }

        _filter = filter;
        OnPropertyChanged(nameof(Filter));
        UpdateChips(CodeSearchQuery.Parse(_query, filter));
        Search(debounce: false);
    }

    private void UpdateChips(CodeSearchQuery query)
    {
        var shown = query.IsLine ? _filter : query.Filter;
        foreach (var chip in Chips)
        {
            chip.Show(chip.Filter == shown);
        }
    }

    private int NextItem(int from, int direction)
    {
        for (var i = from; i >= 0 && i < Rows.Count; i += direction)
        {
            if (Rows[i].IsItem)
            {
                return i;
            }
        }

        for (var i = from; i >= 0 && i < Rows.Count; i -= direction)
        {
            if (Rows[i].IsItem)
            {
                return i;
            }
        }

        return -1;
    }

    // ------------------------------------------------------------------ searching

    private void Search(bool debounce = true)
    {
        if (!IsOpen)
        {
            return;
        }

        _search?.Cancel();
        _search?.Dispose();
        var cts = _search = new CancellationTokenSource();
        _ = SearchAsync(debounce, cts.Token);
    }

    private async Task SearchAsync(bool debounce, CancellationToken cancellationToken)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(true);
            }

            if (IsFeatureTab)
            {
                ShowFeatures(_host.SearchFeatures(_query.Trim()));
                return;
            }

            var query = CodeSearchQuery.Parse(_query, _filter);
            UpdateChips(query);
            if (query.IsEmpty || query.IsLine && query.Line == 0)
            {
                Apply([], incomplete: false, query);
                return;
            }

            IsSearching = true;
            await foreach (var batch in _host.SearchCodeAsync(query, Scope, IncludeExternal, cancellationToken).ConfigureAwait(true))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Apply(batch.Results, batch.Incomplete, query);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                IsSearching = false;
                Status = StatusFor(query);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowFeatures(IReadOnlyList<FeatureSearchItem> items)
    {
        var query = _query.Trim();
        Rows.Clear();
        foreach (var item in items)
        {
            Rows.Add(CodeSearchRow.ForFeature(item, FuzzyMatcher.Match(query, item.Title)?.Positions ?? []));
        }

        IsIncomplete = false;
        IsSearching = false;
        Status = query.Length == 0
            ? Localizer.Get("msg.codeSearch.featureHint", "Type to search commands, settings and options pages.")
            : Rows.Count == 0 ? Localizer.Format("msg.codeSearch.noFeatures", "No commands, settings or options pages match '{0}'.", query) : string.Empty;
        OnPropertyChanged(nameof(IsEmpty));
        _selectedIndex = -1;
        SelectedIndex = Rows.Count == 0 ? -1 : 0;
    }

    private void Apply(IReadOnlyList<CodeSearchResult> results, bool incomplete, CodeSearchQuery query)
    {
        var keep = Selected?.Result?.Key;
        Rows.Clear();
        if (_grouped && !query.IsLine)
        {
            foreach (var group in results.GroupBy(r => r.Kind).OrderBy(g => g.Key switch
            {
                CodeSearchKind.File => 0,
                CodeSearchKind.Type => 1,
                CodeSearchKind.Member => 2,
                _ => 3,
            }))
            {
                Rows.Add(CodeSearchRow.ForHeader(group.Key switch
                {
                    CodeSearchKind.File => Localizer.Get("msg.codeSearch.groupFiles", "Files"),
                    CodeSearchKind.Type => Localizer.Get("msg.codeSearch.groupTypes", "Types"),
                    CodeSearchKind.Member => Localizer.Get("msg.codeSearch.groupMembers", "Members"),
                    _ => Localizer.Get("msg.codeSearch.groupText", "Text"),
                }));
                foreach (var result in group)
                {
                    Rows.Add(CodeSearchRow.ForResult(result));
                }
            }
        }
        else
        {
            foreach (var result in results)
            {
                Rows.Add(CodeSearchRow.ForResult(result));
            }
        }

        IsIncomplete = incomplete;
        Status = StatusFor(query);
        OnPropertyChanged(nameof(IsEmpty));
        var index = keep is null ? -1 : IndexOf(keep);
        _selectedIndex = -2;
        SelectedIndex = index >= 0 ? index : Rows.Count == 0 ? -1 : NextItem(0, 1);
    }

    private int IndexOf(string key)
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].Result?.Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private string StatusFor(CodeSearchQuery query)
    {
        var count = Rows.Count(r => r.IsItem);
        if (query.IsEmpty || query.IsLine && query.Line == 0)
        {
            return query.IsLine
                ? Localizer.Get("msg.codeSearch.lineHint", "Type a line number (and :column) of the current document.")
                : Localizer.Get("msg.codeSearch.codeHint", "Type to search files, types, members and text. f: t: m: x: filter, : goes to a line.");
        }

        if (count == 0)
        {
            return IsSearching
                ? Localizer.Get("msg.codeSearch.searching", "Searching…")
                : Localizer.Format("msg.codeSearch.noResults", "No results for '{0}'.", query.Text);
        }

        return count == 1 ? Localizer.Get("msg.codeSearch.oneResult", "1 result") : Localizer.Format("msg.codeSearch.results", "{0} results", count);
    }

    public void Dispose()
    {
        _search?.Cancel();
        _search?.Dispose();
    }
}
