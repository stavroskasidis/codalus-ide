using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// Issue 355: Options' list is its real height, so the scroll bar's thumb stays under the pointer that drags it. Its rows
/// are from 60 to 550 px high, and a virtualizing panel estimated the list's height from the rows in view: All Settings
/// went from 16,700 to 41,200 px and back while it scrolled, and the thumb ran away from the pointer.
/// </summary>
public class SettingsScrollTests
{
    private sealed record Page(Window Window, SettingsView View, SettingsViewModel Model, ScrollViewer Scroll) : IDisposable
    {
        public IEnumerable<DeferredContent> Rows => View.GetVisualDescendants().OfType<DeferredContent>();

        public void Dispose() => Window.Close();
    }

    private static Page Open(ShellFixture f)
    {
        f.Shell.OpenSettings();
        var document = f.Shell.Layout.ActiveDocument;
        ShellFixture.Pump();
        var window = new Window { Width = 1200, Height = 800, Content = new ContentControl { Content = document } };
        window.Show();
        ShellFixture.Pump();
        var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Content is StackPanel);
        return new Page(window, view, (SettingsViewModel)view.DataContext!, scroll);
    }

    [AvaloniaFact]
    public async Task Dragging_the_thumb_through_all_settings_keeps_it_under_the_pointer()
    {
        await using var f = ShellFixture.Create();
        using var page = Open(f);
        page.Model.SelectedCategory = page.Model.Categories[0];
        ShellFixture.Pump();
        Assert.True(page.Model.Items.Count > 100, $"All Settings has {page.Model.Items.Count} settings");
        Assert.All(page.Rows, r => Assert.True(r.IsBuilt));

        var bar = page.Scroll.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical && b.FindAncestorOfType<ScrollViewer>() == page.Scroll);
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
        Point Center() => thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), page.Window)!.Value;

        ShellFixture.RenderFrame(page.Window);
        var start = Center();
        start = new Point(Math.Round(start.X), Math.Round(start.Y));
        var track = bar.GetVisualDescendants().OfType<Track>().Single();
        var bottom = track.TranslatePoint(new Point(0, track.Bounds.Height), page.Window)!.Value.Y - (thumb.Bounds.Height / 2);
        var extent = page.Scroll.Extent.Height;
        page.Window.MouseDown(start, MouseButton.Left);
        var offsets = new List<double>();
        const int steps = 60;
        for (var step = 1; step <= steps; step++)
        {
            var pointer = start.WithY(Math.Round(start.Y + ((bottom - start.Y) * step / steps)));
            page.Window.MouseMove(pointer, RawInputModifiers.LeftMouseButton);
            ShellFixture.Pump();
            Assert.True(Math.Abs(page.Scroll.Extent.Height - extent) < 1, $"Step {step}: the list's height changed from {extent:F0} to {page.Scroll.Extent.Height:F0} px.");
            Assert.True(Math.Abs(Center().Y - pointer.Y) < 2, $"Step {step}: the thumb's center is at {Center().Y:F1}, the pointer at {pointer.Y:F1}.");
            offsets.Add(page.Scroll.Offset.Y);
        }

        // Past the track's end the list is at its end. (Within 2 px of the pointer is up to about 25 px of the list per
        // pixel of the thumb: Linux CI's last step was 58 px short of the end, run 43701085779.)
        var past = start.WithY(Math.Round(bottom + thumb.Bounds.Height));
        page.Window.MouseMove(past, RawInputModifiers.LeftMouseButton);
        ShellFixture.Pump();
        offsets.Add(page.Scroll.Offset.Y);
        page.Window.MouseUp(past, MouseButton.Left);
        Assert.Equal(offsets.Order(), offsets);
        Assert.Equal(page.Scroll.Extent.Height - page.Scroll.Viewport.Height, offsets[^1], tolerance: 1);
        Assert.True(Math.Abs(page.Scroll.Extent.Height - extent) < 1, $"At the end: the list's height changed from {extent:F0} to {page.Scroll.Extent.Height:F0} px.");
    }

    [AvaloniaFact]
    public async Task Opening_all_settings_makes_the_rows_near_the_view_and_the_rest_in_the_background_without_moving_the_view()
    {
        await using var f = ShellFixture.Create();
        using var page = Open(f);
        page.Model.SelectedCategory = page.Model.Categories[^1];
        ShellFixture.Pump();

        // The first layout makes the rows near the view, not the 177 rows at once (that stalled the UI for seconds).
        page.Model.SelectedCategory = page.Model.Categories[0];
        page.Window.UpdateLayout();
        var built = page.Rows.Count(r => r.IsBuilt);
        Assert.InRange(built, 1, page.Model.Items.Count / 3);
        Assert.Contains(page.View.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("setting") && b.IsEffectivelyVisible);

        // Jumping into the middle makes the rows there in that layout. The rows above are made later, at their real
        // heights, and the setting at the top of the view stays where it is.
        page.Scroll.Offset = new Vector(0, page.Scroll.Extent.Height / 2);
        page.Window.UpdateLayout();
        var top = page.Rows.First(r => r.IsBuilt && r.TranslatePoint(new Point(0, r.Bounds.Height), page.Scroll)!.Value.Y > 0);
        var at = top.TranslatePoint(default, page.Scroll)!.Value.Y;
        Assert.Contains(page.Rows.TakeWhile(r => r != top), r => !r.IsBuilt);
        string State() => $"row {page.Rows.ToList().IndexOf(top)} of {page.Rows.Count()} at {top.TranslatePoint(default, page.Scroll)!.Value.Y:F0} px, "
            + $"offset {page.Scroll.Offset.Y:F0}, extent {page.Scroll.Extent.Height:F0}, viewport {page.Scroll.Viewport.Height:F0}, "
            + $"built {page.Rows.Count(r => r.IsBuilt)}, row height {top.Bounds.Height:F0}";
        var before = State();
        ShellFixture.Pump();
        page.Window.UpdateLayout();
        Assert.All(page.Rows, r => Assert.True(r.IsBuilt));

        // Windows CI once had the row 52 px lower (-60 to -8, run 43733266567), with no cause known yet: what the view
        // and the row were before and after the rows above were made.
        var moved = top.TranslatePoint(default, page.Scroll)!.Value.Y;
        Assert.True(Math.Abs(at - moved) <= 1, $"The setting at the top of the view moved from {at:F0} to {moved:F0} px. Before: {before}. After: {State()}.");
    }
}
