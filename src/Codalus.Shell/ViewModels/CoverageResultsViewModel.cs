using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.ViewModels;

/// <summary>One visible line of the Coverage Results tree (flattened so the list can virtualize).</summary>
public sealed class CoverageRowViewModel(CoverageNode node, int depth, bool expanded) : ObservableObject, ITreeRow
{
    private bool _isExpanded = expanded;

    public CoverageNode Node { get; } = node;

    public string Key => Node.Key;

    public int Depth { get; } = depth;

    public Thickness Indent => new(4 + (Depth * 16), 0, 0, 0);

    public string Label => Node.Label;

    public string Icon => Node.Kind switch
    {
        CoverageNodeKind.Assembly => "package",
        CoverageNodeKind.Namespace => "namespace",
        CoverageNodeKind.Class => "class",
        _ => "method",
    };

    public string LinePercent => CoverageCounts.Percent(Node.Counts.LinePercent);

    public string Lines => string.Format(CultureInfo.CurrentCulture, "{0} / {1}", Node.Counts.LinesCovered, Node.Counts.LinesTotal);

    public string BranchPercent => CoverageCounts.Percent(Node.Counts.BranchPercent);

    /// <summary>The covered share of lines, for the bar (0 to 1).</summary>
    public double Ratio => (Node.Counts.LinePercent ?? 100) / 100;

    public bool HasChildren => Node.Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
            }
        }
    }

    public string? Chevron => HasChildren ? (_isExpanded ? "chevron-down" : "chevron-right") : null;

    /// <summary>
    /// The row's name for a screen reader: a node's expansion, level and place are its tree item's
    /// (<see cref="Views.TreeRowsList"/>, M57), not words in its name.
    /// </summary>
    public string AccessibleName =>
        string.Format(CultureInfo.CurrentCulture, "{0}, {1} lines covered ({2}), {3} branches covered", Label, LinePercent, Lines, BranchPercent);
}

/// <summary>
/// Coverage Results (ADR 0011): the latest coverage as assembly → namespace → class → method with line and branch
/// percentages; the toolbar toggles the editor coloring, exports Cobertura and clears the results.
/// </summary>
public sealed class CoverageResultsViewModel : ObservableObject
{
    public const string ToolWindowId = "coverageResults";

    private readonly ShellHost _shell;
    private readonly CoverageStore _coverage;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private CoverageRowViewModel? _selected;
    private string _filterText = string.Empty;
    private string _summary = string.Empty;
    private bool _refreshQueued;
    private bool _seen;

    public CoverageResultsViewModel(ShellHost shell)
    {
        _shell = shell;
        _coverage = shell.Tests.Coverage;
        _coverage.Changed += (_, _) => QueueRefresh();
        ExportCommand = new RelayCommand(() => _ = shell.ExportCoverageAsync());
        ClearCommand = new RelayCommand(_coverage.Clear);
        CollapseAllCommand = new RelayCommand(() =>
        {
            _expanded.Clear();
            Refresh();
        });
        Refresh();
    }

    public ObservableCollection<CoverageRowViewModel> Rows { get; } = [];

    public RelayCommand ExportCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand CollapseAllCommand { get; }

    public CoverageRowViewModel? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public bool HasCoverage => Rows.Count > 0 || _coverage.HasCoverage;

    public bool IsEmpty => !HasCoverage;

    public string EmptyText => "No coverage yet. Run tests with coverage (Test → Run All Tests with Coverage, or the coverage button in the Test Explorer).";

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    /// <summary>Whether the editor colors covered and uncovered lines.</summary>
    public bool ShowColoring
    {
        get => _coverage.IsVisible;
        set
        {
            if (_coverage.IsVisible != value)
            {
                _coverage.IsVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetField(ref _filterText, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Ui.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    /// <summary>Rebuilds the rows, keeping expansion and selection by key.</summary>
    public void Refresh()
    {
        var tree = _coverage.Tree();
        if (!_seen && tree.Count > 0)
        {
            // The first results open down to the classes.
            _seen = true;
            foreach (var assembly in tree)
            {
                _expanded.Add(assembly.Key);
                foreach (var ns in assembly.Children)
                {
                    _expanded.Add(ns.Key);
                }
            }
        }

        var selectedKey = _selected?.Key;
        var filter = _filterText.Trim();
        var rows = new List<CoverageRowViewModel>();
        foreach (var root in tree)
        {
            Add(root, 0);
        }

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        Selected = selectedKey is null ? null : Rows.FirstOrDefault(r => r.Key == selectedKey);
        var totals = _coverage.Totals;
        Summary = _coverage.HasCoverage
            ? string.Format(
                CultureInfo.CurrentCulture,
                "Lines {0} ({1} of {2}) · Branches {3} ({4} of {5}){6}",
                CoverageCounts.Percent(totals.LinePercent),
                totals.LinesCovered,
                totals.LinesTotal,
                CoverageCounts.Percent(totals.BranchPercent),
                totals.BranchesCovered,
                totals.BranchesTotal,
                _coverage.CollectedAt is { } at ? " · " + at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) : string.Empty)
            : string.Empty;
        OnPropertyChanged(nameof(HasCoverage));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowColoring));

        void Add(CoverageNode node, int depth)
        {
            if (filter.Length > 0 && !Matches(node, filter))
            {
                return;
            }

            // While filtering, everything that matches below is shown.
            var expanded = filter.Length > 0 || _expanded.Contains(node.Key);
            rows.Add(new CoverageRowViewModel(node, depth, expanded && node.Children.Count > 0));
            if (expanded)
            {
                foreach (var child in node.Children)
                {
                    Add(child, depth + 1);
                }
            }
        }
    }

    private static bool Matches(CoverageNode node, string filter) =>
        node.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) || node.Children.Any(c => Matches(c, filter));

    public void SetExpanded(CoverageRowViewModel row, bool expanded)
    {
        if (!row.HasChildren)
        {
            return;
        }

        if (expanded)
        {
            _expanded.Add(row.Key);
        }
        else
        {
            _expanded.Remove(row.Key);
        }

        Refresh();
    }

    /// <summary>Opens the selected class or method at its first line.</summary>
    public Task OpenSelectedAsync()
    {
        if (_selected?.Node is { Uri: { } uri } node && DocumentUris.ToPath(uri) is { } path)
        {
            return _shell.OpenFileAsync(path, preview: true, new Position(node.Line, 0));
        }

        return Task.CompletedTask;
    }
}
