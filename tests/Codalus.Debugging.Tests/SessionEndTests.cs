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
        await WaitUntilAsync(() => !debug.IsActive, "the first session to end", cancellationToken);
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
        await WaitUntilAsync(() => !debug.IsActive, "the second session to end", cancellationToken);
        Assert.True(second.Disposed);
        Assert.Equal(2, Volatile.Read(ref ended));
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
