using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DaedalusStudio.Debugging;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Shell.Tests;

/// <summary>Debugging in the shell (ADR 0007) on samples/Debugging, with the real netcoredbg.</summary>
public sealed class DebugTests
{
    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    [AvaloniaFact]
    public async Task F5_stops_at_a_breakpoint_and_the_debugger_windows_follow()
    {
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Debugging");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Debugging.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);

        // F9 on the [loop] line of an open editor.
        var program = Path.Combine(folder, "Debuggee", "Program.cs");
        var loop = Line(program, "loop");
        var editor = await f.Shell.OpenFileAsync(program, preview: false, new DaedalusStudio.LanguageServices.Protocol.Position(loop - 1, 0));
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        f.Shell.Run("debug.toggleBreakpoint");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.Breakpoints.At(program, loop) is not null);
        f.Shell.Debugger.Breakpoints.Set(f.Shell.Debugger.Breakpoints.At(program, loop)! with { Condition = "i == 1" });

        f.Shell.Run("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 180);
        // The context keys follow the state through Ui.Post.
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("debugStopped"), true));
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } && c.CurrentDebugLine() == loop);

        // Locals, Call Stack, Watch, the Debug Console and a data tip.
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r is { Name: "i", Value: "1" }));
        Assert.Contains(f.Shell.Locals.Roots, r => r is { Name: "total", Value: "1" });
        await ShellFixture.PumpUntil(() => f.Shell.CallStack.Frames.Count > 0);
        Assert.True(f.Shell.CallStack.Frames[0].IsTop);
        await f.Shell.Watch.AddAsync("total * 10");
        Assert.Equal("10", f.Shell.Watch.Roots.Single().Value);
        f.Shell.DebugConsole.Input = "numbers.Count";
        await f.Shell.DebugConsole.SubmitAsync();
        Assert.Contains("> numbers.Count\n5", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
        var controller = f.Shell.ActiveEditor!.Controller!;
        var text = controller.Editor.Document.Text;
        Assert.Equal("numbers", controller.ExpressionAt(text.IndexOf("numbers[i]", StringComparison.Ordinal) + 2));

        // F10 steps; the watch is evaluated again at the next stop.
        f.Shell.Run("debug.stepOver");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.Watch.Roots.Single().Value == "30", seconds: 60);

        // Remove the breakpoint, continue to the end: the session ends with the program's exit code.
        f.Shell.Debugger.Breakpoints.Clear();
        f.Shell.Run("debug.continue");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        Assert.Contains(OperatingSystem.IsMacOS() ? "exited with code" : "exited with code 15", f.Shell.DebugLog.GetText(), StringComparison.Ordinal); // netcoredbg reports 0 on macOS
        Assert.Contains("total=15 Ada is 36", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);

        // The debugger windows exist as views.
        f.Shell.Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DebugConsoleView>().Any(v => v.Text.Contains("total=15", StringComparison.Ordinal)));
        f.Shell.Layout.ShowToolWindow(BreakpointsViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<BreakpointsView>().Any());
    }
}
