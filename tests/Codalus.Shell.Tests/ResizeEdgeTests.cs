using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Docking;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;
using Dock.Avalonia.Controls;
using Dock.Controls.ProportionalStackPanel;

namespace Codalus.Shell.Tests;

/// <summary>
/// Every resize edge can be grabbed across 8 px, whatever it draws (#574: on a high-DPI display the edge between docked
/// windows was a pixel to hit). Graphite's and Daylight's gutter is a 1 px hairline, and the splitter took the pointer
/// only there; Classic's and Harbor's 6 px gutters, an auto-hidden window's slide-out edge and the dividers in views were
/// 6 px or less. What each edge draws stays as it was.
/// </summary>
public sealed class ResizeEdgeTests
{
    /// <summary>Just inside the 8 px grab area, from the edge's middle; and well past it.</summary>
    private const double Inside = ResizeEdges.GrabSize / 2 - 0.25;

    private const double Outside = ResizeEdges.GrabSize / 2 + 2;

    [Theory]
    [InlineData(1, 3.5)]
    [InlineData(6, 1)]
    [InlineData(8, 0)]
    [InlineData(14, 0)] // the Markdown preview's divider, wider already
    public void The_grab_area_reaches_past_a_thin_edge_to_8_px(double thickness, double reach)
    {
        Assert.Equal(new Thickness(-reach, 0), ResizeEdges.GrabMargin(thickness, across: true));
        Assert.Equal(new Thickness(0, -reach), ResizeEdges.GrabMargin(thickness, across: false));
    }

    [AvaloniaFact]
    public async Task The_edges_between_docked_windows_take_the_pointer_across_8_px_in_every_family()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        f.Shell.Layout.ShowToolWindow(BuiltIns.OutputWindow, focus: false);
        foreach (var (theme, gutter) in new[] { ("dark", 6d), ("harbor.dark", 6), ("graphite.dark", 1), ("daylight.light", 1), ("ember.dark", 8) })
        {
            await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None);
            await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == theme);
            ShellFixture.Pump();
            ShellFixture.RenderFrame(f.Window);

            var splitters = f.Window.GetVisualDescendants().OfType<ProportionalStackPanelSplitter>().Where(s => s.IsEffectivelyVisible && s.Bounds.Width > 0 && s.Bounds.Height > 0).ToList();
            Assert.True(splitters.Count >= 2, $"{theme}: {splitters.Count} splitters"); // beside Solution Explorer, over Output
            foreach (var splitter in splitters)
            {
                Assert.Equal(gutter, splitter.Thickness); // drawn as before
                AssertGrabbable(f.Window, splitter, theme);
            }
        }
    }

    [AvaloniaFact]
    public async Task Dragging_from_beside_a_hairline_resizes_the_windows()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "graphite.dark", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "graphite.dark");
        ShellFixture.Pump();
        ShellFixture.RenderFrame(f.Window);

        // The hairline down the window's left side, and a press 3 px left of it: over Solution Explorer's edge before #574.
        var splitter = f.Window.GetVisualDescendants().OfType<ProportionalStackPanelSplitter>()
            .Where(s => s.IsEffectivelyVisible && s.Bounds.Width == 1 && s.Bounds.Height > 100)
            .OrderBy(s => s.TranslatePoint(default, f.Window)!.Value.X).First();
        double Left() => splitter.TranslatePoint(default, f.Window)!.Value.X;
        var before = Left();
        var press = new Point(before - 3, splitter.TranslatePoint(new Point(0, splitter.Bounds.Height / 2), f.Window)!.Value.Y);
        f.Window.MouseDown(press, MouseButton.Left);
        f.Window.MouseMove(press + new Point(20, 0));
        f.Window.MouseMove(press + new Point(40, 0));
        f.Window.MouseUp(press + new Point(40, 0), MouseButton.Left);
        ShellFixture.Pump();
        ShellFixture.RenderFrame(f.Window);
        Assert.Equal(before + 40, Left(), 1.5);
    }

    [AvaloniaFact]
    public async Task An_auto_hidden_windows_slide_out_edge_takes_the_pointer_across_8_px()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        Assert.True(f.Shell.Layout.Restore(new LayoutSnapshot(LayoutSnapshot.CurrentVersion, new LayoutNode("proportional")
        {
            Id = "Main",
            Orientation = "horizontal",
            Children = [new LayoutNode("documents") { Id = LayoutManager.DocumentsId }],
        })
        {
            RightPinned = [GitChangesViewModel.ToolWindowId],
        }));
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId, focus: false);
        GridSplitter? edge = null;
        await ShellFixture.PumpUntil(() =>
        {
            f.Window.UpdateLayout();
            edge = f.Window.GetVisualDescendants().OfType<PinnedDockControl>().SelectMany(p => p.GetVisualDescendants().OfType<GridSplitter>())
                .FirstOrDefault(s => s.Name == "PART_PinnedDockSplitter" && s.IsEffectivelyVisible && s.Bounds.Height > 0);
            return edge is not null;
        });
        ShellFixture.RenderFrame(f.Window);
        AssertGrabbable(f.Window, edge!, "the slide-out");
    }

    [AvaloniaFact]
    public void A_views_divider_takes_the_pointer_across_8_px_whichever_way_it_runs()
    {
        // Two dividers 2 px wide, one between columns, one between rows (ResizeDirection Auto, as Avalonia works it out).
        var columns = new GridSplitter { Width = 2, MinWidth = 0, [Grid.ColumnProperty] = 1 };
        var rows = new GridSplitter { Height = 2, MinHeight = 0, ResizeDirection = GridResizeDirection.Rows, [Grid.RowProperty] = 1, [Grid.ColumnSpanProperty] = 3 };
        var grid = new Grid { ColumnDefinitions = new("*,Auto,*"), RowDefinitions = new("*,Auto,*") };
        grid.Children.Add(new Border { Background = Avalonia.Media.Brushes.White, [Grid.RowSpanProperty] = 3 });
        grid.Children.Add(columns);
        grid.Children.Add(new Border { Background = Avalonia.Media.Brushes.White, [Grid.ColumnProperty] = 2 });
        grid.Children.Add(new Border { Background = Avalonia.Media.Brushes.White, [Grid.ColumnProperty] = 2, [Grid.RowProperty] = 2 });
        grid.Children.Add(rows);
        var window = new Window { Width = 400, Height = 300, Content = grid };
        window.Show();
        try
        {
            ShellFixture.RenderFrame(window);
            Assert.Equal(2, columns.Bounds.Width);
            Assert.Equal(2, rows.Bounds.Height);
            AssertGrabbable(window, columns, "the column divider");
            AssertGrabbable(window, rows, "the row divider");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The pointer finds <paramref name="edge"/>, with its resize cursor, just inside 8 px around its middle line on
    /// either side, and the neighbor past that.
    /// </summary>
    private static void AssertGrabbable(TopLevel top, Control edge, string what)
    {
        var down = edge.Bounds.Width <= edge.Bounds.Height;
        var middle = edge.TranslatePoint(new Point(edge.Bounds.Width / 2, edge.Bounds.Height / 2), top)!.Value;
        Point At(double offset) => down ? middle + new Point(offset, 0) : middle + new Point(0, offset);
        IInputElement? Hit(double offset) => top.InputHitTest(At(offset));
        bool OnEdge(IInputElement? hit) => hit is Visual visual && (visual == edge || visual.GetVisualAncestors().Contains(edge));

        foreach (var offset in new[] { -Inside, 0, Inside })
        {
            var hit = Hit(offset);
            Assert.True(OnEdge(hit), $"{what} ({edge.Bounds.Size}) at {offset:+0.##;-0.##;0} px: {hit?.GetType().Name} took the pointer");
            Assert.Same(edge.Cursor, ((InputElement)hit!).Cursor);
        }

        foreach (var offset in new[] { -Outside, Outside })
        {
            Assert.False(OnEdge(Hit(offset)), $"{what} at {offset:+0.##;-0.##} px still took the pointer");
        }
    }
}
