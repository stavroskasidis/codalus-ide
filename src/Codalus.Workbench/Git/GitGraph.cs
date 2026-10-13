namespace Codalus.Workbench.Git;

/// <summary>
/// Lays out the history graph one page at a time (M13): each lane expects the next commit of one line of history.
/// Commits must arrive in topological order (<c>git log --topo-order</c>), newest first. The state carries over from one
/// page to the next, so a long history is laid out as it's scrolled.
/// </summary>
public sealed class GitGraph
{
    private readonly List<string?> _lanes = [];

    /// <summary>Places each commit of the page (sets <see cref="GitCommit.Graph"/>).</summary>
    public void Add(IEnumerable<GitCommit> commits)
    {
        foreach (var commit in commits)
        {
            commit.Graph = Place(commit);
        }
    }

    private GraphRow Place(GitCommit commit)
    {
        var top = new List<(int, int)>();
        var bottom = new List<(int, int)>();
        var lane = _lanes.IndexOf(commit.Hash);
        if (lane >= 0)
        {
            // Every lane that expected this commit ends at its dot (branches merging back).
            for (var i = 0; i < _lanes.Count; i++)
            {
                if (_lanes[i] == commit.Hash)
                {
                    top.Add((i, lane));
                    if (i != lane)
                    {
                        _lanes[i] = null;
                    }
                }
            }
        }
        else
        {
            lane = Free();
        }

        var passing = new List<int>();
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (i != lane && _lanes[i] is not null)
            {
                passing.Add(i);
            }
        }

        // The first parent continues in this lane; the others join a lane that expects them, or start one.
        _lanes[lane] = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        if (commit.Parents.Count > 0)
        {
            bottom.Add((lane, lane));
        }

        foreach (var parent in commit.Parents.Skip(1))
        {
            var target = _lanes.IndexOf(parent);
            if (target < 0)
            {
                target = Free();
                _lanes[target] = parent;
            }

            bottom.Add((lane, target));
        }

        var count = Math.Max(lane + 1, _lanes.Count);
        while (_lanes.Count > 0 && _lanes[^1] is null)
        {
            _lanes.RemoveAt(_lanes.Count - 1);
        }

        return new GraphRow(lane, count, top, bottom, passing);
    }

    private int Free()
    {
        var free = _lanes.IndexOf(null);
        if (free >= 0)
        {
            return free;
        }

        _lanes.Add(null);
        return _lanes.Count - 1;
    }
}
