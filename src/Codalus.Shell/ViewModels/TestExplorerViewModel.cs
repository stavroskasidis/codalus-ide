using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.ViewModels;

/// <summary>Which outcome style (icon color class) applies; color is never the only cue (the icon shape differs too).</summary>
public sealed record OutcomeStyle(TestOutcome Outcome)
{
    public bool IsPassed => Outcome == TestOutcome.Passed;

    public bool IsFailed => Outcome.IsFailure();

    public bool IsSkipped => Outcome == TestOutcome.Skipped;

    public bool IsRunning => Outcome is TestOutcome.Running or TestOutcome.Queued;

    public bool IsNone => Outcome == TestOutcome.None;
}

/// <summary>One visible line of the Test Explorer (the arranged tree is flattened so the list can virtualize).</summary>
public sealed class TestRowViewModel(TestGroup group, int depth, bool expanded) : ObservableObject
{
    private TestGroup _group = group;
    private bool _isExpanded = expanded;

    public TestGroup Group
    {
        get => _group;
        set
        {
            if (!ReferenceEquals(_group, value))
            {
                _group = value;
                OnPropertyChanged(nameof(Label));
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(HasDetail));
                OnPropertyChanged(nameof(OutcomeIcon));
                OnPropertyChanged(nameof(Style));
                OnPropertyChanged(nameof(HasChildren));
                OnPropertyChanged(nameof(Chevron));
                OnPropertyChanged(nameof(Error));
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(AccessibleName));
                OnPropertyChanged(nameof(Tooltip));
            }
        }
    }

    public string Key => _group.Key;

    public int Depth { get; } = depth;

    public Thickness Indent => new(4 + (Depth * 16), 0, 0, 0);

    public string Label => _group.Label;

    /// <summary>The duration of a test, or the number of tests in a group.</summary>
    public string? Detail => _group.Node is { IsLeaf: true, Result.Duration: { } duration }
        ? TestExplorerViewModel.Format(duration)
        : _group.Children.Count > 0 ? _group.Tests.ToString(CultureInfo.CurrentCulture) : _group.Node?.Description;

    public bool HasDetail => Detail is not null;

    public string? Error => _group.Node?.Error;

    public bool HasError => Error is not null;

    public string OutcomeIcon => Icons(_group.Outcome);

    public OutcomeStyle Style => new(_group.Outcome);

    public bool HasChildren => _group.Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string? Chevron => HasChildren ? (_isExpanded ? "chevron-down" : "chevron-right") : null;

    public string? Tooltip => _group.Node?.FullLabel;

    public string AccessibleName
    {
        get
        {
            var state = HasChildren ? (_isExpanded ? ", expanded" : ", collapsed") : string.Empty;
            return $"{Label}, {TestExplorerViewModel.OutcomeText(_group.Outcome)}{(HasDetail ? ", " + Detail : string.Empty)}{state}";
        }
    }

    internal static string Icons(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => "test-passed",
        TestOutcome.Failed or TestOutcome.Errored => "test-failed",
        TestOutcome.Skipped => "test-skipped",
        TestOutcome.Running or TestOutcome.Queued => "test-running",
        _ => "test-none",
    };
}

/// <summary>A frame of the selected test's stack trace; frames with a location open it.</summary>
public sealed record StackFrameRow(string Text, string? File, int Line)
{
    public bool HasLocation => File is not null;
}

/// <summary>An earlier result of the selected test.</summary>
public sealed record HistoryRow(string Outcome, string When, string Duration, string Icon, OutcomeStyle Style);

/// <summary>
/// The Test Explorer (ADR 0010): the tests of every controller, arranged (<see cref="TestProjection"/>), filtered and
/// flattened for a virtualized list; the selected test's details; the toolbar. Rebuilds are coalesced and keep
/// expansion and selection by key.
/// </summary>
public sealed class TestExplorerViewModel : ObservableObject
{
    public const string ToolWindowId = "testExplorer";

    private static readonly TimeSpan s_filterDelay = TimeSpan.FromMilliseconds(150);
    private readonly ShellHost _shell;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _filterTimer;
    private ObservableCollection<TestRowViewModel> _rows = [];
    private TestRowViewModel? _selected;
    private string _filterText = string.Empty;
    private TestFilter _filter = TestFilter.Empty;
    private TestGrouping _grouping = TestGrouping.Hierarchy;
    private string _summary = string.Empty;
    private int _refreshQueued;
    private Playlist? _playlist;
    private IReadOnlyList<Playlist> _playlists = [];

    public TestExplorerViewModel(ShellHost shell)
    {
        _shell = shell;
        _filterTimer = new DispatcherTimer { Interval = s_filterDelay };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            _filter = TestFilter.Parse(_filterText);
            Refresh();
        };
        shell.Tests.Changed += (_, _) => QueueRefresh();
        shell.Tests.RunningChanged += (_, _) => Ui.Post(() =>
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsIdle));
        });
        RunAllCommand = new RelayCommand(() => _ = shell.RunTestsAsync(TestRunKind.Run, null));
        RunFailedCommand = new RelayCommand(() => _ = shell.Tests.RunFailedAsync());
        RunNotRunCommand = new RelayCommand(() => _ = shell.Tests.RunNotRunAsync());
        RunSelectedCommand = new RelayCommand(() => _ = RunSelectedAsync(TestRunKind.Run));
        DebugSelectedCommand = new RelayCommand(() => _ = RunSelectedAsync(TestRunKind.Debug));
        CoverSelectedCommand = new RelayCommand(() => _ = RunSelectedAsync(TestRunKind.Coverage));
        RepeatLastCommand = new RelayCommand(() => _ = shell.Tests.RepeatLastAsync());
        CancelCommand = new RelayCommand(shell.Tests.Cancel);
        RefreshCommand = new RelayCommand(() => _ = shell.RefreshTestsAsync());
        CollapseAllCommand = new RelayCommand(CollapseAll);
        OpenFrameCommand = new RelayCommand(p =>
        {
            if (p is StackFrameRow { File: { } file } frame)
            {
                _ = shell.OpenFileAsync(file, preview: false, new LanguageServices.Protocol.Position(Math.Max(0, frame.Line - 1), 0));
            }
        });
        Refresh();
    }

    public ObservableCollection<TestRowViewModel> Rows
    {
        get => _rows;
        private set => SetField(ref _rows, value);
    }

    public IReadOnlyList<string> Groupings { get; } = ["Project, Namespace, Class", "Project", "Outcome", "Traits", "Duration"];

    public int GroupingIndex
    {
        get => (int)_grouping;
        set
        {
            var grouping = (TestGrouping)Math.Clamp(value, 0, Groupings.Count - 1);
            if (grouping != _grouping)
            {
                _grouping = grouping;
                OnPropertyChanged();
                Refresh();
            }
        }
    }

    public TestGrouping Grouping
    {
        get => _grouping;
        set => GroupingIndex = (int)value;
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetField(ref _filterText, value))
            {
                _filterTimer.Stop();
                _filterTimer.Start();
            }
        }
    }

    /// <summary>Applies the filter now (tests and Enter in the filter box).</summary>
    public void ApplyFilter()
    {
        _filterTimer.Stop();
        _filter = TestFilter.Parse(_filterText);
        Refresh();
    }

    private IReadOnlyList<TestEnvironment> _environments = [];
    private TestEnvironment? _environment;

    /// <summary>Where tests run (M21b): here, or an environment of <c>testenvironments.json</c>.</summary>
    public IReadOnlyList<string> EnvironmentNames => ["Local", .. _environments.Select(e => e.Name)];

    public bool HasEnvironments => _environments.Count > 0;

    public int EnvironmentIndex
    {
        get => _environment is null ? 0 : _environments.ToList().FindIndex(e => e.Name == _environment.Name) + 1;
        set
        {
            var environment = value <= 0 || value > _environments.Count ? null : _environments[value - 1];
            if (environment?.Name != _environment?.Name)
            {
                _environment = environment;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The environment tests run in; null for this machine.</summary>
    public TestEnvironment? Environment => _environment;

    public void SetEnvironments(IReadOnlyList<TestEnvironment> environments)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetEnvironments(environments));
            return;
        }

        _environments = environments;
        _environment = environments.FirstOrDefault(e => e.Name == _environment?.Name);
        OnPropertyChanged(nameof(EnvironmentNames));
        OnPropertyChanged(nameof(HasEnvironments));
        OnPropertyChanged(nameof(EnvironmentIndex));
    }

    public IReadOnlyList<string> PlaylistNames => ["All Tests", .. _playlists.Select(p => p.Name)];

    public int PlaylistIndex
    {
        get => _playlist is null ? 0 : _playlists.ToList().FindIndex(p => p.Name == _playlist.Name) + 1;
        set
        {
            var playlist = value <= 0 || value > _playlists.Count ? null : _playlists[value - 1];
            if (playlist?.Name != _playlist?.Name)
            {
                _playlist = playlist;
                OnPropertyChanged();
                Refresh();
            }
        }
    }

    public Playlist? Playlist => _playlist;

    public void SetPlaylists(IReadOnlyList<Playlist> playlists, string? select = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetPlaylists(playlists, select));
            return;
        }

        _playlists = playlists;
        OnPropertyChanged(nameof(PlaylistNames));
        if (select is not null || (_playlist is not null && playlists.All(p => p.Name != _playlist.Name)))
        {
            _playlist = playlists.FirstOrDefault(p => p.Name == select);
            Refresh();
        }

        OnPropertyChanged(nameof(PlaylistIndex));
    }

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public bool HasTests => Rows.Count > 0;

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyText => _shell.Tests.Controllers.Count == 0
        ? (_shell.Solutions.IsOpen ? "No test projects found in this solution." : "Open a solution with test projects.")
        : _filter.IsEmpty && _playlist is null ? "No tests found yet. Build the solution to discover them." : "No tests match the filter.";

    public bool IsRunning => _shell.Tests.IsRunning;

    public bool IsIdle => !_shell.Tests.IsRunning;

    public RelayCommand RunAllCommand { get; }

    public RelayCommand RunFailedCommand { get; }

    public RelayCommand RunNotRunCommand { get; }

    public RelayCommand RunSelectedCommand { get; }

    public RelayCommand DebugSelectedCommand { get; }

    public RelayCommand CoverSelectedCommand { get; }

    public RelayCommand RepeatLastCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand CollapseAllCommand { get; }

    public RelayCommand OpenFrameCommand { get; }

    public TestRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                UpdateDetails();
                _shell.Workbench.Context.Set("testExplorerSelection", value is not null);
            }
        }
    }

    // ------------------------------------------------------------------ details

    public bool HasDetails => DetailsTitle is not null;

    public string? DetailsTitle { get; private set; }

    public string? DetailsOutcome { get; private set; }

    public string DetailsIcon { get; private set; } = "test-none";

    public OutcomeStyle DetailsStyle { get; private set; } = new(TestOutcome.None);

    public string? DetailsMessage { get; private set; }

    public bool HasMessage => DetailsMessage is not null;

    public string? DetailsExpected { get; private set; }

    public string? DetailsActual { get; private set; }

    public bool HasExpected => DetailsExpected is not null || DetailsActual is not null;

    public IReadOnlyList<StackFrameRow> DetailsFrames { get; private set; } = [];

    public bool HasFrames => DetailsFrames.Count > 0;

    public string? DetailsOutput { get; private set; }

    public bool HasOutput => !string.IsNullOrEmpty(DetailsOutput);

    public IReadOnlyList<HistoryRow> DetailsHistory { get; private set; } = [];

    public bool HasHistory => DetailsHistory.Count > 1;

    public string? DetailsSource { get; private set; }

    public bool HasSource => DetailsSource is not null;

    private void UpdateDetails()
    {
        var node = _selected?.Group.Node is { } n ? _shell.Tests.Find(n.Key) : null;
        if (node is null || _selected is null)
        {
            DetailsTitle = _selected?.Label;
            DetailsOutcome = _selected is null ? null : $"{OutcomeText(_selected.Group.Outcome)} · {_selected.Group.Tests} tests";
            DetailsMessage = DetailsExpected = DetailsActual = DetailsOutput = DetailsSource = null;
            DetailsFrames = [];
            DetailsHistory = [];
        }
        else
        {
            var result = node.IsLeaf ? node.Result : null;
            DetailsTitle = node.FullLabel;
            var outcome = node.IsLeaf ? node.Outcome : node.AggregateOutcome();
            DetailsOutcome = OutcomeText(outcome) + (result?.Duration is { } d ? " in " + Format(d) : string.Empty)
                             + (result is null ? string.Empty : " · " + result.Finished.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
            DetailsIcon = TestRowViewModel.Icons(outcome);
            DetailsStyle = new OutcomeStyle(outcome);
            var message = result is { Messages.Count: > 0 } ? result.Messages[0] : null;
            DetailsMessage = message?.Message is { Length: > 0 } text ? text : node.Error;
            DetailsExpected = message?.Expected;
            DetailsActual = message?.Actual;
            DetailsFrames = [.. StackTraces.Parse(message?.StackTrace).Select(f => new StackFrameRow(f.Text.Trim(), f.File, f.Line))];
            DetailsOutput = result?.Output;
            DetailsSource = node.Uri is { } uri && LanguageServices.Protocol.DocumentUris.ToPath(uri) is { } path
                ? $"{Path.GetFileName(path)}:{(node.Range?.StartLine ?? 0) + 1}"
                : null;
            DetailsHistory = [.. _shell.Tests.History(node.Key).Select(h => new HistoryRow(
                OutcomeText(h.Outcome),
                h.Finished.LocalDateTime.ToString("g", CultureInfo.CurrentCulture),
                h.Duration is { } hd ? Format(hd) : string.Empty,
                TestRowViewModel.Icons(h.Outcome),
                new OutcomeStyle(h.Outcome)))];
        }

        foreach (var name in new[]
        {
            nameof(HasDetails), nameof(DetailsTitle), nameof(DetailsOutcome), nameof(DetailsIcon), nameof(DetailsStyle), nameof(DetailsMessage), nameof(HasMessage),
            nameof(DetailsExpected), nameof(DetailsActual), nameof(HasExpected), nameof(DetailsFrames), nameof(HasFrames), nameof(DetailsOutput), nameof(HasOutput),
            nameof(DetailsHistory), nameof(HasHistory), nameof(DetailsSource), nameof(HasSource),
        })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Opens the selected test's source (double-click, Enter).</summary>
    public Task OpenSelectedAsync()
    {
        if (_selected?.Group.Node is { Uri: { } uri } node && LanguageServices.Protocol.DocumentUris.ToPath(uri) is { } path)
        {
            return _shell.OpenFileAsync(path, preview: true, new LanguageServices.Protocol.Position(node.Range?.StartLine ?? 0, 0));
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ running

    /// <summary>The keys of the selected row's tests (the row itself, or a synthetic group's tests).</summary>
    public IReadOnlyList<string> SelectedKeys => _selected is null ? [] : [.. _selected.Group.RunKeys()];

    private Task RunSelectedAsync(TestRunKind kind) =>
        _selected is null ? _shell.RunTestsAsync(kind, null) : _shell.RunTestsAsync(kind, SelectedKeys);

    // ------------------------------------------------------------------ rows

    /// <summary>
    /// Refreshes once on the UI thread for any number of changes. The test service raises Changed on its own threads,
    /// so the flag is atomic and the refresh is always posted (a refresh off the UI thread crashed a run on Linux, M34d).
    /// </summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    public void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            QueueRefresh();
            return;
        }

        var playlist = _playlist is null ? null : new HashSet<string>(_playlist.Tests, StringComparer.Ordinal);
        var (groups, summary) = _shell.Tests.Read(roots => (TestProjection.Build(roots, _grouping, _filter, playlist), TestProjection.Summary(roots)));
        Summary = summary;
        var rows = new List<(TestGroup Group, int Depth)>();
        foreach (var group in groups)
        {
            Flatten(group, 0, rows);
        }

        Apply(rows);
        OnPropertyChanged(nameof(HasTests));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        UpdateDetails();
    }

    /// <summary>Groups start expanded down to classes (hierarchy) or one level (other groupings); the user's choice sticks.</summary>
    private bool IsExpanded(TestGroup group, int depth) =>
        group.Children.Count > 0 && !_collapsed.Contains(group.Key) && (_expanded.Contains(group.Key) || (_grouping == TestGrouping.Hierarchy ? depth < 2 : depth < 1) || !_filter.IsEmpty);

    private void Flatten(TestGroup group, int depth, List<(TestGroup, int)> rows)
    {
        rows.Add((group, depth));
        if (IsExpanded(group, depth))
        {
            foreach (var child in group.Children)
            {
                Flatten(child, depth + 1, rows);
            }
        }
    }

    /// <summary>Updates <see cref="Rows"/> in place for small changes (scroll position is kept); large ones swap the list.</summary>
    private void Apply(List<(TestGroup Group, int Depth)> rows)
    {
        var selectedKey = _selected?.Key;
        var prefix = 0;
        while (prefix < rows.Count && prefix < Rows.Count && Rows[prefix].Key == rows[prefix].Group.Key && Rows[prefix].Depth == rows[prefix].Depth)
        {
            Rows[prefix].Group = rows[prefix].Group;
            Rows[prefix].IsExpanded = IsExpanded(rows[prefix].Group, rows[prefix].Depth);
            prefix++;
        }

        var suffix = 0;
        while (suffix < rows.Count - prefix && suffix < Rows.Count - prefix
               && Rows[Rows.Count - 1 - suffix].Key == rows[rows.Count - 1 - suffix].Group.Key
               && Rows[Rows.Count - 1 - suffix].Depth == rows[rows.Count - 1 - suffix].Depth)
        {
            var (group, depth) = rows[rows.Count - 1 - suffix];
            Rows[Rows.Count - 1 - suffix].Group = group;
            Rows[Rows.Count - 1 - suffix].IsExpanded = IsExpanded(group, depth);
            suffix++;
        }

        var removed = Rows.Count - suffix - prefix;
        var added = rows.Count - suffix - prefix;
        if (removed + added > 200)
        {
            // One reset instead of thousands of change events (filtering a large tree).
            Rows = new ObservableCollection<TestRowViewModel>(rows.Select((r, i) =>
                i < prefix ? Rows[i] : i >= rows.Count - suffix ? Rows[Rows.Count - (rows.Count - i)] : new TestRowViewModel(r.Group, r.Depth, IsExpanded(r.Group, r.Depth))));
        }
        else
        {
            for (var i = Rows.Count - suffix - 1; i >= prefix; i--)
            {
                Rows.RemoveAt(i);
            }

            for (var i = prefix; i < rows.Count - suffix; i++)
            {
                Rows.Insert(i, new TestRowViewModel(rows[i].Group, rows[i].Depth, IsExpanded(rows[i].Group, rows[i].Depth)));
            }
        }

        var selected = selectedKey is null ? null : Rows.FirstOrDefault(r => r.Key == selectedKey);
        if (!ReferenceEquals(selected, _selected))
        {
            _selected = selected;
            OnPropertyChanged(nameof(Selected));
        }
    }

    public void SetExpanded(TestRowViewModel row, bool expanded)
    {
        if (!row.HasChildren || row.IsExpanded == expanded)
        {
            return;
        }

        if (expanded)
        {
            _expanded.Add(row.Key);
            _collapsed.Remove(row.Key);
        }
        else
        {
            _collapsed.Add(row.Key);
            _expanded.Remove(row.Key);
        }

        Refresh();
    }

    private void CollapseAll()
    {
        _expanded.Clear();
        foreach (var row in Rows.Where(r => r.HasChildren))
        {
            _collapsed.Add(row.Key);
        }

        Refresh();
    }

    /// <summary>Selects (and reveals) the row of a test item.</summary>
    public void Reveal(string key)
    {
        if (_shell.Tests.Find(key) is not { } node)
        {
            return;
        }

        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            _expanded.Add(parent.Key);
            _collapsed.Remove(parent.Key);
        }

        Refresh();
        Selected = Rows.FirstOrDefault(r => r.Group.Node?.Key == key) ?? Selected;
    }

    internal static string Format(TimeSpan duration) =>
        duration.TotalSeconds >= 1
            ? duration.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture) + " s"
            : duration.TotalMilliseconds < 1 ? "< 1 ms" : duration.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture) + " ms";

    internal static string OutcomeText(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => "Passed",
        TestOutcome.Failed => "Failed",
        TestOutcome.Errored => "Error",
        TestOutcome.Skipped => "Skipped",
        TestOutcome.Running => "Running",
        TestOutcome.Queued => "Queued",
        _ => "Not run",
    };
}
