using Avalonia.Controls;

namespace Codalus.Shell.Controls;

/// <summary>
/// A menu filled each time it opens, before Avalonia makes what shows it. Items added in a MenuFlyout's Opening event
/// never reached the open menu, which showed as an empty strip (AI Chat's pickers in 0.8.0's release candidate; the
/// agent picker, the toolbar's folded groups and the path box's recent locations too).
/// </summary>
public sealed class FilledMenuFlyout(Action<MenuFlyout> fill) : MenuFlyout
{
    protected override bool ShowAtCore(Control placementTarget, bool showAtPointer = false)
    {
        fill(this);
        return base.ShowAtCore(placementTarget, showAtPointer);
    }
}
