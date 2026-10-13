using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>A breakpoint filter on netcoredbg (M14b): of two named threads through one line, only the matching one breaks.</summary>
public sealed class BreakpointFilterSessionTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private DebugService _debug = null!;

    public ValueTask InitializeAsync()
    {
        _debug = new DebugService(() => DebuggeeFixture.Adapter);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _debug.DisposeAsync();

    [Fact]
    public async Task Only_the_filtered_thread_breaks()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var stops = new List<string?>();
        var ended = new TaskCompletionSource();
        _debug.Stopped += (_, stop) =>
        {
            lock (stops)
            {
                stops.Add(_debug.Threads.FirstOrDefault(t => t.Id == stop.ThreadId)?.Name);
            }

            _ = _debug.ContinueAsync();
        };
        _debug.Ended += (_, _) => ended.TrySetResult();
        var depth = Path.Combine(debuggee.Folder, "Depth.cs");
        _debug.Breakpoints.Set(new BreakpointSpec(depth, DebuggeeFixture.Line(depth, "depth-tag"), Filter: "ThreadName = \"Filter B\""));
        await _debug.LaunchAsync(DotnetHost, ["exec", debuggee.Dll, "--depth"], debuggee.Folder, new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.Equal(["Filter B"], stops);
    }

    private static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .First(File.Exists);
}
