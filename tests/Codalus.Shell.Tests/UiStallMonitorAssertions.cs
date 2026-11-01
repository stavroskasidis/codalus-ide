global using UiStallMonitor = Codalus.Shell.Infrastructure.UiStallMonitor;

namespace Codalus.Shell.Tests;

/// <summary>The stall-budget tests' check on the shell's <see cref="UiStallMonitor"/>, which <c>--bench everyday</c> shares.</summary>
internal static class UiStallMonitorAssertions
{
    /// <summary>Fails when the UI thread held a probe longer than <paramref name="budgetMilliseconds"/> since the last reset.</summary>
    public static void AssertResponsive(this UiStallMonitor monitor, string flow, double budgetMilliseconds)
    {
        var report = monitor.Report();
        Assert.True(report.Probes > 0, $"{flow}: the stall monitor saw no probe answered");
        Assert.True(report.LongestMilliseconds <= budgetMilliseconds, $"{flow}: the UI thread didn't answer input for {report.Describe()}; the budget is {budgetMilliseconds:0} ms");
    }
}
