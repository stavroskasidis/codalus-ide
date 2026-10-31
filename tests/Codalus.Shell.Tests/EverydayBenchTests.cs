using Avalonia.Headless.XUnit;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>
/// <c>--bench everyday</c> (M56): CI's benchmark of the everyday flows, which runs only in the benchmarks job of stable
/// releases. These keep it working between them: its stall monitor, and every flow on a small solution.
/// </summary>
public class EverydayBenchTests
{
    [AvaloniaFact]
    public async Task The_stall_monitor_sums_the_waits_over_fifty_milliseconds()
    {
        using var monitor = new UiStallMonitor();
        await ShellFixture.PumpUntil(() => monitor.Report().Probes >= 3);
        monitor.Reset();
        Thread.Sleep(300); // a job that holds the UI thread
        await ShellFixture.PumpUntil(() => monitor.Report().Probes >= 3);

        var report = monitor.Report();
        Assert.InRange(report.LongestMilliseconds, 200, 60_000);
        Assert.InRange(report.Stalls, 1, 100);
        Assert.InRange(report.StalledMilliseconds, report.LongestMilliseconds, 60_000);
        Assert.Equal(report.LongestMilliseconds, monitor.LongestMilliseconds);
    }

    [Fact]
    public void The_bench_option_opens_the_solution_for_the_everyday_flows()
    {
        var root = Directory.CreateTempSubdirectory("codalus-everyday-").FullName;
        try
        {
            var solution = Path.Combine(root, "Solution50.slnx");
            File.WriteAllText(solution, "<Solution />");
            var options = ShellOptions.Parse([solution, "--bench", "everyday", "--exit-on", "done"]);

            Assert.Equal(solution, options.Path);
            Assert.Equal(("everyday", "done"), (options.Bench, options.ExitOn));
            Assert.True(BenchDriver.IsRequested(options));
            Assert.Empty(options.Errors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Every_everyday_flow_runs_and_is_measured()
    {
        await using var f = ShellFixture.Create();
        var root = Path.Combine(f.UserData, "everyday");
        Codalus.Tests.FixtureSdk.Pin(root);
        string[] projects = ["Alpha", "Beta", "Gamma"];
        foreach (var project in projects)
        {
            Directory.CreateDirectory(Path.Combine(root, project));
            await File.WriteAllTextAsync(Path.Combine(root, project, project + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken);
            for (var i = 0; i < 8; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(root, project, $"Type{i:00}.cs"), $"namespace {project};\n\npublic static class Type{i:00}\n{{\n    public static int Value => {i};\n}}\n", TestContext.Current.CancellationToken);
            }
        }

        var solution = Path.Combine(root, "Everyday.slnx");
        await File.WriteAllTextAsync(solution, "<Solution>" + string.Concat(projects.Select(p => $"<Project Path=\"{p}/{p}.csproj\" />")) + "</Solution>", TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(root);
        await f.Shell.OpenSolutionAsync(solution);

        var run = new BenchDriver(f.Window, f.Shell).RunEverydayFlowsAsync();
        await ShellFixture.PumpUntil(() => run.IsCompleted, seconds: 600);
        var flows = await run;

        Assert.Equal(["open_tracked", "tab_switch", "sync_collapsed", "explorer_search", "close_reopen", "typing", "build"], flows.Select(flow => flow.Name));
        Assert.All(flows, flow => Assert.True(flow.Stalls.Probes > 0, $"{flow.Name}: no probe answered"));
        Assert.Equal(20, f.Shell.Editors.Count());
        Assert.True(f.Shell.Explorer.TrackActiveItem);
        Assert.False(f.Shell.HasUnsavedDocuments); // the typing undone, as the build and Close All need
        Assert.True(f.Shell.Builds.LastBuild is { Request.Target: BuildTargets.Rebuild, Result.Succeeded: true }, f.Shell.Builds.Channel.GetText());
    }
}
