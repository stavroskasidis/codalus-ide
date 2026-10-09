using System.Collections.ObjectModel;
using Avalonia.Threading;
using Daedalus.LanguageServices.Protocol;
using Daedalus.Shell.Infrastructure;
using Daedalus.Workbench.Languages;
using Daedalus.Workbench.Projects;

namespace Daedalus.Shell.ViewModels;

/// <summary>One diagnostic in the Error List.</summary>
public sealed record ErrorListRow(string Icon, string Code, string Description, string Project, string File, int Line, int Column, string Path, Position Position)
{
    public string AccessibleName => $"{Icon} {Code}: {Description}, {File} line {Line}";

    public string Location => $"{File}({Line},{Column})";

    public bool IsError => Icon == "error";

    public bool IsWarning => Icon == "warning";
}

/// <summary>
/// The Error List: every diagnostic the language services published, filterable by severity, by the active document
/// and by text. Double-click or Enter opens the location.
/// </summary>
public sealed class ErrorListViewModel : ObservableObject
{
    public const string ToolWindowId = "errorList";

    private readonly DiagnosticsStore _store;
    private readonly SolutionService _solutions;
    private readonly Func<string?> _activePath;
    private readonly Func<string, Position, Task> _open;
    private bool _showErrors = true;
    private bool _showWarnings = true;
    private bool _showMessages = true;
    private bool _currentDocumentOnly;
    private string _filter = string.Empty;
    private bool _refreshScheduled;
    private int _errors;
    private int _warnings;
    private int _messages;

    public ErrorListViewModel(DiagnosticsStore store, SolutionService solutions, Func<string?> activePath, Func<string, Position, Task> open)
    {
        _store = store;
        _solutions = solutions;
        _activePath = activePath;
        _open = open;
        store.Changed += (_, _) => ScheduleRefresh();
        Refresh();
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
        string ProjectOf(string path) =>
            projects.FirstOrDefault(p => p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path)))?.Name ?? string.Empty;

        var all = _store.All.Where(f => !_currentDocumentOnly || SolutionService.PathComparer.Equals(f.Path, active)).ToList();
        var diagnostics = all.SelectMany(f => f.Items.Select(d => (File: f, Diagnostic: d))).Where(x => x.Diagnostic.Severity != DiagnosticSeverity.Hint).ToList();
        _errors = diagnostics.Count(x => x.Diagnostic.Severity == DiagnosticSeverity.Error);
        _warnings = diagnostics.Count(x => x.Diagnostic.Severity == DiagnosticSeverity.Warning);
        _messages = diagnostics.Count(x => x.Diagnostic.Severity == DiagnosticSeverity.Information);

        var rows = diagnostics
            .Where(x => x.Diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => _showErrors,
                DiagnosticSeverity.Warning => _showWarnings,
                _ => _showMessages,
            })
            .Select(x => new ErrorListRow(
                x.Diagnostic.Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "info" },
                x.Diagnostic.Code ?? string.Empty,
                x.Diagnostic.Message,
                ProjectOf(x.File.Path),
                Path.GetFileName(x.File.Path),
                x.Diagnostic.Range.Start.Line + 1,
                x.Diagnostic.Range.Start.Character + 1,
                x.File.Path,
                x.Diagnostic.Range.Start))
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

    public Task OpenAsync(ErrorListRow row) => _open(row.Path, row.Position);
}
