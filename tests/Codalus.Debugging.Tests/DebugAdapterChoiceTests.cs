using System.Threading.Channels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// Which adapter debugs a .NET program (M54, ADR 0086; the default since M58, ADR 0094). Left alone, and with
/// <c>debug.adapter</c> <c>codalus</c>, launches and attaches go to Codalus IDE's own (<c>codalus-debughost --clr-dap</c>),
/// and the Debug Console's first line says so; programs it doesn't debug yet (x64 ones on Apple silicon) go to
/// netcoredbg, and the line says why; <c>netcoredbg</c> chooses the fallback, and nothing is said. Not in the
/// debug-netcoredbg shard (eng/ci-debug-parity.txt): its CODALUS_DEBUG_ADAPTER would override the setting these
/// tests choose.
/// </summary>
public sealed class DebugAdapterChoiceTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private readonly System.Text.StringBuilder _output = new();
    private string? _setting = DebugService.CodalusAdapter;
    private DebugService _debug = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(DebugService.AdapterVariable) is { Length: > 0 }, "CODALUS_DEBUG_ADAPTER overrides the setting these tests choose.");

        // No netcoredbg: a session that goes to it fails, and says so.
        _debug = new DebugService(() => null, () => null)
        {
            AdapterSetting = () => _setting,
            CoreClrAdapter = DebuggeeFixture.CodalusAdapter,
        };
        _debug.Stopped += (_, stop) => _stops.Writer.TryWrite(stop);
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

    private LaunchPlan Plan() => new(DebuggeeFixture.DotnetHost, $"exec \"{debuggee.Dll}\"", debuggee.Folder, new Dictionary<string, string>(), null);

    [Fact]
    public async Task The_setting_sends_a_launch_to_the_codalus_debugger_and_the_debug_console_says_so()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, DebuggeeFixture.Line(debuggee.Program, "loop")));
        await _debug.LaunchAsync(Plan(), Ct);
        var stop = await _stops.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal("breakpoint", stop.Reason);
        Assert.Equal("5", (await _debug.EvaluateAsync("numbers.Count", "watch", Ct)).Result);
        Assert.StartsWith("Debugging with the Codalus debugger.", Output, StringComparison.Ordinal);
        await _debug.StopAsync();
    }

    [Fact]
    public async Task Programs_it_doesnt_debug_yet_go_to_netcoredbg_and_the_debug_console_says_why()
    {
        // An x64 program on an Arm64 machine (M33): netcoredbg 3.1.3's, which isn't here.
        var x64 = await Assert.ThrowsAnyAsync<Exception>(() => _debug.LaunchAsync(Plan() with { RunsAsX64 = true }, Ct));
        Assert.Contains("netcoredbg", x64.Message, StringComparison.Ordinal);
        Assert.Contains("Debugging with netcoredbg: it runs as x64 on Apple silicon", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_terminal_here_a_terminal_launch_uses_the_debug_console()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, DebuggeeFixture.Line(debuggee.Program, "loop")));
        await _debug.LaunchAsync([new DebugLaunch("Debuggee", Plan()) { Console = DebugConsoleKind.Terminal }], Ct);
        await _stops.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Contains("Debugging with the Codalus debugger.", Output, StringComparison.Ordinal);
        Assert.Contains("The integrated terminal isn't available here", Output, StringComparison.Ordinal);
        Assert.Equal(DebugProcess.Launched, Assert.Single(_debug.Processes).Kind);
        await _debug.StopAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Codalus")]
    [InlineData("something-else")]
    public async Task Left_alone_the_codalus_debugger_debugs(string? setting)
    {
        // The default (M58, ADR 0094): only "netcoredbg" chooses the fallback.
        _setting = setting;
        Assert.True(_debug.UsesCodalusAdapter);
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, DebuggeeFixture.Line(debuggee.Program, "loop")));
        await _debug.LaunchAsync(Plan(), Ct);
        await _stops.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.StartsWith("Debugging with the Codalus debugger.", Output, StringComparison.Ordinal);
        await _debug.StopAsync();
    }

    [Theory]
    [InlineData("netcoredbg")]
    [InlineData(" NetCoreDbg ")]
    public async Task The_setting_chooses_netcoredbg_the_fallback_and_nothing_is_said(string setting)
    {
        // No netcoredbg here: the launch says it is missing, which shows where it went.
        _setting = setting;
        Assert.False(_debug.UsesCodalusAdapter);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => _debug.LaunchAsync(Plan(), Ct));
        Assert.Contains("netcoredbg", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Debugging with", Output, StringComparison.Ordinal);
    }
}
