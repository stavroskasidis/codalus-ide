using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Docking;
using Codalus.Shell.ViewModels;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace Codalus.Shell.Tests;

/// <summary>
/// Auto-hidden (unpinned) tool windows slide out at a usable size, and keep the size the user gives them across restarts
/// (the maintainer's report, 2026-11-02: after a restart, Git Changes and Solution Explorer, auto-hidden on the right,
/// slid out a few characters wide, and a width set by dragging was gone once the app was closed and opened again). The
/// tests use tool windows the fixture's default layout doesn't show: one it showed keeps the size it had there, as Dock
/// gives a tool window the user unpins.
/// </summary>
public sealed class AutoHideToolWindowTests
{
    private static LayoutSnapshot Snapshot(WindowPlacement? window = null) =>
        new(LayoutSnapshot.CurrentVersion, new LayoutNode("proportional")
        {
            Id = "Main",
            Orientation = "horizontal",
            Children = [new LayoutNode("documents") { Id = LayoutManager.DocumentsId }],
        })
        {
            RightPinned = [GitChangesViewModel.ToolWindowId, SolutionExplorerViewModel.ToolWindowId],
            BottomPinned = [TaskListViewModel.ToolWindowId],
            Window = window,
        };

    [AvaloniaFact]
    public async Task A_restored_auto_hidden_tool_window_slides_out_at_its_panes_size()
    {
        await using var f = ShellFixture.Create();
        Assert.True(f.Shell.Layout.Restore(Snapshot(new WindowPlacement(1600, 1000, null, null, Maximized: false))));

        // The side's pane takes 22% of the window's width, the bottom's 28% of its height.
        var gitChanges = await SlideOutAsync(f, GitChangesViewModel.ToolWindowId);
        Assert.Equal(1600 * 0.22, gitChanges.Bounds.Width, 1);
        var taskList = await SlideOutAsync(f, TaskListViewModel.ToolWindowId);
        Assert.Equal(1000 * 0.28, taskList.Bounds.Height, 1);

        // A layout without the window's size (a saved window layout) uses the default window's.
        await using var g = ShellFixture.Create();
        Assert.True(g.Shell.Layout.Restore(Snapshot()));
        Assert.Equal(Math.Round(1280 * 0.22), (await SlideOutAsync(g, GitChangesViewModel.ToolWindowId)).Bounds.Width, 1);
    }

    [AvaloniaFact]
    public async Task An_auto_hidden_tool_windows_size_survives_a_restart()
    {
        string saved;
        await using (var f = ShellFixture.Create())
        {
            Assert.True(f.Shell.Layout.Restore(Snapshot()));
            await SlideOutAsync(f, GitChangesViewModel.ToolWindowId);

            // What the slide-out's splitter records as the user drags it.
            f.Shell.Layout.ToolWindow(GitChangesViewModel.ToolWindowId)!.SetPinnedBounds(0, 0, 432, 600);
            f.Shell.Layout.ToolWindow(TaskListViewModel.ToolWindowId)!.SetPinnedBounds(0, 0, 900, 345);
            saved = JsonSerializer.Serialize(f.Shell.Layout.Capture(null), ShellJsonContext.Default.LayoutSnapshot);
        }

        await using var g = ShellFixture.Create();
        Assert.True(g.Shell.Layout.Restore(JsonSerializer.Deserialize(saved, ShellJsonContext.Default.LayoutSnapshot)!));
        Assert.Equal(432, (await SlideOutAsync(g, GitChangesViewModel.ToolWindowId)).Bounds.Width, 1);
        Assert.Equal(345, (await SlideOutAsync(g, TaskListViewModel.ToolWindowId)).Bounds.Height, 1);
    }

    /// <summary>Slides <paramref name="id"/> out (its strip button's click) and returns the slide-out's content once laid out.</summary>
    private static async Task<Control> SlideOutAsync(ShellFixture f, string id)
    {
        f.Shell.Layout.ShowToolWindow(id, focus: false);
        Control? content = null;
        await ShellFixture.PumpUntil(
            () =>
            {
                f.Window.UpdateLayout();
                content = f.Window.GetVisualDescendants().OfType<PinnedDockControl>()
                    .Select(p => p.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault(c => c.Name == "PART_PinnedDock"))
                    .FirstOrDefault(c => c is { IsEffectivelyVisible: true, Bounds.Width: > 0, Bounds.Height: > 0 }
                        && f.Shell.Layout.Root.PinnedDock?.ActiveDockable?.Id == id);
                return content is not null;
            },
            describe: () => $"{id} didn't slide out; the pinned dock shows {f.Shell.Layout.Root.PinnedDock?.ActiveDockable?.Id ?? "nothing"}");
        ShellFixture.Pump();
        f.Window.UpdateLayout();
        return content!;
    }
}
