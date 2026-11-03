using System.Globalization;

namespace Codalus.Shell.Infrastructure;

/// <summary>Where the garbage collector was at a point in time: its collections so far and its total pause (<see cref="GcReport.Mark"/>).</summary>
internal readonly record struct GcMark(int Gen0, int Gen1, int Gen2, long Index, TimeSpan Paused);

/// <summary>
/// The garbage collections since a <see cref="GcMark"/>, for a stall's failure message: a pause the code didn't make
/// (the command sweep, 2026-11-02: a 1.4 s GC pause on Windows with nothing to say which collection it was, or why). The
/// runtime keeps the details of only the last collection of each kind (ephemeral, full blocking, background), so the
/// report names the longest of those that came after the mark, with the heap and the machine's memory as it left them.
/// </summary>
internal static class GcReport
{
    private static readonly GCKind[] s_kinds = [GCKind.Ephemeral, GCKind.FullBlocking, GCKind.Background];

    public static GcMark Mark() =>
        new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetGCMemoryInfo(GCKind.Any).Index, GC.GetTotalPauseDuration());

    /// <summary>
    /// The collections since <paramref name="mark"/> (their pause is the caller's to say): "3 collection(s) (gen0 2, gen1
    /// 0, gen2 1); the longest known, a full blocking gen2 that compacted, paused 1350 ms: heap 410 MB (promoted …),
    /// memory load 93% of 16384 MB (high at 90%), the process's working set 1126 MB".
    /// </summary>
    public static string Since(GcMark mark)
    {
        var (gen0, gen1, gen2) = (GC.CollectionCount(0) - mark.Gen0, GC.CollectionCount(1) - mark.Gen1, GC.CollectionCount(2) - mark.Gen2);
        // A gen1 collection counts as a gen0 too, a gen2 as all three: the counts by the generation each collected.
        var text = string.Create(CultureInfo.InvariantCulture, $"{gen0} collection(s) (gen0 {gen0 - gen1}, gen1 {gen1 - gen2}, gen2 {gen2})");
        if (gen0 == 0)
        {
            return text;
        }

        GCMemoryInfo? longest = null;
        GCKind longestKind = default;
        foreach (var kind in s_kinds)
        {
            var info = GC.GetGCMemoryInfo(kind);
            if (info.Index > mark.Index && (longest is not { } known || Pause(info) > Pause(known)))
            {
                (longest, longestKind) = (info, kind);
            }
        }

        if (longest is not { } gc)
        {
            return text + "; none of them known";
        }

        var kindName = longestKind switch
        {
            GCKind.FullBlocking => "full blocking",
            GCKind.Background => "background",
            _ => "ephemeral",
        };
        return text + string.Create(CultureInfo.InvariantCulture,
            $"; the longest known, a {kindName} gen{gc.Generation}{(gc.Compacted ? " that compacted" : string.Empty)}{(gc.Concurrent ? ", concurrent" : string.Empty)}, paused {Pause(gc).TotalMilliseconds:0} ms: "
            + $"heap {Megabytes(gc.HeapSizeBytes)} (promoted {Megabytes(gc.PromotedBytes)}, fragmented {Megabytes(gc.FragmentedBytes)}, committed {Megabytes(gc.TotalCommittedBytes)}), "
            + $"memory load {Percent(gc.MemoryLoadBytes, gc.TotalAvailableMemoryBytes)} of {Megabytes(gc.TotalAvailableMemoryBytes)} (high at {Percent(gc.HighMemoryLoadThresholdBytes, gc.TotalAvailableMemoryBytes)}), "
            + $"the process's working set {Megabytes(Environment.WorkingSet)}");
    }

    private static TimeSpan Pause(GCMemoryInfo info)
    {
        var total = TimeSpan.Zero;
        foreach (var pause in info.PauseDurations)
        {
            total += pause;
        }

        return total;
    }

    private static string Megabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0} MB");

    private static string Percent(long part, long whole) => whole <= 0 ? "?" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * part / whole:0}%");
}
