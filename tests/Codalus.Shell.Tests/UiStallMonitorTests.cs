using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>The stall monitor's report of its longest wait (<see cref="UiStallMonitor"/>).</summary>
public sealed class UiStallMonitorTests
{
    [AvaloniaFact]
    public async Task The_longest_wait_names_the_collections_that_ran_during_it()
    {
        using var monitor = new UiStallMonitor();
        await ShellFixture.PumpUntil(() => monitor.Report().Probes > 0);
        monitor.Reset();
        monitor.Step = "blocking";
        // Late in the stall, so a probe sent during it (one every 10 ms, a loaded runner allowing) waits through the collection.
        Block(500);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Block(100);

        monitor.Step = null;
        await ShellFixture.PumpUntil(() => monitor.Report().LongestMilliseconds > 0);

        var report = monitor.Report();
        Assert.True(report.LongestMilliseconds >= 400, report.Describe());
        Assert.Equal("blocking", report.LongestStep);
        Assert.Matches(@"; during it, \d+ collection\(s\) \(gen0 \d+, gen1 \d+, gen2 [1-9]\d*\); the longest known, a ", report.Describe());
    }

    private static void Block(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Thread.SpinWait(1000);
        }
    }
}
