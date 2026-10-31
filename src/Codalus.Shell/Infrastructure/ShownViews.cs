namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Whether any view shows a view model, for tool windows that do no work while hidden and catch up when shown (M56). Dock
/// builds a tool window's view again for a new layout while the old one still shows it, so the new view can say it's
/// shown before the old one says it isn't: one flag, set by whichever spoke last, ended up hidden while the window was on
/// screen. Each view speaks for itself, and the model is shown while any of them is.
/// </summary>
internal sealed class ShownViews
{
    private readonly HashSet<object> _views = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether any view shows the model.</summary>
    public bool Any => _views.Count > 0;

    /// <summary>Records whether <paramref name="view"/> shows the model; true when that changed <see cref="Any"/>.</summary>
    public bool Set(object view, bool shown)
    {
        var before = Any;
        if (shown)
        {
            _views.Add(view);
        }
        else
        {
            _views.Remove(view);
        }

        return before != Any;
    }
}
