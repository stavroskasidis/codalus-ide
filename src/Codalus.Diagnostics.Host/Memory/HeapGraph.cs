using System.Text;
using Codalus.Diagnostics.Protocol;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Codalus.Diagnostics.Host.Memory;

/// <summary>
/// A GC heap snapshot (ADR 0046): the objects of one induced full GC as the runtime reported them (its bulk node, edge,
/// root and type events, the way dotnet-gcdump reads them), with each object's size, type and references, and the
/// roots. On load, a breadth-first walk from the roots gives each reachable object the referrer through which it was
/// first found: the paths to roots. Built once, then only read.
/// </summary>
internal sealed class HeapGraph
{
    /// <summary>Root kinds (the runtime's <c>GCRootKind</c>), as negative referrer ids.</summary>
    private static readonly string[] s_rootKinds = ["[stack]", "[finalizer queue]", "[GC handle]", "[older generation]", "[sized ref handle]", "[overflow]", "[static variable]", "[conditional weak table]"];

    private readonly List<long> _sizes = [];
    private readonly List<int> _types = [];
    private readonly List<int> _edgeStart = [0];
    private readonly List<int> _edges = [];
    private readonly List<string> _typeNames = [];
    private int[] _parent = [];
    private int[][] _byType = [];

    /// <summary>Unreached by the walk from the roots (an object kept by nothing the runtime reported).</summary>
    public const int Unreached = int.MinValue;

    public int ObjectCount => _sizes.Count;

    public long Bytes { get; private set; }

    public int TypeCount => _typeNames.Count;

    public int RootCount { get; private set; }

    public string TypeName(int type) => _typeNames[type];

    public int TypeOf(int node) => _types[node];

    public long SizeOf(int node) => _sizes[node];

    public IReadOnlyList<int> ObjectsOf(int type) => _byType[type];

    /// <summary>The object that first reached <paramref name="node"/> from a root; a negative number: the root kind (<c>-1 - kind</c>); <see cref="Unreached"/>.</summary>
    public int Parent(int node) => _parent[node];

    public static string RootName(int referrer) => -1 - referrer is var kind && kind >= 0 && kind < s_rootKinds.Length ? s_rootKinds[kind] : "[root]";

    /// <summary>Reads a snapshot recorded by <see cref="HeapSnapshotRecorder"/> (the events of the first induced full GC in the file).</summary>
    public static HeapGraph Read(string path, CancellationToken cancellationToken)
    {
        using var source = new EventPipeEventSource(path);
        return Read(source, cancellationToken);
    }

    internal static HeapGraph Read(TraceEventDispatcher source, CancellationToken cancellationToken)
    {
        var builder = new Builder();
        var clr = new ClrTraceEventParser(source);
        int? dumping = null;
        var done = false;
        var events = 0;
        clr.GCStart += e =>
        {
            if (dumping is null && !done && e.Depth == 2 && e.Reason == GCReason.Induced)
            {
                dumping = e.Count;
            }
        };
        clr.GCStop += e =>
        {
            if (dumping == e.Count)
            {
                dumping = null;
                done = true;
            }
        };
        bool Active()
        {
            if ((++events & 255) == 0 && cancellationToken.IsCancellationRequested)
            {
                source.StopProcessing();
            }

            // Some runtimes report the heap before announcing the GC: before the first full GC ends, everything counts.
            return !done;
        }

        clr.GCBulkNode += e =>
        {
            if (Active())
            {
                for (var i = 0; i < e.Count; i++)
                {
                    var node = e.Values(i);
                    builder.Node(node.Address, (long)node.Size, node.TypeID, (int)node.EdgeCount);
                }
            }
        };
        clr.GCBulkEdge += e =>
        {
            if (Active())
            {
                for (var i = 0; i < e.Count; i++)
                {
                    builder.Edge(e.Values(i).Target);
                }
            }
        };
        clr.GCBulkRootEdge += e =>
        {
            if (Active())
            {
                for (var i = 0; i < e.Count; i++)
                {
                    var root = e.Values(i);
                    builder.Root(root.RootedNodeAddress, (int)root.GCRootKind);
                }
            }
        };
        clr.GCBulkRootStaticVar += e =>
        {
            if (Active())
            {
                for (var i = 0; i < e.Count; i++)
                {
                    builder.Root(e.Values(i).ObjectID, 6);
                }
            }
        };
        clr.GCBulkRootConditionalWeakTableElementEdge += e =>
        {
            if (Active())
            {
                for (var i = 0; i < e.Count; i++)
                {
                    builder.Root(e.Values(i).GCValueNodeID, 7);
                }
            }
        };
        clr.TypeBulkType += e =>
        {
            for (var i = 0; i < e.Count; i++)
            {
                var type = e.Values(i);
                var parameters = new ulong[type.TypeParameterCount];
                for (var p = 0; p < parameters.Length; p++)
                {
                    parameters[p] = type.TypeParameterID(p);
                }

                builder.Type(type.TypeID, type.TypeName, parameters);
            }
        };
        source.Process();
        cancellationToken.ThrowIfCancellationRequested();
        return builder.Build();
    }

    /// <summary>Collects the events (nodes, then their edges in order, roots and types) and links them by address.</summary>
    internal sealed class Builder
    {
        private readonly List<ulong> _addresses = [];
        private readonly List<long> _sizes = [];
        private readonly List<ulong> _typeIds = [];
        private readonly List<int> _edgeCounts = [];
        private readonly List<ulong> _targets = [];
        private readonly List<(ulong Address, int Kind)> _roots = [];
        private readonly Dictionary<ulong, (string Name, ulong[] Parameters)> _types = [];

        public void Node(ulong address, long size, ulong type, int edges)
        {
            _addresses.Add(address);
            _sizes.Add(size);
            _typeIds.Add(type);
            _edgeCounts.Add(edges);
        }

        public void Edge(ulong target) => _targets.Add(target);

        public void Root(ulong address, int kind) => _roots.Add((address, kind));

        public void Type(ulong id, string name, ulong[] parameters) => _types[id] = (name, parameters);

        private string Name(ulong id, int depth = 0)
        {
            if (!_types.TryGetValue(id, out var type) || string.IsNullOrEmpty(type.Name))
            {
                return "(type 0x" + id.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + ")";
            }

            if (type.Parameters.Length == 0 || depth > 4)
            {
                return type.Name;
            }

            // Dictionary`2 with its parameters: Dictionary<Int32, Order>.
            var name = type.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            var builder = new StringBuilder(tick > 0 ? name[..tick] : name).Append('<');
            for (var i = 0; i < type.Parameters.Length; i++)
            {
                builder.Append(i == 0 ? string.Empty : ", ").Append(Name(type.Parameters[i], depth + 1));
            }

            if (tick > 0 && name.IndexOf('[', tick) is >= 0 and var array)
            {
                return builder.Append('>').Append(name[array..]).ToString();
            }

            return builder.Append('>').ToString();
        }

        public HeapGraph Build()
        {
            var graph = new HeapGraph();
            var index = new Dictionary<ulong, int>(_addresses.Count);
            for (var i = 0; i < _addresses.Count; i++)
            {
                index.TryAdd(_addresses[i], i);
            }

            var typeIndex = new Dictionary<ulong, int>();
            var edge = 0;
            for (var i = 0; i < _addresses.Count; i++)
            {
                if (!typeIndex.TryGetValue(_typeIds[i], out var type))
                {
                    type = graph._typeNames.Count;
                    typeIndex[_typeIds[i]] = type;
                    graph._typeNames.Add(Name(_typeIds[i]));
                }

                graph._sizes.Add(_sizes[i]);
                graph._types.Add(type);
                graph.Bytes += _sizes[i];
                for (var e = 0; e < _edgeCounts[i] && edge < _targets.Count; e++, edge++)
                {
                    if (_targets[edge] != 0 && index.TryGetValue(_targets[edge], out var target))
                    {
                        graph._edges.Add(target);
                    }
                }

                graph._edgeStart.Add(graph._edges.Count);
            }

            var byType = new List<int>[graph._typeNames.Count];
            for (var i = 0; i < graph._types.Count; i++)
            {
                (byType[graph._types[i]] ??= []).Add(i);
            }

            graph._byType = [.. byType.Select(b => b?.ToArray() ?? [])];

            // Breadth first from the roots: each object's parent is the referrer that reached it first.
            var parent = new int[graph._sizes.Count];
            Array.Fill(parent, Unreached);
            var queue = new Queue<int>();
            var roots = 0;
            foreach (var (address, kind) in _roots)
            {
                if (index.TryGetValue(address, out var node) && parent[node] == Unreached)
                {
                    parent[node] = -1 - Math.Clamp(kind, 0, s_rootKinds.Length - 1);
                    queue.Enqueue(node);
                    roots++;
                }
            }

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                for (var e = graph._edgeStart[node]; e < graph._edgeStart[node + 1]; e++)
                {
                    var target = graph._edges[e];
                    if (parent[target] == Unreached)
                    {
                        parent[target] = node;
                        queue.Enqueue(target);
                    }
                }
            }

            graph._parent = parent;
            graph.RootCount = roots;
            return graph;
        }
    }

    // ------------------------------------------------------------------ views

    /// <summary>Types by bytes, or by growth against <paramref name="baseline"/> (types matched by name).</summary>
    public HeapTypesResult Types(HeapGraph? baseline, string? search, int offset, int count)
    {
        var rows = new List<HeapType>();
        Dictionary<string, (int Count, long Bytes)>? before = null;
        if (baseline is not null)
        {
            before = new Dictionary<string, (int, long)>(StringComparer.Ordinal);
            for (var type = 0; type < baseline.TypeCount; type++)
            {
                var objects = baseline.ObjectsOf(type);
                var entry = before.GetValueOrDefault(baseline.TypeName(type));
                before[baseline.TypeName(type)] = (entry.Count + objects.Count, entry.Bytes + objects.Sum(baseline.SizeOf));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var type = 0; type < TypeCount; type++)
        {
            var name = TypeName(type);
            var objects = ObjectsOf(type);
            var bytes = objects.Sum(SizeOf);
            seen.Add(name);
            if (before is not null)
            {
                var old = before.GetValueOrDefault(name);
                rows.Add(new HeapType(type, name, objects.Count, bytes, objects.Count - old.Count, bytes - old.Bytes));
            }
            else
            {
                rows.Add(new HeapType(type, name, objects.Count, bytes));
            }
        }

        if (before is not null)
        {
            // Types that are gone: shrinking to nothing is a change too.
            rows.AddRange(before.Where(b => !seen.Contains(b.Key)).Select(b => new HeapType(-1, b.Key, 0, 0, -b.Value.Count, -b.Value.Bytes)));
        }

        var matching = rows.Where(r => string.IsNullOrEmpty(search) || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        var ordered = (before is null
            ? matching.OrderByDescending(r => r.Bytes).ThenByDescending(r => r.Count)
            : matching.Where(r => r.CountDelta != 0 || r.BytesDelta != 0).OrderByDescending(r => r.BytesDelta).ThenByDescending(r => r.CountDelta))
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        var page = ordered.Skip(offset).Take(Math.Clamp(count, 0, 5000)).ToList();
        return new HeapTypesResult(ObjectCount, Bytes, page, Math.Max(0, ordered.Count - offset - page.Count));
    }

    /// <summary>
    /// The referrers of a path: <paramref name="path"/>[0] is a type; each next id the type of the referrer above. The
    /// objects of the type whose chain of first referrers matches the path are grouped by their next referrer: a type,
    /// or a root kind (negative), or unreached.
    /// </summary>
    public HeapRootsResult Referrers(IReadOnlyList<int> path, int offset, int count)
    {
        if (path.Count == 0 || path[0] < 0 || path[0] >= TypeCount)
        {
            throw new ArgumentException("The path must start with a type of the snapshot.");
        }

        var groups = new Dictionary<int, (int Count, long Bytes, bool More)>();
        foreach (var node in ObjectsOf(path[0]))
        {
            var at = node;
            var matched = true;
            for (var i = 1; i < path.Count; i++)
            {
                var up = _parent[at];
                if (up < 0 || _types[up] != path[i])
                {
                    matched = false;
                    break;
                }

                at = up;
            }

            if (!matched)
            {
                continue;
            }

            var referrer = _parent[at];
            var key = referrer == Unreached ? Unreached : referrer < 0 ? referrer : _types[referrer];
            var entry = groups.GetValueOrDefault(key);
            groups[key] = (entry.Count + 1, entry.Bytes + _sizes[node], entry.More || referrer >= 0);
        }

        var ordered = groups
            .OrderByDescending(g => g.Value.Count)
            .ThenByDescending(g => g.Value.Bytes)
            .Select(g => new HeapReferrer(
                g.Key,
                g.Key == Unreached ? "[not reached from a root]" : g.Key < 0 ? RootName(g.Key) : TypeName(g.Key),
                g.Value.Count,
                g.Value.Bytes,
                g.Key >= 0 && g.Value.More && path.Count < 64))
            .ToList();
        var page = ordered.Skip(offset).Take(Math.Clamp(count, 0, 1000)).ToList();
        return new HeapRootsResult(page, Math.Max(0, ordered.Count - offset - page.Count));
    }
}
