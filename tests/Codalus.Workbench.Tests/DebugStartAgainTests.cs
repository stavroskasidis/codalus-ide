using System.Reflection;
using System.Text.Json;
using Codalus.Debugging;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// A session whose start netcoredbg gave up on (<c>configurationDone</c> failing with 0x80004005: the program's runtime
/// didn't report within its fixed 5 seconds, as on the loaded Windows runners) starts again, a bounded number of times,
/// without ending in between; so does a later program of several startup projects, in its place. The adapter is the
/// scripted one (<c>Codalus.LspConformance.Server --dap</c>), told to fail its first starts.
/// </summary>
public sealed class DebugStartAgainTests : IDisposable
{
    private readonly string _starts = Path.Combine(Path.GetTempPath(), "codalus-debug-starts-" + Guid.NewGuid().ToString("N"));

    private static string Metadata(string key) =>
        typeof(DebugStartAgainTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string ScriptedAdapter => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    public void Dispose()
    {
        foreach (var program in Programs())
        {
            program.Kill();
            program.Dispose();
        }

        File.Delete(_starts);
        File.Delete(_starts + ".programs");
    }

    /// <summary>The ids of the "programs" the timed-out starts left (the scripted adapter lists them), with those still running.</summary>
    private (int[] Ids, List<System.Diagnostics.Process> Running) LeftPrograms()
    {
        var ids = File.Exists(_starts + ".programs")
            ? File.ReadAllLines(_starts + ".programs").Where(l => l.Length > 0).Select(l => int.Parse(l, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
            : [];
        return (ids, Programs());
    }

    private List<System.Diagnostics.Process> Programs()
    {
        var running = new List<System.Diagnostics.Process>();
        if (!File.Exists(_starts + ".programs"))
        {
            return running;
        }

        foreach (var line in File.ReadAllLines(_starts + ".programs").Where(l => l.Length > 0))
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(int.Parse(line, System.Globalization.CultureInfo.InvariantCulture));
                if (process.WaitForExit(5000))
                {
                    process.Dispose();
                }
                else
                {
                    running.Add(process);
                }
            }
            catch (ArgumentException)
            {
                // Gone.
            }
        }

        return running;
    }

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

        // Each timed-out start's program went with its adapter (netcoredbg's disconnect doesn't end one it never saw
        // the runtime of): none is left running.
        var (ids, running) = LeftPrograms();
        Assert.Equal(DebugService.StartupAttempts - 1, ids.Length);
        Assert.True(running.Count == 0, "still running: " + string.Join(", ", running.Select(p => p.Id)));
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

        // The last start's program went too.
        var (ids, running) = LeftPrograms();
        Assert.Equal(DebugService.StartupAttempts, ids.Length);
        Assert.True(running.Count == 0, "still running: " + string.Join(", ", running.Select(p => p.Id)));
    }

    /// <summary>
    /// A program of several startup projects, as <see cref="DebugService.LaunchAsync(IReadOnlyList{DebugLaunch}, CancellationToken, bool)"/>
    /// starts it: the scripted adapter on this machine (behind a path mapping, as a WSL launch is), told through the
    /// launch's environment how many of its starts to fail.
    /// </summary>
    private DebugLaunch StartupProject(string name, int failStarts)
    {
        var folder = AppContext.BaseDirectory;
        var env = new Dictionary<string, string> { ["failStarts"] = failStarts.ToString(System.Globalization.CultureInfo.InvariantCulture), ["startsFile"] = _starts };
        return new DebugLaunch(name, new Codalus.Workbench.Build.LaunchPlan("program", string.Empty, folder, new Dictionary<string, string>(), null))
        {
            Remote = new RemoteLaunch(Adapter(), new LaunchArguments("program", [], folder, env), new SourceMap(), "here"),
        };
    }

    [Fact]
    public async Task A_later_startup_project_whose_start_timed_out_starts_again_in_its_place()
    {
        // PR #23's Linux CI: with two programs starting on a loaded runner, netcoredbg didn't see the second one's
        // runtime start within its 5 seconds, and the second program never joined ("[Partner] Could not start: Failed
        // command 'configurationDone' : 0x80004005").
        await using var debug = new DebugService(() => null);
        var output = new System.Text.StringBuilder();
        var ended = new List<int?>();
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        debug.Ended += (_, code) => { lock (ended) { ended.Add(code); } done.TrySetResult(code); };

        await debug.LaunchAsync([StartupProject("First", failStarts: 0), StartupProject("Second", failStarts: 1)], Timeout());

        // The second program's first start timed out; its second ran it, in the same place, and the session ended once,
        // after both.
        Assert.Equal(7, await done.Task.WaitAsync(Timeout()));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal([7], ended);
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        Assert.Contains("[Second] The debugger didn't see the program's runtime start within its 5 seconds; starting again (2 of 3).", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not start", text, StringComparison.Ordinal);
        Assert.Contains("[Second] The program has exited with code 7", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("Hello from the scripted program").Length - 1);
        Assert.Equal(["First", "Second"], debug.Processes.Select(p => p.Name));
        Assert.All(debug.Processes, p => Assert.Equal(7, p.ExitCode));
        Assert.Equal(DebugState.Idle, debug.State);

        // The timed-out start's program went with its adapter.
        var (ids, running) = LeftPrograms();
        Assert.Single(ids);
        Assert.True(running.Count == 0, "still running: " + string.Join(", ", running.Select(p => p.Id)));
    }

    [Fact]
    public async Task A_later_startup_project_that_keeps_timing_out_is_left_out_after_the_last_attempt()
    {
        await using var debug = new DebugService(() => null);
        var output = new System.Text.StringBuilder();
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        debug.Ended += (_, code) => done.TrySetResult(code);

        await debug.LaunchAsync([StartupProject("First", failStarts: 0), StartupProject("Second", failStarts: 10)], Timeout());

        // The first program ran; the second failed each start and was left out, the session going on without it.
        Assert.Equal(7, await done.Task.WaitAsync(Timeout()));
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        Assert.Contains("starting again (3 of 3)", text, StringComparison.Ordinal);
        Assert.Contains("[Second] Could not start: Failed command 'configurationDone' : 0x80004005", text, StringComparison.Ordinal);
        Assert.Equal(DebugService.StartupAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(_starts));
        Assert.True(debug.Processes[1].Ended);
        Assert.Equal(DebugState.Idle, debug.State);

        // Every start's program went: the last one's with its adapter too.
        var (ids, running) = LeftPrograms();
        Assert.Equal(DebugService.StartupAttempts, ids.Length);
        Assert.True(running.Count == 0, "still running: " + string.Join(", ", running.Select(p => p.Id)));
    }

    [Fact]
    public void Only_netcoredbgs_startup_timeout_starts_again()
    {
        Assert.True(DebugService.IsStartupTimeout(new DapException("configurationDone", "Failed command 'configurationDone' : 0x80004005")));
        Assert.False(DebugService.IsStartupTimeout(new DapException("configurationDone", "Failed command 'configurationDone' : 0x80070057")));
        Assert.False(DebugService.IsStartupTimeout(new DapException("launch", "Failed command 'launch' : 0x80004005")));
    }
}
