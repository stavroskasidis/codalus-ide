using System.Threading.Channels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M54 (ADR 0086): which adapter debugs a .NET program. <c>debug.adapter</c> <c>codalus</c> sends launches and attaches
/// to Codalus IDE's own (<c>codalus-debughost --clr-dap</c>), and the Debug Console's first line says so; programs it
/// doesn't debug yet (x64 ones on Apple silicon, the integrated terminal) go to netcoredbg, and the line says why; left
/// alone, nothing changes. Not in the debug-codalus shard (eng/ci-debug-codalus.txt): its CODALUS_DEBUG_ADAPTER would
/// override the setting these tests choose.
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
            CoreClrAdapter = () => new DebugAdapterStartInfo(DebuggeeFixture.DotnetHost, [Path.Combine(AppContext.BaseDirectory, "codalus-debughost.dll"), CoreClrAdapter.AdapterArgument], AppContext.BaseDirectory),
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
        Assert.StartsWith("Debugging with the Codalus debugger (preview).", Output, StringComparison.Ordinal);
        await _debug.StopAsync();
    }

    [Fact]
    public async Task Programs_it_doesnt_debug_yet_go_to_netcoredbg_and_the_debug_console_says_why()
    {
        // An x64 program on an Arm64 machine (M33): netcoredbg 3.1.3's, which isn't here.
        var x64 = await Assert.ThrowsAnyAsync<Exception>(() => _debug.LaunchAsync(Plan() with { RunsAsX64 = true }, Ct));
        Assert.Contains("netcoredbg", x64.Message, StringComparison.Ordinal);
        Assert.Contains("Debugging with netcoredbg: it runs as x64 on Apple silicon", Output, StringComparison.Ordinal);

        // The integrated terminal: M54b's next step.
        var terminal = await Assert.ThrowsAnyAsync<Exception>(() => _debug.LaunchAsync([new DebugLaunch("Debuggee", Plan()) { Console = DebugConsoleKind.Terminal }], Ct));
        Assert.Contains("netcoredbg", terminal.Message, StringComparison.Ordinal);
        Assert.Contains("Debugging with netcoredbg: it runs in the integrated terminal", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Left_alone_netcoredbg_debugs_and_nothing_is_said()
    {
        _setting = null;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => _debug.LaunchAsync(Plan(), Ct));
        Assert.Contains("netcoredbg", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Debugging with", Output, StringComparison.Ordinal);
    }
}
