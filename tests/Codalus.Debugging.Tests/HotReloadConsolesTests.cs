using System.Threading.Channels;
using Codalus.Platform;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Terminal;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M55 (ADR 0086's phase 2): edits apply to a program the Codalus debugger runs in the integrated terminal, and in a
/// console window of its own (Windows), through <see cref="DebugHotReload"/> as the shell drives it. The launcher starts
/// both, so their modules get Edit and Continue's JIT flags as they load, as a launch's do. Each stops on a line only the
/// edit's version has. The console window opens on the desktop, so it runs on CI and with
/// <c>CODALUS_TEST_CONSOLE_WINDOWS=1</c>.
/// </summary>
[Collection(HotReloadTests.Name)]
public sealed class HotReloadConsolesTests(HotReloadFixture fixture) : IAsyncLifetime
{
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private readonly TerminalService _terminals = new(PlatformInfo.Current, null);
    private readonly ConsoleWindowLauncher _windows = new();
    private DebugService _debug = null!;
    private DebugHotReload _edits = null!;

    private HotReloadFixture.Source CSharp => fixture.CSharp;

    public ValueTask InitializeAsync()
    {
        _debug = new DebugService(() => DebuggeeFixture.Adapter)
        {
            CoreClrAdapter = DebuggeeFixture.CodalusAdapter,
            AdapterSetting = () => DebugService.CodalusAdapter,
            Terminal = new TerminalLauncher(_terminals),
            ConsoleWindows = _windows,
            StartupHook = HeldLaunch.ResolveHook(),
        };
        _debug.Stopped += (_, stop) => _stops.Writer.TryWrite(stop);
        _edits = new DebugHotReload(_debug, () => fixture.Client);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _debug.DisposeAsync();
        await _edits.DisposeAsync();
        _windows.CloseAll();
        _terminals.Dispose();
        await fixture.RestoreAsync();
    }

    [Fact]
    public Task An_edit_applies_to_a_program_in_the_integrated_terminal() => EditAsync(DebugConsoleKind.Terminal);

    [Fact]
    public Task An_edit_applies_to_a_program_in_a_console_window()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Console windows are for Windows.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CI") == "true" || Environment.GetEnvironmentVariable("CODALUS_TEST_CONSOLE_WINDOWS") == "1",
            "It opens a console window on the desktop: set CODALUS_TEST_CONSOLE_WINDOWS=1 (CI runs it).");
        return EditAsync(DebugConsoleKind.Window);
    }

    private async Task EditAsync(DebugConsoleKind console)
    {
        var plan = new LaunchPlan(DebuggeeFixture.DotnetHost, string.Empty, CSharp.Folder, new Dictionary<string, string>(), null) { ArgumentList = ["exec", CSharp.Dll] };
        _debug.Breakpoints.Set(new BreakpointSpec(CSharp.Path, CSharp.Line("call")));
        await _debug.LaunchAsync([new DebugLaunch("HotReload", plan) { Console = console }], TestContext.Current.CancellationToken);
        Assert.Equal("breakpoint", (await NextStopAsync()).Reason);
        await UntilAsync(() => _edits.IsActive, "the Hot Reload session");

        // Message(0) not called yet: the edit adds a line, which a breakpoint stops on.
        await CSharp.EditAsync("        return text;", "        var edited = text + \" in the console\"; // [console]\n        return edited;");
        var outcome = await _edits.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.True(outcome.Kind == HotReloadOutcome.Applied, $"{outcome.Kind}: {outcome.Message} {string.Join("; ", outcome.Diagnostics.Select(d => d.Id + " " + d.Message))}");
        _debug.Breakpoints.Clear();
        _debug.Breakpoints.Set(new BreakpointSpec(CSharp.Path, CSharp.Line("console")));
        await _debug.ContinueAsync();
        Assert.Equal("breakpoint", (await NextStopAsync()).Reason);
        Assert.Equal(CSharp.Line("console"), _debug.Frames[0].Line);
        await _debug.StopAsync();
    }

    private async Task<StopInfo> NextStopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            return await _stops.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The program did not stop. State: {_debug.State}; {_debug.Describe()}");
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(60), "Timed out waiting for " + what);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
