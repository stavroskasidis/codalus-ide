using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Previewer;

namespace Codalus.Shell.Tests;

/// <summary>
/// M40b (ADR 0072, D31) in the headless shell on samples/WindowsDesktop: a WPF project's <c>.xaml</c> goes to the WPF
/// previewer. Off Windows it says "Windows only" and builds and starts nothing, and no live agent joins a WPF app; on
/// Windows the previewer builds the app and renders the window at its design size, follows an edit, selects in the XAML
/// what is clicked and outlines what the caret is on, and the Live Visual Tree and Live Property Explorer show the
/// running app, take a value and go to the XAML.
/// </summary>
public sealed class WpfPreviewerShellTests
{
    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WpfApp.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        return (f, folder);
    }

    private static string WpfApp(string folder) => Path.Combine(folder, "WpfApp", "WpfApp.csproj");

    [AvaloniaFact]
    public async Task Off_windows_the_wpf_previewer_and_live_tree_say_windows_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows the WPF previewer renders (On_windows_the_wpf_previewer_renders_selects_and_the_live_tree_shows_the_app).");
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var path = Path.Combine(folder, "WpfApp", "MainWindow.xaml");
        var document = await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        Assert.Equal(ShellHost.XamlLanguageId, document!.Model.LanguageId);

        // Shift+F7's command: the previewer follows the .xaml editor and says why it doesn't render, before any build.
        var builds = f.Shell.Builds.LastBuild;
        f.Shell.Run("xaml.showPreview");
        var preview = f.Shell.XamlPreview;
        await ShellFixture.PumpUntil(() => preview.Status == PreviewService.WpfWindowsOnlyMessage, seconds: 60);
        Assert.Equal(path, f.Shell.PreviewedPath);
        Assert.False(preview.HasImage);
        Assert.Null(f.Shell.Previewer.Session);
        Assert.Same(builds, f.Shell.Builds.LastBuild);
        Assert.Contains("Windows only", f.Shell.PreviewerLog.GetText(), StringComparison.Ordinal);

        // An edit asks again and gets the same answer.
        document.Model.Text.Insert(document.Model.Text.Text.IndexOf("</Grid>", StringComparison.Ordinal), "<TextBlock Text=\"x\" />");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => preview.Status == PreviewService.WpfWindowsOnlyMessage, seconds: 30);
        Assert.Same(builds, f.Shell.Builds.LastBuild);

        // The live tools: no agent for a WPF app here, and the tree says why.
        Assert.Null(f.Shell.LiveAgentFor(WpfApp(folder)));
        Assert.Contains("WPF app's live visual tree is Windows only", f.Shell.LiveTree.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task On_windows_the_wpf_previewer_renders_selects_and_the_live_tree_shows_the_app()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The WPF previewer and live tree run on Windows only (D31).");
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300);
        var path = Path.Combine(folder, "WpfApp", "MainWindow.xaml");
        var document = await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var model = document!.Model;
        var editor = document.Controller!;

        // The previewer builds the app and renders the window at its design size, with no theme picker (WPF has none).
        f.Shell.ShowXamlPreview();
        var preview = f.Shell.XamlPreview;
        try
        {
            await ShellFixture.PumpUntil(() => preview.FrameCount > 0, seconds: 300);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail($"No frame: {preview.Status} {preview.Error}\n{f.Shell.PreviewerLog.GetText()}\n{f.Shell.Builds.Channel.GetText()}");
        }

        Assert.False(preview.SupportsThemes);
        preview.ZoomResetCommand.Execute(null);
        Assert.Equal((480d, 360d), (preview.ImageWidth, preview.ImageHeight));

        // An edit shows up.
        var frames = preview.FrameCount;
        var add = model.Text.Text.IndexOf("Content=\"Add\"", StringComparison.Ordinal);
        model.Text.Replace(add, "Content=\"Add\"".Length, "Content=\"Add it\"");
        await ShellFixture.PumpUntil(() => preview.FrameCount > frames && preview.Error is null, seconds: 60);

        // A click on the button selects its start tag (the event handler stays in the XAML; the preview ignores it).
        var session = f.Shell.Previewer.Session!;
        PreviewPathStep[] button = [new("MainWindow", 0), new("Grid", 0), new("DockPanel", 0), new("Button", 0)];
        var bounds = await session.BoundsAsync(button, ShellFixture.RequestTimeout());
        Assert.NotNull(bounds);
        for (var attempt = 0; attempt < 20 && !editor.Editor.SelectedText.StartsWith("<Button", StringComparison.Ordinal); attempt++)
        {
            await f.Shell.SelectFromPreviewAsync(bounds.Value.X + bounds.Value.Width / 2, bounds.Value.Y + bounds.Value.Height / 2);
            if (!editor.Editor.SelectedText.StartsWith("<Button", StringComparison.Ordinal))
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }
        }

        Assert.StartsWith("<Button DockPanel.Dock=\"Right\"", editor.Editor.SelectedText, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnAddClicked\"", editor.Editor.SelectedText, StringComparison.Ordinal);

        // The caret in the text box outlines it in the preview.
        editor.Editor.SelectionLength = 0;
        editor.Editor.CaretOffset = model.Text.Text.IndexOf("x:Name=\"NewTitleBox\"", StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => preview.Selection is { Width: > 50, Y: < 60 }, seconds: 90);

        // The loader's error with its line; fixing it renders again.
        var grid = model.Text.Text.IndexOf("<Grid Margin", StringComparison.Ordinal);
        model.Text.Insert(grid + "<Grid".Length, " Orientation=\"Sideways\"");
        await ShellFixture.PumpUntil(() => preview.Error is not null, seconds: 60);
        Assert.Contains("Orientation", preview.Error, StringComparison.Ordinal);
        Assert.Contains("Line ", preview.Error, StringComparison.Ordinal);
        frames = preview.FrameCount;
        model.Text.Remove(grid + "<Grid".Length, " Orientation=\"Sideways\"".Length);
        await ShellFixture.PumpUntil(() => preview.Error is null && preview.FrameCount > frames, seconds: 60);

        // The render scale.
        preview.SelectedScale = "2×";
        await ShellFixture.PumpUntil(() => preview.Frame is { Width: 960, Scale: 2 }, seconds: 60);
        f.Shell.Layout.CloseToolWindow(XamlPreviewViewModel.ToolWindowId);
        await f.Shell.Previewer.InvalidateAsync(); // the designer host lets go of the app's assemblies before it runs

        // Run it: the WPF live agent joins, and the tree, the properties and Go to XAML work as for Avalonia.
        Assert.NotNull(f.Shell.LiveAgentFor(WpfApp(folder)));
        f.Shell.SetStartupProject(WpfApp(folder));
        await f.Shell.StartWithoutDebuggingAsync();
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Live is { IsConnected: true }, seconds: 240);
            var tree = f.Shell.LiveTree;
            await ShellFixture.PumpUntil(() => tree.Roots.Count > 0, seconds: 60);
            var window = tree.Roots[0];
            Assert.Equal("MainWindow", window.Type);
            Assert.DoesNotContain(window.SelfAndDescendants(), n => n.IsTemplated);
            var box = window.SelfAndDescendants().First(n => n.Name == "NewTitleBox");

            tree.ShowVisualTree = true;
            await ShellFixture.PumpUntil(() => tree.Roots.Count > 0 && tree.Roots[0].SelfAndDescendants().Any(n => n.IsTemplated), seconds: 30);
            tree.ShowVisualTree = false;
            await ShellFixture.PumpUntil(() => tree.Roots.Count > 0 && !tree.Roots[0].SelfAndDescendants().Any(n => n.IsTemplated), seconds: 30);

            box = tree.Roots[0].SelfAndDescendants().First(n => n.Name == "NewTitleBox");
            tree.Selected = box;
            var properties = f.Shell.LiveProperties;
            await ShellFixture.PumpUntil(() => properties.Rows.Any(r => r.Name == "Margin"), seconds: 30);
            Assert.True(properties.Rows.Single(r => r.Name == "Margin").IsLocal);
            properties.Rows.Single(r => r.Name == "Margin").Value = "7";
            await ShellFixture.PumpUntil(() => properties.Rows.Any(r => r.Name == "Margin" && r.Source == "Local" && r.Value == "7,7,7,7"), seconds: 30);

            await f.Shell.GoToXamlAsync(box);
            Assert.EndsWith("MainWindow.xaml", f.Shell.ActiveEditor!.Model.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NewTitleBox", f.Shell.ActiveEditor.Controller!.Editor.SelectedText, StringComparison.Ordinal);
        }
        finally
        {
            f.Shell.Runs.StopAll();
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Count == 0, seconds: 60);
        }
    }
}
