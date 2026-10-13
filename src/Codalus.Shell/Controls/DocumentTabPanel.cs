using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Codalus.Shell.Controls;

/// <summary>
/// The tabs of a document group (M34e): one row at their natural widths. When they don't fit, whole tabs are left out
/// from the right, as a classic IDE's document well does, but never a pinned tab (<see cref="DocumentTab.IsPinnedProperty"/>)
/// or the selected one, so pinned tabs stay in view at the left instead of scrolling away. The documents left out are
/// in the Window menu and Ctrl+Tab.
/// </summary>
public sealed class DocumentTabPanel : Panel
{
    static DocumentTabPanel()
    {
        // Which tabs stay depends on the selection and the pins.
        AffectsParentArrange<DocumentTabPanel>(TabStripItem.IsSelectedProperty, DocumentTab.IsPinnedProperty);
    }

    public DocumentTabPanel() => ClipToBounds = true;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Dock's template scrolls the strip sideways (HorizontalScrollBarVisibility="Auto" on the template's own
        // ScrollViewer, which a style can't override), and that gives the panel unlimited width: it needs the group's.
        if (this.FindAncestorOfType<ScrollViewer>() is { } scroll)
        {
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        foreach (var child in Children.Where(c => c.IsVisible))
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children.Where(c => c.IsVisible).ToList();
        var shown = Shown([.. children.Select(c => (c.DesiredSize.Width, Keep(c)))], finalSize.Width);
        double x = 0;
        for (var i = 0; i < children.Count; i++)
        {
            if (!shown[i])
            {
                // Past the right edge, clipped: a zero-size arrange would still give a tab its minimum width.
                children[i].Arrange(new Rect(finalSize.Width, 0, children[i].DesiredSize.Width, finalSize.Height));
                continue;
            }

            children[i].Arrange(new Rect(x, 0, children[i].DesiredSize.Width, finalSize.Height));
            x += children[i].DesiredSize.Width;
        }

        return finalSize;
    }

    private static bool Keep(Control tab) => DocumentTab.GetIsPinned(tab) || tab is TabStripItem { IsSelected: true };

    /// <summary>
    /// Which tabs show in <paramref name="available"/> width: all when they fit; otherwise tabs leave from the right,
    /// except those to keep (pinned, selected), until the rest fit.
    /// </summary>
    public static bool[] Shown(IReadOnlyList<(double Width, bool Keep)> tabs, double available)
    {
        var shown = Enumerable.Repeat(true, tabs.Count).ToArray();
        var total = tabs.Sum(t => t.Width);
        for (var i = tabs.Count - 1; i >= 0 && total > available + 0.5; i--)
        {
            if (!tabs[i].Keep)
            {
                shown[i] = false;
                total -= tabs[i].Width;
            }
        }

        return shown;
    }
}
