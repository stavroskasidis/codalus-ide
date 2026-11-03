using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>The stall-budget tests' check (<see cref="UiStallMonitorAssertions"/>).</summary>
public sealed class UiStallMonitorAssertionsTests
{
    /// <summary>
    /// A flow that ends before the monitor's next probe has none answered: the check waits for one instead of failing
    /// (AspireLogWatchTests on macOS CI, run 41601317512: its measured pass ran in a moment).
    /// </summary>
    [AvaloniaFact]
    public void A_flow_that_ends_between_two_probes_is_measured_by_the_next()
    {
        using var monitor = new UiStallMonitor();
        monitor.Reset();
        monitor.AssertResponsive("nothing at all", 1000);
        Assert.True(monitor.Report().Probes > 0);
    }
}
