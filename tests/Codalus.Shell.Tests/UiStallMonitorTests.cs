using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>The stall-budget tests' check waits for the probe on its way when the flow ends.</summary>
public class UiStallMonitorTests
{
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
}
