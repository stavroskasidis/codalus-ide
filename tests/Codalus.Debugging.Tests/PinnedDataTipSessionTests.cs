using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>Pinned data tips on netcoredbg (M14b): evaluated again at each pause, changes marked.</summary>
public sealed class PinnedDataTipSessionTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private DebugService _debug = null!;

    public ValueTask InitializeAsync()
    {
        _debug = new DebugService(() => DebuggeeFixture.Adapter);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _debug.DisposeAsync();

    [Fact]
    public async Task Pinned_values_refresh_at_each_pause()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var stops = 0;
        _debug.StateChanged += (_, _) =>
        {
            if (_debug.State == DebugState.Stopped)
            {
                Interlocked.Increment(ref stops);
            }
        };
        var depth = Path.Combine(debuggee.Folder, "Depth.cs");
        var loop = DebuggeeFixture.Line(depth, "depth-loop");
        _debug.Breakpoints.Set(new BreakpointSpec(depth, loop));
        var sum = _debug.PinnedTips.Pin(depth, loop, "sum");
        var outside = _debug.PinnedTips.Pin(depth, loop, "notInScope");
        await _debug.LaunchAsync(DotnetHost, ["exec", debuggee.Dll, "--depth"], debuggee.Folder, new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        // First pass of the loop: sum is 0.
        await WaitForAsync(() => stops == 1 && sum.Value is not null && outside.Value is not null);
        Assert.Equal(("0", false), (sum.Value, sum.Changed));
        Assert.True(outside.IsError, outside.Value);

        // Second pass: 1 + 1² = 2, changed.
        await _debug.ContinueAsync();
        await WaitForAsync(() => stops == 2 && sum.Value == "2");
        Assert.True(sum.Changed);

        // The last value stays after the session ends.
        await _debug.StopAsync();
        await WaitForAsync(() => _debug.State == DebugState.Idle);
        Assert.Equal("2", sum.Value);
    }

    private static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .First(File.Exists);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
