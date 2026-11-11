using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M58 (ADR 0094) in the shell, under the Codalus debugger on samples/Debugging (<c>--features</c>): the commands for
/// what it does that netcoredbg doesn't. Set Next Statement, Step Into Specific, the value a method returned in Locals,
/// and Freeze and Thaw in the Threads window; a logpoint and a hit count that never stop the program on the way.
/// </summary>
public sealed class DebugCodalusTests
{
    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    private static async Task<string> OpenAsync(ShellFixture f)
    {
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugAdapterSetting, DebugService.CodalusAdapter, SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("Debugging");
        var properties = Path.Combine(folder, "Debuggee", "Properties");
        Directory.CreateDirectory(properties);
        await File.WriteAllTextAsync(Path.Combine(properties, "launchSettings.json"), """
            { "profiles": { "Features": { "commandName": "Project", "commandLineArgs": "--features" } } }
            """, TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Debugging.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);
        return Path.Combine(folder, "Debuggee", "Features.cs");
    }

    private static Task StoppedAtAsync(ShellFixture f, string file, string marker, int seconds = 60) =>
        ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.ActiveEditor is { Controller: { } c } editor
            && string.Equals(editor.Model.Path, file, StringComparison.OrdinalIgnoreCase) && c.CurrentDebugLine() == Line(file, marker), seconds);

    [AvaloniaFact]
    public async Task Set_next_statement_step_into_specific_returned_values_and_frozen_threads_work_from_their_commands()
    {
        await using var f = ShellFixture.Create();
        var features = await OpenAsync(f);
        var breakpoints = f.Shell.Debugger.Breakpoints;

        // A hit count and a logpoint on the way: applied by the debugger, so the first stop is the breakpoint's.
        breakpoints.Set(new BreakpointSpec(features, Line(features, "features-loop"), HitCondition: "== 4"));
        breakpoints.Set(new BreakpointSpec(features, Line(features, "features-start"), LogMessage: "starting in $FUNCTION"));
        await f.RunAsync("debug.start");
        await StoppedAtAsync(f, features, "features-loop", seconds: 180);
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r is { Name: "i", Value: "3" }));
        Assert.Contains("starting in Debuggee.Features.Run", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
        Assert.Contains("Codalus debugger", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);

        // The commands are offered: the stopped thread's debugger says it can.
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("debugCanSetNextStatement"), true));
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("debugCanStepIntoSpecific"));
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("debugCanFreezeThreads"));

        // Set Next Statement, from the caret: on to the line with the nested calls, the rest of the loop not run.
        breakpoints.Clear();
        var editor = f.Shell.ActiveEditor!;
        editor.Controller!.Editor.TextArea.Caret.Line = Line(features, "features-nested");
        await f.RunAsync("debug.setNextStatement");
        await StoppedAtAsync(f, features, "features-nested");
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r is { Name: "count", Value: "3" }));

        // A line of another method: refused, and the status bar says why.
        Assert.False(await f.Shell.SetNextStatementAsync(features, Line(features, "features-measure-body")));
        Assert.Contains("a line of the method the thread is in", f.Shell.StatusBar.Message, StringComparison.Ordinal);
        Assert.Equal(Line(features, "features-nested"), f.Shell.ActiveEditor!.Controller!.CurrentDebugLine());

        // Step Into Specific: the line's three calls; into the outermost, the two inner ones run without a stop.
        var targets = await f.Shell.Debugger.StepInTargetsAsync();
        Assert.Equal(["Debuggee.Features.Twice", "Debuggee.Features.Join", "Debuggee.Features.Shout"], targets.Select(t => t.Label));
        var stepping = f.Shell.StepIntoSpecificAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen && f.Shell.Palette.Rows.Count == 3);
        f.Shell.Palette.Accept(f.Shell.Palette.Rows.Single(r => r.Label == "Debuggee.Features.Shout"));
        await stepping;
        await StoppedAtAsync(f, features, "features-shout");
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r is { Name: "value", Value: "\"a-bb\"" }));

        // Step Out: what Shout returned, first in Locals.
        await f.RunAsync("debug.stepOut");
        await StoppedAtAsync(f, features, "features-nested");
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Count > 0 && f.Shell.Locals.Roots[0] is { Name: "Debuggee.Features.Shout returned", Value: "\"A-BB!\"" });

        // The Threads window: freeze the ticker, and its counter stands still over two steps; thaw it, and it runs.
        await f.Shell.RunToLineAsync(features, Line(features, "features-freeze"));
        await StoppedAtAsync(f, features, "features-freeze");
        f.Shell.Layout.ShowToolWindow(ThreadsViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ThreadsView>().Any() && f.Shell.ThreadList.Rows.Any(r => r.Name == "Ticker"));
        var ticker = f.Shell.ThreadList.Rows.Single(r => r.Name == "Ticker");
        Assert.True(f.Shell.ThreadList.CanFreeze);
        await f.Shell.ThreadList.FreezeAsync(ticker, frozen: true);
        Assert.Null(f.Shell.ThreadList.FreezeProblem);
        Assert.True(ticker.IsFrozen);
        Assert.Equal("Frozen", ticker.FrozenText);
        Assert.Contains("frozen", ticker.AccessibleName, StringComparison.Ordinal);
        var before = (await f.Shell.Debugger.EvaluateAsync("s_ticks", "watch", TestContext.Current.CancellationToken)).Result;
        await f.RunAsync("debug.stepOver");
        await StoppedAtAsync(f, features, "features-frozen");
        await f.RunAsync("debug.stepOver");
        await StoppedAtAsync(f, features, "features-thawed");
        Assert.Equal(before, (await f.Shell.Debugger.EvaluateAsync("s_ticks", "watch", TestContext.Current.CancellationToken)).Result);

        // Still frozen at the next stop, as the window shows again; then thawed.
        await ShellFixture.PumpUntil(() => f.Shell.ThreadList.Rows.Any(r => r is { Name: "Ticker", IsFrozen: true }));
        ticker = f.Shell.ThreadList.Rows.Single(r => r.Name == "Ticker");
        await f.Shell.ThreadList.FreezeAsync(ticker, frozen: false);
        Assert.False(ticker.IsFrozen);
        await f.RunAsync("debug.stepOver");
        await StoppedAtAsync(f, features, "features-stop");
        Assert.NotEqual(before, (await f.Shell.Debugger.EvaluateAsync("s_ticks", "watch", TestContext.Current.CancellationToken)).Result);

        // To the end: the program's own exit code.
        await f.RunAsync("debug.continue");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        Assert.Contains("features done 3", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
        Assert.Contains("exited with code 3", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Under_netcoredbg_the_commands_are_off_and_breakpoints_behave_as_before()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CODALUS_DEBUG_ADAPTER") == DebugService.CodalusAdapter, "The debug-codalus shard runs every session under the Codalus debugger.");
        Assert.SkipWhen(Codalus.Debugging.DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        await using var f = ShellFixture.Create();
        var features = await OpenAsync(f);
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugAdapterSetting, DebugService.NetcoredbgAdapter, SettingScope.User, CancellationToken.None);

        // The IDE applies the hit count and the message, as it always did for netcoredbg.
        var breakpoints = f.Shell.Debugger.Breakpoints;
        breakpoints.Set(new BreakpointSpec(features, Line(features, "features-loop"), HitCondition: "== 4"));
        breakpoints.Set(new BreakpointSpec(features, Line(features, "features-start"), LogMessage: "starting in $FUNCTION"));
        await f.RunAsync("debug.start");
        await StoppedAtAsync(f, features, "features-loop", seconds: 180);
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r is { Name: "i", Value: "3" }));
        Assert.Contains("starting in ", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);

        Assert.Equal(false, f.Shell.Workbench.Context.GetValue("debugCanSetNextStatement"));
        Assert.Equal(false, f.Shell.Workbench.Context.GetValue("debugCanStepIntoSpecific"));
        Assert.Equal(false, f.Shell.Workbench.Context.GetValue("debugCanFreezeThreads"));
        Assert.Empty(await f.Shell.Debugger.StepInTargetsAsync());
        Assert.False(await f.Shell.SetNextStatementAsync(features, Line(features, "features-nested")));
        Assert.Contains("can't set the next statement", f.Shell.StatusBar.Message, StringComparison.Ordinal);
        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
    }
}
