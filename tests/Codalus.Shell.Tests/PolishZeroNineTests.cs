using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;

namespace Codalus.Shell.Tests;

/// <summary>
/// 0.9's UI fixes from the maintainer's review: auto-hidden tool windows' tabs read top to bottom on both sides, as
/// Classic's, with the bar on the window's outer edge.
/// </summary>
public partial class PolishTests
{
    [AvaloniaFact]
    public async Task Auto_hidden_tabs_read_top_to_bottom_with_their_bar_on_the_outer_edge_on_both_sides()
    {
        await using var f = ShellFixture.Create();
        var layout = f.Shell.Layout;
        Assert.True(layout.Restore(layout.Capture(null) with { LeftPinned = ["solutionExplorer"], RightPinned = [BuiltIns.OutputWindow] }));
        ShellFixture.Pump();
        ShellFixture.Pump();

        ToolPinItemControl Tab(Avalonia.Controls.Dock side) => f.Window.GetVisualDescendants().OfType<ToolPinnedControl>()
            .Where(p => DockPanel.GetDock(p) == side && p.IsVisible)
            .SelectMany(p => p.GetVisualDescendants().OfType<ToolPinItemControl>())
            .Single();

        foreach (var (side, bar) in new[] { (Avalonia.Controls.Dock.Left, new Thickness(0, 0, 0, 3)), (Avalonia.Controls.Dock.Right, new Thickness(0, 3, 0, 0)) })
        {
            var tab = Tab(side);
            var turn = tab.GetVisualDescendants().OfType<LayoutTransformControl>().Single();
            Assert.Equal(90, Assert.IsType<RotateTransform>(turn.LayoutTransform).Angle);

            // Turned clockwise, the border's bottom is the tab's left edge and its top the right one.
            var border = tab.GetVisualDescendants().OfType<Border>().First(b => b.TemplatedParent == tab);
            Assert.Equal(bar, border.BorderThickness);
            Assert.True(tab.Bounds.Height > tab.Bounds.Width, $"the {side} tab is {tab.Bounds}");
        }
    }
}
