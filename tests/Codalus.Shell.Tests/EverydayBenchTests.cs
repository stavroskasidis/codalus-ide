using Avalonia.Headless.XUnit;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>
/// <c>--bench everyday</c> (M56): CI's benchmark of the everyday flows, which runs only in the benchmarks job of stable
/// releases. These keep it working between them: its stall monitor, and every flow on a small solution, each checking that
/// it did its work (a flow that does nothing would measure nothing).
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
                // A warning for the build's flow to find in the Error List, as Solution50's unused fields are.
                var warning = i == 3 ? "#warning everyday\n" : string.Empty;
                await File.WriteAllTextAsync(Path.Combine(root, project, $"Type{i:00}.cs"), $"{warning}namespace {project};\n\npublic static class Type{i:00}\n{{\n    public static int Value => {i};\n}}\n", TestContext.Current.CancellationToken);
            }
        }

        var solution = Path.Combine(root, "Everyday.slnx");
        await File.WriteAllTextAsync(solution, "<Solution>" + string.Concat(projects.Select(p => $"<Project Path=\"{p}/{p}.csproj\" />")) + "</Solution>", TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(root);
        await f.Shell.OpenSolutionAsync(solution);

        var driver = new BenchDriver(f.Window, f.Shell);
        var run = driver.RunEverydayFlowsAsync();
        await ShellFixture.PumpUntil(() => run.IsCompleted, seconds: 600);
        var flows = await run;

        Assert.Equal(Codalus.Diagnostics.BenchEvents.BenchEventNames.EverydayFlows, flows.Select(flow => flow.Name));
        Assert.All(flows, flow => Assert.True(flow.Stalls.Probes > 0, $"{flow.Name}: no probe answered"));
        Assert.All(flows, flow => Assert.NotNull(flow.Stalls.LongestStep));

        // Each flow checked its work: Track and Sync selected the file, each tab and Ctrl+Tab's release activated a document,
        // each filter showed the files it matches and clearing it the tree, the files opened again, the keystrokes reached
        // the file, and the rebuild succeeded and its warnings reached the Error List.
        Assert.Empty(driver.EverydayNotes);
        Assert.Equal(20, f.Shell.Editors.Count());
        Assert.True(f.Shell.Explorer.TrackActiveItem);
        Assert.False(f.Shell.HasUnsavedDocuments); // the typing undone, as the build and Close All need
        Assert.True(f.Shell.Builds.LastBuild is { Request.Target: BuildTargets.Rebuild, Result.Succeeded: true }, f.Shell.Builds.Channel.GetText());
    }
}
