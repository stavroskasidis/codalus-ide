using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// XAML breakpoints, break at a failed binding, Break When Value Changes and the style explanation (M64b, ADR 0103)
/// on samples/Avalonia debugged from the IDE (F5, the Codalus debugger) with the live agent: the breakpoints in
/// MainWindow.axaml's margin stop the app at the element's creation and at the attribute's set, shown at their XAML
/// lines; a failed binding stops as an exception does with the setting on, and not with it off; a property watched in
/// the Live Property Explorer stops the app when it changes; the explorer says why the fixture's styles apply.
/// </summary>
public sealed class XamlBreakpointShellTests
{
    private const string CountMarker = "Text=\"{Binding Count}\"";

    private static bool NoDisplay =>
        OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static int LineOf(string file, string marker) => Array.FindIndex(File.ReadAllLines(file), l => l.Contains(marker, StringComparison.Ordinal)) + 1;

    /// <summary>The fixture with a counter whose Text attribute has a line of its own, and a binding that fails at run time.</summary>
    private static async Task<(string Window, string App)> OpenAsync(ShellFixture f, bool breakOnFailedBinding)
    {
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugAdapterSetting, DebugService.CodalusAdapter, SettingScope.User, CancellationToken.None);
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.BreakOnFailedBindingSetting, breakOnFailedBinding, SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("Avalonia");
        var window = Path.Combine(folder, "AvaloniaApp", "Views", "MainWindow.axaml");
        await File.WriteAllTextAsync(window, (await File.ReadAllTextAsync(window)).Replace(
            "<TextBlock VerticalAlignment=\"Center\" Text=\"{Binding Count}\" />",
            "<TextBlock x:Name=\"CountText\" VerticalAlignment=\"Center\"\n                 " + CountMarker + " />\n      <TextBlock x:Name=\"Broken\" Text=\"{ReflectionBinding NoSuchProperty}\" />",
            StringComparison.Ordinal));
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Avalonia.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        var app = Path.Combine(folder, "AvaloniaApp", "AvaloniaApp.csproj");
        f.Shell.SetStartupProject(app);
        return (window, app);
    }

    private static string Describe(ShellFixture f) =>
        $"state {f.Shell.Debugger.State}, stop {f.Shell.Debugger.CurrentStop}, frame {f.Shell.Debugger.SelectedFrame}, live {(f.Shell.Live is { IsConnected: true } ? "connected" : "not connected")}, xaml stop {f.Shell.LastXamlStop}\n"
        + "frames: " + string.Join(" | ", f.Shell.Debugger.Frames.Take(8).Select(frame => frame.Name)) + "\n"
        + "agent events: " + (f.Shell.Live is { } live ? string.Join(", ", live.EventLog) : "no session") + "\n"
        + f.Shell.DebugLog.GetText() + LiveTreeShellTests.Output(f, "Build") + f.Shell.PreviewerLog.GetText();

    private static async Task<StopInfo> StoppedAsync(ShellFixture f, int seconds = 120)
    {
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.Debugger.CurrentStop is not null, seconds, () => Describe(f));
        return f.Shell.Debugger.CurrentStop!;
    }

    private static async Task ContinueAsync(ShellFixture f)
    {
        var stop = f.Shell.Debugger.CurrentStop;
        await f.RunAsync("debug.continue");
        await ShellFixture.PumpUntil(() => !ReferenceEquals(f.Shell.Debugger.CurrentStop, stop) || f.Shell.Debugger.State != DebugState.Stopped, 60, () => Describe(f));
    }

    [AvaloniaFact]
    public async Task Xaml_breakpoints_a_failed_binding_and_a_watched_value_stop_the_debugged_app_at_their_xaml()
    {
        Assert.SkipWhen(NoDisplay, "No display: the Avalonia app can't open its window.");
        await using var f = ShellFixture.Create();
        var (window, _) = await OpenAsync(f, breakOnFailedBinding: true);
        var buttonLine = LineOf(window, "<Button x:Name=\"IncrementButton\"");
        var countLine = LineOf(window, CountMarker);
        var brokenLine = LineOf(window, "NoSuchProperty");
        var breakpoints = f.Shell.Debugger.Breakpoints;
        breakpoints.Toggle(window, buttonLine);
        breakpoints.Toggle(window, countLine);
        var gate = new LiveTreeShellTests.PlanGate(f.Shell);
        await f.RunAsync("debug.start");
        try
        {
            // The button's creation: a stop at its start tag, as a breakpoint's, before XAML set its content.
            var created = await StoppedAsync(f, seconds: 300);
            Assert.True(created.Reason == "breakpoint" && f.Shell.LastXamlStop is { Kind: "element" }, $"{created} {f.Shell.LastXamlStop} plans {gate.Calls}\n{Describe(f)}");
            Assert.Equal((window, buttonLine), (f.Shell.Debugger.SelectedFrame?.Source?.Path, f.Shell.Debugger.SelectedFrame?.Line));
            Assert.Contains("Button", f.Shell.Debugger.Frames[0].Name, StringComparison.Ordinal);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } editor && editor.Model.Path == window && c.CurrentDebugLine() == buttonLine, 60, () => Describe(f));

            // The app's own frames under it: the window's InitializeComponent, none of the agent's.
            Assert.DoesNotContain(f.Shell.Debugger.Frames.Skip(1), frame => frame.Name.Contains("Codalus.Previewer.Agent", StringComparison.Ordinal));
            Assert.Contains(f.Shell.Debugger.Frames, frame => frame.Name.Contains("InitializeComponent", StringComparison.Ordinal));

            // The breakpoints are bound (the agent said so), not sent to the debugger.
            await ShellFixture.PumpUntil(() => breakpoints.For(window).All(b => breakpoints.StateOf(b) is { Verified: true }), 30,
                () => string.Join("; ", breakpoints.For(window).Select(b => $"{b.Line}: {breakpoints.StateOf(b)}")));

            // On: the count's binding sets its Text (an attribute breakpoint), the broken binding fails (an exception-like stop).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < 4 && !(seen.Contains("attribute") && seen.Contains("binding")); i++)
            {
                await ContinueAsync(f);
                var stop = await StoppedAsync(f);
                var xaml = f.Shell.LastXamlStop!;
                seen.Add(xaml.Kind);
                switch (xaml.Kind)
                {
                    case "attribute":
                        Assert.Equal(("breakpoint", "Text", "0"), (stop.Reason, xaml.Property, xaml.NewValue));
                        Assert.Equal((window, countLine), (f.Shell.Debugger.SelectedFrame?.Source?.Path, f.Shell.Debugger.SelectedFrame?.Line));
                        break;
                    case "binding":
                        Assert.Equal(("exception", ShellHost.BindingFailureExceptionId), (stop.Reason, stop.Exception?.ExceptionId));
                        Assert.Equal((window, brokenLine), (f.Shell.Debugger.SelectedFrame?.Source?.Path, f.Shell.Debugger.SelectedFrame?.Line));
                        Assert.Contains("NoSuchProperty", stop.Description, StringComparison.Ordinal);
                        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller?.IsExceptionHelperOpen == true, 30, () => Describe(f));
                        break;
                    default:
                        Assert.Fail($"An unexpected stop: {xaml}");
                        break;
                }
            }

            Assert.True(seen.SetEquals(["attribute", "binding"]), string.Join(", ", seen));

            // The attribute's breakpoint off; Break When Value Changes on the count's Text, from the Live Property Explorer.
            breakpoints.Remove(breakpoints.At(window, countLine)!);
            breakpoints.Remove(breakpoints.At(window, buttonLine)!);
            await ContinueAsync(f);
            var session = f.Shell.Live!;
            await f.Shell.SyncXamlBreakpointsAsync(session);
            var tree = f.Shell.LiveTree;
            await tree.RefreshAsync();
            await ShellFixture.PumpUntil(() => tree.Roots.Count > 0 && tree.Roots[0].SelfAndDescendants().Any(n => n.Name == "CountText"), 60, () => tree.Message ?? string.Empty);
            var count = tree.Roots[0].SelfAndDescendants().First(n => n.Name == "CountText");
            tree.Selected = count;
            var properties = f.Shell.LiveProperties;
            await ShellFixture.PumpUntil(() => properties.Rows.Any(r => r.Name == "Text"), 30);
            await properties.ToggleWatchAsync(properties.Rows.Single(r => r.Name == "Text"));
            Assert.True(properties.Rows.Single(r => r.Name == "Text").IsWatched);
            var setting = session.SetAsync(count.Id, "Text", "42", CancellationToken.None);
            var changed = await StoppedAsync(f);
            Assert.True(changed.Reason == "data breakpoint" && f.Shell.LastXamlStop is { Kind: "value", Property: "Text", NewValue: "42" }, $"{changed} {f.Shell.LastXamlStop}");
            Assert.Contains("Break When Value Changes", f.Shell.Debugger.Frames[0].Name, StringComparison.Ordinal);
            await ContinueAsync(f);
            await setting;

            // Styles: the greeting's class style applies, the task one doesn't, and says which part fails.
            var greeting = tree.Roots[0].SelfAndDescendants().First(n => n.Name == "GreetingText");
            tree.Selected = greeting;
            // (The count's list, before, has the title style too, not applying: wait for the greeting's.)
            await ShellFixture.PumpUntil(() => properties.Title.Contains("GreetingText", StringComparison.Ordinal) && properties.Styles.Any(s => s.Selector == "TextBlock.title" && s.State == LiveStyleState.Applies), 30,
                () => properties.StylesMessage + string.Join("; ", properties.Styles.Select(s => $"{s.Selector} {s.State} {s.Reason}")));
            Assert.Equal(LiveStyleState.Applies, properties.Styles.Single(s => s.Selector == "TextBlock.title").State);
            Assert.Contains("FontSize = 20", properties.Styles.Single(s => s.Selector == "TextBlock.title").Setters, StringComparison.Ordinal);
            var task = properties.Styles.Single(s => s.Selector == "TextBlock.task");
            Assert.NotEqual(LiveStyleState.Applies, task.State);
            Assert.Contains("'.task'", task.Reason, StringComparison.Ordinal);
            Assert.EndsWith("AppStyles.axaml", task.Style.File, StringComparison.Ordinal);
        }
        finally
        {
            await f.Shell.Debugger.StopAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        }
    }

    [AvaloniaFact]
    public async Task A_failed_binding_doesnt_stop_the_app_with_the_setting_off()
    {
        Assert.SkipWhen(NoDisplay, "No display: the Avalonia app can't open its window.");
        await using var f = ShellFixture.Create();
        var (window, _) = await OpenAsync(f, breakOnFailedBinding: false);
        _ = new LiveTreeShellTests.PlanGate(f.Shell);
        await f.RunAsync("debug.start");
        try
        {
            // The failure reaches the Error List; the app never stops.
            await ShellFixture.PumpUntil(() => f.Shell.ErrorList.LiveRows(ErrorListViewModel.BindingSource).Any(r => r.Description.Contains("NoSuchProperty", StringComparison.Ordinal)),
                seconds: 300, describe: () => Describe(f));
            var row = f.Shell.ErrorList.LiveRows(ErrorListViewModel.BindingSource).First(r => r.Description.Contains("NoSuchProperty", StringComparison.Ordinal));
            Assert.Equal(window, row.Path);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await ShellFixture.PumpUntil(() => true);
            Assert.NotEqual(DebugState.Stopped, f.Shell.Debugger.State);
            Assert.Null(f.Shell.LastXamlStop);
        }
        finally
        {
            await f.Shell.Debugger.StopAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        }
    }
}
