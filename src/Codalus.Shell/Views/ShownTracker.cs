using Avalonia.Controls;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// Tells a tool window view's view model (<see cref="IShownAware"/>) whether the window is shown: attached and visible
/// (M56, as the Solution Explorer tells its own). A closed tool window clears its view's data context before the view
/// leaves the tree, so the view model it told hears it's hidden then; a detaching view still reports itself attached;
/// and a closed or auto-hidden window keeps its view attached, invisible, which only the next layout pass tells.
/// </summary>
internal sealed class ShownTracker
{
    private readonly Control _view;
    private IShownAware? _reported;
    private bool _shown;

    private ShownTracker(Control view)
    {
        _view = view;
        view.DataContextChanged += (_, _) => OnDataContextChanged();
        view.AttachedToVisualTree += (_, _) => Update();
        view.DetachedFromVisualTree += (_, _) => Update(detaching: true); // still attached while the event is raised
        view.LayoutUpdated += (_, _) => Update();
    }

    /// <summary>Starts telling <paramref name="view"/>'s view model whether it's shown.</summary>
    public static void Track(Control view) => _ = new ShownTracker(view);

    private void OnDataContextChanged()
    {
        if (_reported is { } old && !ReferenceEquals(old, _view.DataContext))
        {
            if (_shown)
            {
                old.SetViewShown(false);
            }

            _reported = null;
            _shown = false;
        }

        if (_view.DataContext is IShownAware model && !ReferenceEquals(model, _reported))
        {
            _reported = model;
            _shown = false;
            Update();
        }
    }

    private void Update(bool detaching = false)
    {
        var shown = !detaching && _reported is not null && _view.IsAttachedToVisualTree() && _view.IsEffectivelyVisible;
        if (shown == _shown)
        {
            return;
        }

        _shown = shown;
        _reported?.SetViewShown(shown);
    }
}
