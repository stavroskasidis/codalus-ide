using Avalonia.Headless.XUnit;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// The Live Visual Tree and Live Property Explorer (M19b, ADR 0052) on a real Avalonia app run from the IDE: the agent
/// joins the run, the tree fills, properties show and set, and Go to XAML selects the element's tag.
/// </summary>
public sealed class LiveTreeShellTests
{
    [AvaloniaFact]
    public async Task A_running_avalonia_app_shows_its_tree_and_properties_takes_a_value_and_goes_to_its_xaml()
    {
        // The app runs windowed: Linux needs a display (CI runs the tests under Xvfb).
        Assert.SkipWhen(
            OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
            "No display: the Avalonia app can't open its window.");
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Avalonia");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Avalonia.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        var app = Path.Combine(folder, "AvaloniaApp", "AvaloniaApp.csproj");
        f.Shell.SetStartupProject(app);

        await f.Shell.StartWithoutDebuggingAsync();
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Live is { IsConnected: true }, seconds: 240);
            var tree = f.Shell.LiveTree;
            try
            {
                await ShellFixture.PumpUntil(() => tree.Roots.Count > 0, seconds: 60);
            }
            catch (Xunit.Sdk.TrueException)
            {
                Assert.Fail("The tree stayed empty: " + tree.Message + "\nRun output:\n" + string.Join("\n", f.Shell.Workbench.Output.Channels.Where(c => c.Name.StartsWith("Run", StringComparison.Ordinal)).Select(c => c.GetText())));
            }
            var window = tree.Roots[0];
            Assert.Equal("MainWindow", window.Type);
            var button = window.SelfAndDescendants().FirstOrDefault(n => n.Name == "IncrementButton");
            Assert.NotNull(button);
            Assert.DoesNotContain(window.SelfAndDescendants(), n => n.IsTemplated); // the XAML's elements only

            // The visual tree adds the templates' parts.
            tree.ShowVisualTree = true;
            await ShellFixture.PumpUntil(() => tree.Roots.Count > 0 && tree.Roots[0].SelfAndDescendants().Any(n => n.IsTemplated), seconds: 30);
            tree.ShowVisualTree = false;
            await ShellFixture.PumpUntil(() => tree.Roots.Count > 0 && !tree.Roots[0].SelfAndDescendants().Any(n => n.IsTemplated), seconds: 30);

            // Selecting shows its properties; a value set in the explorer reaches the app.
            button = tree.Roots[0].SelfAndDescendants().First(n => n.Name == "IncrementButton");
            tree.Selected = button;
            var properties = f.Shell.LiveProperties;
            await ShellFixture.PumpUntil(() => properties.Rows.Any(r => r.Name == "Content"), seconds: 30);
            Assert.Equal("Increment", properties.Rows.Single(r => r.Name == "Content").Value);
            var margin = properties.Rows.Single(r => r.Name == "Margin");
            margin.Value = "7,7,7,7";
            await ShellFixture.PumpUntil(() => properties.Rows.Any(r => r.Name == "Margin" && r.Source == "Local" && r.Value.StartsWith('7')), seconds: 30);

            // Go to XAML selects the button's start tag in MainWindow.axaml.
            await f.Shell.GoToXamlAsync(button);
            var editor = f.Shell.ActiveEditor;
            Assert.NotNull(editor);
            Assert.EndsWith("MainWindow.axaml", editor.Model.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("IncrementButton", editor.Controller!.Editor.SelectedText, StringComparison.Ordinal);
        }
        finally
        {
            f.Shell.Runs.StopAll();
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Count == 0, seconds: 60);
        }
    }
}
