namespace Codalus.Shell.ViewModels;

/// <summary>A tool window's view model told whether its window is shown (<see cref="Views.ShownTracker"/>).</summary>
public interface IShownAware
{
    /// <summary>The window was shown, or hidden: closed, auto-hidden, or a tab behind another.</summary>
    void SetViewShown(bool shown);
}

/// <summary>
/// What a debugger window refreshes at each stop, done only while the window is shown (M56, as the classic IDE's
/// windows do): asked for while it's hidden, the refresh waits, and runs once when the window is shown. Threads, Parallel
/// Stacks and Parallel Watch asked the adapter for every thread's stack at each stop, open or not, ahead of the
/// next step's own requests.
/// </summary>
public sealed class ShownRefresh(Action refresh)
{
    private bool _stale = true;

    /// <summary>Whether the window is shown.</summary>
    public bool IsShown { get; private set; }

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

    public void SetShown(bool shown)
    {
        IsShown = shown;
        if (shown && _stale)
        {
            _stale = false;
            refresh();
        }
    }
}
