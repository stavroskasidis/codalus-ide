using System.Reflection;
using System.Text.Json;
using Codalus.Debugging;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Each debug session's own messages on disk, the last few kept (<see cref="DebugSessionLog"/>): what Report a Problem
/// attaches when a session went wrong (a review of 1.0 on macOS reported a crash at the first start with nothing to send).
/// The debugger's own lines go in, timed from the session's start; the program's output doesn't.
/// </summary>
public sealed class DebugSessionLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "codalus-debug-sessions-" + Guid.NewGuid().ToString("N"));

    private static string Metadata(string key) =>
        typeof(DebugSessionLogTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string ScriptedAdapter => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    private static DebugAdapterStartInfo Adapter() => new("dotnet", [ScriptedAdapter, "--dap"], AppContext.BaseDirectory);

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_session_writes_its_lines_timed_and_redacted_and_only_the_last_sessions_are_kept()
    {
        using var log = new DebugSessionLog(_folder);
        Assert.Null(log.Latest);
        log.Write("before any session: nowhere");

        for (var i = 0; i < DebugSessionLog.Kept + 3; i++)
        {
            log.Begin();
            log.Write($"session {i}\n");
            Thread.Sleep(5); // the file names carry milliseconds
        }

        Assert.Equal(DebugSessionLog.Kept, Directory.GetFiles(_folder, "session-*.log").Length);
        log.Write("Connecting with password=hunter2 and two lines\nthe second");
        var text = log.ReadLatest()!;
        Assert.Contains($"session {DebugSessionLog.Kept + 2}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("session 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^\+\d+\.\d{3} s  the second$", text);
        Assert.StartsWith("Debug session, ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_debug_session_s_log_has_the_debugger_s_lines_and_its_end_and_not_the_program_s_output()
    {
        using var log = new DebugSessionLog(_folder);
        await using var debug = new DebugService(() => null);
        log.Follow(debug);
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Ended += (_, code) => done.TrySetResult(code);
        var starts = Path.Combine(_folder, "starts");
        Directory.CreateDirectory(_folder);
        var launch = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["failStarts"] = 1, ["startsFile"] = starts, ["clrStartTimesOut"] = true });

        await debug.LaunchAdapterAsync("Scripted", Adapter(), launch, Timeout());
        Assert.Equal(7, await done.Task.WaitAsync(Timeout()));

        // One session, though it started twice: the timeout, the start again and the end are in it.
        var text = log.ReadLatest()!;
        Assert.Single(Directory.GetFiles(_folder, "session-*.log"));
        Assert.Contains("program's .NET runtime didn't start in 30 seconds", text, StringComparison.Ordinal);
        Assert.Contains("starting again (2 of 3)", text, StringComparison.Ordinal);
        Assert.Contains("The program has exited with code 7", text, StringComparison.Ordinal);
        Assert.Contains("[session] ended: the program exited with code 7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello from the scripted program", text, StringComparison.Ordinal);
        Assert.DoesNotContain("scripted adapter: launch", text, StringComparison.Ordinal); // the adapter's own output event, not the IDE's

        // A start that fails says why, in the next session's log.
        var failing = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["failStarts"] = DebugService.StartupAttempts, ["startsFile"] = starts + "2", ["clrStartTimesOut"] = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAdapterAsync("Scripted", Adapter(), failing, Timeout()));
        Assert.Equal(2, Directory.GetFiles(_folder, "session-*.log").Length);
        Assert.Contains("[session] the start failed: launch: program's .NET runtime didn't start in 30 seconds", log.ReadLatest()!, StringComparison.Ordinal);
    }
}
