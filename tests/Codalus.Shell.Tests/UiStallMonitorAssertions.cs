global using UiStallMonitor = Codalus.Shell.Infrastructure.UiStallMonitor;

namespace Codalus.Shell.Tests;

/// <summary>The stall-budget tests' check on the shell's <see cref="UiStallMonitor"/>, which <c>--bench everyday</c> shares.</summary>
internal static class UiStallMonitorAssertions
{
    /// <summary>
    /// Fails when the UI thread held a probe longer than <paramref name="budgetMilliseconds"/> since the last reset. A flow
    /// that ended before the monitor's next probe (one every 10 ms, later on a loaded runner) has none answered yet: the
    /// next one, which waits on the UI thread as the flow's own would have, is waited for, so only a monitor that never
    /// answers fails here (AspireLogWatchTests on macOS CI, run 41601317512).
    /// </summary>
    public static void AssertResponsive(this UiStallMonitor monitor, string flow, double budgetMilliseconds)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (monitor.Report().Probes == 0 && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            ShellFixture.Pump();
            Thread.Sleep(5);
        }

        var report = monitor.Report();
        Assert.True(report.Probes > 0, $"{flow}: the stall monitor saw no probe answered");
        Assert.True(report.LongestMilliseconds <= budgetMilliseconds, $"{flow}: the UI thread didn't answer input for {report.Describe()}; the budget is {budgetMilliseconds:0} ms");
    }
}
