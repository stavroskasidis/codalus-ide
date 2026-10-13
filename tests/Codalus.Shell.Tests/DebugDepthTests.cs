using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Debugging;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.Tests;

/// <summary>M14a in the shell (ADR 0021): the exception helper, QuickWatch, data tips, visualizers, Run to Cursor, PerfTips, Threads, Modules and breakpoints.</summary>
public sealed class DebugDepthTests
{
    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    [AvaloniaFact]
    public async Task The_debugger_depth_features_work_on_a_paused_program()
    {
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Debugging");
        var properties = Path.Combine(folder, "Debuggee", "Properties");
        Directory.CreateDirectory(properties);
        await File.WriteAllTextAsync(Path.Combine(properties, "launchSettings.json"), """
            { "profiles": { "Depth": { "commandName": "Project", "commandLineArgs": "--depth" } } }
            """, TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Debugging.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 90);
        var depth = Path.Combine(folder, "Debuggee", "Depth.cs");

        // Exception settings: break when InvalidOperationException is thrown (the window's row).
        f.Shell.Run("debug.exceptionSettings");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ExceptionSettingsView>().Any());
        f.Shell.ExceptionSettingsWindow.Rows.Single(r => r.Type == "System.InvalidOperationException").BreakWhenThrown = true;
        Assert.True(f.Shell.Debugger.ExceptionSettings.Breaks("System.InvalidOperationException"));

        f.Shell.Run("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger is { State: DebugState.Stopped, CurrentStop.Reason: "exception" }, seconds: 180);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } && c.CurrentDebugLine() == Line(depth, "depth-outer"));

        // The exception helper opens at the statement with the inner exception.
        var controller = f.Shell.ActiveEditor!.Controller!;
        await ShellFixture.PumpUntil(() => controller.IsExceptionHelperOpen);
        var chain = ExceptionHelper.Chain(f.Shell.Debugger.CurrentStop!.Exception?.Details).ToList();
        Assert.Equal(["System.InvalidOperationException", "System.ArgumentException"], chain.Select(d => d.FullTypeName));

        // QuickWatch on the inner exception.
        await f.Shell.ShowQuickWatchAsync("$exception.InnerException.Message");
        Assert.Equal("\"the inner one\"", f.Shell.QuickWatch.Roots.Single().Value);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<QuickWatchView>().Any());

        // Run to Cursor to the end of Run(): PerfTips, a data tip that edits, a JSON visualizer, Watch editing.
        await f.Shell.RunToLineAsync(depth, Line(depth, "depth-end"));
        await ShellFixture.PumpUntil(() => f.Shell.Debugger is { State: DebugState.Stopped, CurrentStop.Reason: not "exception" } && f.Shell.ActiveEditor?.Controller?.CurrentDebugLine() == Line(depth, "depth-end"), seconds: 60);
        Assert.NotNull(PerfTipRenderer.Text(f.Shell.Debugger.CurrentStop!.Elapsed));
        await ShellFixture.PumpUntil(() => !controller.IsExceptionHelperOpen); // closes through Ui.Post

        controller = f.Shell.ActiveEditor!.Controller!;
        var endLine = controller.Editor.Document.GetLineByNumber(Line(depth, "depth-end"));
        await controller.ShowDataTipAsync(controller.Editor.Document.Text.IndexOf("settings.Retries", endLine.Offset, StringComparison.Ordinal) + 2);
        await ShellFixture.PumpUntil(() => controller.LastDataTip is not null);
        var tip = controller.LastDataTip!;
        var settings = tip.Roots.Single();
        Assert.Equal("settings", settings.Name);
        settings.IsExpanded = true;
        await ShellFixture.PumpUntil(() => settings.Children.Any(c => c.Name == "Retries"));
        Assert.Null(await tip.SetValueAsync(settings.Children.Single(c => c.Name == "Retries"), "9"));
        Assert.Equal("9", (await f.Shell.Debugger.EvaluateAsync("settings.Retries", "watch")).Result);

        await f.Shell.Watch.AddAsync("sum");
        var watch = f.Shell.Watch.Roots.Single(r => r.Name == "sum");
        Assert.Equal("70", watch.Value);
        Assert.Null(await f.Shell.Watch.SetValueAsync(watch, "100"));
        Assert.Equal("100", f.Shell.Watch.Roots.Single(r => r.Name == "sum").Value);

        await f.Shell.Locals.RefreshAsync();
        var json = f.Shell.Locals.Roots.Single(r => r.Name == "json");
        Assert.True(json.IsString);
        Assert.Equal(VisualizerKind.Json, Visualizers.Suggested(Visualizers.Unquote(json.Value))[0]);
        await f.Shell.VisualizeAsync(json, VisualizerKind.Json);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.ReadOnlyLabel == "JSON Visualizer");
        Assert.Contains("\"tags\": [", f.Shell.ActiveEditor!.Model.Text.Text, StringComparison.Ordinal);

        // Grid and image visualizers (M14b): the list as a table, the PNG bytes in the image viewer.
        var squares = f.Shell.Locals.Roots.Single(r => r.Name == "squares");
        Assert.Equal([VisualizerKind.Grid], Visualizers.ForType(squares.Type));
        await f.Shell.VisualizeAsync(squares, VisualizerKind.Grid);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.ReadOnlyLabel == "Grid Visualizer");
        Assert.Contains("squares (12 elements)", f.Shell.ActiveEditor!.Model.Text.Text, StringComparison.Ordinal);
        var pixel = f.Shell.Locals.Roots.Single(r => r.Name == "pixel");
        Assert.Equal([VisualizerKind.Image, VisualizerKind.Grid], Visualizers.ForType(pixel.Type));
        await f.Shell.VisualizeAsync(pixel, VisualizerKind.Image);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is ImageDocument { Path: var image } && image.EndsWith("pixel.png", StringComparison.Ordinal));

        // Pinned data tips (M14b): the data tip pins to its line and floats over the editor.
        tip.Pin();
        var pinned = Assert.Single(f.Shell.Debugger.PinnedTips.All);
        Assert.Equal((depth, Line(depth, "depth-end"), "settings"), (pinned.Path, pinned.Line, pinned.Expression));
        f.Shell.Debugger.PinnedTips.SetComment(pinned, "retries edited above");
        await f.Shell.OpenFileAsync(depth);
        await ShellFixture.PumpUntil(() => controller.PinLayer.Pins.TryGetValue(Line(depth, "depth-end"), out var pin) && pin.IsVisible);
        Assert.Contains(controller.PinLayer.Pins[Line(depth, "depth-end")].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "retries edited above");

        // The Processes window and the Debug Location toolbar (M14b).
        f.Shell.Layout.ShowToolWindow(ProcessesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ProcessesView>().Any());
        var process = Assert.Single(f.Shell.ProcessesWindow.Rows);
        Assert.Equal((f.Shell.Debugger.ProcessId, "Break", "Launched"), ((int?)process.Id, process.State, process.Kind));
        var location = f.Shell.DebugLocation;
        Assert.Same(location, f.Shell.Toolbar.DebugLocation);
        Assert.Equal(f.Shell.Debugger.SelectedThreadId, location.Thread?.Id);
        Assert.Contains("Run", location.Frame?.Frame.Name, StringComparison.Ordinal);
        Assert.True(location.Frames.Count > 1);
        location.Frame = location.Frames[1];
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.SelectedFrame == location.Frames[1].Frame);
        location.Frame = location.Frames[0];

        // Threads and Modules.
        f.Shell.Layout.ShowToolWindow(ThreadsViewModel.ToolWindowId);
        await f.Shell.ThreadList.RefreshAsync();
        Assert.Contains(f.Shell.ThreadList.Rows, r => r.IsCurrent && r.Location.Contains("Run", StringComparison.Ordinal));
        f.Shell.ThreadList.Rows[0].IsFlagged = true;
        f.Shell.ThreadList.FlaggedOnly = true;
        await ShellFixture.PumpUntil(() => f.Shell.ThreadList.Rows.Count == 1);
        f.Shell.Layout.ShowToolWindow(ModulesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Shell.ModuleList.Rows.Any(r => r.Name == "Debuggee.dll"));
        f.Shell.ModuleList.Filter = "debuggee";
        Assert.Single(f.Shell.ModuleList.Rows);

        f.Shell.Run("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);

        // Function breakpoints, labels, export and import.
        f.Shell.Debugger.Breakpoints.SetFunction(new FunctionBreakpointSpec("Debuggee.Depth.Square", Labels: "math"));
        f.Shell.Debugger.Breakpoints.Set(new BreakpointSpec(depth, Line(depth, "depth-loop"), Temporary: true, Labels: "loop, math"));
        f.Shell.Layout.ShowToolWindow(BreakpointsViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Shell.BreakpointList.Rows.Count == 2);
        f.Shell.BreakpointList.Filter = "loop";
        Assert.Single(f.Shell.BreakpointList.Rows);
        f.Shell.BreakpointList.Filter = string.Empty;
        var export = Path.Combine(folder, "breakpoints.json");
        f.Shell.ExportBreakpoints(export);
        f.Shell.Debugger.Breakpoints.Clear();
        Assert.Equal(2, f.Shell.ImportBreakpoints(export));
        Assert.Equal("loop, math", f.Shell.Debugger.Breakpoints.All.Single().Labels);
        Assert.True(f.Shell.Debugger.Breakpoints.All.Single().Temporary);
        Assert.Equal("Debuggee.Depth.Square", f.Shell.Debugger.Breakpoints.Functions.Single().Name);
    }

    [AvaloniaFact]
    public async Task A_dump_opens_in_its_document_with_threads_and_frames()
    {
        await using var f = ShellFixture.Create();
        var summary = new Codalus.Debugging.Companion.DumpSummary(
            "1", Path.Combine(Path.GetTempPath(), "crash.dmp"), ".NET 10.0.0", "X64", 42, ["App.dll"],
            [
                new(1, 100, false, "System.InvalidOperationException: boom", [new("App.Program.Main()", "App.dll", 3, null, null)]),
                new(2, 101, true, null, []),
            ]);
        await using var companion = new DebugCompanion(() => null);
        var document = new DumpDocument(new DumpViewModel(summary, companion, (_, _) => Task.CompletedTask));
        f.Shell.Layout.OpenDocument(document);

        // The view loads (its tab control raises SelectionChanged while the XAML is still populating).
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DumpView>().Any());
        Assert.Equal("Dump: crash.dmp", document.Title);
        Assert.Equal("Thread #1: System.InvalidOperationException: boom", document.ViewModel.Exception);
        Assert.Equal(1, document.ViewModel.SelectedThread?.Thread.ManagedId); // the one that threw, before the finalizer
        Assert.Equal("App.Program.Main()", Assert.Single(document.ViewModel.Frames).Method);

        // Without an exception, the first thread with managed frames, not the finalizer listed before it.
        var calm = summary with { Threads = [new(1, 100, true, null, []), new(2, 101, false, null, [new("App.Program.Main()", "App.dll", 3, null, null)])] };
        Assert.Equal(2, new DumpViewModel(calm, companion, (_, _) => Task.CompletedTask).SelectedThread?.Thread.ManagedId);
    }

    [Theory]
    [InlineData("\"a\\\"b\\\\c\\n\"", "a\"b\\c\n")]
    [InlineData("\"tab\\there\\u0041\"", "tab\there" + "A")]
    [InlineData("plain", "plain")]
    public void Visualizers_unquote_debugger_strings(string value, string expected) =>
        Assert.Equal(expected, Visualizers.Unquote(value));

    [Fact]
    public void Visualizers_format_json_and_xml_and_keep_invalid_text()
    {
        Assert.Equal("{\n  \"a\": [\n    1,\n    2\n  ]\n}", Visualizers.Format(VisualizerKind.Json, "{\"a\":[1,2]}").ReplaceLineEndings("\n"));
        Assert.Equal("<a>\n  <b>x</b>\n</a>", Visualizers.Format(VisualizerKind.Xml, "<a><b>x</b></a>").ReplaceLineEndings("\n"));
        Assert.Equal("{not json", Visualizers.Format(VisualizerKind.Json, "{not json"));
        Assert.Equal(VisualizerKind.Xml, Visualizers.Suggested("<a/>")[0]);
        Assert.Equal(VisualizerKind.Html, Visualizers.Suggested("<!DOCTYPE html><html></html>")[0]);
        Assert.Equal(VisualizerKind.Text, Visualizers.Suggested("hello")[0]);
    }

    [Fact]
    public void The_exception_helper_describes_the_chain()
    {
        var details = new ExceptionDetails("outer", "InvalidOperationException", "System.InvalidOperationException", "   at A()", InnerException: [new ExceptionDetails("inner", "ArgumentException", "System.ArgumentException", "   at B()")]);
        var text = ExceptionHelper.Describe("System.InvalidOperationException", "outer", details).ReplaceLineEndings("\n");
        Assert.Equal("System.InvalidOperationException: outer\n   at A()\nInner exception: System.ArgumentException: inner\n   at B()\n", text);
        Assert.Equal("≤ 13 ms elapsed", PerfTipRenderer.Text(TimeSpan.FromMilliseconds(12.2)));
        Assert.Equal("≤ 1 ms elapsed", PerfTipRenderer.Text(TimeSpan.Zero));
    }
}
