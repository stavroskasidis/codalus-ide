using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>
/// The stall monitor's report of its longest wait (<see cref="UiStallMonitor"/>), and the stall-budget tests' check of it,
/// which waits for the probe on its way when the flow ends.
/// </summary>
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

    [AvaloniaFact]
    public void A_flow_over_before_any_probe_was_answered_is_measured_all_the_same()
    {
        // Nothing runs the dispatcher between the reset and the check, as after a flow that ended within one of the
        // monitor's rounds: the check saw no probe answered (macOS CI, run 41601353003).
        using var monitor = new UiStallMonitor();
        ShellFixture.Pump();
        monitor.Reset();
        monitor.AssertResponsive("a flow of no time", 1000);
        Assert.True(monitor.Report().Probes > 0);
    }

    [AvaloniaFact]
    public void A_stall_at_the_end_of_a_flow_counts()
    {
        // The probe on its way while the flow's last step holds the UI thread measures that step.
        using var monitor = new UiStallMonitor();
        ShellFixture.Pump();
        monitor.Reset();
        Thread.Sleep(30); // the monitor's next probe is posted
        Thread.Sleep(300); // and waits for the UI thread, which the flow's last step holds

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => monitor.AssertResponsive("a flow ending in a stall", 100));
        Assert.Contains("didn't answer input", failure.Message, StringComparison.Ordinal);
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
