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
/// program does next (<c>runtimeAfter</c>). A program started in the terminal is attached to after
/// <c>configurationDone</c>, so there it's the <c>attach</c> that netcoredbg fails, and the same holds. So it does when
/// netcoredbg answers the start with its command timeout (the start ran past its 15 seconds) and goes on starting, for
/// an adapter on this machine; on another, that start fails without starting again.
/// </summary>
public sealed class DebugStartAgainTests : IDisposable
{
    private readonly string _starts = Path.Combine(Path.GetTempPath(), "codalus-debug-starts-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _scratch = [];

    private static string Metadata(string key) =>
        typeof(DebugStartAgainTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string ScriptedAdapter => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    /// <summary>The scripted adapter's own executable, which netcoredbg's arguments make a stand-in for netcoredbg.</summary>
    private static string NetcoredbgStandIn => Path.ChangeExtension(ScriptedAdapter, OperatingSystem.IsWindows() ? ".exe" : null);

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
        foreach (var path in _scratch)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// The integrated terminal as a program held by the startup hook leaves it: each launch starts a "program" (the
    /// scripted adapter's, listed next to the starts file), scripts what it does when netcoredbg's attach times out
    /// (the next of <paramref name="runtimeAfter"/>, see <c>ScriptedDap.AttachScript</c>) and writes its id where the
    /// hook would.
    /// </summary>
    private sealed class HeldTerminal(DebugStartAgainTests test, params string[] runtimeAfter) : ITerminalLauncher
    {
        private int _launches;

        public Task<int?> LaunchAsync(TerminalLaunch launch, CancellationToken cancellationToken)
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet", [ScriptedAdapter, "--program"]) { UseShellExecute = false, RedirectStandardInput = true };
            using var program = System.Diagnostics.Process.Start(start)!;
            var id = program.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            File.AppendAllText(test._starts + ".programs", id + "\n");
            var script = Path.Combine(Path.GetTempPath(), "codalus-scripted-attach-" + id);
            var folder = launch.Environment[HeldLaunch.WaitVariable]!;
            lock (test._scratch)
            {
                test._scratch.Add(script);
                test._scratch.Add(folder);
            }

            File.WriteAllText(script, runtimeAfter[Math.Min(_launches++, runtimeAfter.Length - 1)]);

            // The program reaches the hook a moment later, as one does after its runtime started: by then the session
            // sent configurationDone (netcoredbg's initialized event came with its answer to initialize).
            _ = Task.Run(async () =>
            {
                await Task.Delay(500, CancellationToken.None);
                File.WriteAllText(Path.Combine(folder, "pid.tmp"), id);
                File.Move(Path.Combine(folder, "pid.tmp"), Path.Combine(folder, "pid"));
            }, CancellationToken.None);
            return Task.FromResult<int?>(program.Id);
        }
    }

    /// <summary>A session whose program starts in <paramref name="terminal"/>, debugged by the netcoredbg stand-in; its Debug Console goes to <paramref name="output"/>.</summary>
    private static async Task<(DebugService Debug, Task<int?> Ended)> LaunchInTerminalAsync(HeldTerminal terminal, System.Text.StringBuilder output)
    {
        var debug = new DebugService(() => NetcoredbgStandIn) { Terminal = terminal, StartupHook = "Codalus.Debugging.StartupHook.dll" };
        var ended = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        debug.Ended += (_, code) => ended.TrySetResult(code);
        var plan = new Codalus.Workbench.Build.LaunchPlan("dotnet", string.Empty, AppContext.BaseDirectory, new Dictionary<string, string>(), null) { ArgumentList = ["exec", "program.dll"] };
        await debug.LaunchAsync([new DebugLaunch("Program", plan) { Console = DebugConsoleKind.Terminal }], Timeout());
        return (debug, ended.Task);
    }

    private static async Task<int?> EndedAsync(Task<int?> ended, System.Text.StringBuilder output)
    {
        try
        {
            return await ended.WaitAsync(Timeout());
        }
        catch (OperationCanceledException)
        {
            lock (output)
            {
                Assert.Fail($"The session didn't end. Debug Console:\n{output}");
            }

            throw;
        }
    }

    [Fact]
    public async Task A_program_in_the_terminal_whose_attach_timed_out_waits_for_its_runtime_while_it_runs()
    {
        // PR #35's Windows CI: netcoredbg failed the attach to the program held in the terminal with 0x80004005 (its
        // 5 seconds), and the launch failed, where a launch with the Debug Console would have waited for the runtime.
        var output = new System.Text.StringBuilder();
        var (debug, ended) = await LaunchInTerminalAsync(new HeldTerminal(this, "1500"), output);
        await using (debug)
        {
            var program = Assert.Single(ProgramIds());
            Assert.Equal(program, debug.Processes.Single().Id);
            Assert.Equal(DebugProcess.Terminal, debug.Processes.Single().Kind);
            Assert.Equal(7, await EndedAsync(ended, output));
        }

        string text;
        lock (output)
        {
            text = output.ToString();
        }

        Assert.Contains("The debugger didn't see the program's runtime start within its 5 seconds; waiting for it", text, StringComparison.Ordinal);
        Assert.Contains("The debugger saw the program's runtime start ", text, StringComparison.Ordinal);
        Assert.Contains("Hello from the scripted program", text, StringComparison.Ordinal);
        Assert.DoesNotContain("starting again", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_in_the_terminal_that_ended_before_its_attach_completed_starts_again()
    {
        // The first program ends before its runtime reports (empty script), the second one's reports.
        var output = new System.Text.StringBuilder();
        var (debug, ended) = await LaunchInTerminalAsync(new HeldTerminal(this, string.Empty, "500"), output);
        await using (debug)
        {
            Assert.Equal(7, await EndedAsync(ended, output));
            var ids = ProgramIds();
            Assert.Equal(2, ids.Length);
            Assert.Equal(ids[1], debug.Processes.Single().Id);
        }

        lock (output)
        {
            Assert.Contains("The debugger didn't see the program's runtime start; starting again (2 of 3).", output.ToString(), StringComparison.Ordinal);
        }

        Assert.True(LeftPrograms().Running.Count == 0);
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

    /// <param name="timesOut">The failed starts get netcoredbg's command timeout (they ran past its 15 seconds), not its 5-second answer.</param>
    private JsonElement Launch(int failStarts, string? runtimeAfter = null, bool timesOut = false)
    {
        var launch = new Dictionary<string, object> { ["failStarts"] = failStarts, ["startsFile"] = _starts };
        if (runtimeAfter is not null)
        {
            launch["runtimeAfter"] = runtimeAfter;
        }

        if (timesOut)
        {
            launch["startTimesOut"] = true;
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

    [Fact]
    public async Task A_start_on_another_machine_that_netcoredbg_answers_with_its_command_timeout_fails_without_starting_again()
    {
        // netcoredbg goes on starting after its command timeout. Here the start waits for a program that still runs; on
        // another machine (an SSH, container or WSL transport) nothing can, and starting again would leave behind there the
        // program netcoredbg is still starting, each attempt taking its 15 seconds again. The stand-in is told it's
        // behind a transport.
        await using var debug = new DebugService(() => null);
        var output = new System.Text.StringBuilder();
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        var remote = Adapter() with { Transport = "ssh scripted" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAdapterAsync("Scripted", remote, Launch(failStarts: 10, timesOut: true), Timeout()));

        Assert.Equal(DebugAdapter.CommandTimeoutMessage, error.Message);
        Assert.Equal("1", File.ReadAllText(_starts));
        lock (output)
        {
            Assert.DoesNotContain("starting again", output.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal(DebugState.Idle, debug.State);
    }

    /// <summary>
    /// A program of several startup projects, as <see cref="DebugService.LaunchAsync(IReadOnlyList{DebugLaunch}, CancellationToken, bool)"/>
    /// starts it: the scripted adapter on this machine (behind a path mapping, as a WSL launch is), told through the
    /// launch's environment how many of its starts to fail.
    /// </summary>
    /// <param name="keepsRunning">The program runs until the session ends it, instead of printing and exiting with 7.</param>
    private DebugLaunch StartupProject(string name, int failStarts, string? runtimeAfter = null, bool keepsRunning = false)
    {
        var folder = AppContext.BaseDirectory;
        var env = new Dictionary<string, string> { ["failStarts"] = failStarts.ToString(System.Globalization.CultureInfo.InvariantCulture), ["startsFile"] = _starts };
        if (runtimeAfter is not null)
        {
            env["runtimeAfter"] = runtimeAfter;
        }

        if (keepsRunning)
        {
            env["keepsRunning"] = "true";
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
    public async Task A_program_added_to_a_running_session_has_its_place_at_once_and_joins_once_its_runtime_reports()
    {
        // An Aspire AppHost's projects (ShellHost.StartRunSessionAsync): the AppHost's orchestrator gives the IDE 120
        // seconds to answer its request for a project, and a start under netcoredbg on a loaded runner, made again when
        // it times out, can take longer; past them the orchestrator gave up on the project (the Aspire F5 test on Windows
        // CI). The program gets its place at once, and its start goes on: here netcoredbg's wait given up on, then the
        // runtime reporting 1.5 seconds later.
        await using var debug = new DebugService(() => null);
        var output = new System.Text.StringBuilder();
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        await debug.LaunchAsync([StartupProject("AppHost", failStarts: 0, keepsRunning: true)], Timeout(), compound: true);

        var (index, joined) = debug.AddLaunch(StartupProject("Api", failStarts: 1, runtimeAfter: "1500"), Timeout());

        Assert.Equal(1, index);
        Assert.False(joined.IsCompleted);
        Assert.Equal(["AppHost", "Api"], debug.Processes.Select(p => p.Name));
        Assert.True(await joined.WaitAsync(Timeout()));
        Assert.Equal(Assert.Single(ProgramIds()), debug.Processes[1].Id);
        lock (output)
        {
            Assert.Contains("[Api] The debugger saw the program's runtime start ", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Could not start", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_program_added_to_a_running_session_that_cannot_start_ends_in_its_place_and_the_session_goes_on()
    {
        // Its failure comes after the answer: the program is marked ended (the IDE execution server's sessionTerminated)
        // and the Debug Console says why.
        await using var debug = new DebugService(() => null);
        var output = new System.Text.StringBuilder();
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        await debug.LaunchAsync([StartupProject("AppHost", failStarts: 0, keepsRunning: true)], Timeout(), compound: true);

        var (index, joined) = debug.AddLaunch(StartupProject("Api", failStarts: 10), Timeout());

        Assert.False(await joined.WaitAsync(Timeout()));
        Assert.True(debug.Processes[index].Ended);
        Assert.False(debug.Processes[0].Ended);
        Assert.NotEqual(DebugState.Idle, debug.State);
        lock (output)
        {
            Assert.Contains("[Api] Could not start: Failed command 'configurationDone' : 0x80004005", output.ToString(), StringComparison.Ordinal);
        }

        var (ids, running) = LeftPrograms();
        Assert.Equal(DebugService.StartupAttempts, ids.Length);
        Assert.True(running.Count == 0, "still running: " + string.Join(", ", running.Select(p => p.Id)));
    }

    [Fact]
    public async Task An_attach_past_netcoredbgs_command_timeout_goes_on_and_a_Break_All_meanwhile_waits_for_it()
    {
        // PR #97's Windows CI (WinUIShellTests): netcoredbg's attach to the packaged WinUI app ran past its fixed 15
        // seconds. It answered configurationDone with its command timeout and went on attaching, but the session ended
        // ("Could not start WinUIApp: Command execution timed out."), and the Break All sent as the session showed running
        // got E_FAIL for its threads, netcoredbg not having the process yet. The stand-in's attach answers so: the runtime
        // reports a second after the timeout, and threads fails until a moment after that (ScriptedDap.AttachLateAsync).
        using var program = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", [ScriptedAdapter, "--program"]) { UseShellExecute = false, RedirectStandardInput = true })!;
        var id = program.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.AppendAllText(_starts + ".programs", id + "\n");
        var script = Path.Combine(Path.GetTempPath(), "codalus-scripted-attach-" + id);
        _scratch.Add(script);
        File.WriteAllText(script, "slow 1000");
        try
        {
            await using var debug = new DebugService(() => NetcoredbgStandIn);
            var output = new System.Text.StringBuilder();
            var stopped = new TaskCompletionSource<StopInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? pausing = null;
            debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
            debug.Stopped += (_, stop) => stopped.TrySetResult(stop);
            debug.StateChanged += (_, _) =>
            {
                // Break All as soon as the session shows running, as the WinUI test does: before netcoredbg attaches.
                if (debug.State == DebugState.Running && pausing is null)
                {
                    pausing = debug.PauseAsync();
                }
            };

            await debug.AttachAsync(program.Id, Timeout());

            // The session went on with the program once its runtime reported, and Break All stopped it.
            Assert.NotEqual(DebugState.Idle, debug.State);
            Assert.Equal(program.Id, debug.ProcessId);
            Assert.NotNull(pausing);
            await pausing.WaitAsync(Timeout());
            var stop = await stopped.Task.WaitAsync(Timeout());
            Assert.Equal("pause", stop.Reason);
            Assert.Equal(1, stop.ThreadId);
            Assert.Equal(DebugState.Stopped, debug.State);
            Assert.Equal(["App.Program.Main"], debug.Frames.Select(f => f.Name));
            string text;
            lock (output)
            {
                text = output.ToString();
            }

            Assert.Contains("The debugger took longer than its 15 seconds to attach to the program; waiting for its runtime (up to 60 seconds more).", text, StringComparison.Ordinal);
            Assert.Contains("The debugger saw the program's runtime start ", text, StringComparison.Ordinal);
            Assert.Contains("The debugger isn't attached to the program yet; Break All waits for it.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("starting again", text, StringComparison.Ordinal);
            await debug.DetachAsync();
            Assert.Equal(DebugState.Idle, debug.State);
            Assert.False(program.HasExited);
        }
        finally
        {
            program.Kill();
        }
    }

    [Fact]
    public void Only_netcoredbgs_startup_timeout_starts_again()
    {
        // netcoredbg's 5-second answer, here and on another machine.
        foreach (var local in new[] { true, false })
        {
            Assert.True(DebugService.IsStartupTimeout(new DapException("configurationDone", "Failed command 'configurationDone' : 0x80004005"), local));
            Assert.False(DebugService.IsStartupTimeout(new DapException("configurationDone", "Failed command 'configurationDone' : 0x80070057"), local));
            Assert.False(DebugService.IsStartupTimeout(new DapException("launch", "Failed command 'launch' : 0x80004005"), local));

            // An attach after configurationDone (a program started in the terminal) carries the start, and the timeout.
            Assert.True(DebugService.IsStartupTimeout(new DapException("attach", "Failed command 'attach' : 0x80004005"), local));
            Assert.False(DebugService.IsStartupTimeout(new DapException("attach", "Failed command 'attach' : 0x80070057"), local));
        }

        // netcoredbg's command timeout to the request that carries the start (it ran past 15 seconds, and goes on), on
        // this machine only: on another nothing waits for the program, and starting again would leave it behind there.
        Assert.True(DebugService.IsStartupTimeout(new DapException("configurationDone", DebugAdapter.CommandTimeoutMessage), local: true));
        Assert.True(DebugService.IsStartupTimeout(new DapException("attach", DebugAdapter.CommandTimeoutMessage), local: true));
        Assert.False(DebugService.IsStartupTimeout(new DapException("launch", DebugAdapter.CommandTimeoutMessage), local: true));
        Assert.False(DebugService.IsStartupTimeout(new DapException("evaluate", DebugAdapter.CommandTimeoutMessage), local: true));
        Assert.False(DebugService.IsStartupTimeout(new DapException("configurationDone", DebugAdapter.CommandTimeoutMessage), local: false));
        Assert.False(DebugService.IsStartupTimeout(new DapException("attach", DebugAdapter.CommandTimeoutMessage), local: false));
    }
}
