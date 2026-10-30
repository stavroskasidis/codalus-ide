using Avalonia.Threading;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Build;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.ViewModels;

/// <summary>One diagnostic in the Error List. <see cref="Source"/> is <c>Build</c>, <c>IntelliSense</c> or both.</summary>
public sealed record ErrorListRow(string Icon, string Code, string Description, string Project, string File, int Line, int Column, string Path, Position Position)
{
    public string Source { get; init; } = ErrorListViewModel.IntelliSenseSource;

    /// <summary>"error CS0103: …, Program.cs line 4"; a diagnostic without a code (a project that can't build, M43a) reads "error: …".</summary>
    public string AccessibleName => $"{Icon}{(Code.Length > 0 ? " " + Code : string.Empty)}: {Description}, {File} line {Line}";

    public string Location => $"{File}({Line},{Column})";

    public bool IsError => Icon == "error";

    public bool IsWarning => Icon == "warning";
}

/// <summary>A group's header row in the Error List: its title and count; a click or Enter collapses or expands it.</summary>
public sealed class ErrorListGroup(string key, string title, int count, bool isExpanded) : ObservableObject
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    public int Count { get; } = count;

    public bool IsExpanded { get; } = isExpanded;

    public string Chevron => IsExpanded ? "chevron-down" : "chevron-right";

    public string Header => $"{Title} ({Count})";

    public string AccessibleName => $"{Title}, {Count} {(IsExpanded ? Localizer.Get("errorList.expanded", "expanded") : Localizer.Get("errorList.collapsed", "collapsed"))}";
}

/// <summary>
/// The Error List: every diagnostic the language services published and the errors and warnings of the last build
/// (ADR 0006), filterable by source, severity, the active document and text, and grouped by project, file, severity,
/// code or source (the <c>errorList.groupBy</c> setting; groups collapse). A build diagnostic that a language service
/// also reports is shown once. Double-click or Enter opens the location. The analysis scope picker is the
/// <c>languageServices.analysisScope</c> setting: where analyzers run in the background.
/// </summary>
public sealed class ErrorListViewModel : ObservableObject
{
    public const string ToolWindowId = "errorList";
    public const string IntelliSenseSource = "IntelliSense";
    public const string BuildSource = "Build";
    public const string BothSources = "Build + IntelliSense";

    /// <summary>Rude edits and compile errors of Hot Reload while debugging (M55).</summary>
    public const string HotReloadSource = "Hot Reload";

    private IReadOnlyList<ErrorListRow> _hotReload = [];

    /// <summary>
    /// Shows Hot Reload's rude edits or compile errors (M55: an edit that couldn't be applied while debugging), replacing
    /// the last ones; empty clears them.
    /// </summary>
    public void SetHotReload(IReadOnlyList<ErrorListRow> rows)
    {
        _hotReload = [.. rows.Select(r => r with { Source = HotReloadSource })];
        ScheduleRefresh();
    }
    public const string GroupBySetting = "errorList.groupBy";
    public const string AnalysisScopeSetting = "languageServices.analysisScope";

    /// <summary>The <see cref="GroupBySetting"/> values, in the picker's order.</summary>
    public static readonly string[] GroupByValues = ["none", "project", "file", "severity", "code", "source"];

    /// <summary>The <see cref="AnalysisScopeSetting"/> values, in the picker's order.</summary>
    public static readonly string[] AnalysisScopeValues = ["openDocuments", "currentProject", "solution"];

    private readonly DiagnosticsStore _store;
    private readonly BuildService _builds;
    private readonly SolutionService _solutions;
    private readonly Func<string?> _activePath;
    private readonly Func<string, Position, Task> _open;
    private readonly SettingsService? _settings;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private bool _showErrors = true;
    private bool _showWarnings = true;
    private bool _showMessages = true;
    private bool _currentDocumentOnly;
    private int _sourceFilter;
    private string _filter = string.Empty;
    private int _refreshScheduled;
    private bool _computing;
    private bool _computeAgain;
    private int _generation;
    private int _errors;
    private int _warnings;
    private int _messages;

    /// <summary>Fix with AI for a row (M51): opens it, then fixes the diagnostic at the caret through the preview.</summary>
    public Func<ErrorListRow, Task>? FixWithAi { get; set; }

    /// <summary>Explain with AI for a row (M51): asks AI Chat about it with the code around it.</summary>
    public Func<ErrorListRow, Task>? ExplainWithAi { get; set; }

    /// <summary>Whether AI assistance is on, so the AI items show in a row's menu.</summary>
    public Func<bool> CanUseAi { get; set; } = () => false;

    public ErrorListViewModel(DiagnosticsStore store, BuildService builds, SolutionService solutions, Func<string?> activePath, Func<string, Position, Task> open, SettingsService? settings = null)
    {
        _store = store;
        _builds = builds;
        _solutions = solutions;
        _activePath = activePath;
        _open = open;
        _settings = settings;
        store.Changed += (_, _) => ScheduleRefresh();
        builds.DiagnosticsChanged += (_, _) => ScheduleRefresh();
        if (settings is not null)
        {
            settings.Changed += (_, e) =>
            {
                if (e.Affects(GroupBySetting))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _collapsed.Clear();
                        OnPropertyChanged(nameof(GroupBy));
                        Refresh();
                    });
                }

                if (e.Affects(AnalysisScopeSetting))
                {
                    Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(AnalysisScope)));
                }
            };
        }

        Refresh();
    }

    private string _groupBy = "none";

    /// <summary>The index in <see cref="GroupByValues"/> of the grouping: none, project, file, severity, code or source.</summary>
    public int GroupBy
    {
        get => Math.Max(0, Array.IndexOf(GroupByValues, _settings?.GetString(GroupBySetting, "none") ?? _groupBy));
        set
        {
            var key = GroupByValues[Math.Clamp(value, 0, GroupByValues.Length - 1)];
            if (key == GroupByValues[GroupBy])
            {
                return;
            }

            if (_settings is null)
            {
                _groupBy = key;
                _collapsed.Clear();
                OnPropertyChanged();
                Refresh();
            }
            else
            {
                _ = _settings.SetAsync(GroupBySetting, key, SettingScope.User, CancellationToken.None);
            }
        }
    }

    /// <summary>The index in <see cref="AnalysisScopeValues"/> of where analyzers run in the background.</summary>
    public int AnalysisScope
    {
        get => Math.Max(0, Array.IndexOf(AnalysisScopeValues, _settings?.GetString(AnalysisScopeSetting, "openDocuments") ?? "openDocuments"));
        set
        {
            var key = AnalysisScopeValues[Math.Clamp(value, 0, AnalysisScopeValues.Length - 1)];
            if (_settings is not null && key != AnalysisScopeValues[AnalysisScope])
            {
                _ = _settings.SetAsync(AnalysisScopeSetting, key, SettingScope.User, CancellationToken.None);
            }
        }
    }

    /// <summary>What the list shows: the <see cref="Rows"/>, under an <see cref="ErrorListGroup"/> header per group when grouped.</summary>
    public ResettableCollection<object> Items { get; } = [];

    /// <summary>
    /// Collapses or expands a group: the rows shown are grouped again, already sorted and filtered, without being built
    /// again from the diagnostics (M56: with 100,000 errors each toggle sorted them all on the UI thread).
    /// </summary>
    public void ToggleGroup(ErrorListGroup group)
    {
        if (!_collapsed.Remove(group.Key))
        {
            _collapsed.Add(group.Key);
        }

        // Rows being built for the old collapse state are built again for this one.
        _generation++;
        _computeAgain |= _computing;
        Items.Reset(GroupItems(Rows, GroupByValues[GroupBy], _collapsed));
    }

    /// <summary>0: Build + IntelliSense, 1: Build only, 2: IntelliSense only.</summary>
    public int SourceFilter
    {
        get => _sourceFilter;
        set
        {
            if (SetField(ref _sourceFilter, Math.Clamp(value, 0, 2)))
            {
                Refresh();
            }
        }
    }

    public ResettableCollection<ErrorListRow> Rows { get; } = [];

    public bool ShowErrors
    {
        get => _showErrors;
        set
        {
            if (SetField(ref _showErrors, value))
            {
                Refresh();
            }
        }
    }

    public bool ShowWarnings
    {
        get => _showWarnings;
        set
        {
            if (SetField(ref _showWarnings, value))
            {
                Refresh();
            }
        }
    }

    public bool ShowMessages
    {
        get => _showMessages;
        set
        {
            if (SetField(ref _showMessages, value))
            {
                Refresh();
            }
        }
    }

    public bool CurrentDocumentOnly
    {
        get => _currentDocumentOnly;
        set
        {
            if (SetField(ref _currentDocumentOnly, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>The search box: the rows are built again once typing pauses, off the UI thread.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                (_filterRefresh ??= new UiDebouncer(Refresh, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(600))).Request();
            }
        }
    }

    private UiDebouncer? _filterRefresh;

    /// <summary>Whether the rows shown are being built again (tests).</summary>
    internal bool IsRefreshing => _computing || _filterRefresh?.IsPending == true;

    public string ErrorsLabel => _errors == 1 ? "1 Error" : $"{_errors} Errors";

    public string WarningsLabel => _warnings == 1 ? "1 Warning" : $"{_warnings} Warnings";

    public string MessagesLabel => _messages == 1 ? "1 Message" : $"{_messages} Messages";

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The active document changed (for <see cref="CurrentDocumentOnly"/>).</summary>
    public void OnActiveDocumentChanged()
    {
        if (_currentDocumentOnly)
        {
            Refresh();
        }
    }

    /// <summary>
    /// A change of diagnostics (published on any thread, a file at a time): one refresh for a burst, its rows built on
    /// the thread pool. A solution whose packages didn't restore has hundreds of thousands of errors, which froze the
    /// window when they were built on the UI thread.
    /// </summary>
    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshScheduled, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _refreshScheduled, 0);
            _ = RefreshInBackgroundAsync();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Builds the rows on the thread pool and shows them, unless a <see cref="Refresh"/> came meanwhile. Changes during a
    /// build start one more after it, so a stream of them still shows something.
    /// </summary>
    private async Task RefreshInBackgroundAsync()
    {
        if (_computing)
        {
            _computeAgain = true;
            return;
        }

        _computing = true;
        try
        {
            do
            {
                _computeAgain = false;
                var generation = _generation;
                var query = Query();
                var view = await Task.Run(() => Compute(query, _store, _builds, _hotReload)).ConfigureAwait(true);
                if (generation == _generation)
                {
                    Apply(view);
                }
            }
            while (_computeAgain);
        }
        finally
        {
            _computing = false;
        }
    }

    /// <summary>
    /// Shows the diagnostics again (a filter, a grouping, the scope or the active document changed), built off the UI
    /// thread; rows being built for an older query are dropped. On the UI thread, each toggle, keystroke in the search box
    /// and tab switch (with Current Document) built and sorted every row: up to a second with 100,000 errors (M56).
    /// </summary>
    public void Refresh()
    {
        _generation++;
        _ = RefreshInBackgroundAsync();
    }

    /// <summary>What the list shows, read on the UI thread so that <see cref="Compute"/> can run on any.</summary>
    private sealed record ListQuery(
        string? ActivePath,
        IReadOnlyList<ProjectInfo> Projects,
        bool CurrentDocumentOnly,
        int SourceFilter,
        bool ShowErrors,
        bool ShowWarnings,
        bool ShowMessages,
        string Filter,
        string GroupBy,
        IReadOnlySet<string> Collapsed);

    private sealed record ListView(List<ErrorListRow> Rows, List<object> Items, int Errors, int Warnings, int Messages);

    private ListQuery Query() => new(
        _activePath(),
        [.. _solutions.Projects.Values],
        _currentDocumentOnly,
        _sourceFilter,
        _showErrors,
        _showWarnings,
        _showMessages,
        _filter,
        GroupByValues[GroupBy],
        new HashSet<string>(_collapsed, StringComparer.Ordinal));

    private static ListView Compute(ListQuery query, DiagnosticsStore store, BuildService builds, IReadOnlyList<ErrorListRow> hotReload)
    {
        var projectName = ProjectNames(query.Projects);
        bool InScope(string path) => !query.CurrentDocumentOnly || SolutionService.PathComparer.Equals(path, query.ActivePath);
        var all = new List<ErrorListRow>();
        if (query.SourceFilter != 1)
        {
            foreach (var file in store.All.Where(f => InScope(f.Path)))
            {
                // Once per file, not per diagnostic: a file can have thousands.
                var project = projectName(file.Path);
                var name = Path.GetFileName(file.Path);
                foreach (var d in file.Items.Where(d => d.Severity != DiagnosticSeverity.Hint))
                {
                    all.Add(new ErrorListRow(
                        d.Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "info" },
                        d.Code ?? string.Empty,
                        d.Message,
                        project,
                        name,
                        d.Range.Start.Line + 1,
                        d.Range.Start.Character + 1,
                        file.Path,
                        d.Range.Start));
                }
            }
        }

        if (query.SourceFilter != 2 && builds.Diagnostics.Select(FromBuild).Where(r => InScope(r.Path)).ToList() is { Count: > 0 } fromBuild)
        {
            // A build diagnostic that a language service also reports marks that row (the first of its kind) instead.
            var keys = fromBuild.Select(Key).ToHashSet(RowKeyComparer.Instance);
            var seen = new Dictionary<(string Path, int Line, string Code), int>(RowKeyComparer.Instance);
            for (var i = 0; i < all.Count; i++)
            {
                if (keys.Contains(Key(all[i])))
                {
                    seen.TryAdd(Key(all[i]), i);
                }
            }

            foreach (var row in fromBuild)
            {
                if (seen.TryGetValue(Key(row), out var index))
                {
                    all[index] = all[index] with { Source = BothSources };
                }
                else
                {
                    all.Add(row);
                }
            }
        }

        if (query.SourceFilter != 2)
        {
            all.AddRange(hotReload.Where(r => InScope(r.Path)));
        }

        var errors = all.Count(r => r.IsError);
        var warnings = all.Count(r => r.IsWarning);
        var filter = query.Filter;
        var rows = all
            .Where(r => r.Icon switch
            {
                "error" => query.ShowErrors,
                "warning" => query.ShowWarnings,
                _ => query.ShowMessages,
            })
            .Where(r => filter.Length == 0
                        || r.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.Code.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.File.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.Project.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Icon switch { "error" => 0, "warning" => 1, _ => 2 })
            .ThenBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Line)
            .ToList();

        return new ListView(rows, GroupItems(rows, query.GroupBy, query.Collapsed), errors, warnings, all.Count - errors - warnings);
    }

    /// <summary>What the list shows for <paramref name="rows"/> (sorted): the rows, or each group's header and, expanded, its rows.</summary>
    private static List<object> GroupItems(IReadOnlyList<ErrorListRow> rows, string groupBy, IReadOnlySet<string> collapsed)
    {
        var items = new List<object>(rows.Count);
        if (groupBy == "none")
        {
            items.AddRange(rows);
            return items;
        }

        foreach (var (key, title, members) in Groups(rows, groupBy))
        {
            var expanded = !collapsed.Contains(key);
            items.Add(new ErrorListGroup(key, title, members.Count, expanded));
            if (expanded)
            {
                items.AddRange(members);
            }
        }

        return items;
    }

    private void Apply(ListView view)
    {
        // One reset each: an Add per row raised a change per row, hundreds of thousands of them.
        Rows.Reset(view.Rows);
        Items.Reset(view.Items);
        _errors = view.Errors;
        _warnings = view.Warnings;
        _messages = view.Messages;
        OnPropertyChanged(nameof(ErrorsLabel));
        OnPropertyChanged(nameof(WarningsLabel));
        OnPropertyChanged(nameof(MessagesLabel));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// The groups of <paramref name="rows"/> (already sorted) by <paramref name="groupBy"/>: severities in their order,
    /// the rest by name with the rows that have none last; each row keeps its order within its group.
    /// </summary>
    public static IReadOnlyList<(string Key, string Title, IReadOnlyList<ErrorListRow> Rows)> Groups(IReadOnlyList<ErrorListRow> rows, string groupBy)
    {
        string KeyOf(ErrorListRow row) => groupBy switch
        {
            "project" => row.Project,
            "file" => row.Path,
            "severity" => row.Icon,
            "code" => row.Code,
            "source" => row.Source,
            _ => string.Empty,
        };

        string TitleOf(string key, ErrorListRow first) => groupBy switch
        {
            "severity" => key switch
            {
                "error" => Localizer.Get("errorList.errors", "Errors"),
                "warning" => Localizer.Get("errorList.warnings", "Warnings"),
                _ => Localizer.Get("errorList.messages", "Messages"),
            },
            _ when key.Length == 0 => groupBy switch
            {
                "project" => Localizer.Get("errorList.noProject", "(no project)"),
                "file" => Localizer.Get("errorList.noFile", "(no file)"),
                _ => Localizer.Get("errorList.noCode", "(no code)"),
            },
            "file" => $"{first.File} — {Path.GetDirectoryName(key)}",
            _ => key,
        };

        var comparer = groupBy == "file" ? (StringComparer)SolutionService.PathComparer : StringComparer.OrdinalIgnoreCase;
        return
        [
            .. rows.GroupBy(KeyOf, comparer)
                .OrderBy(g => groupBy == "severity" ? g.Key switch { "error" => 0, "warning" => 1, _ => 2 } : g.Key.Length == 0 ? 1 : 0)
                .ThenBy(g => groupBy == "file" ? g.First().File : g.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (groupBy + ":" + g.Key, TitleOf(g.Key, g.First()), (IReadOnlyList<ErrorListRow>)[.. g])),
        ];
    }

    /// <summary>
    /// The project of a file: one that lists it, else the one whose folder holds it (the deepest: a test project's files
    /// weren't matched, M34d's review).
    /// </summary>
    public static ProjectInfo? ProjectOf(IEnumerable<ProjectInfo> projects, string path)
    {
        var list = projects as IReadOnlyCollection<ProjectInfo> ?? [.. projects];
        return list.FirstOrDefault(p => p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path))) ?? ProjectHolding(list, path);
    }

    /// <summary>The project whose folder holds <paramref name="path"/>, the deepest.</summary>
    private static ProjectInfo? ProjectHolding(IEnumerable<ProjectInfo> projects, string path)
    {
        return projects.Where(p => Path.GetDirectoryName(p.Path) is { } folder && IsUnder(path, folder)).MaxBy(p => p.Path.Length);

        static bool IsUnder(string path, string folder) =>
            path.Length > folder.Length && path.StartsWith(folder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
            && path[folder.Length] is '/' or '\\';
    }

    /// <summary>A row as text for the clipboard: code, description, project, file and line, tab-separated.</summary>
    public static string CopyText(ErrorListRow row) =>
        string.Join('\t', row.Code, row.Description, row.Project, row.File, row.Line.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public Task OpenAsync(ErrorListRow row) => row.Path.Length == 0 ? Task.CompletedTask : _open(row.Path, row.Position);

    /// <summary>
    /// The project name of each file, as <see cref="ProjectOf"/> finds it, from one index of the projects' items: looking
    /// through every project's items for each diagnostic took minutes for a large solution with many errors.
    /// </summary>
    internal static Func<string, string> ProjectNames(IReadOnlyList<ProjectInfo> projects)
    {
        var listed = new Dictionary<string, string>(SolutionService.PathComparer);
        foreach (var project in projects)
        {
            foreach (var item in project.Items)
            {
                listed.TryAdd(item.Path, project.Name); // the first project that lists it, as ProjectOf
            }
        }

        var byFolder = new Dictionary<string, string>(SolutionService.PathComparer);
        return path => listed.TryGetValue(path, out var name) ? name
            : byFolder.TryGetValue(path, out name) ? name
            : byFolder[path] = ProjectHolding(projects, path)?.Name ?? string.Empty;
    }

    private static (string Path, int Line, string Code) Key(ErrorListRow row) => (row.Path, row.Line, row.Code);

    /// <summary>Equal locations and codes, the path compared ignoring case (a build may spell it differently).</summary>
    private sealed class RowKeyComparer : IEqualityComparer<(string Path, int Line, string Code)>
    {
        public static readonly RowKeyComparer Instance = new();

        public bool Equals((string Path, int Line, string Code) x, (string Path, int Line, string Code) y) =>
            x.Line == y.Line && string.Equals(x.Code, y.Code, StringComparison.Ordinal) && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Path, int Line, string Code) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path), key.Line, StringComparer.Ordinal.GetHashCode(key.Code));
    }

    /// <summary>A build diagnostic as a row: its file (or the project file when MSBuild gave none), 1-based position.</summary>
    private static ErrorListRow FromBuild(BuildDiagnosticInfo d)
    {
        var path = d.File ?? d.Project ?? string.Empty;
        var project = d.Project is { } p ? Path.GetFileNameWithoutExtension(p) : string.Empty;
        return new ErrorListRow(
            d.Severity == BuildSeverities.Error ? "error" : "warning",
            d.Code ?? string.Empty,
            d.Message,
            project,
            Path.GetFileName(path),
            Math.Max(1, d.Line),
            Math.Max(1, d.Column),
            path,
            new Position(Math.Max(0, d.Line - 1), Math.Max(0, d.Column - 1)))
        {
            Source = BuildSource,
        };
    }
}
