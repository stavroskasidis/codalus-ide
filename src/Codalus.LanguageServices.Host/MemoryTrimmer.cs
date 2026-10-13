using System.Runtime;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// Gives memory back after bursts of work (loading projects, analyzing a large file): once the host has been idle for
/// a few seconds after a burst, one compacting collection, at most once a minute. Roslyn allocates heavily while it
/// binds, and the garbage otherwise stays committed in a long-running process.
/// </summary>
internal sealed class MemoryTrimmer : IDisposable
{
    private static readonly TimeSpan s_idle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_minimumInterval = TimeSpan.FromMinutes(1);
    private readonly Timer _timer;
    private DateTime _last = DateTime.MinValue;

    public MemoryTrimmer() => _timer = new Timer(_ => Trim());

    /// <summary>Work just finished: trim once the host stays idle.</summary>
    public void WorkDone() => _timer.Change(s_idle, Timeout.InfiniteTimeSpan);

    private void Trim()
    {
        if (DateTime.UtcNow - _last < s_minimumInterval)
        {
            return;
        }

        _last = DateTime.UtcNow;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    public void Dispose() => _timer.Dispose();
}
