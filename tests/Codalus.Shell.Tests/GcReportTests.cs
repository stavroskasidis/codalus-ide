using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>
/// The garbage collections behind a long pause, as the command sweep reports them (2026-11-02: a 1.4 s pause on Windows
/// that came with only its length). Other tests run alongside and collect too, so the counts are at least, not exactly.
/// </summary>
public sealed class GcReportTests
{
    [Fact]
    public void A_full_compacting_collection_is_named_with_the_heap_and_the_memory_it_left()
    {
        var mark = GcReport.Mark();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var report = GcReport.Since(mark);

        var gen2 = int.Parse(System.Text.RegularExpressions.Regex.Match(report, @"gen2 (\d+)\)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(gen2 >= 1, report);
        Assert.Contains("the longest known, a ", report, StringComparison.Ordinal);
        Assert.Matches(@"memory load \d+% of \d+ MB \(high at \d+%\), the process's working set \d+ MB$", report);
    }
}
