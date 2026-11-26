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
        var blocked = System.Diagnostics.Stopwatch.GetTimestamp();
        // Late in the stall, so a probe sent during it (one every 10 ms, a loaded runner allowing) waits through the collection.
        Block(500);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Block(100);

        monitor.Step = null;
        await ShellFixture.PumpUntil(() => monitor.Report().LongestMilliseconds > 0);

        var report = monitor.Report();
        // Whether the monitor's thread posted a probe during the block, or only after it (macOS CI, run 41968010181: one
        // probe of 0 ms; run 42894614787 showed it had posted one 49 ms in, and the 0 ms probe was one sent before the reset).
        var first = monitor.FirstSent;
        var sent = first == 0 ? "no probe posted since the reset"
            : $"the first probe since the reset posted {System.Diagnostics.Stopwatch.GetElapsedTime(blocked, first).TotalMilliseconds:0} ms after the 600 ms block began, {System.Diagnostics.Stopwatch.GetElapsedTime(blocked).TotalMilliseconds:0} ms ago";
        Assert.True(report.LongestMilliseconds >= 400, $"{report.Describe()}; {sent}");
        Assert.Equal("blocking", report.LongestStep);
        Assert.Matches(@"; during it, \d+ collection\(s\) \(gen0 \d+, gen1 \d+, gen2 [1-9]\d*\); the longest known, a ", report.Describe());
    }

    [AvaloniaFact]
    public async Task A_probe_answered_before_a_reset_isnt_counted_after_it()
    {
        // The probe answered just before the reset and recorded just after it: it read as a 0 ms measurement of what
        // followed, and the probe that waited through it wasn't recorded yet (macOS CI, run 42894614787).
        using var monitor = new UiStallMonitor();
        using var answered = new ManualResetEventSlim();
        using var reset = new ManualResetEventSlim();
        using var next = new ManualResetEventSlim();
        var calls = 0;
        var probesAtNext = -1;
        monitor.BeforeRecord = () =>
        {
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    answered.Set();
                    reset.Wait(TimeSpan.FromSeconds(30));
                    break;
                case 2:
                    probesAtNext = monitor.Report().Probes;
                    next.Set();
                    break;
            }
        };

        await ShellFixture.PumpUntil(() => answered.IsSet);
        monitor.Reset();
        reset.Set();
        await ShellFixture.PumpUntil(() => next.IsSet);
        Assert.Equal(0, probesAtNext);
    }

    [AvaloniaFact]
    public async Task The_probe_on_its_way_at_a_reset_measures_the_stall_after_it()
    {
        // No other probe is posted while it waits, so it is the only measure of a stall right after the reset (Linux CI,
        // run 42903894635: a monitor that dropped it failed EverydayBenchTests and A_stall_at_the_end_of_a_flow_counts).
        using var monitor = new UiStallMonitor();
        var waiting = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            // A probe posted after this reset, which waits: the UI thread isn't pumped. (One posted just before the reset
            // isn't seen as FirstSent, and no other comes until it is answered: pump again.)
            ShellFixture.Pump();
            monitor.Reset();
            var round = System.Diagnostics.Stopwatch.StartNew();
            while (monitor.FirstSent == 0 && round.ElapsedMilliseconds < 200)
            {
                Thread.Sleep(1);
            }
        }
        while (monitor.FirstSent == 0 && waiting.Elapsed < TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, monitor.FirstSent);
        monitor.Reset(); // the probe is on its way
        Thread.Sleep(300); // a job that holds the UI thread
        await ShellFixture.PumpUntil(() => monitor.Report().Probes > 0);

        // The wait with its GC pauses, which the monitor takes out of it (Linux CI, run 43709911399: a gen2 collection of
        // the test process paused it 139 ms inside the 300, and the wait alone was 159 ms), as EverydayBenchTests counts it.
        var report = monitor.Report();
        Assert.True(report.LongestMilliseconds + report.LongestGcMilliseconds >= 250, report.Describe());
    }

    [AvaloniaFact]
    public async Task A_block_held_until_a_probe_waited_through_it_is_measured_whole_though_the_probe_is_posted_late()
    {
        // The monitor's thread kept from posting for the block's first 150 ms, as a busy runner can keep it: a sleep of
        // 300 ms then measures about 140 (Windows CI, run 43178619011: 154 ms), and tests that need a wait of a known
        // length hold the UI thread until a probe has waited it.
        using var monitor = new UiStallMonitor();
        using var kept = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var keep = 0;
        monitor.BeforeRecord = () =>
        {
            if (Interlocked.CompareExchange(ref keep, 2, 1) == 1)
            {
                kept.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        await ShellFixture.PumpUntil(() => monitor.Report().Probes > 0);
        Volatile.Write(ref keep, 1);
        await ShellFixture.PumpUntil(() => kept.IsSet);
        monitor.Reset();
        using var later = new Timer(_ => release.Set(), null, 150, Timeout.Infinite);
        var held = System.Diagnostics.Stopwatch.StartNew();
        monitor.HoldUntilAProbeWaited(300);
        var heldFor = held.Elapsed.TotalMilliseconds;
        await ShellFixture.PumpUntil(() => monitor.Report().LongestMilliseconds > 0);

        var report = monitor.Report();
        Assert.True(report.LongestMilliseconds + report.LongestGcMilliseconds >= 300, $"{report.Describe()}; the UI thread was held for {heldFor:0} ms");
        Assert.True(heldFor >= 440, $"The probe was posted at once: the UI thread was held for {heldFor:0} ms");
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
        // The probe on its way while the flow's last step holds the UI thread measures that step. The step holds it until
        // a probe has waited 300 ms: a sleep of 30 ms for the probe to be posted, then 300, measured under the budget when
        // the monitor's thread posted it late into the sleep (Windows CI, run 45877959665: no failure to throw).
        using var monitor = new UiStallMonitor();
        ShellFixture.Pump();
        monitor.Reset();
        monitor.HoldUntilAProbeWaited(300);

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => monitor.AssertResponsive("a flow ending in a stall", 100));
        Assert.Contains("didn't answer input", failure.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_stall_says_the_ui_threads_state_in_the_kernel_on_linux()
    {
        // A wait in no posted job: the UI thread runnable and not run, waiting in the kernel, or stopped (Linux CI, run
        // 44073138183, 2428 ms in no posted job). Here it sleeps, so the kernel has it asleep (S) for most of the wait.
        // It holds the UI thread until a probe has waited 300 ms, whenever the monitor's thread posted it: past the first 50
        // ms of a wait, where the kernel's state is sampled (a probe posted late into a fixed sleep, as in Windows CI's run
        // 45877959665 of A_stall_at_the_end_of_a_flow_counts, waits less).
        using var monitor = new UiStallMonitor();
        ShellFixture.Pump();
        monitor.Reset();
        monitor.HoldUntilAProbeWaited(300); // asleep in the kernel, but for its 1 ms wakes
        await ShellFixture.PumpUntil(() => monitor.Report().LongestMilliseconds > 0);

        var report = monitor.Report();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Null(report.LongestKernel);
            return;
        }

        Assert.True(report.LongestKernel?.StartsWith('S') == true, report.Describe());
        Assert.Contains("the UI thread in the kernel then: S", report.Describe(), StringComparison.Ordinal);
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
