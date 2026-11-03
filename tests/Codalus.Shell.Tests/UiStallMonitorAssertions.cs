global using UiStallMonitor = Codalus.Shell.Infrastructure.UiStallMonitor;

namespace Codalus.Shell.Tests;

/// <summary>The stall-budget tests' check on the shell's <see cref="UiStallMonitor"/>, which <c>--bench everyday</c> shares.</summary>
internal static class UiStallMonitorAssertions
{
    /// <summary>Fails when the UI thread held a probe longer than <paramref name="budgetMilliseconds"/> since the last reset.</summary>
    /// <remarks>
    /// The probe on its way when the flow ended is waited for first: it measures the flow's last stretch, and a flow that
    /// ends within one of the monitor's 10 ms rounds, with nothing to run the dispatcher after it, had no probe answered at
    /// all (macOS CI, run 41601353003).
    /// </remarks>
    public static void AssertResponsive(this UiStallMonitor monitor, string flow, double budgetMilliseconds)
    {
        var before = monitor.Report().Probes;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (monitor.Report().Probes <= before && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            ShellFixture.Pump();
            Thread.Sleep(1);
        }

        var report = monitor.Report();
        Assert.True(report.Probes > 0, $"{flow}: the stall monitor saw no probe answered");
        Assert.True(report.LongestMilliseconds <= budgetMilliseconds, $"{flow}: the UI thread didn't answer input for {report.Describe()}; the budget is {budgetMilliseconds:0} ms");
    }
}
