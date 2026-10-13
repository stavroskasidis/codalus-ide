using Codalus.Diagnostics.Host.Cpu;
using Codalus.Diagnostics.Protocol;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The profile views (M18, ADR 0045) on a profile built by hand: totals, the top-down and bottom-up trees, recursion,
/// caller/callee, the flame graph's layout and pruning, filters and source lines.
/// </summary>
public sealed class CpuViewsTests
{
    /// <summary>
    /// Thread 1: Main → Work → Hot (6 samples, at 0..5 ms), Main → Work (2), Main → Rec → Rec → Hot (2).
    /// Thread 2: Main → Idle (2 samples, external). Addresses give lines: Hot 1 → hot.cs:10, Work → hot.cs:5.
    /// </summary>
    private static (CpuViews Views, int Main, int Work, int Hot, int Rec) Build()
    {
        var p = new CpuProfile();
        var main = p.AddFunction("App.Main", "App");
        var work = p.AddFunction("App.Work", "App");
        var hot = p.AddFunction("App.Hot", "App");
        var rec = p.AddFunction("App.Rec", "App");
        var idle = p.AddFunction("App.Idle", "App");

        var sMain = p.AddStack(-1, main, 100);
        var sWork = p.AddStack(sMain, work, 200);
        var sHot = p.AddStack(sWork, hot, 300);
        var sRec1 = p.AddStack(sMain, rec, 400);
        var sRec2 = p.AddStack(sRec1, rec, 401);
        var sRecHot = p.AddStack(sRec2, hot, 300);
        var sIdle = p.AddStack(sMain, idle, 500);
        var t = 0.0;
        for (var i = 0; i < 6; i++)
        {
            p.AddSample(t++, 1, sHot, external: false);
        }

        p.AddSample(t++, 1, sWork, external: false);
        p.AddSample(t++, 1, sWork, external: false);
        p.AddSample(t++, 1, sRecHot, external: false);
        p.AddSample(t++, 1, sRecHot, external: false);
        p.AddSample(t++, 2, sIdle, external: true);
        p.AddSample(t, 2, sIdle, external: true);
        p.Duration = 12;
        p.Seal();
        p.ResolveLines(a => a switch { 300 => ("/src/hot.cs", 10), 200 => ("/src/hot.cs", 5), _ => null }, CancellationToken.None);
        return (new CpuViews(p), main, work, hot, rec);
    }

    [Fact]
    public void Top_down_counts_inclusive_and_exclusive_samples()
    {
        var (views, main, work, hot, rec) = Build();
        var roots = views.TopDown(new CpuFilter(), -1, 0, 10);
        Assert.Equal(10, roots.Total); // external samples don't count by default
        var root = Assert.Single(roots.Nodes);
        Assert.Equal((main, 10, 0), (root.Function, root.Inclusive, root.Exclusive));
        var children = views.TopDown(new CpuFilter(), root.Id, 0, 10).Nodes;
        Assert.Equal([work, rec], children.Select(c => c.Function)); // Work (8), then Rec (2)
        Assert.Equal((8, 2, true), (children[0].Inclusive, children[0].Exclusive, children[0].HasChildren));
        var leaf = Assert.Single(views.TopDown(new CpuFilter(), children[0].Id, 0, 10).Nodes);
        Assert.Equal((hot, 6, 6, false), (leaf.Function, leaf.Inclusive, leaf.Exclusive, leaf.HasChildren));

        var withNative = views.TopDown(new CpuFilter(IncludeExternal: true), root.Id, 0, 10);
        Assert.Equal(12, withNative.Total);
        Assert.Equal(3, withNative.Nodes.Count);
        var page = views.TopDown(new CpuFilter(IncludeExternal: true), root.Id, 1, 1);
        Assert.Equal(1, page.More);
    }

    [Fact]
    public void Functions_count_recursion_once_and_caller_callee_follows_it()
    {
        var (views, main, work, hot, rec) = Build();
        var functions = views.Functions(new CpuFilter(), null, 0, 10).Functions;
        Assert.Equal(hot, functions[0].Id);
        Assert.Equal((8, 8), (functions[0].Inclusive, functions[0].Exclusive));
        var recursive = functions.Single(f => f.Id == rec);
        Assert.Equal((2, 0), (recursive.Inclusive, recursive.Exclusive)); // not 4: the inner call is the same samples
        Assert.Equal(10, functions.Single(f => f.Id == main).Inclusive);

        var callerCallee = views.CallerCallee(new CpuFilter(), hot);
        Assert.Equal([(work, 6), (rec, 2)], callerCallee.Callers.Select(c => (c.Id, c.Inclusive)));
        Assert.Empty(callerCallee.Callees);
        Assert.Equal([(hot, 2)], views.CallerCallee(new CpuFilter(), rec).Callees.Select(c => (c.Id, c.Inclusive))); // not itself
        Assert.Equal("/src/hot.cs", functions[0].File);
        Assert.Equal(10, functions[0].Line);
    }

    [Fact]
    public void Bottom_up_walks_from_the_hot_functions_to_their_callers()
    {
        var (views, main, work, hot, rec) = Build();
        var roots = views.BottomUp(new CpuFilter(), [], 0, 10).Nodes;
        Assert.Equal([(hot, 8), (work, 2)], roots.Select(r => (r.Function, r.Inclusive)));
        var callers = views.BottomUp(new CpuFilter(), [hot], 0, 10).Nodes;
        Assert.Equal([(work, 6), (rec, 2)], callers.Select(r => (r.Function, r.Inclusive)));
        var recCallers = views.BottomUp(new CpuFilter(), [hot, rec], 0, 10).Nodes;
        Assert.Equal([(rec, 2)], recCallers.Select(r => (r.Function, r.Inclusive)));
        var top = views.BottomUp(new CpuFilter(), [hot, work], 0, 10).Nodes;
        var mainNode = Assert.Single(top);
        Assert.Equal((main, 6, 6, false), (mainNode.Function, mainNode.Inclusive, mainNode.Exclusive, mainNode.HasChildren));
    }

    [Fact]
    public void The_flame_graph_lays_children_inside_their_parents_and_prunes_narrow_frames()
    {
        var (views, main, work, hot, rec) = Build();
        var flame = views.Flame(new CpuFilter(), -1, 0, 1000);
        Assert.Equal(10, flame.Total);
        Assert.False(flame.Truncated);
        var frames = flame.Frames;
        Assert.Equal((main, 0, 0, 10), (frames[0].Function, frames[0].Depth, frames[0].Start, frames[0].Width));
        var workFrame = frames.Single(f => f.Function == work);
        var recFrames = frames.Where(f => f.Function == rec).ToList();
        Assert.Equal((1, 0, 8), (workFrame.Depth, workFrame.Start, workFrame.Width));
        Assert.Equal([(1, 8, 2), (2, 8, 2)], recFrames.Select(f => (f.Depth, f.Start, f.Width)));
        Assert.Equal([(2, 0, 6), (3, 8, 2)], frames.Where(f => f.Function == hot).Select(f => (f.Depth, f.Start, f.Width)));

        var pruned = views.Flame(new CpuFilter(), -1, 0.3, 1000);
        Assert.True(pruned.Truncated);
        Assert.DoesNotContain(pruned.Frames, f => f.Function == rec);

        var capped = views.Flame(new CpuFilter(), -1, 0, 2);
        Assert.True(capped.Truncated);
        Assert.True(capped.Frames.Count <= 2);

        var zoom = views.Flame(new CpuFilter(), workFrame.Node, 0, 1000);
        Assert.Equal(8, zoom.Total);
        Assert.Equal([main, work], zoom.Path.Select(n => n.Function));
        Assert.Equal((work, 0, 0), (zoom.Frames[0].Function, zoom.Frames[0].Depth, zoom.Frames[0].Start));
    }

    [Fact]
    public void Filters_by_time_and_thread_and_the_timeline_ignores_the_time_range()
    {
        var (views, _, _, hot, _) = Build();
        var early = views.Functions(new CpuFilter(EndMilliseconds: 3), null, 0, 10);
        Assert.Equal(4, early.Total);
        Assert.Equal(4, early.Functions.Single(f => f.Id == hot).Exclusive);
        var thread2 = views.Functions(new CpuFilter(Threads: [2], IncludeExternal: true), null, 0, 10);
        Assert.Equal(2, thread2.Total);
        Assert.Equal(0, views.Functions(new CpuFilter(Threads: [2]), null, 0, 10).Total);

        var timeline = views.Timeline(new CpuFilter(EndMilliseconds: 1), 4);
        Assert.Equal(4, timeline.Samples.Count);
        Assert.Equal(10, timeline.Samples.Sum());
        Assert.Equal(3, timeline.BucketMilliseconds);

        var threads = views.Threads();
        Assert.Equal([(1, 10), (2, 0)], threads.Select(t => (t.Id, t.Samples)));
    }

    [Fact]
    public void Source_lines_count_each_sample_once_per_line()
    {
        var (views, _, _, _, _) = Build();
        var files = views.Files(new CpuFilter());
        Assert.True(files.Resolved);
        Assert.Equal([("/src/hot.cs", 10)], files.Files.Select(f => (f.Path, f.Inclusive)));
        var lines = views.Lines(new CpuFilter(), "\\src\\hot.cs");
        Assert.Equal([(5, 8, 2), (10, 8, 8)], lines.Lines.Select(l => (l.Line, l.Inclusive, l.Exclusive)));
        Assert.Empty(views.Lines(new CpuFilter(), "/src/other.cs").Lines);
    }

    [Fact]
    public void Lines_are_empty_until_resolved()
    {
        var p = new CpuProfile();
        var f = p.AddFunction("F", null);
        p.AddSample(0, 1, p.AddStack(-1, f, 1), external: false);
        p.Seal();
        var views = new CpuViews(p);
        Assert.False(views.Files(new CpuFilter()).Resolved);
        Assert.Null(views.Functions(new CpuFilter(), null, 0, 1).Functions[0].File);
    }

    [Fact]
    public void Histogram_quantiles_are_parsed()
    {
        Assert.Equal(new (double, double)[] { (0.5, 1.5), (0.95, 3), (0.99, 10) }, Codalus.Diagnostics.Host.Sessions.CounterSession.Quantiles("0.5=1.5;0.95=3;0.99=10;bad"));
        Assert.Empty(Codalus.Diagnostics.Host.Sessions.CounterSession.Quantiles(null));
    }
}
