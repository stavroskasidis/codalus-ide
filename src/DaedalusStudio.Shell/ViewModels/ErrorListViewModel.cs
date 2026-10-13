using System.Collections.ObjectModel;
using Avalonia.Threading;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>One diagnostic in the Error List. <see cref="Source"/> is <c>Build</c>, <c>IntelliSense</c> or both.</summary>
public sealed record ErrorListRow(string Icon, string Code, string Description, string Project, string File, int Line, int Column, string Path, Position Position)
{
    public string Source { get; init; } = ErrorListViewModel.IntelliSenseSource;

    public string AccessibleName => $"{Icon} {Code}: {Description}, {File} line {Line}";

    public string Location => $"{File}({Line},{Column})";

    public bool IsError => Icon == "error";

    public bool IsWarning => Icon == "warning";
}

/// <summary>
/// The Error List: every diagnostic the language services published and the errors and warnings of the last build
/// (ADR 0006), filterable by source, severity, the active document and text. A build diagnostic that a language
/// service also reports is shown once. Double-click or Enter opens the location.
/// </summary>
public sealed class ErrorListViewModel : ObservableObject
{
    public const string ToolWindowId = "errorList";
    public const string IntelliSenseSource = "IntelliSense";
    public const string BuildSource = "Build";
    public const string BothSources = "Build + IntelliSense";

    private readonly DiagnosticsStore _store;
    private readonly BuildService _builds;
    private readonly SolutionService _solutions;
    private readonly Func<string?> _activePath;
    private readonly Func<string, Position, Task> _open;
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

    public ErrorListViewModel(DiagnosticsStore store, BuildService builds, SolutionService solutions, Func<string?> activePath, Func<string, Position, Task> open)
    {
        _store = store;
        _builds = builds;
        _solutions = solutions;
        _activePath = activePath;
        _open = open;
        store.Changed += (_, _) => ScheduleRefresh();
        builds.DiagnosticsChanged += (_, _) => ScheduleRefresh();
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
        // A project that lists the file, else the one whose folder holds it (the deepest: a test project's files weren't
        // matched, M34d's review).
        string ProjectOf(string path) =>
            (projects.FirstOrDefault(p => p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path)))
             ?? projects.Where(p => Path.GetDirectoryName(p.Path) is { } folder && IsUnder(path, folder)).MaxBy(p => p.Path.Length))?.Name ?? string.Empty;

        static bool IsUnder(string path, string folder) =>
            path.Length > folder.Length && path.StartsWith(folder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
            && path[folder.Length] is '/' or '\\';

        bool InScope(string path) => !_currentDocumentOnly || SolutionService.PathComparer.Equals(path, active);
        var intelliSense = _sourceFilter == 1
            ? []
            : _store.All
                .Where(f => InScope(f.Path))
                .SelectMany(f => f.Items.Where(d => d.Severity != DiagnosticSeverity.Hint).Select(d => new ErrorListRow(
                    d.Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "info" },
                    d.Code ?? string.Empty,
                    d.Message,
                    ProjectOf(f.Path),
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

        OnPropertyChanged(nameof(ErrorsLabel));
        OnPropertyChanged(nameof(WarningsLabel));
        OnPropertyChanged(nameof(MessagesLabel));
        OnPropertyChanged(nameof(IsEmpty));
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
