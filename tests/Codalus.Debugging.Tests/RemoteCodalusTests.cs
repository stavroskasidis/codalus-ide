using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Debugging.Remote;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M58b (ADR 0094): the Codalus debugger on another machine, through the pipe transports to the local stand-ins
/// (<see cref="RemoteFixture"/>). Its bundle (<c>codalus-debughost</c> with its own runtime,
/// <c>eng/get-codalus-debugger.sh</c>) is copied to the target on first use and started there with <c>--clr-dap</c>: an
/// attach over SSH and a launch, with what only this debugger does (a <c>new</c> expression, Set Next Statement, a hit
/// count it applies). A target without a bundle for its platform gets netcoredbg, and the first line says so.
/// </summary>
public sealed class RemoteCodalusTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private readonly TaskCompletionSource<int?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _output = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "codalus-remote-" + Guid.NewGuid().ToString("N")[..8]);
    private DebugService _debug = null!;

    /// <summary>The debugger on the stand-ins goes to /tmp, not the user's home.</summary>
    private const string TestHome = "/tmp/codalus-tests/debugger";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        // No debugger of this machine's: every session here goes through the target's.
        _debug = new DebugService(() => null) { CoreClrAdapter = () => null };
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

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
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

    private async Task<StopInfo> NextStopAsync(int seconds = 120)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            return await _stops.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The program did not stop. State: {_debug.State}. Output:\n{Output}");
        }
    }

    /// <summary>The SSH stand-in as a target, with this machine's bundle; skipped without either (a failure in CI, which must have the bundle).</summary>
    private SshTarget Target()
    {
        var ssh = RemoteFixture.FakeSsh(_folder);
        Assert.SkipWhen(ssh is null, "The SSH stand-in runs on Linux and macOS.");
        var platform = RemoteDebuggers.LocalPlatform;
        var bundle = platform is null ? null : RemoteDebuggers.CodalusBundle(platform);
        if (bundle is null && Environment.GetEnvironmentVariable("CI") == "true")
        {
            Assert.Fail($"The Codalus debugger's bundle for {platform} isn't built (eng/get-codalus-debugger.sh).");
        }

        Assert.SkipWhen(bundle is null, $"The Codalus debugger's bundle for {platform} isn't built (eng/get-codalus-debugger.sh).");
        return new SshTarget("stand-in.invalid", "tester", 2222, ssh!) { Home = TestHome };
    }

    private static async Task<RemoteDebugger> EnsureAsync(RemoteTarget target)
    {
        var log = new List<string>();
        var debugger = await new RemoteDebuggers(Path.Combine(Path.GetTempPath(), "codalus-remote-cache")).EnsureAsync(target, codalus: true, _ => Task.FromResult(true), new Progress<string>(log.Add), Ct);
        Assert.True(debugger.IsCodalus, string.Join('\n', log));
        Assert.EndsWith("/" + RemoteDebuggers.CodalusExecutable, debugger.Path, StringComparison.Ordinal);
        Assert.StartsWith(TestHome + "/codalus-", debugger.Path, StringComparison.Ordinal);
        return debugger;
    }

    [Fact]
    public async Task Attach_over_SSH_copies_the_bundle_once_and_debugs_with_what_only_this_debugger_does()
    {
        var target = Target();
        var debugger = await EnsureAsync(target);

        // The second time it is there: nothing is asked.
        var again = await new RemoteDebuggers(Path.Combine(Path.GetTempPath(), "codalus-remote-cache")).EnsureAsync(target, codalus: true, _ => throw new InvalidOperationException("asked again"), null, Ct);
        Assert.Equal(debugger, again);

        var signal = Path.Combine(_folder, "go");
        using var program = Process.Start(new ProcessStartInfo(DebuggeeFixture.DotnetHost, ["exec", debuggee.Dll, "--wait-for", signal]) { UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            RemoteProcess? process = null;
            for (var i = 0; i < 100 && process is null; i++)
            {
                process = (await RemoteShell.ProcessesAsync(target, Ct)).FirstOrDefault(p => p.Id == program.Id);
                if (process is null)
                {
                    await Task.Delay(200, Ct);
                }
            }

            Assert.NotNull(process);
            _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, DebuggeeFixture.Line(debuggee.Program, "done")));
            await _debug.AttachAsync(process.Id, Ct, new RemoteAttach(RemoteShell.Adapter(target, debugger), new SourceMap(), target.Display), "Debuggee");
            Assert.Equal(target.Display, _debug.Processes.Single().Target);
            Assert.StartsWith("Debugging with the Codalus debugger.", Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(signal, "go", Ct);

            var stop = await NextStopAsync();
            Assert.Equal("breakpoint", stop.Reason);
            Assert.Equal(debuggee.Program, _debug.SelectedFrame?.Source?.Path);
            Assert.Equal("15", (await _debug.EvaluateAsync("total", "watch", Ct)).Result);

            // This debugger's own, through the mapping adapter: an object made in the program, and Set Next Statement.
            Assert.Equal("2", (await _debug.EvaluateAsync("new System.Text.StringBuilder(\"ab\").Length", "watch", Ct)).Result);
            Assert.True(_debug.CanSetNextStatement);
            await _debug.DetachAsync();
            Assert.Equal(DebugState.Idle, _debug.State);
            await program.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(15, program.ExitCode);
        }
        finally
        {
            if (!program.HasExited)
            {
                program.Kill();
            }
        }
    }

    [Fact]
    public async Task A_launch_over_SSH_starts_the_program_there_and_the_debugger_counts_the_hits()
    {
        var target = Target();
        var debugger = await EnsureAsync(target);
        var depth = Path.Combine(debuggee.Folder, "Depth.cs");
        var arguments = new LaunchArguments(DebuggeeFixture.DotnetHost, ["exec", debuggee.Dll, "--depth"], debuggee.Folder, new Dictionary<string, string> { ["GREETING"] = "from the target" });
        var plan = new Workbench.Build.LaunchPlan(DebuggeeFixture.DotnetHost, $"exec \"{debuggee.Dll}\" --depth", debuggee.Folder, new Dictionary<string, string>(), null);

        // A hit count: applied by the debugger on the target, so the first stop is the third pass.
        _debug.Breakpoints.Set(new BreakpointSpec(depth, DebuggeeFixture.Line(depth, "depth-loop"), HitCondition: "== 3"));
        await _debug.LaunchAsync([new DebugLaunch("Debuggee", plan) { Remote = new RemoteLaunch(RemoteShell.Adapter(target, debugger), arguments, new SourceMap(), target.Display) }], Ct);
        var stop = await NextStopAsync();
        Assert.Equal("breakpoint", stop.Reason);
        Assert.Equal(depth, _debug.SelectedFrame?.Source?.Path);
        Assert.Equal("\"from the target\"", (await _debug.EvaluateAsync("System.Environment.GetEnvironmentVariable(\"GREETING\")", "watch", Ct)).Result);
        Assert.Equal(1, _stops.Reader.Count + 1); // one stop: the two passes before it never stopped the program
        _debug.Breakpoints.Clear();
        await _debug.ContinueAsync();
        await _ended.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
    }

    [Fact]
    public async Task Display_attributes_are_read_on_the_target_by_the_bundles_companion()
    {
        // The modules are the target's, so the companion in the bundle there reads them: [DebuggerDisplay] and
        // [DebuggerBrowsable] apply in a remote session as in a local one.
        var target = Target();
        var debugger = await EnsureAsync(target);
        Assert.Null(RemoteShell.Companion(target, new RemoteDebugger("/tmp/netcoredbg", IsCodalus: false)));
        await using var companion = new DebugCompanion(() => RemoteShell.Companion(target, debugger));
        _debug.Displays = new DebuggerDisplays(async (type, modules, ct) => await companion.TypeDisplayAsync(type, modules, ct)) { Budget = TimeSpan.FromSeconds(60) };

        var depth = Path.Combine(debuggee.Folder, "Depth.cs");
        var arguments = new LaunchArguments(DebuggeeFixture.DotnetHost, ["exec", debuggee.Dll, "--depth"], debuggee.Folder, []);
        var plan = new Workbench.Build.LaunchPlan(DebuggeeFixture.DotnetHost, $"exec \"{debuggee.Dll}\" --depth", debuggee.Folder, new Dictionary<string, string>(), null);
        _debug.Breakpoints.Set(new BreakpointSpec(depth, DebuggeeFixture.Line(depth, "depth-start")));
        await _debug.LaunchAsync([new DebugLaunch("Debuggee", plan) { Remote = new RemoteLaunch(RemoteShell.Adapter(target, debugger), arguments, new SourceMap(), target.Display) }], Ct);
        await NextStopAsync();

        var locals = await _debug.VariablesAsync((await _debug.ScopesAsync(_debug.SelectedFrame!))[0].VariablesReference);
        var order = Assert.Single(locals, v => v.Name == "order");
        Assert.Equal("Order 7: Ada (1 lines)", order.Value);
        var members = await _debug.VariablesAsync(order.VariablesReference);
        Assert.Contains(members, m => m.Name == "Id");
        Assert.DoesNotContain(members, m => m.Name is "_version" or "Internal");
        await _debug.StopAsync();
    }

    [Fact]
    public async Task A_target_without_a_bundle_for_its_platform_gets_netcoredbg_and_is_told_so()
    {
        var ssh = RemoteFixture.FakeSsh(_folder);
        Assert.SkipWhen(ssh is null, "The SSH stand-in runs on Linux and macOS.");
        var log = new List<string>();
        var none = await RemoteDebuggers.EnsureCodalusAsync(new NoBundleTarget(new SshTarget("stand-in.invalid", "tester", 2222, ssh!)) { Home = TestHome }, _ => Task.FromResult(true), new Progress<string>(log.Add), Ct);
        Assert.Null(none);
        for (var i = 0; i < 100 && log.Count == 0; i++)
        {
            await Task.Delay(20, Ct); // Progress<T> posts its lines
        }

        Assert.Contains(log, l => l.Contains("netcoredbg debugs there", StringComparison.Ordinal));
    }

    /// <summary>The stand-in, answering as a platform the Codalus debugger has no bundle for.</summary>
    private sealed record NoBundleTarget(SshTarget Inner) : RemoteTarget
    {
        public override string Display => Inner.Display;

        public override (string Program, IReadOnlyList<string> Arguments) Shell(string script) => Inner.Shell(script.Replace("$(uname -s) $(uname -m)", "Plan9 mips", StringComparison.Ordinal));
    }
}
