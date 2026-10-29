using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A row of the References window: a group header (a project, a definition or a file, with its count), or a reference
/// with its columns (code, file, line, column, project, containing member and type, kind).
/// </summary>
public sealed record ReferenceRow(string Path, int Line, int Column, string Text, bool IsHeader)
{
    /// <summary>A reference: read, write, declaration or reference (<see cref="ReferenceItemKinds"/>).</summary>
    public string? Kind { get; init; }

    public string? Project { get; init; }

    public string? ContainingMember { get; init; }

    public string? ContainingType { get; init; }

    /// <summary>The definition the reference is to (a Definition group's header).</summary>
    public string? Definition { get; init; }

    public int? DefinitionKind { get; init; }

    /// <summary>The positions in <see cref="Text"/> of the symbol's name, drawn highlighted.</summary>
    public IReadOnlyList<int>? Highlights { get; init; }

    /// <summary>How many characters the reference's name takes (the selection when it's opened).</summary>
    public int Length { get; init; }

    /// <summary>How deep in the groups the row sits (0: the outermost).</summary>
    public int Depth { get; init; }

    /// <summary>A header: the key its collapsed state is kept under.</summary>
    public string? GroupKey { get; init; }

    public bool IsExpanded { get; init; } = true;

    /// <summary>A header: how many references its group holds.</summary>
    public int Count { get; init; }

    /// <summary>A header: its icon (the project's, the definition's kind, the file's type).</summary>
    public string? HeaderIcon { get; init; }

    /// <summary>A header of a Definition group (its icon is the definition's kind).</summary>
    public bool IsDefinition { get; init; }

    /// <summary>A reference: its index in the window's references (before filters and groups); -1 for headers.</summary>
    public int Source { get; init; } = -1;

    public string? Chevron => GroupKey is null ? null : IsExpanded ? "chevron-down" : "chevron-right";

    public bool IsCollapsible => GroupKey is not null;

    /// <summary>The Code column of a header: its label and count, <c>App (3)</c>.</summary>
    public string Label => IsHeader ? $"{Text} ({Count.ToString(CultureInfo.InvariantCulture)})" : Text;

    /// <summary>A header takes the whole row; a reference's code only its column.</summary>
    public int Span => IsHeader ? ReferencesViewModel.ColumnCount : 1;

    /// <summary>A header by its depth (chevron, icon, text); a reference under its group's text.</summary>
    public Avalonia.Thickness Indent => new(IsHeader ? 4 + (Depth * 18) : Depth == 0 ? 8 : 40 + ((Depth - 1) * 18), 0, 0, 0);

    public string FileName => IsHeader ? string.Empty : System.IO.Path.GetFileName(Path);

    public string LineLabel => IsHeader ? string.Empty : Line.ToString(CultureInfo.InvariantCulture);

    public string ColumnLabel => IsHeader ? string.Empty : (Column + 1).ToString(CultureInfo.InvariantCulture);

    public string KindLabel => Kind is null ? string.Empty : KindTitle(Kind);

    public string AccessibleName => IsHeader ? $"{Label}, {(IsExpanded ? "expanded" : "collapsed")}"
        : $"{KindLabel}, {FileName} line {Line}: {Text}";

    /// <summary>The Kind column: Read, Write, Declaration or Reference.</summary>
    public static string KindTitle(string kind) => kind switch
    {
        ReferenceItemKinds.Declaration => Localizer.Get("references.kindDeclaration", "Declaration"),
        ReferenceItemKinds.Write => Localizer.Get("references.kindWrite", "Write"),
        ReferenceItemKinds.Read => Localizer.Get("references.kindRead", "Read"),
        _ => Localizer.Get("references.kindReference", "Reference"),
    };
}

/// <summary>How the References window groups its results (Group by), the classic IDE's choices.</summary>
public static class ReferencesGrouping
{
    public const string ProjectThenDefinition = "Project then Definition";
    public const string DefinitionOnly = "Definition only";
    public const string DefinitionThenProject = "Definition then Project";
    public const string DefinitionThenPath = "Definition then Path";
    public const string ProjectThenDefinitionThenPath = "Project, Definition then Path";

    public static IReadOnlyList<string> All { get; } = [ProjectThenDefinition, DefinitionOnly, DefinitionThenProject, DefinitionThenPath, ProjectThenDefinitionThenPath];

    /// <summary>The groupings' values in the <c>references.groupBy</c> setting, in the order of <see cref="All"/>.</summary>
    public static IReadOnlyList<string> Ids { get; } = ["projectThenDefinition", "definitionOnly", "definitionThenProject", "definitionThenPath", "projectThenDefinitionThenPath"];

    public static string FromId(string? id) => Array.IndexOf(Ids.ToArray(), id) is var i and >= 0 ? All[i] : ProjectThenDefinition;

    public static string ToId(string grouping) => Ids[Math.Max(0, Array.IndexOf(All.ToArray(), grouping))];

    /// <summary>The levels of a grouping, outermost first.</summary>
    internal static IReadOnlyList<ReferenceColumn> Levels(string grouping) => grouping switch
    {
        DefinitionOnly => [ReferenceColumn.Definition],
        DefinitionThenProject => [ReferenceColumn.Definition, ReferenceColumn.Project],
        DefinitionThenPath => [ReferenceColumn.Definition, ReferenceColumn.File],
        ProjectThenDefinitionThenPath => [ReferenceColumn.Project, ReferenceColumn.Definition, ReferenceColumn.File],
        _ => [ReferenceColumn.Project, ReferenceColumn.Definition],
    };
}

/// <summary>Which results the References window shows (its scope box): the search itself covers the whole solution.</summary>
public static class ReferencesScope
{
    public const string EntireSolution = "Entire solution";
    public const string CurrentProject = "Current project";
    public const string CurrentDocument = "Current document";
    public const string OpenDocuments = "Open documents";

    public static IReadOnlyList<string> All { get; } = [EntireSolution, CurrentProject, CurrentDocument, OpenDocuments];
}

/// <summary>The References window's columns, in order (the ones a filter applies to have a filter button).</summary>
public enum ReferenceColumn
{
    Code,
    File,
    Line,
    Column,
    Project,
    ContainingMember,
    ContainingType,
    Kind,
    Definition,
}

/// <summary>A value in a column's filter list: shown when checked.</summary>
public sealed class ReferenceFilterValue(ReferenceColumnFilter owner, string value, int count) : ObservableObject
{
    private bool _isChecked = true;

    public string Value { get; } = value;

    /// <summary>The value as the list shows it, with how many references have it.</summary>
    public string Display => $"{(Value.Length == 0 ? Localizer.Get("references.empty", "(empty)") : Value)} ({count.ToString(CultureInfo.InvariantCulture)})";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetField(ref _isChecked, value))
            {
                owner.Changed();
            }
        }
    }

    /// <summary>Sets the check without telling the filter (it refreshes once for the whole list).</summary>
    internal void Reset(bool value) => _ = SetField(ref _isChecked, value, nameof(IsChecked));
}

/// <summary>
/// A column's filter (the classic IDE's filter button in the column's header): the column's values, each with a check
/// box; the references whose value is unchecked are hidden.
/// </summary>
public sealed class ReferenceColumnFilter : ObservableObject
{
    private readonly ReferencesViewModel _owner;
    private bool _suspended;

    internal ReferenceColumnFilter(ReferencesViewModel owner, ReferenceColumn column)
    {
        _owner = owner;
        Column = column;
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        ClearCommand = new RelayCommand(() => SetAll(false));
    }

    public ReferenceColumn Column { get; }

    public ObservableCollection<ReferenceFilterValue> Values { get; } = [];

    public RelayCommand SelectAllCommand { get; }

    public RelayCommand ClearCommand { get; }

    /// <summary>Some value is unchecked: the header's filter button shows it is on.</summary>
    public bool IsActive => Values.Any(v => !v.IsChecked);

    /// <summary>The values unchecked: the references with them are hidden.</summary>
    internal HashSet<string> Hidden() => Values.Where(v => !v.IsChecked).Select(v => v.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>Lists the values of the references (keeping what was unchecked).</summary>
    internal void Load(IEnumerable<string> values)
    {
        var hidden = Hidden();
        Values.Clear();
        foreach (var group in values.GroupBy(v => v, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var value = new ReferenceFilterValue(this, group.Key, group.Count());
            value.Reset(!hidden.Contains(group.Key));
            Values.Add(value);
        }

        Notify();
    }

    internal void SetAll(bool value, bool refresh = true)
    {
        _suspended = true;
        foreach (var item in Values)
        {
            item.IsChecked = value;
        }

        _suspended = false;
        Notify();
        if (refresh)
        {
            _owner.Refresh();
        }
    }

    internal void Changed()
    {
        if (!_suspended)
        {
            Notify();
            _owner.Refresh();
        }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsActive));
        _owner.FiltersChanged();
    }
}

/// <summary>
/// One References window (Find All References, M12a; the classic IDE's window since 0.6): the references to a symbol,
/// grouped as Group by says (Project then Definition by default) in a tree whose headers collapse, or flat (List View).
/// The scope box, the column filters and the search box narrow what shows; the search itself covers the solution. With
/// Keep Results on, the next Find All References opens another window.
/// </summary>
public sealed class ReferencesViewModel : ObservableObject
{
    public const string ToolWindowId = "references";

    /// <summary>The setting Group by is kept in (one of <see cref="ReferencesGrouping.Ids"/>).</summary>
    public const string GroupBySetting = "references.groupBy";

    /// <summary>The setting the column widths are kept in: File's to Kind's, comma-separated.</summary>
    public const string ColumnWidthsSetting = "references.columnWidths";

    /// <summary>The window's columns: Code, File, Line, Col, Project, Containing member, Containing type, Kind.</summary>
    public const int ColumnCount = 8;

    /// <summary>The columns' widths when the user hasn't changed them (Code fills what the others leave).</summary>
    public static IReadOnlyList<double> DefaultWidths { get; } = [0, 140, 48, 40, 140, 180, 140, 80];

    private readonly List<ReferenceRow> _references = [];
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private string _symbol = string.Empty;
    private string _groupBy = ReferencesGrouping.ProjectThenDefinition;
    private string _scope = ReferencesScope.EntireSolution;
    private string _filter = string.Empty;
    private bool _isListView;
    private bool _keepResults;
    private ReferenceRow? _selectedRow;
    private IReadOnlyList<double> _widths = DefaultWidths;

    /// <param name="open">Opens a file at a position, selecting that many characters.</param>
    public ReferencesViewModel(string id, Func<string, Position, int, Task> open)
    {
        Id = id;
        Open = open;
        Filters = [.. new[] { ReferenceColumn.File, ReferenceColumn.Project, ReferenceColumn.ContainingMember, ReferenceColumn.ContainingType, ReferenceColumn.Kind }.Select(c => new ReferenceColumnFilter(this, c))];
        NextCommand = new RelayCommand(() => _ = GoToAsync(1));
        PreviousCommand = new RelayCommand(() => _ = GoToAsync(-1));
        ExpandAllCommand = new RelayCommand(() => SetAllExpanded(true));
        CollapseAllCommand = new RelayCommand(() => SetAllExpanded(false));
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => HasFilters);
    }

    /// <summary>The tool window's id: <see cref="ToolWindowId"/>, or <c>references:2</c>… for the windows Keep Results opened.</summary>
    public string Id { get; }

    internal Func<string, Position, int, Task> Open { get; }

    /// <summary>The scope box's documents: the active one, its project, the open ones.</summary>
    public Func<string?>? ActiveDocument { get; set; }

    public Func<string?>? ActiveProject { get; set; }

    public Func<IReadOnlyCollection<string>>? OpenDocuments { get; set; }

    /// <summary>The folder headers' paths are shown below (the solution's folder).</summary>
    public Func<string?>? RootFolder { get; set; }

    /// <summary>The column widths changed (a splitter was let go): they are saved.</summary>
    public Action<IReadOnlyList<double>>? WidthsChanged { get; set; }

    /// <summary>Group by changed: it is saved.</summary>
    public Action<string>? GroupByChanged { get; set; }

    /// <summary>Restores Group by without saving it again.</summary>
    public void LoadGroupBy(string grouping)
    {
        if (SetField(ref _groupBy, ReferencesGrouping.All.Contains(grouping) ? grouping : ReferencesGrouping.ProjectThenDefinition, nameof(GroupBy)))
        {
            Refresh();
        }
    }

    /// <summary>The tab's title changed.</summary>
    public event EventHandler? TitleChanged;

    public static IReadOnlyList<string> GroupByOptions => ReferencesGrouping.All;

    public static IReadOnlyList<string> ScopeOptions => ReferencesScope.All;

    public ObservableCollection<ReferenceRow> Rows { get; } = [];

    /// <summary>The filter buttons of the File, Project, Containing member, Containing type and Kind columns.</summary>
    public IReadOnlyList<ReferenceColumnFilter> Filters { get; }

    public ReferenceColumnFilter FileFilter => Filters[0];

    public ReferenceColumnFilter ProjectFilter => Filters[1];

    public ReferenceColumnFilter MemberFilter => Filters[2];

    public ReferenceColumnFilter TypeFilter => Filters[3];

    public ReferenceColumnFilter KindFilter => Filters[4];

    public RelayCommand NextCommand { get; }

    public RelayCommand PreviousCommand { get; }

    public RelayCommand ExpandAllCommand { get; }

    public RelayCommand CollapseAllCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>The symbol searched for.</summary>
    public string Symbol => _symbol;

    /// <summary>The tab's title: <c>'Circle' references</c>, or References before a search.</summary>
    public string Title => _symbol.Length == 0 ? Localizer.Get("toolWindow.references", "References")
        : Localizer.Format("references.title", "'{0}' references", _symbol);

    /// <summary>The title with the scope, as the classic IDE's caption: <c>'Circle' references - Entire solution</c>.</summary>
    public string Caption => _symbol.Length == 0 ? Title : $"{Title} - {_scope}";

    public bool HasResults => _references.Count > 0;

    /// <summary>How many references there are, and how many show.</summary>
    public string Summary
    {
        get
        {
            var shown = Rows.Count(r => !r.IsHeader);
            return _references.Count == 0 ? string.Empty
                : shown == _references.Count ? Localizer.Format("references.summary", "{0} result(s)", _references.Count)
                : Localizer.Format("references.summaryShown", "{0} of {1} result(s) shown", shown, _references.Count);
        }
    }

    public string GroupBy
    {
        get => _groupBy;
        set
        {
            if (SetField(ref _groupBy, ReferencesGrouping.All.Contains(value) ? value : ReferencesGrouping.ProjectThenDefinition))
            {
                _collapsed.Clear();
                Refresh();
                GroupByChanged?.Invoke(_groupBy);
            }
        }
    }

    public string Scope
    {
        get => _scope;
        set
        {
            if (SetField(ref _scope, ReferencesScope.All.Contains(value) ? value : ReferencesScope.EntireSolution))
            {
                OnPropertyChanged(nameof(Caption));
                Refresh();
            }
        }
    }

    /// <summary>Search: shows the references whose code, file, member or type contains the text.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                FiltersChanged();
                Refresh();
            }
        }
    }

    /// <summary>List View: the references flat, without groups.</summary>
    public bool IsListView
    {
        get => _isListView;
        set
        {
            if (SetField(ref _isListView, value))
            {
                OnPropertyChanged(nameof(CanGroup));
                Refresh();
            }
        }
    }

    public bool CanGroup => !_isListView;

    /// <summary>Keep Results: the next Find All References opens another window and leaves this one as it is.</summary>
    public bool KeepResults
    {
        get => _keepResults;
        set => SetField(ref _keepResults, value);
    }

    public ReferenceRow? SelectedRow
    {
        get => _selectedRow;
        set => SetField(ref _selectedRow, value);
    }

    /// <summary>A column filter or the search is on (Clear All Filters is enabled).</summary>
    public bool HasFilters => _filter.Length > 0 || Filters.Any(f => f.IsActive);

    /// <summary>The columns' widths in pixels (Code's 0: it takes what the others leave); a splitter sets them as it moves.</summary>
    public IReadOnlyList<double> Widths
    {
        get => _widths;
        set
        {
            if (value.Count == ColumnCount)
            {
                SetField(ref _widths, value);
            }
        }
    }

    /// <summary>A splitter was let go: the widths are saved.</summary>
    public void CommitWidths() => WidthsChanged?.Invoke(_widths);

    /// <summary>
    /// Moves the splitter at the right edge of <paramref name="column"/> by <paramref name="delta"/> pixels: Code's
    /// narrows or widens File (Code takes the rest), another's widens or narrows its own column.
    /// </summary>
    public void Resize(int column, double delta)
    {
        const double MinWidth = 24;
        var widths = _widths.ToArray();
        if (column <= 0)
        {
            widths[1] = Math.Max(MinWidth, widths[1] - delta);
        }
        else if (column < widths.Length)
        {
            widths[column] = Math.Max(MinWidth, widths[column] + delta);
        }

        Widths = widths;
    }

    /// <summary>Restores saved widths: File's to Kind's, comma-separated (Code takes the rest); others are ignored.</summary>
    public void LoadWidths(string? saved)
    {
        var parts = (saved ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        var widths = new List<double> { 0 };
        foreach (var part in parts)
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) && width >= 8 && width <= 2000)
            {
                widths.Add(width);
            }
        }

        if (widths.Count == ColumnCount)
        {
            SetField(ref _widths, widths, nameof(Widths));
        }
    }

    /// <summary>The widths as the setting keeps them: File's to Kind's.</summary>
    public static string FormatWidths(IReadOnlyList<double> widths) =>
        string.Join(",", widths.Skip(1).Select(w => Math.Round(w).ToString(CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ results

    /// <summary>Shows classified references (<c>codalus/references</c>) to <paramref name="symbol"/>.</summary>
    public void Show(string symbol, IReadOnlyList<ReferenceItem> items)
    {
        _references.Clear();
        _collapsed.Clear();
        foreach (var item in items)
        {
            var path = DocumentUris.ToPath(item.Location.Uri) ?? DocumentUris.GeneratedPath(item.Location.Uri) ?? item.Location.Uri;
            var range = item.Location.Range;
            var length = range.End.Line == range.Start.Line ? Math.Max(0, range.End.Character - range.Start.Character) : 0;
            IReadOnlyList<int>? highlights = item.TextStart is int start && start >= 0 && length > 0 && start + length <= item.LineText.Length
                ? [.. Enumerable.Range(start, length)]
                : null;
            _references.Add(new ReferenceRow(path, range.Start.Line + 1, range.Start.Character, item.LineText, false)
            {
                Kind = item.Kind,
                Project = item.Project,
                ContainingMember = item.ContainingMember,
                ContainingType = item.ContainingType,
                Definition = item.Definition ?? symbol,
                DefinitionKind = item.DefinitionKind,
                Highlights = highlights,
                Length = length,
                Source = _references.Count,
            });
        }

        SetSymbol(symbol);
    }

    /// <summary>
    /// Shows plain locations (another language's server): their lines read from <paramref name="lineText"/>, all under
    /// the one definition, <paramref name="symbol"/>.
    /// </summary>
    public void ShowLocations(string symbol, IReadOnlyList<Location> locations, Func<string, int, string> lineText, Func<string, string?>? project = null)
    {
        Show(symbol, [.. locations.Select(l => (Location: l, Path: DocumentUris.ToPath(l.Uri)))
            .Where(x => x.Path is not null)
            .Select(x =>
            {
                var raw = lineText(x.Path!, x.Location.Range.Start.Line);
                return new ReferenceItem(x.Location, ReferenceItemKinds.Reference, null, project?.Invoke(x.Path!), raw.Trim())
                {
                    Definition = symbol,
                    TextStart = x.Location.Range.Start.Character - (raw.Length - raw.TrimStart().Length),
                };
            })]);
    }

    private void SetSymbol(string symbol)
    {
        _symbol = symbol;
        OnPropertyChanged(nameof(Symbol));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(HasResults));
        foreach (var filter in Filters)
        {
            filter.Load(_references.Select(r => ColumnValue(r, filter.Column)));
        }

        SelectedRow = null;
        Refresh();
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _references.Clear();
        SetSymbol(string.Empty);
    }

    /// <summary>A column's text for a reference (what its filter lists and the search looks in).</summary>
    public static string ColumnValue(ReferenceRow row, ReferenceColumn column) => column switch
    {
        ReferenceColumn.Code => row.Text,
        ReferenceColumn.File => row.FileName,
        ReferenceColumn.Line => row.LineLabel,
        ReferenceColumn.Column => row.ColumnLabel,
        ReferenceColumn.Project => row.Project ?? string.Empty,
        ReferenceColumn.ContainingMember => row.ContainingMember ?? string.Empty,
        ReferenceColumn.ContainingType => row.ContainingType ?? string.Empty,
        ReferenceColumn.Kind => row.KindLabel,
        ReferenceColumn.Definition => row.Definition ?? string.Empty,
        _ => string.Empty,
    };

    // ------------------------------------------------------------------ the rows shown

    internal void FiltersChanged()
    {
        OnPropertyChanged(nameof(HasFilters));
        ClearFiltersCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Clear All Filters: every column's values checked again, and the search emptied.</summary>
    public void ClearFilters()
    {
        foreach (var filter in Filters)
        {
            filter.SetAll(true, refresh: false);
        }

        _filter = string.Empty;
        OnPropertyChanged(nameof(Filter));
        FiltersChanged();
        Refresh();
    }

    /// <summary>Collapses or expands a header's group.</summary>
    public void Toggle(ReferenceRow header)
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
            SelectedRow = Rows.FirstOrDefault(r => r.GroupKey == key);
        }
    }

    /// <summary>Expands (true) or collapses a header's group; false when it already was.</summary>
    public bool SetExpanded(ReferenceRow header, bool expanded)
    {
        if (header.GroupKey is null || header.IsExpanded == expanded)
        {
            return false;
        }

        Toggle(header);
        return true;
    }

    /// <summary>Expand All and Collapse All: every group, at every level.</summary>
    public void SetAllExpanded(bool expanded)
    {
        _collapsed.Clear();
        if (!expanded)
        {
            // The keys of every group, collapsed or not: arrange with nothing collapsed, then collapse them all.
            foreach (var key in Arrange().Where(r => r.GroupKey is not null).Select(r => r.GroupKey!))
            {
                _collapsed.Add(key);
            }
        }

        Refresh();
    }

    /// <summary>Shows the references through the scope, the filters and the grouping, keeping the selection.</summary>
    internal void Refresh()
    {
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

        SelectedRow = selected is null || Rows.Contains(selected) ? selected
            : selected.IsHeader ? Rows.FirstOrDefault(r => r.GroupKey is not null && r.GroupKey == selected.GroupKey)
            : Rows.FirstOrDefault(r => !r.IsHeader && r.Source == selected.Source);
        OnPropertyChanged(nameof(Summary));
    }

    private List<ReferenceRow> Arrange()
    {
        var scope = ScopeFilter();
        var hidden = Filters.Select(f => (f.Column, Values: f.Hidden())).Where(f => f.Values.Count > 0).ToList();
        var shown = _references.Where(r => scope(r) && hidden.All(f => !f.Values.Contains(ColumnValue(r, f.Column))) && PassesSearch(r))
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Line).ThenBy(r => r.Column)
            .ToList();
        var rows = new List<ReferenceRow>(shown.Count + 16);
        if (_isListView)
        {
            rows.AddRange(shown);
        }
        else
        {
            AddGroups(shown, ReferencesGrouping.Levels(_groupBy), 0, string.Empty);
        }

        return rows;

        void AddGroups(List<ReferenceRow> references, IReadOnlyList<ReferenceColumn> levels, int depth, string parentKey)
        {
            if (depth == levels.Count)
            {
                rows.AddRange(references.Select(r => r with { Depth = depth }));
                return;
            }

            var level = levels[depth];
            var groups = references.GroupBy(r => level == ReferenceColumn.File ? r.Path : ColumnValue(r, level), level == ReferenceColumn.File ? SolutionService.PathComparer : StringComparer.Ordinal);
            foreach (var group in groups.OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var key = $"{parentKey}/{level}:{group.Key}";
                var first = group.First();
                var header = new ReferenceRow(level == ReferenceColumn.File ? group.Key : first.Path, 0, 0, GroupLabel(level, group.Key), true)
                {
                    GroupKey = key,
                    IsExpanded = !_collapsed.Contains(key),
                    Depth = depth,
                    Count = group.Count(),
                    IsDefinition = level == ReferenceColumn.Definition,
                    HeaderIcon = level switch
                    {
                        ReferenceColumn.Project => "project",
                        ReferenceColumn.Definition => first.DefinitionKind is int kind ? SymbolNodeViewModel.IconFor(kind) : "dot",
                        _ => Controls.FileIcons.ForFile(System.IO.Path.GetFileName(group.Key)),
                    },
                };
                rows.Add(header);
                if (header.IsExpanded)
                {
                    AddGroups([.. group], levels, depth + 1, key);
                }
            }
        }
    }

    private string GroupLabel(ReferenceColumn level, string value) => level switch
    {
        ReferenceColumn.Project when value.Length == 0 => Localizer.Get("references.miscellaneousFiles", "Miscellaneous Files"),
        ReferenceColumn.File => Relative(value),
        _ => value,
    };

    /// <summary>A file as a Path header shows it: below the solution's folder when it is in it, its full path otherwise.</summary>
    private string Relative(string path)
    {
        if (RootFolder?.Invoke() is not { Length: > 0 } root)
        {
            return path;
        }

        var relative = System.IO.Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative) ? path : relative;
    }

    private Func<ReferenceRow, bool> ScopeFilter()
    {
        switch (_scope)
        {
            case ReferencesScope.CurrentDocument:
                var document = ActiveDocument?.Invoke();
                return r => document is not null && SolutionService.PathComparer.Equals(r.Path, document);
            case ReferencesScope.CurrentProject:
                var project = ActiveProject?.Invoke();
                return r => project is not null && string.Equals(r.Project, project, StringComparison.Ordinal);
            case ReferencesScope.OpenDocuments:
                var open = new HashSet<string>(OpenDocuments?.Invoke() ?? [], SolutionService.PathComparer);
                return r => open.Contains(r.Path);
            default:
                return _ => true;
        }
    }

    private bool PassesSearch(ReferenceRow row) => _filter.Length == 0
        || new[] { ReferenceColumn.Code, ReferenceColumn.File, ReferenceColumn.Project, ReferenceColumn.ContainingMember, ReferenceColumn.ContainingType, ReferenceColumn.Definition }
            .Any(c => ColumnValue(row, c).Contains(_filter, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ opening, walking, copying

    public Task OpenAsync(ReferenceRow row) => row.IsHeader ? Task.CompletedTask : Open(row.Path, new Position(row.Line - 1, row.Column), row.Length);

    /// <summary>Goes to the next (1) or previous (-1) reference after the selected row, wrapping around, and opens it.</summary>
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
            if (Rows[index] is { IsHeader: false } row)
            {
                SelectedRow = row;
                return OpenAsync(row);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Copy: the selected reference's columns, tab-separated; a header's whole group (its label, then its references,
    /// indented); nothing selected, every row shown.
    /// </summary>
    public string CopyText()
    {
        IEnumerable<ReferenceRow> rows = Rows;
        if (_selectedRow is { } selected && Rows.IndexOf(selected) is var start and >= 0)
        {
            rows = selected.IsHeader
                ? Rows.Skip(start).TakeWhile((r, i) => i == 0 || !(r.IsHeader && r.Depth <= selected.Depth))
                : [selected];
        }

        return string.Join(Environment.NewLine, rows.Select(r => r.IsHeader
            ? new string(' ', r.Depth * 2) + r.Label
            : string.Join('\t', Enum.GetValues<ReferenceColumn>().Where(c => c != ReferenceColumn.Definition).Select(c => ColumnValue(r, c)))));
    }
}
