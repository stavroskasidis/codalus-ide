using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Debugging.Tests;

/// <summary>A session whose adapter can't start leaves the service idle (no netcoredbg needed).</summary>
public sealed class DebugServiceStartTests
{
    [Fact]
    public async Task A_missing_debugger_fails_the_start_and_the_next_start_fails_the_same_way()
    {
        var debug = new DebugService(() => null);
        var environment = new Dictionary<string, string>();
        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync("app", [], ".", environment, TestContext.Current.CancellationToken));
        Assert.Contains("netcoredbg", first.Message, StringComparison.Ordinal);
        Assert.Equal(DebugState.Idle, debug.State);
        Assert.False(debug.IsActive);

        // Not "a debug session is already running".
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync("app", [], ".", environment, TestContext.Current.CancellationToken));
        Assert.Contains("netcoredbg", second.Message, StringComparison.Ordinal);
        await debug.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
