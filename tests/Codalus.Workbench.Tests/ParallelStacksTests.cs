using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

public sealed class ParallelStacksTests
{
    [Fact]
    public void Shared_callers_appear_once_and_the_threads_branch_where_their_stacks_differ()
    {
        var tree = ParallelStacks.Build(new Dictionary<int, IReadOnlyList<string>>
        {
            [1] = ["Sleep", "Wait", "Worker.Run", "ThreadStart"],
            [2] = ["Compute", "Worker.Run", "ThreadStart"],
            [3] = ["Sleep", "Wait", "Worker.Run", "ThreadStart"],
            [4] = ["Main"],
            [5] = [],
        });

        Assert.Equal(2, tree.Count); // the threads' roots: ThreadStart and Main
        var workers = tree[0];
        Assert.Equal(["Worker.Run", "ThreadStart"], workers.Frames);
        Assert.Equal([1, 2, 3], workers.Threads);
        Assert.Equal(2, workers.Children.Count);
        var waiting = Assert.Single(workers.Children, c => c.Threads.Count == 2);
        Assert.Equal(["Sleep", "Wait"], waiting.Frames);
        Assert.Equal([1, 3], waiting.Threads);
        Assert.Empty(waiting.Children);
        Assert.Equal(["Compute"], Assert.Single(workers.Children, c => c.Threads.Count == 1).Frames);

        Assert.Equal(["Main"], tree[1].Frames);
        Assert.Equal([4], tree[1].Threads);
    }

    [Fact]
    public void A_thread_whose_stack_ends_inside_a_shared_part_stops_the_shared_run_there()
    {
        var tree = ParallelStacks.Build(new Dictionary<int, IReadOnlyList<string>>
        {
            [1] = ["B", "A"],
            [2] = ["C", "B", "A"],
        });

        var root = Assert.Single(tree);
        Assert.Equal(["B", "A"], root.Frames);
        Assert.Equal([1, 2], root.Threads);
        Assert.Equal(["C"], Assert.Single(root.Children).Frames);
    }
}
