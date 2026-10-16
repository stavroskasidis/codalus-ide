using System.Collections.ObjectModel;
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

    public string AccessibleName => $"{Icon} {Code}: {Description}, {File} line {Line}";

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
    private bool _refreshScheduled;
    private int _errors;
    private int _warnings;
    private int _messages;

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
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>Collapses or expands a group.</summary>
    public void ToggleGroup(ErrorListGroup group)
    {
        if (!_collapsed.Remove(group.Key))
        {
            _collapsed.Add(group.Key);
        }

        Refresh();
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

    public ObservableCollection<ErrorListRow> Rows { get; } = [];

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

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

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

    private void ScheduleRefresh()
    {
        if (_refreshScheduled)
        {
            return;
        }

        _refreshScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshScheduled = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    public void Refresh()
    {
        var active = _activePath();
        var projects = _solutions.Projects.Values.ToList();
        string ProjectName(string path) => ProjectOf(projects, path)?.Name ?? string.Empty;

        bool InScope(string path) => !_currentDocumentOnly || SolutionService.PathComparer.Equals(path, active);
        var intelliSense = _sourceFilter == 1
            ? []
            : _store.All
                .Where(f => InScope(f.Path))
                .SelectMany(f => f.Items.Where(d => d.Severity != DiagnosticSeverity.Hint).Select(d => new ErrorListRow(
                    d.Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "info" },
                    d.Code ?? string.Empty,
                    d.Message,
                    ProjectName(f.Path),
                    Path.GetFileName(f.Path),
                    d.Range.Start.Line + 1,
                    d.Range.Start.Character + 1,
                    f.Path,
                    d.Range.Start)))
                .ToList();
        var all = new List<ErrorListRow>(intelliSense);
        if (_sourceFilter != 2)
        {
            var seen = intelliSense.Select((r, i) => (Key: Key(r), Index: i)).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Index);
            foreach (var row in _builds.Diagnostics.Select(FromBuild).Where(r => InScope(r.Path)))
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

        _errors = all.Count(r => r.IsError);
        _warnings = all.Count(r => r.IsWarning);
        _messages = all.Count - _errors - _warnings;

        var rows = all
            .Where(r => r.Icon switch
            {
                "error" => _showErrors,
                "warning" => _showWarnings,
                _ => _showMessages,
            })
            .Where(r => _filter.Length == 0
                        || r.Description.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                        || r.Code.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                        || r.File.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                        || r.Project.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Icon switch { "error" => 0, "warning" => 1, _ => 2 })
            .ThenBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Line)
            .ToList();

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        Items.Clear();
        var groupBy = GroupByValues[GroupBy];
        if (groupBy == "none")
        {
            foreach (var row in rows)
            {
                Items.Add(row);
            }
        }
        else
        {
            foreach (var (key, title, members) in Groups(rows, groupBy))
            {
                var expanded = !_collapsed.Contains(key);
                Items.Add(new ErrorListGroup(key, title, members.Count, expanded));
                if (expanded)
                {
                    foreach (var row in members)
                    {
                        Items.Add(row);
                    }
                }
            }
        }

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
        return list.FirstOrDefault(p => p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path)))
               ?? list.Where(p => Path.GetDirectoryName(p.Path) is { } folder && IsUnder(path, folder)).MaxBy(p => p.Path.Length);

        static bool IsUnder(string path, string folder) =>
            path.Length > folder.Length && path.StartsWith(folder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
            && path[folder.Length] is '/' or '\\';
    }

    public Task OpenAsync(ErrorListRow row) => row.Path.Length == 0 ? Task.CompletedTask : _open(row.Path, row.Position);

    private static (string Path, int Line, string Code) Key(ErrorListRow row) => (row.Path.ToUpperInvariant(), row.Line, row.Code);

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
