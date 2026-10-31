using Codalus.Workbench.Git;

namespace Codalus.Shell.Editor;

/// <summary>
/// A document's blame, indexed for its Git CodeLens entries (<see cref="LensDetails.Git(IReadOnlyList{BlameRange}, int, int, DateTimeOffset)"/>):
/// the ranges in line order, each with whether it's committed, so a member's history finds its ranges by a binary search
/// instead of a pass over all of them. After each pause in typing in a large file, every lens went through every range
/// (and asked each whether its commit id was all zeros, a walk of its 40 characters): 8,000 lenses over 20,000 ranges took
/// seconds on the UI thread (M56). Built once per blame.
/// </summary>
internal sealed class GitLensIndex
{
    private readonly BlameRange[] _ranges;
    private readonly bool[] _committed;

    // Ranges that overlap (not git's): a range ending past a later one's end defeats the search, so they're walked in full.
    private readonly bool _overlapping;

    public GitLensIndex(IReadOnlyList<BlameRange> blame)
    {
        Blame = blame;

        // git's blame lists them in line order, merged and without overlaps (GitParsers.ParseBlame); sorted again in case.
        _ranges = [.. blame];
        if (!IsSorted(_ranges))
        {
            Array.Sort(_ranges, (a, b) => a.StartLine.CompareTo(b.StartLine));
        }

        _committed = [.. _ranges.Select(r => !r.IsUncommitted)];
        _overlapping = !IsDisjoint(_ranges);
    }

    /// <summary>The blame indexed.</summary>
    public IReadOnlyList<BlameRange> Blame { get; }

    /// <summary>How many ranges the lookups have looked at (tests: about log2 of the ranges, plus those in the span, a lookup).</summary>
    internal long Visits { get; private set; }

    /// <summary>The history of lines <paramref name="startLine"/>–<paramref name="endLine"/> (one-based), as <see cref="LensDetails.Git(IReadOnlyList{BlameRange}, int, int, DateTimeOffset)"/> words it.</summary>
    public string? Text(int startLine, int endLine, DateTimeOffset now)
    {
        var committed = new List<BlameRange>();
        for (var i = _overlapping ? 0 : First(startLine); i < _ranges.Length && _ranges[i].StartLine <= endLine; i++)
        {
            Visits++;
            if (_committed[i] && _ranges[i].StartLine + _ranges[i].LineCount - 1 >= startLine)
            {
                committed.Add(_ranges[i]);
            }
        }

        return LensDetails.History(committed, now);
    }

    /// <summary>The first range that ends on or after <paramref name="line"/>.</summary>
    private int First(int line)
    {
        int low = 0, high = _ranges.Length;
        while (low < high)
        {
            Visits++;
            var middle = low + ((high - low) / 2);
            if (_ranges[middle].StartLine + _ranges[middle].LineCount - 1 < line)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static bool IsSorted(BlameRange[] ranges)
    {
        for (var i = 1; i < ranges.Length; i++)
        {
            if (ranges[i].StartLine < ranges[i - 1].StartLine)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDisjoint(BlameRange[] ranges)
    {
        for (var i = 1; i < ranges.Length; i++)
        {
            if (ranges[i].StartLine < ranges[i - 1].StartLine + ranges[i - 1].LineCount)
            {
                return false;
            }
        }

        return true;
    }
}
