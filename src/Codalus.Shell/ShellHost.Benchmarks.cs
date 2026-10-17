using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Diagnostics;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// BenchmarkDotNet (M18b, ADR 0046): the benchmarks of the solution's projects that reference BenchmarkDotNet, gutter
/// glyphs to run them, the Benchmarks window with the results and the history. Running builds and runs the user's
/// code, so it needs a trusted solution.
/// </summary>
public sealed partial class ShellHost
{
    private int _benchmarkScan;

    public BenchmarkService Benchmarks { get; private set; } = null!;

    public BenchmarksViewModel BenchmarksWindow { get; private set; } = null!;

    public Workbench.Output.OutputChannel BenchmarkLog => Workbench.Output.GetOrCreate("codalus.benchmarks", "Benchmarks");

    private void InitializeBenchmarks()
    {
        Benchmarks = new BenchmarkService(() => Solutions.Solution?.Path is { } path ? (Directory.Exists(path) ? path : Path.GetDirectoryName(path)) : null, Paths.State, BuildForBenchmarksAsync);
        Benchmarks.Output += (_, line) => BenchmarkLog.AppendLine(line);
        BenchmarksWindow = new BenchmarksViewModel(Benchmarks, RunBenchmarksAsync, OpenSourceLineAsync);
        Solutions.SolutionChanged += (_, _) => Ui.Post(() => _ = ScanBenchmarks());
        Solutions.ProjectChanged += (_, _) => Ui.Post(() => _ = ScanBenchmarks());
    }

    /// <summary>Finds the solution's benchmarks again, off the UI thread (the latest scan wins).</summary>
    public Task ScanBenchmarks()
    {
        var scan = Interlocked.Increment(ref _benchmarkScan);
        var projects = Solutions.Projects.Values.Select(p => p.Path).ToList();
        return Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false); // projects arrive one by one
            if (scan == Volatile.Read(ref _benchmarkScan))
            {
                Benchmarks.Refresh(projects);
            }
        });
    }

    public void ShowBenchmarks() => Layout.ShowToolWindow(BenchmarksViewModel.ToolWindowId, focus: true);

    /// <summary>Runs benchmarks (a trusted solution only), BenchmarkDotNet's output in the Benchmarks output channel.</summary>
    public async Task<BenchmarkRun?> RunBenchmarksAsync(BenchmarkRequest request)
    {
        if (Benchmarks.IsRunning)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.benchmarks.benchmarksAreAlreadyRunning", "Benchmarks are already running."));
            return null;
        }

        if (!RequireTrustedSolution("Running benchmarks"))
        {
            return null;
        }

        Layout.ShowToolWindow(BenchmarksViewModel.ToolWindowId, focus: false);
        Workbench.Output.Show(BenchmarkLog);
        StatusBar.ShowMessage(Localizer.Get("msg.benchmarks.runningBenchmarks", "Running benchmarks…"), TimeSpan.FromSeconds(5));
        try
        {
            var run = await Benchmarks.RunAsync(request, CancellationToken.None).ConfigureAwait(true);
            if (run.Results.Count == 0)
            {
                Workbench.Notifications.Warning(Localizer.Format("msg.benchmarks.theBenchmarksProducedNoResultsExit", "The benchmarks produced no results (exit code {0}); see the Benchmarks output.", run.ExitCode));
            }

            return run;
        }
        catch (OperationCanceledException)
        {
            BenchmarksWindow.Status = "Stopped.";
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error("The benchmarks could not run: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Builds a benchmark project in Release through the IDE's build before <c>dotnet run --no-build</c>: the build host
    /// runs it after its restore (the one after opening the solution) and holds off restores during it, so no second
    /// restore of the project runs at the same time (both wrote its <c>obj/*.nuget.g.props</c>). A build in progress
    /// finishes first, as for <c>dotnet ef</c>.
    /// </summary>
    private async Task<bool> BuildForBenchmarksAsync(string project, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string> { ["Configuration"] = BenchmarkService.Configuration };
        while (true)
        {
            while (Builds.IsBuilding)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var result = await Builds.BuildAsync(BuildTargets.Build, project, properties, cancellationToken).ConfigureAwait(false);
                return result is { Succeeded: true, Canceled: false };
            }
            catch (InvalidOperationException)
            {
                // Another build started in between (the only reason BuildAsync throws): wait for it, then build.
            }
        }
    }

    BenchmarkService Editor.IEditorServices.Benchmarks => Benchmarks;

    /// <summary>A benchmark glyph: runs that benchmark (or its class's) and shows the results.</summary>
    public async Task RunBenchmarkAsync(string path, BenchmarkMethod benchmark)
    {
        if (Benchmarks.ProjectOf(path) is not { } project)
        {
            return;
        }

        ShowBenchmarks();
        await BenchmarksWindow.RunAsync(project, [benchmark.Filter]).ConfigureAwait(true);
    }
}
