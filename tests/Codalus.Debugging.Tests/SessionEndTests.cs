using Codalus.Workbench.Debugging;
using FakeAdapter = Codalus.Debugging.Tests.CompoundDebugAdapterTests.FakeAdapter;

namespace Codalus.Debugging.Tests;

/// <summary>
/// A session's adapter ends only its own session. Its <c>terminated</c> event and its end (the adapter process exiting)
/// each end the session, so the second of them comes after the session ended, on the thread pool; one that ran late,
/// after the next session started, ended that one as it started (Linux CI run 41641994303: the smoke sweep's Start
/// Debugging right after a session ran to its end got <c>configurationDone</c> answered, then the IDE's disconnect).
/// </summary>
public sealed class SessionEndTests
{
    [Fact]
    public async Task The_last_sessions_late_end_leaves_the_next_session_running()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var debug = new DebugService(() => null);
        var ended = 0;
        debug.Ended += (_, _) => Interlocked.Increment(ref ended);

        var first = new FakeAdapter([]) { ClosesOnDispose = false };
        await StartAsync(debug, first, cancellationToken);
        await first.RaiseAsync(DapEvents.Terminated, new ExitedEvent(0), DapJsonContext.Default.ExitedEvent);
        // Ended is raised just after the state goes Idle (DebugService.EndAsync), so the wait is for both: a check of
        // IsActive alone could run between the two (Linux CI, PR #300's run 41898116148: "Expected: 1, Actual: 0").
        await WaitUntilAsync(() => !debug.IsActive && Volatile.Read(ref ended) >= 1, "the first session to end", cancellationToken);
        Assert.Equal(1, Volatile.Read(ref ended));

        var second = new FakeAdapter([]);
        await StartAsync(debug, second, cancellationToken);

        // The first adapter's process exits now, and its terminated event comes again.
        first.Close();
        await first.RaiseAsync(DapEvents.Terminated, new ExitedEvent(0), DapJsonContext.Default.ExitedEvent);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(1))
        {
            Assert.True(debug.IsActive, $"the first session's late end ended the second session ({debug.State}, ended {Volatile.Read(ref ended)} times, the second adapter disposed: {second.Disposed})");
            await Task.Delay(20, cancellationToken);
        }

        Assert.False(second.Disposed);
        Assert.Equal(1, Volatile.Read(ref ended));

        await second.RaiseAsync(DapEvents.Terminated, new ExitedEvent(0), DapJsonContext.Default.ExitedEvent);
        await WaitUntilAsync(() => !debug.IsActive && Volatile.Read(ref ended) >= 2, "the second session to end", cancellationToken);
        Assert.True(second.Disposed);
        Assert.Equal(2, Volatile.Read(ref ended));
    }

    /// <summary>
    /// A launched program's session that ends without the adapter's <c>exited</c> event says so in the Debug Console, with
    /// what the adapter's process did, rather than only an exit code of 0: netcoredbg and Codalus IDE's adapter both went
    /// silent after a program's end on Linux CI (runs 41898116148 and 41547730079).
    /// </summary>
    [Fact]
    public async Task A_session_that_ends_without_the_programs_exit_says_so()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var debug = new DebugService(() => null);
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        debug.Output += (_, o) => output.Enqueue(o.Text);
        var ended = 0;
        debug.Ended += (_, _) => Interlocked.Increment(ref ended);

        var silent = new FakeAdapter([]);
        await StartAsync(debug, silent, cancellationToken);
        silent.Close();
        await WaitUntilAsync(() => Volatile.Read(ref ended) == 1, "the silent session to end", cancellationToken);
        Assert.Contains(output, line => line.Contains("The debugger ended the session without reporting the program's exit code, its connection closed", StringComparison.Ordinal));

        output.Clear();
        var reported = new FakeAdapter([]);
        await StartAsync(debug, reported, cancellationToken);
        await reported.RaiseAsync(DapEvents.Exited, new ExitedEvent(3), DapJsonContext.Default.ExitedEvent);
        await reported.RaiseAsync(DapEvents.Terminated, new ExitedEvent(3), DapJsonContext.Default.ExitedEvent);
        await WaitUntilAsync(() => Volatile.Read(ref ended) == 2, "the reported session to end", cancellationToken);
        Assert.Contains(output, line => line.Contains("The program has exited with code 3", StringComparison.Ordinal));
        Assert.DoesNotContain(output, line => line.Contains("without reporting", StringComparison.Ordinal));
    }

    private static Task StartAsync(DebugService debug, FakeAdapter adapter, CancellationToken cancellationToken) =>
        debug.StartAsync(_ => Task.FromResult(adapter), _ => Task.CompletedTask, attached: false, processes: [], cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

    private static async Task WaitUntilAsync(Func<bool> condition, string what, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"timed out waiting for {what}");
            await Task.Delay(10, cancellationToken);
        }
    }
}
