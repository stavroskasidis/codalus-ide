using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Shell.Editor;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// Inline values while debugging (M62, ADR 0101): at a stop, the language service names what each line reads and the
/// editor draws the values after the lines, from the frame's scopes and evaluated fields; a changed value is marked; they
/// clear when the program goes on, and the setting turns them off.
/// </summary>
public sealed class InlineValuesTests
{
    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    [AvaloniaFact]
    public async Task A_stop_draws_each_lines_values_and_marks_the_ones_a_step_changed()
    {
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Debugging");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Debugging.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count > 0, seconds: 90);
        var program = Path.Combine(folder, "Debuggee", "Program.cs");
        await f.Shell.OpenFileAsync(program);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready" }, seconds: 180);
        var loop = Line(program, "loop");
        f.Shell.Debugger.Breakpoints.Toggle(program, loop);

        await f.RunAsync("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger is { State: DebugState.Stopped } && f.Shell.ActiveEditor?.Controller?.CurrentDebugLine() == loop, seconds: 180);
        var controller = f.Shell.ActiveEditor!.Controller!;
        await ShellFixture.PumpUntil(() => controller.InlineValueLines.Any(l => l.Line == loop), seconds: 60);

        // The loop's line: total and i (numbers too), from the frame's scopes; the lines above it up to the method's start.
        var atLoop = controller.InlineValueLines.Single(l => l.Line == loop).Values;
        Assert.Contains(atLoop, v => v.Name == "total" && v.Value == "0");
        Assert.Contains(atLoop, v => v.Name == "i" && v.Value == "0");
        Assert.Contains(controller.InlineValueLines, l => l.Line == loop - 4 && l.Values.Any(v => v.Name == "numbers"));
        Assert.DoesNotContain(controller.InlineValueLines, l => l.Line > loop);
        Assert.StartsWith("total = 0", InlineValuesRenderer.Text(controller.InlineValueLines.Single(l => l.Line == loop)), StringComparison.Ordinal);

        // The next iteration: total changed, numbers didn't.
        await f.Shell.Debugger.ContinueAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Debugger is { State: DebugState.Stopped } && controller.InlineValueLines.Any(l => l.Line == loop && l.Values.Any(v => v.Name == "total" && v.Value == "1")), seconds: 60);
        var next = controller.InlineValueLines.Single(l => l.Line == loop).Values;
        Assert.True(next.Single(v => v.Name == "total").Changed);
        Assert.False(controller.InlineValueLines.Single(l => l.Line == loop - 4).Values.Single(v => v.Name == "numbers").Changed);

        // Off: nothing is drawn at the next stop.
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.InlineValuesKey, false, SettingScope.User, CancellationToken.None);
        await f.Shell.Debugger.ContinueAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Debugger is { State: DebugState.Stopped } && controller.CurrentDebugLine() == loop, seconds: 60);
        await controller.InlineValuesLoaded;
        Assert.Empty(controller.InlineValueLines);

        // Going on clears them.
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.InlineValuesKey, true, SettingScope.User, CancellationToken.None);
        f.Shell.Debugger.Breakpoints.Clear();
        await f.Shell.Debugger.ContinueAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State != DebugState.Stopped, seconds: 60);
        Assert.Empty(controller.InlineValueLines);
        await f.Shell.Debugger.StopAsync();
    }

    [AvaloniaFact]
    public void A_long_value_is_cut_with_an_ellipsis()
    {
        var line = new InlineValueLine(3, [("text", new string('x', 200), false), ("n", "1\n2", true)]);
        var text = InlineValuesRenderer.Text(line);
        Assert.Contains("…", text, StringComparison.Ordinal);
        Assert.EndsWith("n = 1 2", text, StringComparison.Ordinal);
        Assert.Equal(InlineValueLine.MaxValueLength, InlineValueLine.Shorten(new string('x', 200)).Length);
    }
}
