using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Codalus.Diagnostics.Protocol;
using Avalonia.Headless;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Diagnostics;

namespace Codalus.Shell.Tests;

/// <summary>
/// Profiling and diagnostics in the shell (M18a, ADR 0044 and 0045) on samples/Diagnostics: the Performance Profiler
/// launching the startup project with CPU Usage and .NET Counters, the report's views, hot lines in the editor; the
/// Diagnostic Tools window following a run; the .NET Counters window and its export; the flame graph control.
/// </summary>
public sealed class ProfilerShellTests
{
    private static ProfilingReportViewModel? Report(ShellFixture f) =>
        f.Shell.Layout.Documents.OfType<SolutionManagementDocument>().Select(d => d.ViewModel).OfType<ProfilingReportViewModel>().FirstOrDefault();

    private static async Task<string> OpenDiagnosticsAsync(ShellFixture f, string? arguments = null)
    {
        var folder = f.CopySample("Diagnostics");
        if (arguments is not null)
        {
            File.WriteAllText(Path.Combine(folder, "CpuBound", "Properties", "launchSettings.json"), $$"""{ "profiles": { "CpuBound": { "commandName": "Project", "commandLineArgs": "{{arguments}}" } } }""");
        }

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Diagnostics.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 6 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        f.Shell.SetStartupProject(Path.Combine(folder, "CpuBound", "CpuBound.csproj"));
        return folder;
    }

    [AvaloniaFact]
    public async Task The_profiler_profiles_the_startup_project_from_launch_and_shows_its_report()
    {
        await using var f = ShellFixture.Create();
        var folder = await OpenDiagnosticsAsync(f);
        var hub = f.Shell.OpenProfiler();
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<PerformanceProfilerView>().Any());
        Assert.Contains("CpuBound", hub.StartupProject, StringComparison.Ordinal);
        Assert.True(hub.CanStart);

        // Launched held before Main, profiled for its 3 seconds; its end stops the collection and opens the report.
        var run = await hub.StartAsync();
        Assert.NotNull(run);
        Assert.True(hub.IsCollecting);
        Assert.Equal([ProfilerTools.Cpu, ProfilerTools.Counters], run.Tools);
        await ShellFixture.PumpUntil(() => !hub.IsCollecting, seconds: 240);
        await ShellFixture.PumpUntil(() => Report(f) is { Total: > 0, CallTree.Count: > 0, IsLoading: false } r && r.HotFiles.Count > 0, seconds: 180);
        var report = Report(f)!;
        Assert.False(report.HasError, report.Error);

        var session = Assert.Single(hub.Sessions).Session;
        Assert.Equal(Path.Combine(folder, ".codalus", "profiler"), Path.GetDirectoryName(session.Folder));
        Assert.True(File.Exists(Path.Combine(session.Folder, ProfilerSession.FileName)));
        Assert.True(File.Exists(session.TracePath));
        Assert.True(File.Exists(session.CountersPath));
        Assert.Equal(ProfilerSession.LaunchTarget, session.Target);

        // The views: the hot function first, its callers, the flame graph, the counters.
        Assert.Contains("Primes.IsPrime", report.Functions[0].Name, StringComparison.Ordinal);
        Assert.Contains("Primes.IsPrime", report.BottomUp[0].Name, StringComparison.Ordinal);
        Assert.Contains(report.Functions, fn => fn.Name.Contains("<Main>$", StringComparison.Ordinal)); // from launch: startup included
        report.SelectedFunction = report.Functions[0];
        await ShellFixture.PumpUntil(() => report.Callers.Count > 0);
        Assert.Contains("Primes.Count", report.Callers[0].Name, StringComparison.Ordinal);
        Assert.NotNull(report.FlameFrames);
        Assert.Contains(report.FlameFrames, frame => frame.Name.Contains("Primes.IsPrime", StringComparison.Ordinal));
        Assert.Contains(report.CounterSummaries, c => c.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));

        // The flame graph renders; zooming into a frame shows its subtree, Back returns.
        report.SelectedTab = 3;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<FlameGraph>().Any(g => g.Bounds.Height > FlameGraph.RowHeight));
        var count = report.FlameFrames.First(frame => frame.Name.Contains("Primes.Count", StringComparison.Ordinal));
        await report.ZoomToAsync(count);
        Assert.Equal(count.Width, report.FlameTotal);
        Assert.Contains("Primes.Count", report.FlamePath, StringComparison.Ordinal);
        await report.ZoomOutAsync();
        Assert.Equal(report.Total, report.FlameTotal);

        // A time range filters every view.
        var all = report.Total;
        await report.SelectRangeAsync(0, 0.5);
        await ShellFixture.PumpUntil(() => !report.IsLoading);
        Assert.InRange(report.Total, 1, all - 1);
        Assert.True(report.HasRange);
        await report.SelectRangeAsync(double.NaN, double.NaN);
        await ShellFixture.PumpUntil(() => !report.IsLoading && report.Total == all);

        // Hot lines in the gutter: the [hot] line of Program.cs.
        var program = Path.Combine(folder, "CpuBound", "Program.cs");
        var hotLine = Array.FindIndex(File.ReadAllLines(program), l => l.Contains("// [hot]", StringComparison.Ordinal)) + 1;
        Assert.Contains(report.HotFiles, h => h.Name == "Program.cs");
        var editor = await f.Shell.OpenFileAsync(program);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null && editor.Controller.HotLineMargin.Lines.Count > 0);
        var margin = editor!.Controller!.HotLineMargin;
        // Among the hottest lines (on a loaded runner the caller's loop, Count's line 46, can come first in a short trace).
        var hottest = margin.Lines.Values.OrderByDescending(l => l.Exclusive).Take(3).FirstOrDefault(l => Math.Abs(l.Line - hotLine) <= 2);
        Assert.True(hottest is not null, $"The [hot] line {hotLine} isn't among the hottest: {string.Join(", ", margin.Lines.Values.OrderByDescending(l => l.Exclusive).Take(5).Select(l => $"{l.Line} ({l.Exclusive})"))}");
        Assert.Contains("of the CPU samples", HotLineMargin.Describe(hottest!, f.Shell.Profiling.HotLines.Total), StringComparison.Ordinal);
        report.ShowHotLines = false;
        await ShellFixture.PumpUntil(() => margin.Lines.Count == 0);
        report.ShowHotLines = true;

        // Go to Source opens the hot function at its hottest line.
        await report.GoToSourceAsync(report.Functions[0]);
        Assert.Equal(program, f.Shell.ActiveEditor?.Model.Path);

        // Closing the report clears the hot lines.
        f.Shell.Layout.CloseDocument(f.Shell.Layout.Documents.OfType<SolutionManagementDocument>().First(d => d.ViewModel == report));
        await ShellFixture.PumpUntil(() => !f.Shell.Profiling.HotLines.HasLines);
    }

    [AvaloniaFact]
    public async Task Diagnostic_tools_follow_a_run_and_the_counters_window_exports()
    {
        await using var f = ShellFixture.Create();
        var folder = await OpenDiagnosticsAsync(f, arguments: "0");
        f.Shell.ShowDiagnosticTools();
        await f.Shell.StartWithoutDebuggingAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Count == 1, seconds: 120);
        var program = f.Shell.Runs.Running[0];
        try
        {
            var tools = f.Shell.DiagnosticTools;
            await ShellFixture.PumpUntil(() => tools.IsMonitoring && tools.ProcessId == program.ProcessId, seconds: 60);
            await ShellFixture.PumpUntil(() => tools.Cpu.Count >= 2 && tools.WorkingSet.Count >= 1 && tools.Counters.Any(c => c.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)), seconds: 60);
            Assert.True(f.Window.GetVisualDescendants().OfType<TimeSeriesChart>().Count() >= 3);

            // The .NET Counters window: by process id, the runtime's counters, the fixture's EventCounter and meter.
            f.Shell.ShowCounters();
            var counters = f.Shell.CountersWindow;
            counters.Target = program.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            counters.EventSources = "System.Runtime[cpu-usage;working-set], Codalus-Samples-CpuBound";
            counters.Meters = "Codalus.Samples.CpuBound[items.processed]";
            await counters.StartAsync();
            Assert.True(counters.IsRunning, counters.Status);
            await ShellFixture.PumpUntil(() => counters.Rows.Any(r => r.Key.Name == "items.processed") && counters.Rows.Any(r => r.Key.Name == "work-items") && counters.Rows.Any(r => r.Key.Name == "cpu-usage"), seconds: 60);
            Assert.DoesNotContain(counters.Rows, r => r.Key.Name == "gc-heap-size");
            var csv = await counters.ExportAsync(csv: true, Path.Combine(f.UserData, "counters.csv"));
            Assert.NotNull(csv);
            Assert.Contains("Codalus.Samples.CpuBound,items.processed", await File.ReadAllTextAsync(csv), StringComparison.Ordinal);
            var json = await counters.ExportAsync(csv: false, Path.Combine(f.UserData, "counters.json"));
            Assert.Equal(program.ProcessId, CounterExport.ReadJson(await File.ReadAllTextAsync(json!))!.ProcessId);
            await counters.StopAsync();
            await ShellFixture.PumpUntil(() => !counters.IsRunning);
        }
        finally
        {
            f.Shell.StopRunning();
        }

        await ShellFixture.PumpUntil(() => !f.Shell.DiagnosticTools.IsMonitoring && f.Shell.DiagnosticTools.Status.Contains("exited", StringComparison.Ordinal), seconds: 60);
        _ = folder;
    }

    [AvaloniaFact]
    public void The_flame_graph_navigates_by_keyboard_and_renders_within_its_budget()
    {
        // A wide, deep graph: 10 levels of 600 frames each (6,000 frames).
        var frames = new List<FlameFrame>();
        var node = 0;
        for (var depth = 0; depth < 10; depth++)
        {
            for (var i = 0; i < 600; i++)
            {
                frames.Add(new FlameFrame(node++, i, $"App.Level{depth}.Method{i}(int)", depth, i * 10, 10, 1));
            }
        }

        var graph = new FlameGraph { Frames = frames, Total = 6000, Width = 1600 };
        var window = new Window { Content = new ScrollViewer { Content = graph }, Width = 1600, Height = 400 };
        window.Show();
        ShellFixture.Pump();
        Assert.Equal((FlameGraph.RowHeight * 10) + 2, graph.DesiredSize.Height);

        // Keyboard: down to a child, right to a neighbour, up to the parent; Enter asks for the frame's subtree.
        graph.Focus();
        FlameFrame? activated = null;
        graph.FrameActivated += (_, frame) => activated = frame;
        graph.SelectedFrame = frames[0];
        Assert.Equal(frames[600], FlameGraph.Move(frames, frames[0], Key.Down));
        Assert.Equal(frames[1], FlameGraph.Move(frames, frames[0], Key.Right));
        Assert.Equal(frames[0], FlameGraph.Move(frames, frames[600], Key.Up));
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.Equal(frames[600], activated);

        // Zoom in around the left edge, then reset.
        graph.Zoom(4, 0);
        Assert.Equal((0, 1500), graph.View);
        Assert.Equal("App.Level0.Method0", FlameGraph.ShortName(frames[0].Name));

        // Rendering all of it into a bitmap: the budget is 50 ms a frame (measured; asserted loosely, CI machines vary).
        using var bitmap = new RenderTargetBitmap(new PixelSize(1600, 200));
        graph.ResetView();
        bitmap.Render(graph);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            graph.Zoom(1.1, 0.5);
            bitmap.Render(graph);
        }

        var perFrame = clock.Elapsed.TotalMilliseconds / 10;
        TestContext.Current.TestOutputHelper?.WriteLine($"Flame graph: {perFrame:0.0} ms per frame for {frames.Count} frames");
        if (Environment.GetEnvironmentVariable("CODALUS_BUDGET_LOG") is { Length: > 0 } budgetLog)
        {
            File.AppendAllText(budgetLog, $"flame graph render: {perFrame:0.0} ms per frame, {frames.Count} frames{Environment.NewLine}");
        }

        Assert.True(perFrame < 250, $"{perFrame:0.0} ms per frame");
        window.Close();
    }
}
