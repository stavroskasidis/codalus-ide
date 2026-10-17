using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Diagnostics.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Diagnostics;

namespace Codalus.Shell.Tests;

/// <summary>
/// The M18b tools in the shell (ADR 0046) on samples/Diagnostics: the Performance Profiler with allocations, .NET
/// Async, threads, exceptions and the events viewer; Memory Usage's snapshots, their diff and the full dump; comparing
/// sessions; BenchmarkDotNet's glyphs, results and history.
/// </summary>
public sealed class ProfilerToolsShellTests
{
    private static T? Document<T>(ShellFixture f) where T : class =>
        f.Shell.Layout.Documents.OfType<SolutionManagementDocument>().Select(d => d.ViewModel).OfType<T>().LastOrDefault();

    private static async Task<string> OpenDiagnosticsAsync(ShellFixture f, string startup, string arguments)
    {
        var folder = f.CopySample("Diagnostics");
        Directory.CreateDirectory(Path.Combine(folder, startup, "Properties"));
        File.WriteAllText(Path.Combine(folder, startup, "Properties", "launchSettings.json"), $$"""{ "profiles": { "{{startup}}": { "commandName": "Project", "commandLineArgs": "{{arguments}}" } } }""");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Diagnostics.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 6 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 300);
        f.Shell.SetStartupProject(Path.Combine(folder, startup, startup + ".csproj"));
        return folder;
    }

    private static PerformanceProfilerViewModel Hub(ShellFixture f, params string[] tools)
    {
        var hub = f.Shell.OpenProfiler();
        foreach (var tool in hub.Tools)
        {
            hub.Use(tool.Id, tools.Contains(tool.Id));
        }

        return hub;
    }

    [AvaloniaFact]
    public async Task The_profiler_records_async_threads_exceptions_and_allocations_and_compares_sessions()
    {
        await using var f = ShellFixture.Create();
        var folder = await OpenDiagnosticsAsync(f, "AsyncHeavy", "3");
        var hub = Hub(f, ProfilerTools.Cpu, ProfilerTools.Allocations, ProfilerTools.Async, ProfilerTools.Threads, ProfilerTools.Exceptions);
        Assert.True(hub.CanStart);
        var run = await hub.StartAsync();
        Assert.NotNull(run);
        Assert.Equal([ProfilerTools.Cpu, ProfilerTools.Allocations, ProfilerTools.Async, ProfilerTools.Threads, ProfilerTools.Exceptions], run.Tools);
        await ShellFixture.PumpUntil(() => !hub.IsCollecting, seconds: 240);
        await ShellFixture.PumpUntil(() => Document<ProfilingReportViewModel>(f) is { IsLoading: false, ToolsLoaded: true }, seconds: 180);
        var report = Document<ProfilingReportViewModel>(f)!;
        Assert.False(report.HasError, report.Error);
        var session = Assert.Single(hub.Sessions).Session;
        Assert.Equal("trace.nettrace", session.Trace);

        // The stack profiles: CPU first, then allocations, contention and exceptions to choose from.
        Assert.Equal(ProfileKinds.Cpu, report.SelectedProfile?.Kind);
        Assert.Contains(report.Profiles, p => p.Kind == ProfileKinds.Allocations);
        Assert.Contains(report.Profiles, p => p.Kind == ProfileKinds.Contention);
        report.SelectedProfile = report.Profiles.First(p => p.Kind == ProfileKinds.Exceptions);
        await ShellFixture.PumpUntil(() => !report.IsLoading && report.BottomUp.Count > 0 && report.BottomUp[0].Name == "System.InvalidOperationException", seconds: 60);

        // The tabs: allocations, .NET Async, threads, exceptions, events.
        Assert.True(report.HasAllocations && report.HasAsync && report.HasThreads && report.HasExceptions && report.HasEvents);
        Assert.NotEmpty(report.AllocationTypes);
        report.TaskSearch = "SlowChain";
        await report.LoadTasksAsync();
        Assert.Contains(report.Tasks, t => t.Task.Method == "Worker.SlowChainAsync");
        report.SelectedTask = report.Tasks[0];
        Assert.NotEmpty(report.TaskStack);
        Assert.NotEmpty(report.ThreadRows);
        Assert.NotEmpty(report.ContentionTree);
        Assert.Contains(report.PoolEvents, p => p.Event.Kind == "start");
        var thrown = report.ExceptionGroups[0];
        Assert.Equal("System.InvalidOperationException", thrown.Type);
        Assert.Contains("Worker.Validate", thrown.ThrowSite, StringComparison.Ordinal);
        Assert.NotNull(report.ExceptionTimeline);
        report.SelectedTab = ProfilingReportViewModel.ThreadsTab;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<StateStrip>().Any(s => s.Bounds.Width > 0));
        await report.OpenExceptionAsync(thrown);
        Assert.Equal(Path.Combine(folder, "AsyncHeavy", "Program.cs"), f.Shell.ActiveEditor?.Model.Path);

        // The events viewer: filtered by an event, more pages, exports.
        Assert.NotEmpty(report.EventCounts);
        await report.FilterByAsync(report.EventCounts.First(c => c.Count.Name == "Exception/Start"));
        Assert.NotEmpty(report.Events);
        Assert.All(report.Events, e => Assert.Equal("Microsoft-Windows-DotNETRuntime/Exception/Start", e.Name));
        var speedscope = await report.ExportAsync(ExportFormats.Speedscope, Path.Combine(f.UserData, "exceptions.speedscope.json"));
        Assert.True(File.Exists(speedscope));
        var saved = await report.SaveTraceAsync(Path.Combine(f.UserData, "copy.nettrace"));
        Assert.True(File.Exists(saved));

        // Comparing sessions: a CPU session of CpuBound against this one.
        f.Shell.SetStartupProject(Path.Combine(folder, "CpuBound", "CpuBound.csproj"));
        foreach (var tool in hub.Tools)
        {
            hub.Use(tool.Id, tool.Id == ProfilerTools.Cpu);
        }

        Assert.NotNull(await hub.StartAsync());
        await ShellFixture.PumpUntil(() => !hub.IsCollecting && hub.Sessions.Count == 2, seconds: 240);
        hub.SelectedSession = hub.Sessions.First(s => s.Session.ProcessName == "CpuBound");
        hub.CompareBaseline = hub.Sessions.First(s => s.Session.ProcessName == "AsyncHeavy");
        hub.CompareCommand.Execute(null);
        try
        {
            await ShellFixture.PumpUntil(() => Document<ProfilingComparisonViewModel>(f) is { } c && (c.Functions.Count > 0 || c.Status?.StartsWith("The sessions", StringComparison.Ordinal) == true), seconds: 180);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Assert.Fail($"{ex.Message} documents: {string.Join(", ", f.Shell.Layout.Documents.Select(d => d.GetType().Name + ":" + d.Title))}; comparison: {Document<ProfilingComparisonViewModel>(f)?.Status}");
        }

        var comparison = Document<ProfilingComparisonViewModel>(f)!;
        Assert.True(comparison.Functions.Count > 0, comparison.Status);
        var isPrime = comparison.Functions.First(r => r.Name.Contains("Primes.IsPrime", StringComparison.Ordinal));
        Assert.True(isPrime.Row.Delta > 0 && isPrime.Worse);
    }

    [AvaloniaFact]
    public async Task Memory_usage_takes_snapshots_diffs_them_and_inspects_a_full_dump()
    {
        await using var f = ShellFixture.Create();
        await OpenDiagnosticsAsync(f, "Leaky", "0");
        var hub = Hub(f, ProfilerTools.Memory);
        var run = await hub.StartAsync();
        Assert.NotNull(run);
        Assert.True(hub.CanSnapshot);
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.Equal("heap-1.nettrace", await hub.TakeSnapshotAsync());
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        Assert.Equal("heap-2.nettrace", await hub.TakeSnapshotAsync());
        Assert.Equal("full.dmp", await hub.CreateDumpAsync());
        Assert.Contains("Snapshots taken: 2", hub.SnapshotText, StringComparison.Ordinal);
        await hub.StopAsync();
        await ShellFixture.PumpUntil(() => !hub.IsCollecting, seconds: 120);

        await ShellFixture.PumpUntil(() => Document<ProfilingReportViewModel>(f) is { HeapTypes.Count: > 0 } r && r.Snapshots.Count == 2, seconds: 180);
        var report = Document<ProfilingReportViewModel>(f)!;
        Assert.Equal(ProfilingReportViewModel.MemoryTab, report.SelectedTab);
        Assert.False(report.HasCpu);
        Assert.Equal(2, report.SelectedSnapshot?.Number);
        Assert.Equal(1, report.SelectedBaseline?.Snapshot?.Number); // compared with the one before
        var order = report.HeapTypes.First(t => t.Name == "Order");
        Assert.True(order.Type.CountDelta > 0, order.AccessibleName);

        // Paths to roots for Order.
        report.SelectedHeapType = order;
        await ShellFixture.PumpUntil(() => report.Referrers.Count > 0, seconds: 60);
        var first = report.Referrers[0];
        if (first.Referrer.HasChildren)
        {
            first.IsExpanded = true;
            await ShellFixture.PumpUntil(() => first.Children.Count > 0 && first.Children[0].Referrer.Id != int.MaxValue, seconds: 60);
        }

        // Types by size without a baseline.
        report.SelectedBaseline = report.Baselines[0];
        await ShellFixture.PumpUntil(() => report.HeapTypes.Count > 0 && report.HeapTypes[0].SizeDiff is null, seconds: 60);

        // The full dump opens in the dump view (ClrMD in the debug companion host).
        Assert.True(report.HasDump);
        await report.InspectDumpAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<DumpDocument>().Any(), seconds: 120);
    }

    [AvaloniaFact]
    public async Task Benchmarks_have_glyphs_run_from_them_and_keep_a_history()
    {
        await using var f = ShellFixture.Create();
        var folder = await OpenDiagnosticsAsync(f, "CpuBound", "1");
        var window = f.Shell.BenchmarksWindow;
        await ShellFixture.PumpUntil(() => f.Shell.Benchmarks.All.Count >= 4 && window.Benchmarks.Count >= 4, seconds: 60);
        Assert.Contains(window.Benchmarks, b => b.Title == "StringBenchmarks.Concat");
        Assert.DoesNotContain(window.Benchmarks, b => b.Title.EndsWith(".Helper", StringComparison.Ordinal));

        // Glyphs in the editor: each [Benchmark] method and its class.
        var file = Path.Combine(folder, "Benchmarks", "StringBenchmarks.cs");
        var editor = await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => editor?.Controller is { BenchmarkMargin.Glyphs.Count: > 0 });
        var glyphs = editor!.Controller!.BenchmarkMargin.Glyphs;
        var lines = File.ReadAllLines(file);
        var concat = glyphs.Single(g => g.Method == "Concat");
        Assert.Contains("public string Concat()", lines[concat.Line - 1], StringComparison.Ordinal);
        Assert.Contains(glyphs, g => g is { IsType: true, Type: "StringBenchmarks" });
        Assert.Contains(glyphs, g => g is { Method: "Sum", Type: "HashBenchmarks" });

        // Running one from its glyph: BenchmarkDotNet's Dry job in process, then again for the history.
        window.Job = "Dry";
        window.InProcess = true;
        string Describe()
        {
            var output = f.Shell.BenchmarkLog.GetText();
            return $"status '{window.Status}', running {window.IsRunning}, {window.Results.Count} results, {window.History.Count} in the history, "
                + $"{f.Shell.Benchmarks.History().Runs.Count} runs in {f.Shell.Benchmarks.HistoryPath}, project of the file '{f.Shell.Benchmarks.ProjectOf(file)}'\n"
                + "Notifications:\n" + string.Join('\n', f.Shell.Workbench.Notifications.Items.Select(n => n.Message))
                + "\nBenchmarks output (the end):\n" + output[Math.Max(0, output.Length - 6000)..];
        }

        await f.Shell.RunBenchmarkAsync(file, concat);
        await ShellFixture.PumpUntil(() => window.Results.Count > 0 && !window.IsRunning, seconds: 600, Describe);
        var result = Assert.Single(window.Results);
        Assert.Equal("Concat", result.Result.Method);
        Assert.True(result.Result.Mean > 0);
        Assert.True(result.Result.Allocated > 0, "MemoryDiagnoser's allocations");
        await f.Shell.RunBenchmarkAsync(file, concat);
        await ShellFixture.PumpUntil(() => window.History.Count == 2 && !window.IsRunning, seconds: 600, Describe);
        Assert.True(File.Exists(f.Shell.Benchmarks.HistoryPath));
        Assert.StartsWith(Path.Combine(folder, ".codalus", "benchmarks"), f.Shell.Benchmarks.HistoryPath, StringComparison.Ordinal);
    }
}
