namespace DaedalusStudio.Workbench.Debugging;

/// <summary>
/// A node of Parallel Stacks (M14b): frames that a set of threads share, callers before callees. <see cref="Frames"/> is
/// top first, like a call stack; <see cref="Children"/> are where the threads go their own ways.
/// </summary>
public sealed record ParallelStackNode(IReadOnlyList<string> Frames, IReadOnlyList<int> Threads, IReadOnlyList<ParallelStackNode> Children);

/// <summary>Merges the threads' call stacks into the tree Parallel Stacks shows: shared callers once, then the branches.</summary>
public static class ParallelStacks
{
    /// <param name="stacks">Each thread's frame names, top (the innermost call) first.</param>
    public static IReadOnlyList<ParallelStackNode> Build(IReadOnlyDictionary<int, IReadOnlyList<string>> stacks) =>
        Build([.. stacks.Where(s => s.Value.Count > 0).Select(s => (s.Key, Frames: s.Value.Reverse().ToArray()))], 0);

    private static List<ParallelStackNode> Build(List<(int Thread, string[] Frames)> threads, int start)
    {
        var nodes = new List<ParallelStackNode>();
        foreach (var group in threads.Where(t => t.Frames.Length > start).GroupBy(t => t.Frames[start], StringComparer.Ordinal).OrderBy(g => g.Min(t => t.Thread)))
        {
            var members = group.ToList();
            // The frames they all share from here: the shortest stack bounds it.
            var length = 1;
            while (start + length < members.Min(m => m.Frames.Length)
                   && members.All(m => string.Equals(m.Frames[start + length], members[0].Frames[start + length], StringComparison.Ordinal)))
            {
                length++;
            }

            var shared = members[0].Frames[start..(start + length)];
            Array.Reverse(shared); // top first, like a call stack
            nodes.Add(new ParallelStackNode(shared, [.. members.Select(m => m.Thread).Order()], Build(members, start + length)));
        }

        return nodes;
    }
}
