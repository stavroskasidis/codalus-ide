using System.Diagnostics;
using System.Threading.Channels;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Debugging.Tests;

/// <summary>
/// Debugging in a console window of its own (Windows' default, as a classic IDE debugs console and web apps) on the real
/// netcoredbg: the program starts in the window held by the startup hook, the debugger attaches, it stops at a
/// breakpoint, and Stop ends it and closes the window. It opens a window on the desktop, so it runs on CI and with
/// <c>DAEDALUS_TEST_CONSOLE_WINDOWS=1</c>.
/// </summary>
public sealed class ConsoleWindowLaunchTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private readonly ConsoleWindowLauncher _windows = new();
    private DebugService _debug = null!;

    public ValueTask InitializeAsync()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Console windows are for Windows.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CI") == "true" || Environment.GetEnvironmentVariable("DAEDALUS_TEST_CONSOLE_WINDOWS") == "1",
            "It opens a console window on the desktop: set DAEDALUS_TEST_CONSOLE_WINDOWS=1 (CI runs it).");
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        _debug = new DebugService(() => DebuggeeFixture.Adapter)
        {
            ConsoleWindows = _windows,
            StartupHook = HeldLaunch.ResolveHook() ?? throw new InvalidOperationException("The startup hook isn't next to the tests."),
        };
        _debug.Stopped += (_, stop) => _stops.Writer.TryWrite(stop);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_debug is not null)
        {
            await _debug.DisposeAsync();
        }

        _windows.CloseAll();
    }

    [Fact]
    public async Task A_program_in_a_console_window_stops_at_a_breakpoint_and_Stop_closes_its_window()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } fromEnv
            ? fromEnv
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Select(d => Path.Combine(d, "dotnet.exe")).First(File.Exists);
        var plan = new LaunchPlan(host, string.Empty, debuggee.Folder, new Dictionary<string, string>(), null) { ArgumentList = ["exec", debuggee.Dll] };
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, DebuggeeFixture.Line(debuggee.Program, "print")));
        await _debug.LaunchAsync([new DebugLaunch("Debuggee", plan) { Console = DebugConsoleKind.Window }], TestContext.Current.CancellationToken);

        var process = Assert.Single(_debug.Processes);
        Assert.Equal(DebugProcess.ConsoleWindow, process.Kind);
        Assert.True(_debug.CanDetach);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var stop = await _stops.Reader.ReadAsync(timeout.Token);
        Assert.Equal("breakpoint", stop.Reason);

        // The window is cmd, running the program as its child.
        var program = Process.GetProcessById(_debug.ProcessId ?? throw new InvalidOperationException("No process id."));
        await _debug.StopAsync();
        Assert.Equal(DebugState.Idle, _debug.State);
        Assert.True(program.WaitForExit(30_000), "Stop ends the program");
    }
}
