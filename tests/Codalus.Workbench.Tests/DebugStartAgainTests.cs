using System.Reflection;
using System.Text.Json;
using Codalus.Debugging;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// A session whose start netcoredbg gave up on (<c>configurationDone</c> failing with 0x80004005: the program's runtime
/// didn't report within its fixed 5 seconds, as on the loaded Windows runners) starts again, a bounded number of times,
/// without ending in between. The adapter is the scripted one (<c>Codalus.LspConformance.Server --dap</c>), told to fail
/// its first starts.
/// </summary>
public sealed class DebugStartAgainTests : IDisposable
{
    private readonly string _starts = Path.Combine(Path.GetTempPath(), "codalus-debug-starts-" + Guid.NewGuid().ToString("N"));

    private static string Metadata(string key) =>
        typeof(DebugStartAgainTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string ScriptedAdapter => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    public void Dispose() => File.Delete(_starts);

    private JsonElement Launch(int failStarts) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["failStarts"] = failStarts, ["startsFile"] = _starts });

    private static DebugAdapterStartInfo Adapter() => new("dotnet", [ScriptedAdapter, "--dap"], AppContext.BaseDirectory);

    [Fact]
    public async Task A_start_the_debugger_timed_out_on_starts_again_and_the_session_ends_once()
    {
        await using var debug = new DebugService(() => null);
        var output = new System.Text.StringBuilder();
        var ended = new List<int?>();
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        debug.Ended += (_, code) => { lock (ended) { ended.Add(code); } done.TrySetResult(code); };

        await debug.LaunchAdapterAsync("Scripted", Adapter(), Launch(failStarts: DebugService.StartupAttempts - 1), Timeout());

        // The third start ran the "program": it printed and exited with 7, and that is the session's one end.
        Assert.Equal(7, await done.Task.WaitAsync(Timeout()));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal([7], ended);
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        Assert.Contains("starting again (2 of 3)", text, StringComparison.Ordinal);
        Assert.Contains("starting again (3 of 3)", text, StringComparison.Ordinal);
        Assert.Contains("Hello from the scripted program", text, StringComparison.Ordinal);
        Assert.Equal(DebugState.Idle, debug.State);
    }

    [Fact]
    public async Task A_start_that_keeps_timing_out_fails_after_the_last_attempt()
    {
        await using var debug = new DebugService(() => null);
        var ended = 0;
        debug.Ended += (_, _) => Interlocked.Increment(ref ended);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAdapterAsync("Scripted", Adapter(), Launch(failStarts: 10), Timeout()));

        Assert.Contains("0x80004005", error.Message, StringComparison.Ordinal);
        Assert.Equal(DebugService.StartupAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(_starts));
        Assert.Equal(1, Volatile.Read(ref ended));
        Assert.Equal(DebugState.Idle, debug.State);
    }

    [Fact]
    public void Only_netcoredbgs_startup_timeout_starts_again()
    {
        Assert.True(DebugService.IsStartupTimeout(new DapException("configurationDone", "Failed command 'configurationDone' : 0x80004005")));
        Assert.False(DebugService.IsStartupTimeout(new DapException("configurationDone", "Failed command 'configurationDone' : 0x80070057")));
        Assert.False(DebugService.IsStartupTimeout(new DapException("launch", "Failed command 'launch' : 0x80004005")));
    }
}
