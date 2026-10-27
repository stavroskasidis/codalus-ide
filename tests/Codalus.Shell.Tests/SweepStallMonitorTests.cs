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

            // The UI thread itself blocked: that is a stall. A probe is sent every 20 ms, later on a loaded machine, so the
            // wait it sees can start a little after the block did.
            stall.Reset();
            Thread.Sleep(700);
            await PumpForAsync(TimeSpan.FromMilliseconds(200));
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
            await Task.Run(() => Ui.Post(() =>
            {
                var started = clock.ElapsedMilliseconds;
                Thread.Sleep(700);
                ran = $"from {started} to {clock.ElapsedMilliseconds} ms on thread {Environment.CurrentManagedThreadId} (the UI thread is {ui}, UI access {Dispatcher.UIThread.CheckAccess()})";
            }));
            var posted = clock.ElapsedMilliseconds;
            await PumpForAsync(TimeSpan.FromMilliseconds(1000));

            // Seen 0 ms once on macOS CI (job 122325528410), in 2.7 s where the test takes 1.2 s; it didn't happen again
            // locally under load: the message says where the job ran and whether the monitor answered any probe.
            Assert.True(stall.MaxMilliseconds >= 400,
                $"The UI thread was blocked for 700 ms, but the monitor saw {stall.MaxMilliseconds:0} ms in {stall.Probes} probe(s). Posted after {posted} ms; the job ran {ran ?? "never"}.");
            Assert.StartsWith("the job posted at SweepStallMonitorTests.cs:", stall.JobOfMax);
        }
        finally
        {
            stall.Stop();
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
