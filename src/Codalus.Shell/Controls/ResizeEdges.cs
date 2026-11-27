using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Dock.Controls.ProportionalStackPanel;

namespace Codalus.Shell.Controls;

/// <summary>
/// Every resize edge can be grabbed across at least <see cref="GrabSize"/> px (#574): the splitters between docked windows
/// (Dock's <see cref="ProportionalStackPanelSplitter"/>), an auto-hidden tool window's slide-out edge and the dividers in
/// views (<see cref="GridSplitter"/>). Graphite's and Daylight's gutter is a 1 px hairline, and the splitter was only as
/// wide as what it draws, so on a high-DPI display the edge was a pixel to hit. What an edge draws doesn't change: its
/// template (<c>ShellStyles.axaml</c>) has a transparent <see cref="GrabAreaName"/> over it, stretched across the edge here
/// to <see cref="GrabSize"/> into the panes on either side. The edge doesn't clip to its bounds (Dock's splitter did, so
/// nothing past them took the pointer), and it is raised over its neighbors so the pane after it doesn't take the pointer
/// there first. The resize cursor is the only feedback, as before.
/// </summary>
internal static class ResizeEdges
{
    /// <summary>How wide an edge can be grabbed, its own width included.</summary>
    public const double GrabSize = 8;

    /// <summary>The template part that takes the pointer beyond what the edge draws.</summary>
    public const string GrabAreaName = "PART_GrabArea";

    private static bool s_installed;

    /// <summary>Sizes every edge's grab area as its template is applied and whenever its size or direction changes.</summary>
    public static void Install()
    {
        if (s_installed)
        {
            return;
        }

        s_installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<GridSplitter>((edge, _) => Update(edge));
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ProportionalStackPanelSplitter>((edge, _) => Update(edge));
        Control.SizeChangedEvent.AddClassHandler<GridSplitter>((edge, _) => Update(edge));
        Control.SizeChangedEvent.AddClassHandler<ProportionalStackPanelSplitter>((edge, _) => Update(edge));
        GridSplitter.ResizeDirectionProperty.Changed.AddClassHandler<GridSplitter>((edge, _) => Update(edge));
    }

    /// <summary>The margin that stretches an edge's grab area across a line <paramref name="thickness"/> px wide.</summary>
    internal static Thickness GrabMargin(double thickness, bool across)
    {
        var reach = Math.Max(0, (GrabSize - thickness) / 2);
        return across ? new Thickness(-reach, 0) : new Thickness(0, -reach);
    }

    /// <summary>Whether <paramref name="edge"/> runs down (it resizes columns, its grab area reaches left and right).</summary>
    internal static bool? RunsDown(Thumb edge) => edge switch
    {
        ProportionalStackPanelSplitter => edge.Bounds.Width <= edge.Bounds.Height, // its height follows the panel's orientation
        GridSplitter splitter => splitter.ResizeDirection switch
        {
            GridResizeDirection.Columns => true,
            GridResizeDirection.Rows => false,
            // Avalonia's own rule for Auto (GridSplitter.GetEffectiveResizeDirection).
            _ when splitter.HorizontalAlignment != HorizontalAlignment.Stretch => true,
            _ when splitter.VerticalAlignment != VerticalAlignment.Stretch => false,
            _ => splitter.Bounds.Width <= splitter.Bounds.Height,
        },
        _ => null,
    };

    private static void Update(Thumb edge)
    {
        if (RunsDown(edge) is not { } down
            || edge.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == GrabAreaName) is not { } area)
        {
            return;
        }

        area.Margin = GrabMargin(down ? edge.Bounds.Width : edge.Bounds.Height, down);

        // Over the pane after it, which the panel draws (and hit-tests) on top. Dock's panel holds each splitter in a
        // ContentPresenter of its own, so that's what's raised there.
        edge.ZIndex = 1;
        if (edge.GetVisualParent() is ContentPresenter presenter && presenter.Child == edge)
        {
            presenter.ZIndex = 1;
        }
    }
}
