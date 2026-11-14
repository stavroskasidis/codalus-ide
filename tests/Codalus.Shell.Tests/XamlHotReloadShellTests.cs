using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Previewer;

namespace Codalus.Shell.Tests;

/// <summary>
/// XAML Hot Reload and failed bindings (M64, ADR 0103) on samples/Avalonia run from the IDE (Ctrl+F5) with the live
/// agent: a binding that fails in the app is an Error List warning at its XAML line; a saved edit of MainWindow.axaml
/// changes the running window (a new element, a changed property) without a restart, keeping the app's state; an edit
/// the app can't take (a handler it doesn't have) is an Error List error at its line and leaves the window as it was.
/// </summary>
public sealed class XamlHotReloadShellTests
{
    [AvaloniaFact]
    public async Task A_saved_xaml_edit_reaches_the_running_app_and_failed_bindings_are_listed()
    {
        // The app runs windowed: Linux needs a display (CI runs the tests under Xvfb).
        Assert.SkipWhen(
            OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
            "No display: the Avalonia app can't open its window.");
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Avalonia");
        var window = Path.Combine(folder, "AvaloniaApp", "Views", "MainWindow.axaml");

        // A binding the view model can't satisfy at run time (a reflection binding: the compiled ones are checked by the build).
        await File.WriteAllTextAsync(window, (await File.ReadAllTextAsync(window)).Replace(
            "<TextBlock VerticalAlignment=\"Center\" Text=\"{Binding Count}\" />",
            "<TextBlock VerticalAlignment=\"Center\" Text=\"{Binding Count}\" />\n      <TextBlock x:Name=\"Broken\" Text=\"{ReflectionBinding NoSuchProperty}\" />",
            StringComparison.Ordinal));
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Avalonia.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        f.Shell.SetStartupProject(Path.Combine(folder, "AvaloniaApp", "AvaloniaApp.csproj"));
        var gate = new LiveTreeShellTests.PlanGate(f.Shell);
        await f.Shell.StartWithoutDebuggingAsync();
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Live is { IsConnected: true } || LiveTreeShellTests.Output(f, "Run").Contains("Codalus previewer agent:", StringComparison.Ordinal),
                seconds: 240, describe: () => $"plans {gate.Calls} ({gate.Log})\n" + LiveTreeShellTests.Output(f, "Run") + LiveTreeShellTests.Output(f, "Build"));
            Assert.True(f.Shell.Live is { IsConnected: true }, LiveTreeShellTests.Output(f, "Run"));
            var session = f.Shell.Live!;

            // The failed binding: a warning at its line of MainWindow.axaml.
            var errors = f.Shell.ErrorList;
            await ShellFixture.PumpUntil(() => errors.LiveRows(ErrorListViewModel.BindingSource).Any(r => r.Description.Contains("NoSuchProperty", StringComparison.Ordinal)), seconds: 60,
                describe: () => string.Join("; ", errors.LiveRows(ErrorListViewModel.BindingSource).Select(r => r.Description)) + "\n" + f.Shell.PreviewerLog.GetText());
            var binding = errors.LiveRows(ErrorListViewModel.BindingSource).First(r => r.Description.Contains("NoSuchProperty", StringComparison.Ordinal));
            Assert.Equal(("warning", ShellHost.BindingFailureCode, window), (binding.Icon, binding.Code, binding.Path));
            var lines = (await File.ReadAllTextAsync(window)).Split('\n');
            Assert.Contains("NoSuchProperty", lines[binding.Line - 1], StringComparison.Ordinal);

            // The window's tree as the app shows it, and a value set in the app (state XAML doesn't set).
            var tree = f.Shell.LiveTree;
            await ShellFixture.PumpUntil(() => tree.Roots.Count > 0, seconds: 60, describe: () => tree.Message ?? string.Empty);
            var root = tree.Roots[0];
            Assert.DoesNotContain(root.SelfAndDescendants(), n => n.Name == "HotReloaded");
            var windowId = (await session.WindowsAsync(CancellationToken.None)).Single().Id;
            await session.SetAsync(windowId, "Tag", "kept", CancellationToken.None);

            // A saved edit: a new element and a changed button, in the running window.
            var document = await f.Shell.OpenFileAsync(window, preview: false, null);
            await ShellFixture.PumpUntil(() => document?.Controller is not null);
            var model = document!.Model;
            var button = "Content=\"Increment\"";
            model.Text.Replace(model.Text.Text.IndexOf(button, StringComparison.Ordinal), button.Length, "Content=\"Add one\"");
            var greeting = "<TextBlock x:Name=\"GreetingText\"";
            model.Text.Insert(model.Text.Text.IndexOf(greeting, StringComparison.Ordinal), "<TextBlock x:Name=\"HotReloaded\" DockPanel.Dock=\"Top\" Text=\"Reloaded\" />\n    ");
            await model.SaveAsync(CancellationToken.None);
            await ShellFixture.PumpUntil(() => f.Shell.LastXamlReload is { } last && last.Path == window, seconds: 120, describe: () => f.Shell.StatusBar.Message ?? string.Empty);
            var applied = f.Shell.LastXamlReload!.Value.Result;
            Assert.True(applied.Outcome == LiveReloadOutcome.Applied && applied.Count == 1, $"{applied.Outcome} {applied.Count}: {applied.Message}");

            var nodes = await session.TreeAsync(windowId, visual: false, CancellationToken.None);
            var added = Assert.Single(nodes, n => n.Name == "HotReloaded");
            Assert.Equal("Reloaded", (await session.PropertiesAsync(added.Id, CancellationToken.None)).Single(p => p.Name == "Text").Value);
            var increment = Assert.Single(nodes, n => n.Name == "IncrementButton");
            Assert.Equal("Add one", (await session.PropertiesAsync(increment.Id, CancellationToken.None)).Single(p => p.Name == "Content").Value);
            Assert.Equal("kept", (await session.PropertiesAsync(windowId, CancellationToken.None)).Single(p => p.Name == "Tag").Value);
            Assert.Empty(errors.LiveRows(ErrorListViewModel.XamlHotReloadSource));

            // An edit the running app can't take: an error at its line; the window keeps the last good XAML.
            model.Text.Replace(model.Text.Text.IndexOf("Content=\"Add one\"", StringComparison.Ordinal), "Content=\"Add one\"".Length, "Content=\"Add one\" Click=\"NoSuchHandler\"");
            await model.SaveAsync(CancellationToken.None);
            await ShellFixture.PumpUntil(() => f.Shell.LastXamlReload is { } last && last.Result.Outcome == LiveReloadOutcome.Failed, seconds: 120, describe: () => f.Shell.StatusBar.Message ?? string.Empty);
            var rude = Assert.Single(errors.LiveRows(ErrorListViewModel.XamlHotReloadSource));
            Assert.Equal(("error", ShellHost.XamlHotReloadCode, window), (rude.Icon, rude.Code, rude.Path));
            Assert.Contains("NoSuchHandler", model.Text.Text.Split('\n')[rude.Line - 1], StringComparison.Ordinal);
            Assert.Single(await session.TreeAsync(windowId, visual: false, CancellationToken.None), n => n.Name == "HotReloaded");
        }
        finally
        {
            f.Shell.Runs.StopAll();
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Count == 0, seconds: 60);
        }
    }
}
