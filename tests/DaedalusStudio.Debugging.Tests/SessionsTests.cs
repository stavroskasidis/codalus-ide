using System.Text;
using System.Threading.Channels;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Debugging.Tests;

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
    public async Task A_source_generator_stops_at_a_breakpoint_when_the_compiler_runs_on_its_target()
    {
        var (inputs, sdk) = await fixture.CompilerInputsAsync("GeneratorTarget");
        var output = Path.Combine(Path.GetTempPath(), "daedalus-roslyn-test-" + Guid.NewGuid().ToString("N")[..8]);
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
