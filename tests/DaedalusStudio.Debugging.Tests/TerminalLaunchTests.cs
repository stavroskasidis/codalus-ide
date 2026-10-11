using System.Diagnostics;
using System.Threading.Channels;
using DaedalusStudio.Platform;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Terminal;

namespace DaedalusStudio.Debugging.Tests;

/// <summary>
/// Debugging in the integrated terminal (M14c, ADR 0039) on the real netcoredbg and a real pseudo-terminal: the program
/// starts there held by the startup hook, the debugger attaches, the console takes input, and Detach All leaves it
/// running (the M14b gap).
/// </summary>
public sealed class TerminalLaunchTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private readonly TaskCompletionSource<int?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TerminalService _terminals = new(PlatformInfo.Current, null);
    private TerminalLauncher _launcher = null!;
    private DebugService _debug = null!;

    public ValueTask InitializeAsync()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        _launcher = new TerminalLauncher(_terminals);
        _debug = new DebugService(() => DebuggeeFixture.Adapter)
        {
            Terminal = _launcher,
            StartupHook = HeldLaunch.ResolveHook() ?? throw new InvalidOperationException("The startup hook isn't next to the tests."),
        };
        _debug.Stopped += (_, stop) => _stops.Writer.TryWrite(stop);
        _debug.Ended += (_, code) => _ended.TrySetResult(code);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_debug is not null)
        {
            await _debug.DisposeAsync();
        }

        _terminals.Dispose();
    }

    private static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .First(File.Exists);

    private LaunchPlan Plan(params string[] arguments) =>
        new(DotnetHost, string.Empty, debuggee.Folder, new Dictionary<string, string>(), null) { ArgumentList = ["exec", debuggee.Dll, .. arguments] };

    private async Task<StopInfo> NextStopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            return await _stops.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The program did not stop. State: {_debug.State}. Terminal:\n{Screen()}");
        }
    }

    private string Screen()
    {
        if (_launcher.Last is not { } session)
        {
            return "(no terminal)";
        }

        lock (session.Terminal.SyncRoot)
        {
            return string.Join('\n', Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText().TrimEnd()));
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string what, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(what);
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_program_in_the_terminal_reads_input_stops_at_a_breakpoint_and_runs_on_after_Detach_All()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "netcoredbg can't attach to a program held at startup on macOS (0x80004005, found on the Mac); the shell uses the Debug Console there.");
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, DebuggeeFixture.Line(debuggee.Program, "loop")));
        await _debug.LaunchAsync([new DebugLaunch("Debuggee", Plan("--read")) { Console = DebugConsoleKind.Terminal }], TestContext.Current.CancellationToken);

        var session = _launcher.Last ?? throw new InvalidOperationException("No terminal started.");
        var process = Assert.Single(_debug.Processes);
        Assert.Equal(DebugProcess.Terminal, process.Kind);
        Assert.True(_debug.CanDetach);
        Assert.False(_debug.IsAttached); // a launch, for Stop and Restart

        // The console is the terminal's: what is typed there reaches the program.
        session.Write("hello from the terminal\r");
        var stop = await NextStopAsync();
        Assert.Equal("breakpoint", stop.Reason);
        await UntilAsync(() => Screen().Contains("echo: hello from the terminal", StringComparison.Ordinal), "the program echoes the line (the terminal may draw it after the stop):\n" + Screen(), 30);
        Assert.Equal("0", (await _debug.EvaluateAsync("i", "watch", TestContext.Current.CancellationToken)).Result);
        Assert.Equal(process.Id ?? _debug.ProcessId, _debug.ProcessId);

        // Detach All: the program goes on in its terminal, printing and exiting with its code.
        await _debug.DetachAsync();
        Assert.Equal(DebugState.Idle, _debug.State);
        await UntilAsync(() => session.ExitCode is not null, "the program exits after Detach All:\n" + Screen(), 90);
        Assert.Equal(15, session.ExitCode);
        Assert.Contains("done", Screen(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detach_All_from_an_apphost_stopped_at_a_breakpoint_leaves_it_running()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "netcoredbg can't attach to a program held at startup on macOS (0x80004005, found on the Mac); the shell uses the Debug Console there.");
        // The apphost, as launch profiles run it: netcoredbg used to end it when detaching while it was stopped.
        var apphost = Path.Combine(Path.GetDirectoryName(debuggee.Dll)!, OperatingSystem.IsWindows() ? "Debuggee.exe" : "Debuggee");
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, DebuggeeFixture.Line(debuggee.Program, "loop")));
        _debug.ExceptionSettings.SetAll(true);
        await _debug.LaunchAsync([new DebugLaunch("Debuggee", new LaunchPlan(apphost, "--none", debuggee.Folder, new Dictionary<string, string>(), null)) { Console = DebugConsoleKind.Terminal }], TestContext.Current.CancellationToken);
        var session = _launcher.Last ?? throw new InvalidOperationException("No terminal started.");
        await NextStopAsync();

        await _debug.DetachAsync();
        await UntilAsync(() => session.ExitCode is not null, "the program exits after Detach All:\n" + Screen(), 90);
        Assert.Equal(15, session.ExitCode);
        Assert.Contains("caught boom", Screen(), StringComparison.Ordinal); // past the breakpoint and the exception settings
    }

    [Fact]
    public async Task Stop_ends_a_program_launched_in_the_terminal()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "netcoredbg can't attach to a program held at startup on macOS (0x80004005, found on the Mac); the shell uses the Debug Console there.");
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, DebuggeeFixture.Line(debuggee.Program, "print")));
        await _debug.LaunchAsync([new DebugLaunch("Debuggee", Plan()) { Console = DebugConsoleKind.Terminal }], TestContext.Current.CancellationToken);
        await NextStopAsync();
        var pid = _debug.ProcessId ?? throw new InvalidOperationException("No process id.");

        await _debug.StopAsync();
        await UntilAsync(() =>
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }, "Stop ends the program", 30);
    }

    [Fact]
    public void The_hook_extends_existing_startup_hooks_and_names_the_folder()
    {
        var environment = HeldLaunch.Environment(new Dictionary<string, string> { ["DOTNET_STARTUP_HOOKS"] = "other.dll", ["A"] = "1" }, "hook.dll", "folder");
        Assert.Equal("hook.dll" + Path.PathSeparator + "other.dll", environment["DOTNET_STARTUP_HOOKS"]);
        Assert.Equal("folder", environment[HeldLaunch.WaitVariable]);
        Assert.Equal("1", environment["A"]);
        Assert.Equal("App.dll", DebugService.EntryModule(new LaunchPlan("dotnet", "exec \"C:\\x\\App.dll\" a", ".", new Dictionary<string, string>(), null)));
        Assert.Equal("App.dll", DebugService.EntryModule(new LaunchPlan("/bin/App", string.Empty, ".", new Dictionary<string, string>(), null)));
    }
}
