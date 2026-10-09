using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Dock.Model.Core;

namespace DaedalusStudio.Shell.Controls;

/// <summary>Implemented by pane views that know which control should get focus when the pane is entered.</summary>
public interface IPaneView
{
    void FocusPrimary();
}

/// <summary>
/// Wraps the view of every tool window and document. It identifies the pane for the <c>focusedView</c> context
/// key and for F6 pane cycling.
/// </summary>
public sealed class PaneHost : Decorator
{
    private static readonly List<PaneHost> s_attached = [];

    public PaneHost(IDockable dockable, Control content)
    {
        Dockable = dockable;
        Child = content;

        // Focusable as a last resort, so F6 always lands somewhere; Tab skips it.
        Focusable = true;
        IsTabStop = false;
    }

    public IDockable Dockable { get; }

    /// <summary>Focuses the pane's primary control (or its first focusable descendant).</summary>
    public void FocusContent()
    {
        if (Child is IPaneView view)
        {
            view.FocusPrimary();
            if (IsKeyboardFocusWithin)
            {
                return;
            }
        }

        var first = this.GetVisualDescendants()
            .OfType<InputElement>()
            .FirstOrDefault(c => c != this && c.Focusable && c.IsEffectivelyEnabled && c.IsEffectivelyVisible);
        if (first?.Focus(NavigationMethod.Tab) != true || !IsKeyboardFocusWithin)
        {
            Focus(NavigationMethod.Tab);
        }
    }

    /// <summary>The visible panes of <paramref name="root"/> in reading order (top to bottom, left to right).</summary>
    public static IReadOnlyList<PaneHost> VisiblePanes(Visual root) =>
        [.. s_attached
            .Where(p => p.IsEffectivelyVisible && p.Bounds.Width > 0 && p.Bounds.Height > 0 && TopLevel.GetTopLevel(p) == TopLevel.GetTopLevel(root))
            .Select(p => (Pane: p, Origin: p.TranslatePoint(default, root) ?? default))
            .OrderBy(p => Math.Round(p.Origin.Y / 8))
            .ThenBy(p => p.Origin.X)
            .Select(p => p.Pane)];

    /// <summary>The pane containing <paramref name="visual"/>, if any.</summary>
    public static PaneHost? Of(Visual? visual) =>
        visual is null ? null : visual as PaneHost ?? visual.FindAncestorOfType<PaneHost>();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        s_attached.Add(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        s_attached.Remove(this);
    }
}
