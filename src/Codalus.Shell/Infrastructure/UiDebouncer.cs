using System.Diagnostics;
using Avalonia.Threading;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Runs an action on the UI thread once for a burst of requests: when none has come for <c>delay</c>, and at least
/// every <c>maxDelay</c> while they keep coming. Requested from any thread. For what a solution's projects change: a
/// large solution's open evaluates them one at a time (and again after a restore and the design-time builds), so a
/// reaction that walks every project ran once per project, hundreds of times while the solution loaded.
/// </summary>
internal sealed class UiDebouncer(Action action, TimeSpan delay, TimeSpan maxDelay)
{
    private DispatcherTimer? _timer;
    private long _firstRequest; // when the oldest request not yet served came (Stopwatch ticks), or 0
    private int _posted;

    /// <summary>Whether a request waits to be served (tests).</summary>
    internal bool IsPending => _timer?.IsEnabled == true || Volatile.Read(ref _posted) == 1;

    public void Request()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Restart();
        }
        else if (Interlocked.Exchange(ref _posted, 1) == 0)
        {
            // One post for the requests from other threads until it runs.
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _posted, 0);
                Restart();
            });
        }
    }

    /// <summary>Serves a waiting request now (the action runs once more for it).</summary>
    public void Flush()
    {
        if (_timer?.IsEnabled == true)
        {
            Run();
        }
    }

    private void Restart()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = delay };
            _timer.Tick += (_, _) => Run();
        }

        if (_firstRequest == 0)
        {
            _firstRequest = Stopwatch.GetTimestamp();
        }
        else if (Stopwatch.GetElapsedTime(_firstRequest) >= maxDelay)
        {
            return; // the timer's next tick serves it: the burst has waited long enough
        }

        _timer.Stop();
        _timer.Start();
    }

    private void Run()
    {
        _timer!.Stop();
        _firstRequest = 0;
        action();
    }
}
