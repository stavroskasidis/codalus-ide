using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Previewer;

namespace Codalus.Shell.Tests;

/// <summary>
/// M60b's desktop-app tools for agents (ADR 0099) on the Avalonia fixture (samples/Avalonia) run from the IDE: the agent
/// starts it off-screen (Xvfb on Linux), reads its window with a picture and its controls, clicks Increment inside the
/// app and reads the count that changed.
/// </summary>
public sealed class IdeToolsDesktopAppShellTests
{
    private static async Task<IdeToolResult> CallAsync(ShellFixture f, string tool, string json = "{}") =>
        await f.Shell.IdeToolAsync(tool, JsonElement.Parse(json), "Claude", CancellationToken.None);

    private static string Output(ShellFixture f) =>
        string.Concat(f.Shell.Workbench.Output.Channels.Where(c => c.Name.StartsWith("Run", StringComparison.Ordinal) || c.Name.StartsWith("Build", StringComparison.Ordinal)).Select(c => $"--- {c.Name}\n{c.GetText()}\n"));

    [AvaloniaFact]
    public async Task An_agent_runs_the_avalonia_fixture_off_screen_reads_its_window_and_clicks_a_button_in_it()
    {
        // The app opens a window: on Linux, on the display there is, or on an Xvfb of the IDE's.
        var display = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        Assert.SkipWhen(
            OperatingSystem.IsLinux() && !display && OffScreenDisplay.Locate(Environment.GetEnvironmentVariable("PATH")) is null,
            "No display and no Xvfb: the Avalonia app can't open its window.");
        await using var f = ShellFixture.Create();

        // Nothing runs yet: the tools say how to start an app.
        var none = await CallAsync(f, "desktop_app_windows");
        Assert.True(none.IsError);
        Assert.Contains("run_project or debug_start", none.Text, StringComparison.Ordinal);

        var folder = f.CopySample("Avalonia");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Avalonia.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        f.Shell.SetStartupProject(Path.Combine(folder, "AvaloniaApp", "AvaloniaApp.csproj"));
        try
        {
            var started = await CallAsync(f, "run_project", """{"offScreen":true}""");
            Assert.False(started.IsError, started.Text + Output(f));
            if (OperatingSystem.IsLinux())
            {
                Assert.Contains("Its windows open off-screen (Xvfb, display :", started.Text, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("open as usual", started.Text, StringComparison.Ordinal);
            }

            // Its window, with a picture of it.
            var windows = await CallAsync(f, "desktop_app_windows");
            Assert.False(windows.IsError, windows.Text + Output(f));
            Assert.Contains("\"Avalonia fixture\" (AvaloniaApp.Views.MainWindow, 480x360)", windows.Text, StringComparison.Ordinal);
            var picture = Convert.FromBase64String(Assert.Single(windows.Images).Data);
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], picture[..4]);

            // Its controls: the button, the count it shows, the list's items; each with its bounds in the window.
            var controls = await CallAsync(f, "desktop_app_controls");
            Assert.False(controls.IsError, controls.Text);
            var button = Assert.Single(controls.Text.Split('\n'), l => l.Contains("Button (IncrementButton) \"Increment\"", StringComparison.Ordinal));
            Assert.Matches(@" at \d+(\.\d)?,\d+(\.\d)? \d+(\.\d)?x\d+(\.\d)?$", button.TrimEnd());
            Assert.Contains("TextBlock \"0\"", controls.Text, StringComparison.Ordinal);
            Assert.Contains("\"Write the XAML\"", controls.Text, StringComparison.Ordinal);
            var filtered = await CallAsync(f, "desktop_app_controls", """{"filter":"IncrementButton"}""");
            Assert.Contains("MainWindow", filtered.Text, StringComparison.Ordinal); // with its ancestors
            Assert.DoesNotContain("Write the XAML", filtered.Text, StringComparison.Ordinal);

            // A click inside the app, by the button's name: the count goes up, and the AI Output channel has the call.
            var clicked = await CallAsync(f, "desktop_app_input", """{"name":"IncrementButton","kind":"click"}""");
            Assert.False(clicked.IsError, clicked.Text);
            Assert.StartsWith("Clicked Button 'IncrementButton': invoked.", clicked.Text, StringComparison.Ordinal);
            var after = await CallAsync(f, "desktop_app_controls", """{"filter":"TextBlock"}""");
            Assert.Contains("TextBlock \"1\"", after.Text, StringComparison.Ordinal);
            Assert.Contains("IDE tool (Claude): desktop_app_input", f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText(), StringComparison.Ordinal);

            // A control that isn't there, and a scroll of the list.
            var missing = await CallAsync(f, "desktop_app_input", """{"name":"NoSuchControl","kind":"click"}""");
            Assert.True(missing.IsError);
            var scrolled = await CallAsync(f, "desktop_app_input", """{"name":"Tasks","kind":"scroll","direction":"bottom"}""");
            Assert.False(scrolled.IsError, scrolled.Text);
            Assert.Contains("Scrolled ScrollViewer", scrolled.Text, StringComparison.Ordinal);
        }
        finally
        {
            await CallAsync(f, "stop_run");
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Count == 0, seconds: 60);
        }

        // The app ended: the tools say so.
        var ended = await CallAsync(f, "desktop_app_controls");
        Assert.True(ended.IsError);
    }

    [Fact]
    public void Controls_read_as_an_indented_tree_and_a_filter_keeps_the_ancestors()
    {
        IReadOnlyList<LiveControl> controls =
        [
            new(1, 0, "MainWindow", "", "App", [], new LiveBounds(0, 0, 480, 360)),
            new(2, 1, "StackPanel", "", "", [], new LiveBounds(12, 12, 456, 40)),
            new(3, 2, "Button", "Go", "Go", ["focused"], new LiveBounds(12, 12, 60.26, 32)),
            new(4, 2, "TextBlock", "", "0", ["disabled"], null),
            new(5, 1, "ListBox", "Tasks", "", ["selectedIndex=1"], new LiveBounds(12, 60, 456, 200)),
        ];
        Assert.Equal(
            "#1 MainWindow \"App\" at 0,0 480x360\n  #2 StackPanel at 12,12 456x40\n    #3 Button (Go) \"Go\" [focused] at 12,12 60.3x32\n    #4 TextBlock \"0\" [disabled]\n  #5 ListBox (Tasks) [selectedIndex=1] at 12,60 456x200\n",
            ShellHost.FormatControls(controls, null).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(
            "#1 MainWindow \"App\" at 0,0 480x360\n  #2 StackPanel at 12,12 456x40\n    #3 Button (Go) \"Go\" [focused] at 12,12 60.3x32\n",
            ShellHost.FormatControls(controls, "button").Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.StartsWith("No control's", ShellHost.FormatControls(controls, "nothing"), StringComparison.Ordinal);
    }
}
