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
/// With <see cref="PinnedRow"/> (<c>workbench.editor.pinnedTabsOnSeparateRow</c>), a group with pinned and unpinned
/// tabs shows its pinned tabs on a row of their own above the others, as the classic IDE does; each row then leaves
/// tabs out on its own, keeping only the selected one.
/// </summary>
public sealed class DocumentTabPanel : Panel
{
    /// <summary>Whether pinned tabs take a row of their own above the others.</summary>
    public static readonly StyledProperty<bool> PinnedRowProperty = AvaloniaProperty.Register<DocumentTabPanel, bool>(nameof(PinnedRow));

    static DocumentTabPanel()
    {
        // Which tabs stay depends on the selection; which row a tab takes, and so the panel's height, on the pins.
        AffectsParentArrange<DocumentTabPanel>(TabStripItem.IsSelectedProperty);
        AffectsParentMeasure<DocumentTabPanel>(DocumentTab.IsPinnedProperty);
        AffectsMeasure<DocumentTabPanel>(PinnedRowProperty);
    }

    public DocumentTabPanel() => ClipToBounds = true;

    public bool PinnedRow
    {
        get => GetValue(PinnedRowProperty);
        set => SetValue(PinnedRowProperty, value);
    }

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
        foreach (var row in Rows())
        {
            double rowWidth = 0, rowHeight = 0;
            foreach (var child in row)
            {
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                rowWidth += child.DesiredSize.Width;
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            width = Math.Max(width, rowWidth);
            height += rowHeight;
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = Rows();
        double y = 0;
        foreach (var row in rows)
        {
            // One row takes the panel's height, as before; two take their own.
            var height = rows.Count == 1 ? finalSize.Height : row.Max(c => c.DesiredSize.Height);
            ArrangeRow(row, keepPinned: rows.Count == 1, y, finalSize.Width, height);
            y += height;
        }

        return finalSize;
    }

    private static void ArrangeRow(List<Control> children, bool keepPinned, double y, double available, double height)
    {
        var shown = Shown([.. children.Select(c => (c.DesiredSize.Width, Keep(c, keepPinned)))], available);
        double x = 0;
        for (var i = 0; i < children.Count; i++)
        {
            if (!shown[i])
            {
                // Past the right edge, clipped: a zero-size arrange would still give a tab its minimum width.
                children[i].Arrange(new Rect(available, y, children[i].DesiredSize.Width, height));
                continue;
            }

            // The tabs to keep can be wider than the group on their own (a pinned and a selected tab with long names): the
            // last one gets what is left, its title cut off, rather than running past the edge.
            var width = Fitted(children[i].DesiredSize.Width, x, available);
            children[i].Arrange(new Rect(x, y, width, height));
            x += width;
        }
    }

    /// <summary>The visible tabs by row: the pinned ones and the others when <see cref="PinnedRow"/> has both, else one row.</summary>
    private List<List<Control>> Rows()
    {
        var children = Children.Where(c => c.IsVisible).ToList();
        var pinned = children.Where(DocumentTab.GetIsPinned).ToList();
        return PinnedRow && pinned.Count > 0 && pinned.Count < children.Count
            ? [pinned, [.. children.Where(c => !DocumentTab.GetIsPinned(c))]]
            : [children];
    }

    /// <summary>A shown tab's width at <paramref name="x"/>: its own, or what is left of <paramref name="available"/>.</summary>
    public static double Fitted(double width, double x, double available) => Math.Max(0, Math.Min(width, available - x));

    private static bool Keep(Control tab, bool keepPinned) =>
        (keepPinned && DocumentTab.GetIsPinned(tab)) || tab is TabStripItem { IsSelected: true };

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
