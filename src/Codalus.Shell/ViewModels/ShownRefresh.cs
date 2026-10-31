using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.ViewModels;

/// <summary>A tool window's view model told whether its window is shown (<see cref="Views.ShownTracker"/>).</summary>
public interface IShownAware
{
    /// <summary>
    /// <paramref name="view"/> shows the window, or no longer does: closed, auto-hidden, or a tab behind another. Each view
    /// speaks for itself: Dock builds a window's view again for a new layout while the old one still shows it.
    /// </summary>
    void SetViewShown(object view, bool shown);
}

/// <summary>
/// What a debugger window refreshes at each stop, done only while the window is shown (M56, as the classic IDE's
/// windows do): asked for while it's hidden, the refresh waits, and runs once when the window is shown. Threads, Parallel
/// Stacks and Parallel Watch asked the adapter for every thread's stack at each stop, open or not, ahead of the
/// next step's own requests. The window is shown while any of its views shows it (<see cref="ShownViews"/>).
/// </summary>
/// <param name="refresh">The refresh.</param>
/// <param name="stale">Whether the window refreshes when it's first shown, asked or not.</param>
public sealed class ShownRefresh(Action refresh, bool stale = true)
{
    private readonly ShownViews _views = new();
    private bool _stale = stale;

    /// <summary>Whether the window is shown.</summary>
    public bool IsShown => _views.Any;

    /// <summary>Refreshes now while the window is shown; else when it is.</summary>
    public void Request()
    {
        if (IsShown)
        {
            _stale = false;
            refresh();
        }
        else
        {
            _stale = true;
        }
    }

    /// <summary>The window refreshed by itself (a command, a test): it's up to date, and showing it asks nothing again.</summary>
    public void Fresh() => _stale = false;

    /// <summary><paramref name="view"/> shows the window, or no longer does: a refresh asked for meanwhile runs once it's shown.</summary>
    public void SetShown(object view, bool shown)
    {
        if (_views.Set(view, shown) && shown && _stale)
        {
            _stale = false;
            refresh();
        }
    }
}
