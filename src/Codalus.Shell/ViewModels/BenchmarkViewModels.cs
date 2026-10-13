using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Diagnostics;

namespace Codalus.Shell.ViewModels;

/// <summary>A benchmark found in the solution.</summary>
public sealed record BenchmarkRowViewModel(string Project, string File, BenchmarkMethod Method)
{
    public string Title => Method.Title;

    public string Detail => $"{System.IO.Path.GetFileNameWithoutExtension(Project)} · {System.IO.Path.GetFileName(File)}:{Method.Line.ToString(CultureInfo.InvariantCulture)}";

    public string AccessibleName => $"{Title}, in {Detail}";
}

/// <summary>A benchmark's result in the last run.</summary>
public sealed record BenchmarkResultRowViewModel(BenchmarkResult Result, double? BaselineMean)
{
    public string Method => Result.Parameters is { } parameters ? $"{Result.Type}.{Result.Method} [{parameters}]" : $"{Result.Type}.{Result.Method}";

    public string Mean => BenchmarkService.FormatTime(Result.Mean);

    public string Error => Result.Error is { } error ? BenchmarkService.FormatTime(error) : "-";

    public string StandardDeviation => BenchmarkService.FormatTime(Result.StandardDeviation);

    public string Allocated => BenchmarkService.FormatBytes(Result.Allocated);

    public string Ratio => BaselineMean is > 0 and var baseline ? (Result.Mean / baseline).ToString("0.00", CultureInfo.CurrentCulture) : "-";

    public string AccessibleName => $"{Method}: mean {Mean}, error {Error}, deviation {StandardDeviation}, allocated {Allocated}";
}

/// <summary>A run of one benchmark in its history.</summary>
public sealed record BenchmarkHistoryRowViewModel(DateTimeOffset At, string Job, BenchmarkResult Result, double? Previous)
{
    public string Time => At.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    public string Mean => BenchmarkService.FormatTime(Result.Mean);

    public string Allocated => BenchmarkService.FormatBytes(Result.Allocated);

    public string Change => Previous is > 0 and var previous ? ((Result.Mean / previous) - 1).ToString("+0.0%;-0.0%;0.0%", CultureInfo.CurrentCulture) : string.Empty;

    public string AccessibleName => $"{Time}, {Job} job: mean {Mean}{(Change.Length == 0 ? string.Empty : ", " + Change + " from the run before")}, allocated {Allocated}";
}

/// <summary>
/// The Benchmarks window (M18b, ADR 0046): the solution's BenchmarkDotNet benchmarks, running them (all, the selected
/// one or a class's from its glyph) with a job, the last run's results, and a benchmark's history across runs.
/// </summary>
public sealed class BenchmarksViewModel : ObservableObject
{
    public const string ToolWindowId = "diagnostics.benchmarks";

    private readonly BenchmarkService _service;
    private readonly Func<BenchmarkRequest, Task<BenchmarkRun?>> _run;
    private readonly Func<string, int, Task> _openSource;
    private BenchmarkRowViewModel? _selected;
    private BenchmarkResultRowViewModel? _selectedResult;
    private string _job = "Short";
    private bool _inProcess;
    private string? _status = "Benchmarks are found in projects that reference BenchmarkDotNet.";

    public BenchmarksViewModel(BenchmarkService service, Func<BenchmarkRequest, Task<BenchmarkRun?>> run, Func<string, int, Task> openSource)
    {
        _service = service;
        _run = run;
        _openSource = openSource;
        RunAllCommand = new RelayCommand(() => _ = RunAllAsync(), () => !_service.IsRunning && Benchmarks.Count > 0);
        RunSelectedCommand = new RelayCommand(() => _ = RunSelectedAsync(), () => !_service.IsRunning && _selected is not null);
        StopCommand = new RelayCommand(_service.Cancel, () => _service.IsRunning);
        service.Changed += (_, _) => Ui.Post(Sync);
        Sync();
    }

    public ICommand RunAllCommand { get; }

    public ICommand RunSelectedCommand { get; }

    public ICommand StopCommand { get; }

    public ObservableCollection<BenchmarkRowViewModel> Benchmarks { get; } = [];

    public ObservableCollection<BenchmarkResultRowViewModel> Results { get; } = [];

    public ObservableCollection<BenchmarkHistoryRowViewModel> History { get; } = [];

    /// <summary>BenchmarkDotNet's jobs: Dry (one iteration, a smoke check) to Default (as long as it takes).</summary>
    public IReadOnlyList<string> Jobs { get; } = ["Dry", "Short", "Medium", "Default"];

    public string Job
    {
        get => _job;
        set => SetField(ref _job, value ?? "Short");
    }

    /// <summary>Run in the benchmark project's own process (faster; no separate build of a generated project).</summary>
    public bool InProcess
    {
        get => _inProcess;
        set => SetField(ref _inProcess, value);
    }

    public bool IsRunning => _service.IsRunning;

    public string? Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public BenchmarkRowViewModel? SelectedBenchmark
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                ((RelayCommand)RunSelectedCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public BenchmarkResultRowViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (SetField(ref _selectedResult, value))
            {
                LoadHistory(value?.Result.FullName);
            }
        }
    }

    private void Sync()
    {
        var selected = _selected?.Method.Filter;
        Benchmarks.Clear();
        foreach (var (project, file, method) in _service.All.OrderBy(b => b.Method.FullType, StringComparer.Ordinal).ThenBy(b => b.Method.Line))
        {
            Benchmarks.Add(new BenchmarkRowViewModel(project, file, method));
        }

        SelectedBenchmark = Benchmarks.FirstOrDefault(b => b.Method.Filter == selected);
        OnPropertyChanged(nameof(IsRunning));
        foreach (var command in new[] { RunAllCommand, RunSelectedCommand, StopCommand })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
    }

    public Task<BenchmarkRun?> RunAllAsync() =>
        _service.Projects is [var project, ..] ? RunAsync(project, [.. _service.All.Where(b => b.Project == project).Select(b => b.Method.FullType + ".*").Distinct()]) : Task.FromResult<BenchmarkRun?>(null);

    public Task<BenchmarkRun?> RunSelectedAsync() =>
        _selected is { } row ? RunAsync(row.Project, [row.Method.Filter]) : Task.FromResult<BenchmarkRun?>(null);

    /// <summary>Runs benchmarks with the window's job; the results replace the last run's.</summary>
    public async Task<BenchmarkRun?> RunAsync(string project, IReadOnlyList<string> filters)
    {
        Status = $"Running {string.Join(", ", filters)} ({_job} job{(_inProcess ? ", in process" : string.Empty)})… BenchmarkDotNet's output is in the Output window.";
        var run = await _run(new BenchmarkRequest(project, filters, _job, _inProcess)).ConfigureAwait(true);
        if (run is null)
        {
            Status = "The benchmarks didn't run.";
            return null;
        }

        ShowResults(run);
        return run;
    }

    public void ShowResults(BenchmarkRun run)
    {
        Results.Clear();
        var baselines = run.Results.GroupBy(r => (r.Type, r.Parameters)).ToDictionary(g => g.Key, g => g.First().Mean);
        foreach (var result in run.Results)
        {
            Results.Add(new BenchmarkResultRowViewModel(result, run.Results.Count > 1 ? baselines.GetValueOrDefault((result.Type, result.Parameters)) : null));
        }

        Status = run.Results.Count == 0
            ? $"No results: BenchmarkDotNet exited with {run.ExitCode.ToString(CultureInfo.InvariantCulture)} (see the Output window)."
            : string.Create(CultureInfo.CurrentCulture, $"{run.Results.Count} benchmarks at {run.At.LocalDateTime:T} ({run.Job} job); ratios against each class's first. Select one for its history.");
        SelectedResult = Results.Count > 0 ? Results[0] : null;
    }

    private void LoadHistory(string? fullName)
    {
        History.Clear();
        if (fullName is null)
        {
            return;
        }

        double? previous = null;
        var rows = new List<BenchmarkHistoryRowViewModel>();
        foreach (var (at, job, result) in _service.HistoryOf(fullName))
        {
            rows.Add(new BenchmarkHistoryRowViewModel(at, job, result, previous));
            previous = result.Mean;
        }

        foreach (var row in Enumerable.Reverse(rows))
        {
            History.Add(row);
        }
    }

    public Task OpenAsync(BenchmarkRowViewModel row) => _openSource(row.File, row.Method.Line);
}
