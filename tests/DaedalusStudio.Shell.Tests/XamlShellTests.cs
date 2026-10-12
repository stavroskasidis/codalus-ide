using Avalonia.Headless.XUnit;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Previewer;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// M19a (ADR 0047 and 0048) in the headless shell on samples/Avalonia: .axaml files are Avalonia XAML served by the C#
/// host (completion in the editor); the XAML Previewer builds the app, renders the editor's text, follows edits,
/// selects in the XAML what is clicked and outlines what the caret is on, shows the loader's errors, rebuilds when the
/// code-behind's project changes, and the XAML compiler's build errors land in their file.
/// </summary>
public sealed class XamlShellTests
{
    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("Avalonia");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Avalonia.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 2 }, seconds: 240);
        return (f, folder);
    }

    [AvaloniaFact]
    public async Task The_previewer_follows_the_editor_and_selects_both_ways()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var path = Path.Combine(folder, "AvaloniaApp", "Views", "MainWindow.axaml");
        var document = await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var model = document!.Model;
        Assert.Equal(ShellHost.AxamlLanguageId, model.LanguageId);

        // Completion in the editor comes from the C# host's XAML service.
        var editor = document.Controller!;
        var dock = model.Text.Text.IndexOf("<ListBox x:Name", StringComparison.Ordinal);
        model.Text.Insert(dock, "<Bu />\n    ");
        editor.Editor.CaretOffset = dock + 3;
        editor.ShowCompletion();
        await ShellFixture.PumpUntil(() => editor.CompletionWindow is { } w && w.CompletionList.CompletionData.Count > 0, seconds: 90);
        Assert.Contains(editor.CompletionWindow!.CompletionList.CompletionData, d => d.Text == "Button");
        editor.CompletionWindow.Close();
        model.Text.Remove(dock, "<Bu />\n    ".Length);

        // The previewer builds the app and renders the window at its design size.
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

        Assert.NotNull(preview.Image);

        // The preview fits the pane by default (never above 100%); 100% is the zoom button.
        Assert.True(preview.FitToPane && preview.Zoom <= 1);
        preview.ZoomResetCommand.Execute(null);
        Assert.False(preview.FitToPane);
        Assert.Equal((480d, 360d), (preview.ImageWidth, preview.ImageHeight));
        Assert.Equal(path, f.Shell.PreviewedPath);

        // An edit shows up.
        var frames = preview.FrameCount;
        var text = model.Text.Text.IndexOf("Text=\"{Binding Greeting}\"", StringComparison.Ordinal);
        model.Text.Replace(text, "Text=\"{Binding Greeting}\"".Length, "Text=\"Edited in the editor\"");
        await ShellFixture.PumpUntil(() => preview.FrameCount > frames, seconds: 60);

        // Zoom doubles the shown size; the frame is the same.
        preview.ZoomInCommand.Execute(null);
        Assert.Equal(1.25, preview.Zoom);
        Assert.Equal(600d, preview.ImageWidth);
        preview.ZoomResetCommand.Execute(null);

        // A click on the button selects its start tag.
        var session = f.Shell.Previewer.Session!;
        PreviewPathStep[] button = [new("MainWindow", 0), new("DockPanel", 0), new("StackPanel", 0), new("Button", 0)];
        var bounds = await session.BoundsAsync(button, ShellFixture.RequestTimeout());
        Assert.NotNull(bounds);
        await f.Shell.SelectFromPreviewAsync(bounds.Value.X + bounds.Value.Width / 2, bounds.Value.Y + bounds.Value.Height / 2);
        Assert.StartsWith("<Button x:Name=\"IncrementButton\"", editor.Editor.SelectedText, StringComparison.Ordinal);
        Assert.EndsWith("/>", editor.Editor.SelectedText, StringComparison.Ordinal);

        // The caret on the greeting outlines it in the preview.
        editor.Editor.SelectionLength = 0;
        editor.Editor.CaretOffset = model.Text.Text.IndexOf("x:Name=\"GreetingText\"", StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => preview.Selection is { Y: < 20, Width: > 50 }, seconds: 90);
        Assert.True(preview.HasSelection);

        // The loader's errors show with their line; fixing them renders again.
        var list = model.Text.Text.IndexOf("<ListBox x:Name", StringComparison.Ordinal);
        model.Text.Insert(list + "<ListBox".Length, " Orientation=\"Sideways\"");
        await ShellFixture.PumpUntil(() => preview.Error is not null, seconds: 60);
        Assert.Contains("Line 25", preview.Error, StringComparison.Ordinal);
        Assert.Contains("Orientation", preview.Error, StringComparison.Ordinal);
        frames = preview.FrameCount;
        model.Text.Remove(list + "<ListBox".Length, " Orientation=\"Sideways\"".Length);
        await ShellFixture.PumpUntil(() => preview.Error is null && preview.FrameCount > frames, seconds: 60);

        // The theme variant and the render scale.
        frames = preview.FrameCount;
        preview.SelectedTheme = "Dark";
        await ShellFixture.PumpUntil(() => preview.FrameCount > frames, seconds: 30);
        preview.SelectedScale = "200%";
        await ShellFixture.PumpUntil(() => preview.Frame is { Width: 960, Scale: 2 }, seconds: 60);
        Assert.Equal(480d, preview.ImageWidth);

        // Fit again: the frame within the pane.
        preview.FitCommand.Execute(null);
        Assert.True(preview.FitToPane && preview.ImageWidth <= 480);
    }

    [AvaloniaFact]
    public async Task Code_changes_rebuild_the_preview_and_xaml_compiler_errors_land_in_their_file()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var todo = Path.Combine(folder, "AvaloniaApp", "Views", "TodoItemView.axaml");
        var document = await f.Shell.OpenFileAsync(todo, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        f.Shell.ShowXamlPreview();
        var preview = f.Shell.XamlPreview;
        await ShellFixture.PumpUntil(() => preview.FrameCount > 0, seconds: 300);
        preview.ZoomResetCommand.Execute(null);
        Assert.Equal((240d, 32d), (preview.ImageWidth, preview.ImageHeight));
        var firstFrame = preview.Frame!;

        // The view model's code changes and is saved: the preview rebuilds and shows it (Design.DataContext's title).
        var viewModel = await f.Shell.OpenFileAsync(Path.Combine(folder, "AvaloniaApp", "ViewModels", "TodoItemViewModel.cs"), preview: false, null);
        await ShellFixture.PumpUntil(() => viewModel?.Controller is not null);
        var code = viewModel!.Model;
        var setter = code.Text.Text.IndexOf("set => Set(ref _title, value);", StringComparison.Ordinal);
        code.Text.Replace(setter, "set => Set(ref _title, value);".Length, "set => Set(ref _title, value.ToUpperInvariant());");
        await code.SaveAsync(CancellationToken.None);
        var frames = preview.FrameCount;
        await ShellFixture.PumpUntil(() => preview.FrameCount > frames && preview.Frame is { } frame && !frame.Data.AsSpan().SequenceEqual(firstFrame.Data), seconds: 300);
        Assert.Contains("rebuilding", f.Shell.PreviewerLog.GetText(), StringComparison.Ordinal);

        // A XAML compiler error from a build lands in its .axaml file as a diagnostic.
        var styles = Path.Combine(folder, "AvaloniaApp", "Styles", "AppStyles.axaml");
        await File.WriteAllTextAsync(styles, (await File.ReadAllTextAsync(styles)).Replace("<Setter Property=\"FontStyle\" Value=\"Normal\" />", "<Setter Property=\"NoSuchProperty\" Value=\"Normal\" />", StringComparison.Ordinal));
        var build = await f.Shell.Builds.BuildAsync("Build", Path.Combine(folder, "AvaloniaApp", "AvaloniaApp.csproj"), CancellationToken.None);
        Assert.False(build.Succeeded);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.Get(styles, XamlBuildDiagnostics.Source) is { Items.Count: > 0 }, seconds: 30);
        var error = f.Shell.Languages.Diagnostics.Get(styles, XamlBuildDiagnostics.Source)!.Items[0];
        Assert.StartsWith("AVLN", error.Code, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllLines(styles).ToList().FindIndex(l => l.Contains("NoSuchProperty", StringComparison.Ordinal)), error.Range.Start.Line);
    }
}
