using System.Globalization;
using DaedalusStudio.Diagnostics.Protocol;

namespace DaedalusStudio.Diagnostics.Host.Cpu;

/// <summary>
/// The views of a <see cref="CpuProfile"/> (ADR 0045): the top-down call tree, the bottom-up tree of callers, the
/// functions, caller/callee, the flame graph, the timeline and source lines, each for a filter (a time range, threads,
/// and whether samples in native code count). Totals for a filter are computed once and kept for the last few filters,
/// so paging and expanding are cheap; everything is answered a page or a pruned level at a time.
/// </summary>
internal sealed class CpuViews(CpuProfile profile)
{
    private const int CachedFilters = 4;

    private readonly List<(string Key, Totals Totals)> _cache = [];
    private readonly Lock _gate = new();

    public CpuProfile Profile => profile;

    /// <summary>The sample counts of one filter.</summary>
    internal sealed class Totals
    {
        public required int Total { get; init; }

        public required int[] NodeInclusive { get; init; }

        public required int[] NodeExclusive { get; init; }

        public required int[] FunctionInclusive { get; init; }

        public required int[] FunctionExclusive { get; init; }

        /// <summary>Source lines (built on first use, once lines are resolved).</summary>
        public LineTotals? Lines { get; set; }
    }

    internal sealed class LineTotals
    {
        public required int[] Inclusive { get; init; }

        public required int[] Exclusive { get; init; }

        /// <summary>Each function's hottest line (-1: none).</summary>
        public required int[] FunctionLine { get; init; }
    }

    internal static string Key(CpuFilter filter) =>
        string.Join(
            '|',
            filter.StartMilliseconds?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
            filter.EndMilliseconds?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
            filter.Threads is { Count: > 0 } threads ? string.Join(',', threads.Order()) : "*",
            filter.IncludeExternal ? "x" : "m",
            filter.Profile ?? "cpu");

    internal static bool Matches(in CpuSample sample, CpuFilter filter, HashSet<int>? threads) =>
        (filter.IncludeExternal || !sample.External)
        && (filter.StartMilliseconds is not { } start || sample.Time >= start)
        && (filter.EndMilliseconds is not { } end || sample.Time <= end)
        && (threads is null || threads.Contains(sample.Thread));

    private static HashSet<int>? ThreadSet(CpuFilter filter) => filter.Threads is { Count: > 0 } threads ? [.. threads] : null;

    public Totals For(CpuFilter filter)
    {
        var key = Key(filter);
        lock (_gate)
        {
            var hit = _cache.FindIndex(c => c.Key == key);
            if (hit >= 0)
            {
                var entry = _cache[hit];
                _cache.RemoveAt(hit);
                _cache.Add(entry);
                return entry.Totals;
            }
        }

        var totals = Compute(filter);
        lock (_gate)
        {
            _cache.RemoveAll(c => c.Key == key);
            _cache.Add((key, totals));
            if (_cache.Count > CachedFilters)
            {
                _cache.RemoveAt(0);
            }
        }

        return totals;
    }

    private Totals Compute(CpuFilter filter)
    {
        var threads = ThreadSet(filter);
        var nodeInclusive = new int[profile.NodeCount];
        var nodeExclusive = new int[profile.NodeCount];
        var total = 0;
        foreach (var sample in profile.Samples)
        {
            if (Matches(sample, filter, threads))
            {
                nodeExclusive[profile.StackNode(sample.Stack)] += sample.Weight;
                total += sample.Weight;
            }
        }

        Array.Copy(nodeExclusive, nodeInclusive, nodeExclusive.Length);
        for (var node = profile.NodeCount - 1; node >= 0; node--)
        {
            var parent = profile.NodeParent(node);
            if (parent >= 0)
            {
                nodeInclusive[parent] += nodeInclusive[node];
            }
        }

        var functionInclusive = new int[profile.FunctionCount];
        var functionExclusive = new int[profile.FunctionCount];
        for (var node = 0; node < profile.NodeCount; node++)
        {
            var function = profile.NodeFunction(node);
            functionExclusive[function] += nodeExclusive[node];
            if (!profile.IsRecursive(node))
            {
                functionInclusive[function] += nodeInclusive[node];
            }
        }

        return new Totals
        {
            Total = total,
            NodeInclusive = nodeInclusive,
            NodeExclusive = nodeExclusive,
            FunctionInclusive = functionInclusive,
            FunctionExclusive = functionExclusive,
        };
    }

    // ------------------------------------------------------------------ top-down

    public CpuTreeResult TopDown(CpuFilter filter, int node, int offset, int count)
    {
        var totals = For(filter);
        var children = profile.Children(node)
            .Where(c => totals.NodeInclusive[c] > 0)
            .OrderByDescending(c => totals.NodeInclusive[c])
            .ThenBy(c => profile.FunctionName(profile.NodeFunction(c)), StringComparer.Ordinal)
            .ToList();
        var page = children.Skip(offset).Take(count).Select(c => Node(c, totals)).ToList();
        return new CpuTreeResult(totals.Total, page, Math.Max(0, children.Count - offset - page.Count));
    }

    private CpuNode Node(int node, Totals totals)
    {
        var function = profile.NodeFunction(node);
        return new CpuNode(
            node,
            function,
            profile.FunctionName(function),
            profile.FunctionModule(function),
            totals.NodeInclusive[node],
            totals.NodeExclusive[node],
            profile.Children(node).Any(c => totals.NodeInclusive[c] > 0));
    }

    // ------------------------------------------------------------------ bottom-up

    /// <summary>
    /// The callers of <paramref name="path"/> (function ids from a function with self time up through its callers);
    /// an empty path lists the functions with self time. A node's samples are those whose stack, read from the leaf
    /// up, starts with its path.
    /// </summary>
    public CpuTreeResult BottomUp(CpuFilter filter, IReadOnlyList<int> path, int offset, int count)
    {
        var totals = For(filter);
        List<(int Function, int Inclusive, int Ends, bool HasChildren)> rows;
        if (path.Count == 0)
        {
            rows = [];
            for (var function = 0; function < profile.FunctionCount; function++)
            {
                if (totals.FunctionExclusive[function] > 0)
                {
                    var nodes = profile.NodesOf(function);
                    var ends = nodes.Where(n => profile.NodeParent(n) < 0).Sum(n => totals.NodeExclusive[n]);
                    rows.Add((function, totals.FunctionExclusive[function], ends, totals.FunctionExclusive[function] > ends));
                }
            }
        }
        else
        {
            var callers = new Dictionary<int, (int Inclusive, int Ends, bool HasChildren)>();
            foreach (var leaf in profile.NodesOf(path[0]))
            {
                var weight = totals.NodeExclusive[leaf];
                if (weight == 0)
                {
                    continue;
                }

                // Walk up the path; the frame above its end is a caller.
                var at = leaf;
                var matched = true;
                for (var i = 1; i < path.Count; i++)
                {
                    at = profile.NodeParent(at);
                    if (at < 0 || profile.NodeFunction(at) != path[i])
                    {
                        matched = false;
                        break;
                    }
                }

                var caller = matched ? profile.NodeParent(at) : -1;
                if (caller < 0)
                {
                    continue;
                }

                var function = profile.NodeFunction(caller);
                var entry = callers.GetValueOrDefault(function);
                var top = profile.NodeParent(caller) < 0;
                callers[function] = (entry.Inclusive + weight, entry.Ends + (top ? weight : 0), entry.HasChildren || !top);
            }

            rows = [.. callers.Select(c => (c.Key, c.Value.Inclusive, c.Value.Ends, c.Value.HasChildren))];
        }

        var ordered = rows.OrderByDescending(r => r.Inclusive).ThenBy(r => profile.FunctionName(r.Function), StringComparer.Ordinal).ToList();
        var page = ordered.Skip(offset).Take(count)
            .Select(r => new CpuNode(r.Function, r.Function, profile.FunctionName(r.Function), profile.FunctionModule(r.Function), r.Inclusive, r.Ends, r.HasChildren))
            .ToList();
        return new CpuTreeResult(totals.Total, page, Math.Max(0, ordered.Count - offset - page.Count));
    }

    // ------------------------------------------------------------------ functions

    public CpuFunctionsResult Functions(CpuFilter filter, string? search, int offset, int count)
    {
        var totals = For(filter);
        var lines = LinesOf(filter);
        var matching = Enumerable.Range(0, profile.FunctionCount)
            .Where(f => totals.FunctionInclusive[f] > 0 && (string.IsNullOrEmpty(search) || profile.FunctionName(f).Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => totals.FunctionExclusive[f])
            .ThenByDescending(f => totals.FunctionInclusive[f])
            .ThenBy(f => profile.FunctionName(f), StringComparer.Ordinal)
            .ToList();
        var page = matching.Skip(offset).Take(count).Select(f => Function(f, totals, lines)).ToList();
        return new CpuFunctionsResult(totals.Total, page, Math.Max(0, matching.Count - offset - page.Count));
    }

    private CpuFunction Function(int function, Totals totals, LineTotals? lines, int? inclusive = null, int? exclusive = null)
    {
        string? file = null;
        int? line = null;
        if (lines is not null && lines.FunctionLine[function] is >= 0 and var hot)
        {
            (file, var number) = profile.LineAt(hot);
            line = number;
        }

        return new CpuFunction(
            function,
            profile.FunctionName(function),
            profile.FunctionModule(function),
            inclusive ?? totals.FunctionInclusive[function],
            exclusive ?? totals.FunctionExclusive[function],
            file,
            line);
    }

    public CpuCallerCalleeResult CallerCallee(CpuFilter filter, int function)
    {
        if (function < 0 || function >= profile.FunctionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(function), "No such function.");
        }

        var totals = For(filter);
        var lines = LinesOf(filter);
        var callers = new Dictionary<int, int>();
        var callees = new Dictionary<int, int>();
        // Callers: of the outermost calls (a recursive call's time is its outer call's). Callees: of every call, but not
        // the function calling itself, so direct recursion shows the callees it really has.
        foreach (var node in profile.NodesOf(function))
        {
            if (totals.NodeInclusive[node] == 0)
            {
                continue;
            }

            if (!profile.IsRecursive(node) && profile.NodeParent(node) is >= 0 and var parent)
            {
                var caller = profile.NodeFunction(parent);
                callers[caller] = callers.GetValueOrDefault(caller) + totals.NodeInclusive[node];
            }

            foreach (var child in profile.Children(node))
            {
                if (totals.NodeInclusive[child] > 0 && profile.NodeFunction(child) != function)
                {
                    var callee = profile.NodeFunction(child);
                    callees[callee] = callees.GetValueOrDefault(callee) + totals.NodeInclusive[child];
                }
            }
        }

        IReadOnlyList<CpuFunction> Rows(Dictionary<int, int> rows) =>
            [.. rows.OrderByDescending(r => r.Value).ThenBy(r => profile.FunctionName(r.Key), StringComparer.Ordinal).Select(r => Function(r.Key, totals, lines, r.Value, 0))];

        return new CpuCallerCalleeResult(totals.Total, Function(function, totals, lines), Rows(callers), Rows(callees));
    }

    // ------------------------------------------------------------------ flame graph

    public CpuFlameResult Flame(CpuFilter filter, int root, double minFraction, int maxFrames)
    {
        var totals = For(filter);
        var path = new List<CpuNode>();
        for (var at = root; at >= 0; at = profile.NodeParent(at))
        {
            path.Insert(0, Node(at, totals));
        }

        var width = root < 0 ? totals.Total : totals.NodeInclusive[root];
        maxFrames = Math.Clamp(maxFrames, 1, 100_000);
        var minimum = Math.Max(1, (int)Math.Ceiling(width * Math.Clamp(minFraction, 0, 1)));
        while (true)
        {
            var frames = new List<FlameFrame>();
            var truncated = false;
            var overflow = false;

            // Depth-first: each frame's children laid out left to right from its start, the widest first.
            var stack = new Stack<(int Node, int Depth, int Start)>();
            void PushChildren(int parent, int depth, int start)
            {
                var children = profile.Children(parent).Where(c => totals.NodeInclusive[c] > 0).OrderByDescending(c => totals.NodeInclusive[c]).ThenBy(c => c).ToList();
                var offsets = new List<(int Node, int Start)>();
                foreach (var child in children)
                {
                    if (totals.NodeInclusive[child] < minimum)
                    {
                        truncated = true;
                        continue;
                    }

                    offsets.Add((child, start));
                    start += totals.NodeInclusive[child];
                }

                for (var i = offsets.Count - 1; i >= 0; i--)
                {
                    stack.Push((offsets[i].Node, depth, offsets[i].Start));
                }
            }

            if (root < 0)
            {
                PushChildren(-1, 0, 0);
            }
            else
            {
                stack.Push((root, 0, 0));
            }

            while (stack.Count > 0)
            {
                var (node, depth, start) = stack.Pop();
                if (frames.Count >= maxFrames)
                {
                    overflow = true;
                    break;
                }

                var function = profile.NodeFunction(node);
                frames.Add(new FlameFrame(node, function, profile.FunctionName(function), depth, start, totals.NodeInclusive[node], totals.NodeExclusive[node]));
                PushChildren(node, depth + 1, start);
            }

            if (!overflow || minimum >= width)
            {
                return new CpuFlameResult(width, frames, path, truncated || overflow);
            }

            minimum *= 2; // too many frames: leave out the narrowest and lay out again
        }
    }

    // ------------------------------------------------------------------ timeline and threads

    public CpuTimelineResult Timeline(CpuFilter filter, int buckets)
    {
        buckets = Math.Clamp(buckets, 1, 10_000);
        var threads = ThreadSet(filter);
        var all = filter with { StartMilliseconds = null, EndMilliseconds = null };
        var duration = Math.Max(profile.Duration, 1);
        var size = duration / buckets;
        var counts = new int[buckets];
        foreach (var sample in profile.Samples)
        {
            if (Matches(sample, all, threads))
            {
                counts[Math.Clamp((int)(sample.Time / size), 0, buckets - 1)] += sample.Weight;
            }
        }

        return new CpuTimelineResult(duration, size, counts);
    }

    public IReadOnlyList<ThreadSummary> Threads()
    {
        var counts = new Dictionary<int, int>();
        foreach (var sample in profile.Samples)
        {
            if (!sample.External)
            {
                counts[sample.Thread] = counts.GetValueOrDefault(sample.Thread) + 1;
            }
        }

        return [.. profile.ThreadNames.Keys
            .Select(t => new ThreadSummary(t, profile.ThreadNames[t], counts.GetValueOrDefault(t)))
            .OrderByDescending(t => t.Samples)
            .ThenBy(t => t.Id)];
    }

    // ------------------------------------------------------------------ source lines

    /// <summary>The filter's line totals, or null while lines are still being resolved.</summary>
    internal LineTotals? LinesOf(CpuFilter filter)
    {
        if (!profile.LinesResolved)
        {
            return null;
        }

        var totals = For(filter);
        if (totals.Lines is { } known)
        {
            return known;
        }

        var threads = ThreadSet(filter);
        var inclusive = new int[profile.LineCount];
        var exclusive = new int[profile.LineCount];
        var seen = new int[profile.LineCount];
        var perFunction = new Dictionary<(int Function, int Line), int>();
        var functionSeen = new Dictionary<(int Function, int Line), int>();
        var stamp = 0;
        foreach (var sample in profile.Samples)
        {
            if (!Matches(sample, filter, threads))
            {
                continue;
            }

            stamp++;
            var leafLine = profile.StackLine(sample.Stack);
            if (leafLine >= 0)
            {
                exclusive[leafLine] += sample.Weight;
            }

            for (var stack = sample.Stack; stack >= 0; stack = profile.StackParent(stack))
            {
                var line = profile.StackLine(stack);
                if (line < 0)
                {
                    continue;
                }

                if (seen[line] != stamp)
                {
                    seen[line] = stamp;
                    inclusive[line] += sample.Weight;
                }

                var key = (profile.NodeFunction(profile.StackNode(stack)), line);
                if (functionSeen.GetValueOrDefault(key) != stamp)
                {
                    functionSeen[key] = stamp;
                    perFunction[key] = perFunction.GetValueOrDefault(key) + sample.Weight;
                }
            }
        }

        var functionLine = new int[profile.FunctionCount];
        Array.Fill(functionLine, -1);
        var best = new int[profile.FunctionCount];
        foreach (var ((function, line), samples) in perFunction)
        {
            if (samples > best[function] || (samples == best[function] && line < functionLine[function]))
            {
                best[function] = samples;
                functionLine[function] = line;
            }
        }

        var result = new LineTotals { Inclusive = inclusive, Exclusive = exclusive, FunctionLine = functionLine };
        totals.Lines = result;
        return result;
    }

    public CpuFilesResult Files(CpuFilter filter)
    {
        var totals = For(filter);
        if (LinesOf(filter) is null)
        {
            return new CpuFilesResult(totals.Total, [], Resolved: false);
        }

        var files = new int[profile.Files.Count];
        var seen = new int[profile.Files.Count];
        Array.Fill(seen, -1);
        var threads = ThreadSet(filter);
        var index = 0;
        foreach (var sample in profile.Samples)
        {
            index++;
            if (!Matches(sample, filter, threads))
            {
                continue;
            }

            for (var stack = sample.Stack; stack >= 0; stack = profile.StackParent(stack))
            {
                var line = profile.StackLine(stack);
                if (line >= 0 && seen[profile.LineFile(line)] != index)
                {
                    seen[profile.LineFile(line)] = index;
                    files[profile.LineFile(line)] += sample.Weight;
                }
            }
        }

        return new CpuFilesResult(
            totals.Total,
            [.. Enumerable.Range(0, files.Length).Where(f => files[f] > 0).OrderByDescending(f => files[f]).Select(f => new CpuFile(profile.Files[f], files[f]))],
            Resolved: true);
    }

    public CpuLinesResult Lines(CpuFilter filter, string file)
    {
        var totals = For(filter);
        if (LinesOf(filter) is not { } lines)
        {
            return new CpuLinesResult(totals.Total, []);
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalized = Normalize(file);
        var rows = new List<CpuLine>();
        for (var line = 0; line < profile.LineCount; line++)
        {
            if (lines.Inclusive[line] > 0 && string.Equals(Normalize(profile.Files[profile.LineFile(line)]), normalized, comparer))
            {
                rows.Add(new CpuLine(profile.LineNumber(line), lines.Inclusive[line], lines.Exclusive[line]));
            }
        }

        return new CpuLinesResult(totals.Total, [.. rows.OrderBy(r => r.Line)]);
    }

    internal static string Normalize(string path) => path.Replace('\\', '/');

    // ------------------------------------------------------------------ comparing

    /// <summary>
    /// Two profiles' functions side by side (comparing sessions): matched by name and module, each one's self and total
    /// share of its own profile, the largest changes of the total share first. Only the filter's native-code choice
    /// applies (time ranges and threads differ between traces).
    /// </summary>
    public static CpuCompareResult Compare(CpuViews baseline, CpuViews current, CpuFilter filter, int count)
    {
        var all = new CpuFilter(IncludeExternal: filter.IncludeExternal, Profile: filter.Profile);
        var before = Shares(baseline, all);
        var after = Shares(current, all);
        var rows = new List<CpuCompareRow>();
        foreach (var key in before.Keys.Union(after.Keys))
        {
            var b = before.GetValueOrDefault(key);
            var a = after.GetValueOrDefault(key);
            rows.Add(new CpuCompareRow(key.Name, key.Module, b.Self, b.Total, a.Self, a.Total, a.Total - b.Total));
        }

        return new CpuCompareResult(
            baseline.For(all).Total,
            current.For(all).Total,
            [.. rows.OrderByDescending(r => Math.Abs(r.Delta)).ThenByDescending(r => Math.Abs(r.Self - r.BaselineSelf)).ThenBy(r => r.Name, StringComparer.Ordinal).Take(Math.Clamp(count, 1, 5000))]);
    }

    private static Dictionary<(string Name, string? Module), (double Self, double Total)> Shares(CpuViews views, CpuFilter filter)
    {
        var totals = views.For(filter);
        var shares = new Dictionary<(string, string?), (double, double)>();
        if (totals.Total == 0)
        {
            return shares;
        }

        for (var function = 0; function < views.Profile.FunctionCount; function++)
        {
            if (totals.FunctionInclusive[function] > 0)
            {
                var key = (views.Profile.FunctionName(function), views.Profile.FunctionModule(function));
                var known = shares.GetValueOrDefault(key);
                shares[key] = (known.Item1 + ((double)totals.FunctionExclusive[function] / totals.Total), Math.Max(known.Item2, (double)totals.FunctionInclusive[function] / totals.Total));
            }
        }

        return shares;
    }
}
