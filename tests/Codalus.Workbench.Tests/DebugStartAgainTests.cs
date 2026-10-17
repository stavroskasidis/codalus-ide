using System.Reflection;
using System.Text.Json;
using Codalus.Debugging;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// A start netcoredbg gave up on (<c>configurationDone</c> failing with 0x80004005: the program's runtime didn't report
/// within its fixed 5 seconds, as on the loaded Windows runners). While the program runs, the session waits for its
/// runtime and goes on once netcoredbg reports it; a program that ended first starts again, a bounded number of times,
/// without the session ending in between; so does a later program of several startup projects, in its place. The
/// adapter is the scripted one (<c>Codalus.LspConformance.Server --dap</c>), told to fail its first starts and what the
/// program does next (<c>runtimeAfter</c>).
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
    private (int[] Ids, List<System.Diagnostics.Process> Running) LeftPrograms() => (ProgramIds(), Programs());

    /// <summary>The ids of the programs the timed-out starts started.</summary>
    private int[] ProgramIds() =>
        File.Exists(_starts + ".programs")
            ? File.ReadAllLines(_starts + ".programs").Where(l => l.Length > 0).Select(l => int.Parse(l, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
            : [];

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

    private JsonElement Launch(int failStarts, string? runtimeAfter = null)
    {
        var launch = new Dictionary<string, object> { ["failStarts"] = failStarts, ["startsFile"] = _starts };
        if (runtimeAfter is not null)
        {
            launch["runtimeAfter"] = runtimeAfter;
        }

        return JsonSerializer.SerializeToElement(launch);
    }

    private static DebugAdapterStartInfo Adapter() => new("dotnet", [ScriptedAdapter, "--dap"], AppContext.BaseDirectory);

    [Fact]
    public async Task A_start_whose_program_runs_on_waits_for_its_runtime_instead_of_starting_again()
    {
        // PR #23's Windows CI: netcoredbg gave up on the pinned data tip test's net11.0 program three times, each start
        // after exactly 5 seconds, and starting again began each one from nothing on the loaded runner.
        await using var debug = new DebugService(() => null);
        var output = new System.Text.StringBuilder();
        var ended = new List<int?>();
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        debug.Ended += (_, code) => { lock (ended) { ended.Add(code); } done.TrySetResult(code); };

        await debug.LaunchAdapterAsync("Scripted", Adapter(), Launch(failStarts: 1, runtimeAfter: "1500"), Timeout());

        // One start: the program's runtime reported 1.5 s after netcoredbg gave up, and the session went on with it,
        // under the program's process id, which netcoredbg doesn't report then.
        var program = Assert.Single(ProgramIds());
        Assert.Equal(program, await debug.WaitForProcessIdAsync(Timeout()));
        Assert.Equal(program, debug.Processes.Single().Id);
        Assert.Equal(7, await done.Task.WaitAsync(Timeout()));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal([7], ended);
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        Assert.Contains("The debugger didn't see the program's runtime start within its 5 seconds; waiting for it", text, StringComparison.Ordinal);
        Assert.Contains("The debugger saw the program's runtime start ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("starting again", text, StringComparison.Ordinal);
        Assert.Contains("Hello from the scripted program", text, StringComparison.Ordinal);
        Assert.Equal("1", File.ReadAllText(_starts));
        Assert.Equal(DebugState.Idle, debug.State);
        Assert.True(LeftPrograms().Running.Count == 0);
    }

    [Fact]
    public async Task A_late_runtime_the_adapter_reports_only_after_a_request_is_debugged_too()
    {
        // netcoredbg on macOS (PR #24's CI): its CreateProcess callback for a runtime that reports after its 5 seconds
        // waits for stdin, which its main thread holds while it waits for the next request. The wait sends requests.
        await using var debug = new DebugService(() => null);
        var output = new System.Text.StringBuilder();
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        debug.Ended += (_, code) => done.TrySetResult(code);

        await debug.LaunchAdapterAsync("Scripted", Adapter(), Launch(failStarts: 1, runtimeAfter: "1500+request"), Timeout());

        var program = Assert.Single(ProgramIds());
        Assert.Equal(program, await debug.WaitForProcessIdAsync(Timeout()));
        Assert.Equal(7, await done.Task.WaitAsync(Timeout()));
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        Assert.Contains("The debugger saw the program's runtime start ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("starting again", text, StringComparison.Ordinal);
        Assert.Contains("Hello from the scripted program", text, StringComparison.Ordinal);
        Assert.Equal("1", File.ReadAllText(_starts));
        Assert.True(LeftPrograms().Running.Count == 0);
    }

    [Fact]
    public async Task A_start_whose_runtime_never_reports_ends_its_program_after_the_longer_wait()
    {
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Timeout());
        adapter.LateRuntimeWait = TimeSpan.FromSeconds(2);
        var log = new System.Text.StringBuilder();
        adapter.Log += (_, line) => { lock (log) { log.AppendLine(line); } };
        await adapter.LaunchAsync(Launch(failStarts: 1, runtimeAfter: "never"), DapJsonContext.Default.JsonElement, Timeout());
        await adapter.Initialized.WaitAsync(Timeout());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ConfigurationDoneAsync(Timeout()));

        Assert.Contains("didn't see the program's runtime start within 7 seconds, and ended the program", error.Message, StringComparison.Ordinal);
        Assert.True(DebugAdapter.IsStartupTimeout(error.InnerException!));
        lock (log)
        {
            Assert.Contains("waiting for it (up to 2 seconds more)", log.ToString(), StringComparison.Ordinal);
        }

        var (ids, running) = LeftPrograms();
        Assert.Single(ids);
        Assert.True(running.Count == 0, "still running: " + string.Join(", ", running.Select(p => p.Id)));
    }

    [Fact]
    public async Task A_start_whose_runtime_started_but_is_never_taken_up_ends_its_program_and_times_out_to_start_again()
    {
        // PR #32's Linux CI: the .NET 11 runtime's debugger transport deadlocked at its start (netcoredbg and the runtime
        // each blocked opening a different FIFO of the same name), and the start waited out the longer wait to fail.
        // The scripted adapter's "program" is a .NET process, so its runtime's transport is there, and it never reports:
        // the start ends it and fails as netcoredbg's timeout, which DebugService answers by starting again.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The runtime's transport is looked for on Linux (DebugAdapter.HasRuntimeTransport).");
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Timeout());
        adapter.StuckRuntimeWait = TimeSpan.FromSeconds(1);
        var log = new System.Text.StringBuilder();
        adapter.Log += (_, line) => { lock (log) { log.AppendLine(line); } };
        await adapter.LaunchAsync(Launch(failStarts: 1, runtimeAfter: "never"), DapJsonContext.Default.JsonElement, Timeout());
        await adapter.Initialized.WaitAsync(Timeout());
        var waited = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<DapException>(() => adapter.ConfigurationDoneAsync(Timeout()));

        Assert.True(DebugAdapter.IsStartupTimeout(error));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), $"took {waited.Elapsed}, not the 60 seconds of the longer wait");
        lock (log)
        {
            Assert.Contains("The program's runtime started, but the debugger hasn't taken it up in 1 seconds", log.ToString(), StringComparison.Ordinal);
        }

        var (ids, running) = LeftPrograms();
        Assert.Single(ids);
        Assert.True(running.Count == 0, "still running: " + string.Join(", ", running.Select(p => p.Id)));
    }

    [Fact]
    public async Task A_start_whose_program_ended_before_its_runtime_started_starts_again_and_the_session_ends_once()
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
    private DebugLaunch StartupProject(string name, int failStarts, string? runtimeAfter = null)
    {
        var folder = AppContext.BaseDirectory;
        var env = new Dictionary<string, string> { ["failStarts"] = failStarts.ToString(System.Globalization.CultureInfo.InvariantCulture), ["startsFile"] = _starts };
        if (runtimeAfter is not null)
        {
            env["runtimeAfter"] = runtimeAfter;
        }

        return new DebugLaunch(name, new Codalus.Workbench.Build.LaunchPlan("program", string.Empty, folder, new Dictionary<string, string>(), null))
        {
            Remote = new RemoteLaunch(Adapter(), new LaunchArguments("program", [], folder, env), new SourceMap(), "here"),
        };
    }

    [Fact]
    public async Task A_later_startup_project_whose_program_runs_on_joins_once_its_runtime_reports()
    {
        await using var debug = new DebugService(() => null);
        var output = new System.Text.StringBuilder();
        var ended = new List<int?>();
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        debug.Ended += (_, code) => { lock (ended) { ended.Add(code); } done.TrySetResult(code); };

        await debug.LaunchAsync([StartupProject("First", failStarts: 0), StartupProject("Second", failStarts: 1, runtimeAfter: "1500")], Timeout());

        // The second program joined on its first start, under its process id, and the session ended once, after both.
        Assert.Equal(Assert.Single(ProgramIds()), debug.Processes[1].Id);
        Assert.Equal(7, await done.Task.WaitAsync(Timeout()));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal([7], ended);
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        Assert.Contains("[Second] The debugger didn't see the program's runtime start within its 5 seconds; waiting for it", text, StringComparison.Ordinal);
        Assert.Contains("[Second] The debugger saw the program's runtime start ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("starting again", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not start", text, StringComparison.Ordinal);
        Assert.Contains("[Second] The program has exited with code 7", text, StringComparison.Ordinal);
        Assert.Equal(["First", "Second"], debug.Processes.Select(p => p.Name));
        Assert.All(debug.Processes, p => Assert.Equal(7, p.ExitCode));
        Assert.Equal("1", File.ReadAllText(_starts));
        Assert.Equal(DebugState.Idle, debug.State);
        Assert.True(LeftPrograms().Running.Count == 0);
    }

    [Fact]
    public async Task A_later_startup_project_whose_program_ended_before_its_runtime_started_starts_again_in_its_place()
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

        Assert.Contains("[Second] The debugger didn't see the program's runtime start; starting again (2 of 3).", text, StringComparison.Ordinal);
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
