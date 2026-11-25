using System.Text;
using System.Threading.Channels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M14c sessions on the real netcoredbg: several startup projects in one compound session, and a source generator
/// debugged by running the compiler on its target project (the <c>DebugRoslynComponent</c> profile).
/// </summary>
public sealed class SessionsTests(SessionsFixture fixture) : IClassFixture<SessionsFixture>, IAsyncLifetime
{
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private readonly TaskCompletionSource<int?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _output = new();
    private DebugService _debug = null!;

    public ValueTask InitializeAsync()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        _debug = new DebugService(() => DebuggeeFixture.Adapter);
        _debug.Stopped += (_, stop) => _stops.Writer.TryWrite(stop);
        _debug.Ended += (_, code) => _ended.TrySetResult(code);
        _debug.Output += (_, o) =>
        {
            lock (_output)
            {
                _output.Append(o.Text);
            }
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_debug is not null)
        {
            await _debug.DisposeAsync();
        }
    }

    private string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .First(File.Exists);

    private LaunchPlan Plan(string project, params string[] arguments) =>
        new(DotnetHost, string.Empty, Path.GetDirectoryName(fixture.Project(project))!, new Dictionary<string, string>(), null) { ArgumentList = ["exec", fixture.Dll(project), .. arguments] };

    private async Task<StopInfo> NextStopAsync(int seconds = 90)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            return await _stops.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            var breakpoints = string.Join("; ", _debug.Breakpoints.All.Select(b => $"{b.Path}:{b.Line} {_debug.Breakpoints.StateOf(b)}"));
            throw new TimeoutException($"The program did not stop. State: {_debug.State}. Breakpoints: {breakpoints}. Output:\n{Output}");
        }
    }

    [Fact]
    public async Task Two_startup_projects_debug_in_one_session_until_both_end()
    {
        var debuggee = fixture.Source("Debuggee", "Program.cs");
        var partner = fixture.Source("Partner", "Program.cs");
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee, DebuggeeFixture.Line(debuggee, "print")));
        _debug.Breakpoints.Set(new BreakpointSpec(partner, DebuggeeFixture.Line(partner, "partner")));

        await _debug.LaunchAsync([new DebugLaunch("Debuggee", Plan("Debuggee")), new DebugLaunch("Partner", Plan("Partner", "hello", "there"))], TestContext.Current.CancellationToken);
        Assert.True(_debug.IsCompound);
        Assert.Equal(["Debuggee", "Partner"], _debug.Processes.Select(p => p.Name));

        // Each program stops at its own breakpoint; the second waits while the first shows.
        var stops = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            await NextStopAsync();
            var file = Path.GetFileName(Path.GetDirectoryName(_debug.SelectedFrame?.Source?.Path) ?? string.Empty);
            stops.Add(file);
            if (file == "Partner")
            {
                Assert.Equal("\"hello there\"", (await _debug.EvaluateAsync("greeting", "watch", TestContext.Current.CancellationToken)).Result);
                Assert.Contains(_debug.Threads, t => t.Name.StartsWith("Partner: ", StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal("15", (await _debug.EvaluateAsync("total", "watch", TestContext.Current.CancellationToken)).Result);
            }

            await _debug.ContinueAsync();
        }

        Assert.Equal(["Debuggee", "Partner"], stops.Order());
        await _ended.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.All(_debug.Processes, p => Assert.NotNull(p.Id));
        Assert.Contains("partner says hello there", Output, StringComparison.Ordinal);
        Assert.Contains("total=15 Ada is 36", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_program_ending_leaves_the_session_to_the_other()
    {
        // Without pausing the other programs at a stop (M58's debug.breakAllProcesses off; the Codalus debugger pauses
        // them, BreakAllSessionTests), so Partner runs to its end while Debuggee is stopped.
        _debug.BreakAllProcesses = () => false;
        var debuggee = fixture.Source("Debuggee", "Program.cs");
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee, DebuggeeFixture.Line(debuggee, "done")));

        await _debug.LaunchAsync([new DebugLaunch("Partner", Plan("Partner")), new DebugLaunch("Debuggee", Plan("Debuggee"))], TestContext.Current.CancellationToken);
        await NextStopAsync();
        Assert.EndsWith("Program.cs", _debug.SelectedFrame?.Source?.Path, StringComparison.Ordinal);

        // Partner (the first program) has ended meanwhile; the session goes on with Debuggee.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!_debug.Processes[0].Ended && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(_debug.Processes[0].Ended);
        Assert.Equal(DebugState.Stopped, _debug.State);
        await _debug.StopAsync();
        Assert.Equal(DebugState.Idle, _debug.State);
    }

    [Fact]
    public async Task Terminate_Process_on_the_last_program_ends_the_session()
    {
        // #547 (Linux smoke, run 45670700961): the session's last program ended alone ends the session, without an error.
        var partner = fixture.Source("Partner", "Program.cs");
        _debug.Breakpoints.Set(new BreakpointSpec(partner, DebuggeeFixture.Line(partner, "partner")));
        await _debug.LaunchAsync([new DebugLaunch("Partner", Plan("Partner"))], TestContext.Current.CancellationToken, joinable: true);
        await NextStopAsync();

        await _debug.EndProcessAsync(0, detach: false);
        await _ended.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(DebugState.Idle, _debug.State);
    }

    [Fact]
    public async Task An_attach_joins_a_session_and_Stop_ends_the_launched_program_but_leaves_the_attached_one_running()
    {
        // #547: Attach to Process while debugging adds the process to the session; Stop ends what it launched and
        // detaches from what it attached to.
        var partner = fixture.Source("Partner", "Program.cs");
        var go = Path.Combine(Path.GetTempPath(), "codalus-attach-join-" + Guid.NewGuid().ToString("N"));
        using var waiting = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DotnetHost) { ArgumentList = { "exec", fixture.Dll("Debuggee"), "--wait-for", go }, RedirectStandardOutput = true })!;
        try
        {
            Assert.Equal("waiting", await waiting.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
            _debug.Breakpoints.Set(new BreakpointSpec(partner, DebuggeeFixture.Line(partner, "partner")));
            await _debug.LaunchAsync([new DebugLaunch("Partner", Plan("Partner"))], TestContext.Current.CancellationToken, joinable: true);
            await NextStopAsync();

            Assert.Equal(1, await _debug.AttachAsync(waiting.Id, TestContext.Current.CancellationToken, name: "Debuggee"));
            Assert.Equal([DebugProcess.Launched, DebugProcess.Attached], _debug.Processes.Select(p => p.Kind));
            Assert.False(_debug.IsAttached); // not every program was attached to

            await _debug.StopAsync();
            Assert.Equal(DebugState.Idle, _debug.State);
            Assert.False(waiting.HasExited); // detached from, not ended
        }
        finally
        {
            await File.WriteAllTextAsync(go, string.Empty, TestContext.Current.CancellationToken);
            if (!waiting.WaitForExit(30_000))
            {
                waiting.Kill();
            }

            File.Delete(go);
        }
    }

    [Fact]
    public async Task Step_Into_New_Instance_stops_at_the_program_entry_point()
    {
        // #547: netcoredbg's stopAtEntry, for a program that joins the session as for the session's first. The first
        // program is held at a breakpoint meanwhile, so the session doesn't end before the second joins.
        var debuggee = fixture.Source("Debuggee", "Program.cs");
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee, DebuggeeFixture.Line(debuggee, "print")));
        await _debug.LaunchAsync([new DebugLaunch("Debuggee", Plan("Debuggee"))], TestContext.Current.CancellationToken, joinable: true);
        Assert.Equal("breakpoint", (await NextStopAsync()).Reason);

        var (_, joined) = _debug.AddLaunch(new DebugLaunch("Partner", Plan("Partner", "hello", "there")) { StopAtEntry = true }, TestContext.Current.CancellationToken);
        Assert.True(await joined.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
        await _debug.ContinueAsync();

        var stop = await NextStopAsync();
        Assert.Equal("entry", stop.Reason);
        Assert.Equal(fixture.Source("Partner", "Program.cs"), _debug.SelectedFrame?.Source?.Path);
        await _debug.StopAsync();
        Assert.Equal(DebugState.Idle, _debug.State);
    }

    [Fact]
    public async Task A_project_started_from_Solution_Explorer_joins_a_one_project_session()
    {
        // #547: F5 of one project is a session another program can join (Debug → Start New Instance). Until one does,
        // it looks like any one-program session: its threads and exit don't carry the program's name.
        var debuggee = fixture.Source("Debuggee", "Program.cs");
        var partner = fixture.Source("Partner", "Program.cs");
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee, DebuggeeFixture.Line(debuggee, "print")));
        _debug.Breakpoints.Set(new BreakpointSpec(partner, DebuggeeFixture.Line(partner, "partner")));

        await _debug.LaunchAsync([new DebugLaunch("Debuggee", Plan("Debuggee"))], TestContext.Current.CancellationToken, joinable: true);
        Assert.True(_debug.CanAddLaunch);
        await NextStopAsync();
        Assert.EndsWith(Path.Combine("Debuggee", "Program.cs"), _debug.SelectedFrame?.Source?.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(_debug.Threads, t => t.Name.StartsWith("Debuggee: ", StringComparison.Ordinal));

        // While it's stopped, a second project joins the session and stops at its own breakpoint once the first goes on.
        var (index, joined) = _debug.AddLaunch(new DebugLaunch("Partner", Plan("Partner", "hello", "there")), TestContext.Current.CancellationToken);
        Assert.Equal(1, index);
        Assert.True(await joined.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
        Assert.Equal(["Debuggee", "Partner"], _debug.Processes.Select(p => p.Name));
        await _debug.ContinueAsync();

        var stop = await NextStopAsync();
        Assert.EndsWith(Path.Combine("Partner", "Program.cs"), _debug.SelectedFrame?.Source?.Path, StringComparison.Ordinal);
        Assert.Equal("\"hello there\"", (await _debug.EvaluateAsync("greeting", "watch", TestContext.Current.CancellationToken)).Result);
        Assert.Contains(_debug.Threads, t => t.Name.StartsWith("Partner: ", StringComparison.Ordinal));
        await _debug.ContinueAsync();

        await _ended.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.All(_debug.Processes, p => Assert.True(p.Ended));
        Assert.Contains("partner says hello there", Output, StringComparison.Ordinal);
        Assert.Contains("total=15 Ada is 36", Output, StringComparison.Ordinal);
        Assert.NotNull(stop);
    }

    [Fact]
    public async Task A_source_generator_stops_at_a_breakpoint_when_the_compiler_runs_on_its_target()
    {
        var (inputs, sdk) = await fixture.CompilerInputsAsync("GeneratorTarget");
        var output = Path.Combine(Path.GetTempPath(), "codalus-roslyn-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var plan = RoslynComponents.Plan(sdk, inputs, output);
            var generator = fixture.Source("Generator", "HelloGenerator.cs");
            _debug.Breakpoints.Set(new BreakpointSpec(generator, DebuggeeFixture.Line(generator, "generate")));

            await _debug.LaunchAsync(plan, TestContext.Current.CancellationToken);
            var stop = await NextStopAsync(180);
            Assert.Equal("breakpoint", stop.Reason);
            Assert.Contains("Customer", (await _debug.EvaluateAsync("list", "watch", TestContext.Current.CancellationToken)).Result, StringComparison.Ordinal);

            await _debug.ContinueAsync();
            DebuggeeFixture.AssertExitCode(0, await _ended.Task.WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(output, "GeneratorTarget.dll")), "the compiler wrote its output to the temporary folder:\n" + Output);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void The_compiler_arguments_are_rewritten_into_the_output_folder()
    {
        var rewritten = RoslynComponents.Rewrite(["/out:obj\\Debug\\App.dll", "/refout:obj\\refint\\App.dll", "/refonly", "/doc:obj\\App.xml", "/analyzer:\"C:\\Program Files\\x.dll\"", "Program.cs"], Path.Combine("C:", "out dir"));
        Assert.Equal(4, rewritten.Count);
        Assert.Equal("/out:\"" + Path.Combine("C:", "out dir", "App.dll") + "\"", rewritten[0]);
        Assert.Equal("/doc:\"" + Path.Combine("C:", "out dir", "App.xml") + "\"", rewritten[1]);
        Assert.Equal("/analyzer:\"C:\\Program Files\\x.dll\"", RoslynComponents.Quote(rewritten[2]));
        Assert.Equal("Program.cs", rewritten[3]);
        Assert.Equal("\"a b\\\\\"", RoslynComponents.Quote("a b\\"));
    }
}
