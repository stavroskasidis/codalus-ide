using System.Runtime;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// Gives memory back after bursts of work (loading projects, analyzing a large file): once the host has been idle for
/// a few seconds after a burst, one compacting collection, at most once a minute. Roslyn allocates heavily while it
/// binds, and the garbage otherwise stays committed in a long-running process.
/// </summary>
/// <remarks>
/// Every open document's analysis says its work is done, so a pause of a few seconds in typing came after "work":
/// a full, blocking, compacting collection at most once a minute while someone edits, which holds every request
/// for its duration. It runs now only after a burst: what the process allocated since the last trim (a project
/// load, the background pass, a large file) passes <see cref="BurstBytes"/>, or the heap is that fragmented.
/// </remarks>
internal sealed class MemoryTrimmer : IDisposable
{
    private static readonly TimeSpan s_idle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_minimumInterval = TimeSpan.FromMinutes(1);

    /// <summary>How much allocation (or fragmentation) counts as a burst worth a collection.</summary>
    public const long BurstBytes = 256L * 1024 * 1024;

    private readonly Timer _timer;
    private readonly Func<DateTime> _now;
    private readonly Func<long> _allocated;
    private readonly Func<long> _fragmented;
    private readonly Action _collect;
    private long _lastTicks = DateTime.MinValue.Ticks;
    private long _allocatedAtLast;

    public MemoryTrimmer()
        : this(() => DateTime.UtcNow, () => GC.GetTotalAllocatedBytes(), () => GC.GetGCMemoryInfo().FragmentedBytes, Collect)
    {
    }

    /// <summary>For tests: the clock, the allocation and fragmentation probes and the collection.</summary>
    internal MemoryTrimmer(Func<DateTime> now, Func<long> allocated, Func<long> fragmented, Action collect)
    {
        _now = now;
        _allocated = allocated;
        _fragmented = fragmented;
        _collect = collect;
        _timer = new Timer(_ => Trim());
    }

    /// <summary>Work just finished: trim once the host stays idle.</summary>
    public void WorkDone() => _timer.Change(s_idle, Timeout.InfiniteTimeSpan);

    /// <summary>What the timer does once the host stayed idle (tests call it directly); true if it collected.</summary>
    internal bool Trim()
    {
        var now = _now();
        if (now.Ticks - Interlocked.Read(ref _lastTicks) < s_minimumInterval.Ticks)
        {
            return false;
        }

        var allocated = _allocated();
        if (allocated - Interlocked.Read(ref _allocatedAtLast) < BurstBytes && _fragmented() < BurstBytes)
        {
            return false; // small work since the last trim (typing, a few requests): nothing worth a blocking collection
        }

        Interlocked.Exchange(ref _lastTicks, now.Ticks);
        _collect();
        Interlocked.Exchange(ref _allocatedAtLast, _allocated());
        return true;
    }

    private static void Collect()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    public void Dispose() => _timer.Dispose();
}
