using Avalonia.Headless.XUnit;

namespace DaedalusStudio.Shell.Tests;

/// <summary>Named window layouts, full screen and distraction-free mode (M23a).</summary>
public sealed class WindowLayoutTests
{
    [AvaloniaFact]
    public async Task A_saved_layout_comes_back_and_distraction_free_mode_hides_everything_but_the_documents_and_returns()
    {
        await using var f = ShellFixture.Create();

        // The tool windows the test arranges: the Start page opens on its own when the Welcome plugin activates.
        List<string> Tools() => [.. f.Shell.Layout.OpenToolWindows.Select(t => t.Id).Where(id => id != "welcome.view").Order(StringComparer.Ordinal)];
        f.Shell.Layout.ShowToolWindow(ViewModels.ErrorListViewModel.ToolWindowId, focus: false);
        ShellFixture.Pump();
        var withErrorList = Tools();
        Assert.Contains(ViewModels.ErrorListViewModel.ToolWindowId, withErrorList);

        // Saved, changed, applied: the tool windows are back.
        await f.Shell.SaveWindowLayoutAsync("Debugging");
        Assert.Contains("Debugging", f.Shell.SavedLayouts().Keys);
        f.Shell.Layout.CloseToolWindow(ViewModels.ErrorListViewModel.ToolWindowId);
        ShellFixture.Pump();
        Assert.False(f.Shell.Layout.IsVisible(ViewModels.ErrorListViewModel.ToolWindowId));
        await f.Shell.ApplyWindowLayoutAsync("Debugging");
        ShellFixture.Pump();
        Assert.Equal(withErrorList, Tools());
        await f.Shell.DeleteWindowLayoutAsync("Debugging");
        Assert.DoesNotContain("Debugging", f.Shell.SavedLayouts().Keys);

        // Full screen, and back.
        f.Shell.Run("workbench.toggleFullScreen");
        Assert.True(f.Shell.Window!.IsFullScreen);
        f.Shell.Run("workbench.toggleFullScreen");
        Assert.False(f.Shell.Window.IsFullScreen);

        // Distraction-free: no tool windows, toolbar or status bar, full screen; again: as before.
        f.Shell.Run("workbench.toggleDistractionFree");
        ShellFixture.Pump();
        Assert.True(f.Shell.IsDistractionFree);
        Assert.Empty(Tools());
        Assert.False(f.Shell.Toolbar.IsVisible);
        Assert.False(f.Shell.StatusBar.IsVisible);
        Assert.True(f.Shell.Window.IsFullScreen);
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("distractionFree"));
        f.Shell.Run("workbench.toggleDistractionFree");
        ShellFixture.Pump();
        Assert.False(f.Shell.IsDistractionFree);
        Assert.Equal(withErrorList, Tools());
        Assert.True(f.Shell.Toolbar.IsVisible);
        Assert.True(f.Shell.StatusBar.IsVisible);
        Assert.False(f.Shell.Window.IsFullScreen);
    }
}
