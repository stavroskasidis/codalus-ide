using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>
/// The stall monitor of the command sweep (and of the tests that use it) measures the UI thread only. It timed the
/// thread pool's queue as well, so a pool busy with blocking work read as a stalled UI thread: HotReloadRestartTests
/// failed on Linux CI with 790 and 966 ms while the UI thread answered within 60 ms.
/// </summary>
/// <remarks>It blocks the thread pool for a while, so it runs alone, after the tests that run in parallel.</remarks>
[Collection(PoolBlockingTests.Name)]
public sealed class SweepStallMonitorTests
{
    [AvaloniaFact]
    public async Task A_busy_thread_pool_is_not_a_stalled_UI_thread_and_a_blocked_UI_thread_is()
    {
        var stall = new SmokeTest.SweepStallMonitor();
        try
        {
            await PumpForAsync(TimeSpan.FromMilliseconds(200));
            stall.Reset();

            // Every pool thread blocked for 1.5 s, and more work queued behind them (ending process trees, waiting for
            // a program's output): the pool adds threads slowly, so a continuation queued there waits about that long.
            using var release = new ManualResetEventSlim();
            var blockers = Enumerable.Range(0, Environment.ProcessorCount * 4).Select(_ => Task.Run(() => release.Wait(TimeSpan.FromSeconds(10)))).ToArray();
            var releaser = new Thread(() =>
            {
                Thread.Sleep(1500);
                release.Set();
            }) { IsBackground = true };
            releaser.Start();
            await PumpForAsync(TimeSpan.FromMilliseconds(1800));
            await Task.WhenAll(blockers);
            await PumpForAsync(TimeSpan.FromMilliseconds(100));
            Assert.True(stall.MaxMilliseconds < 500, $"A busy thread pool read as a UI stall of {stall.MaxMilliseconds:0} ms.");

            // The UI thread itself blocked: that is a stall.
            stall.Reset();
            Assert.Null(BlockUntilMeasured(stall, 700));
            await PumpUntilRecordedAsync(stall);
            Assert.True(stall.MaxMilliseconds >= 400, $"The UI thread was blocked for 700 ms, but the monitor saw {stall.MaxMilliseconds:0} ms.");
        }
        finally
        {
            stall.Stop();
        }
    }

    [AvaloniaFact]
    public async Task A_stall_names_the_posted_job_the_UI_thread_spent_it_on()
    {
        var stall = new SmokeTest.SweepStallMonitor();
        try
        {
            await PumpForAsync(TimeSpan.FromMilliseconds(200));
            stall.Reset();

            // Posted from another thread, as a workbench event's handler is: a job of the dispatcher's queue.
            var clock = Stopwatch.StartNew();
            var ui = Environment.CurrentManagedThreadId;
            string? ran = null;
            string? unmeasured = null;
            await Task.Run(() => Ui.Post(() =>
            {
                var started = clock.ElapsedMilliseconds;
                unmeasured = BlockUntilMeasured(stall, 700);
                ran = $"from {started} to {clock.ElapsedMilliseconds} ms on thread {Environment.CurrentManagedThreadId} (the UI thread is {ui}, UI access {Dispatcher.UIThread.CheckAccess()})";
            }));
            var posted = clock.ElapsedMilliseconds;
            await PumpForAsync(TimeSpan.FromMilliseconds(100));
            Assert.True(ran is not null, $"The job didn't run (posted after {posted} ms).");
            Assert.Null(unmeasured);
            await PumpUntilRecordedAsync(stall);

            Assert.True(stall.MaxMilliseconds >= 400,
                $"The UI thread was blocked for 700 ms, but the monitor saw {stall.MaxMilliseconds:0} ms in {stall.Probes} probe(s). Posted after {posted} ms; the job ran {ran ?? "never"}.");
            Assert.StartsWith("the job posted at SweepStallMonitorTests.cs:", stall.JobOfMax);
        }
        finally
        {
            stall.Stop();
        }
    }

    /// <summary>
    /// Blocks the UI thread (the caller) until a probe of the monitor waits for it and the monitor has sampled what it runs
    /// three times, then for <paramref name="milliseconds"/> more. A block of a fixed length isn't enough: the monitor
    /// probes every 20 ms and samples every 100 ms, but its thread can wake hundreds of milliseconds late on a loaded
    /// runner (macOS CI, job 122325528410: 2.7 s where the test takes 1.2 s), which could miss a block of 700 ms.
    /// </summary>
    /// <returns>Why the monitor didn't measure the block, or null (a posted job can't fail the test itself).</returns>
    private static string? BlockUntilMeasured(SmokeTest.SweepStallMonitor stall, int milliseconds)
    {
        if (!SpinWait.SpinUntil(() => stall.ProbePending, TimeSpan.FromSeconds(30)))
        {
            return $"No probe of the monitor waited for the blocked UI thread in 30 s ({stall.Probes} answered since the reset).";
        }

        var samples = stall.SamplesTaken;
        if (!SpinWait.SpinUntil(() => stall.SamplesTaken >= samples + 3, TimeSpan.FromSeconds(30)))
        {
            return $"The monitor took {stall.SamplesTaken - samples} sample(s) of the blocked UI thread in 30 s.";
        }

        Thread.Sleep(milliseconds);
        return null;
    }

    /// <summary>
    /// Runs the dispatcher's jobs until the monitor has recorded every probe sent so far, the one that waited for the
    /// block among them (or 30 s). The probe is answered when the block ends, but the monitor's thread records its wait
    /// only when it next wakes, which can be hundreds of milliseconds later on a loaded runner: 300 ms of pumping after a
    /// 700 ms block read 0 ms on macOS CI (job 122325528410), and the monitor had recorded only the probe before it.
    /// </summary>
    private static async Task PumpUntilRecordedAsync(SmokeTest.SweepStallMonitor stall)
    {
        var sent = stall.ProbesSent;
        var clock = Stopwatch.StartNew();
        while (stall.ProbesRecorded < sent && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            await PumpForAsync(TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>Runs the dispatcher's jobs for a while without blocking the UI thread between them (no pool timer needed).</summary>
    private static async Task PumpForAsync(TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            Dispatcher.UIThread.RunJobs();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
    }
}

/// <summary>Tests that block the thread pool: alone, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PoolBlockingTests
{
    public const string Name = "Tests that block the thread pool";
}
