using System.Runtime.InteropServices;

namespace DaedalusStudio.Diagnostics.Host.Cpu;

/// <summary>
/// A sample: when (milliseconds from the trace's start), which thread, its stack (leaf frame), whether it was in
/// native code, and its weight (1 for a CPU sample; KB for an allocation tick, microseconds for a contention).
/// </summary>
internal readonly record struct CpuSample(double Time, int Thread, int Stack, bool External, int Weight = 1);

/// <summary>
/// A CPU sampling profile, independent of where it came from (ADR 0044): the functions, the top-down call tree of
/// functions (a node per distinct call path), the address-level stacks that samples point at (each frame keeps its
/// code address, for source lines), and the samples. Nodes and stacks are numbered so a parent comes before its
/// children, which lets totals be summed in one backward pass. Built once, then only read (queries are in
/// <see cref="CpuViews"/>), except for source lines, which are resolved later (<see cref="ResolveLines"/>).
/// </summary>
internal sealed class CpuProfile
{
    private readonly Dictionary<(string Name, string? Module), int> _functionIds = [];
    private readonly Dictionary<(int Parent, int Function), int> _nodeIds = [];
    private readonly List<string> _functionNames = [];
    private readonly List<string?> _functionModules = [];
    private readonly List<int> _nodeParent = [];
    private readonly List<int> _nodeFunction = [];
    private readonly List<int> _stackParent = [];
    private readonly List<int> _stackNode = [];
    private readonly List<long> _stackAddress = [];
    private readonly List<CpuSample> _samples = [];
    private readonly Dictionary<int, string> _threadNames = [];
    private int[][] _children = [];
    private int[][] _nodesByFunction = [];
    private bool[] _recursive = [];
    private int[] _stackLine = [];
    private readonly List<(int File, int Line)> _lines = [];
    private readonly List<string> _files = [];
    private volatile bool _linesResolved;

    public string? ProcessName { get; set; }

    public int? ProcessId { get; set; }

    public double Duration { get; set; }

    /// <summary>What a sample's weight counts (<c>samples</c>, <c>KB</c>, <c>µs</c>, <c>exceptions</c>).</summary>
    public string Unit { get; set; } = "samples";

    public int FunctionCount => _functionNames.Count;

    public int NodeCount => _nodeParent.Count;

    public int StackCount => _stackParent.Count;

    public IReadOnlyList<CpuSample> Samples => _samples;

    public IReadOnlyDictionary<int, string> ThreadNames => _threadNames;

    /// <summary>Whether <see cref="ResolveLines"/> has finished.</summary>
    public bool LinesResolved => _linesResolved;

    public string FunctionName(int function) => _functionNames[function];

    public string? FunctionModule(int function) => _functionModules[function];

    public int NodeParent(int node) => _nodeParent[node];

    public int NodeFunction(int node) => _nodeFunction[node];

    /// <summary>Whether a caller further up the node's path is the same function (recursion: its time is counted there).</summary>
    public bool IsRecursive(int node) => _recursive[node];

    public IReadOnlyList<int> Children(int node) => node < 0 ? _children[^1] : _children[node];

    public IReadOnlyList<int> NodesOf(int function) => _nodesByFunction[function];

    public int StackParent(int stack) => _stackParent[stack];

    public int StackNode(int stack) => _stackNode[stack];

    /// <summary>The source line of a stack frame (an index into <see cref="LineAt"/>), or -1.</summary>
    public int StackLine(int stack) => _linesResolved ? _stackLine[stack] : -1;

    public (string File, int Line) LineAt(int line) => (_files[_lines[line].File], _lines[line].Line);

    public int LineFile(int line) => _lines[line].File;

    public int LineNumber(int line) => _lines[line].Line;

    public int LineCount => _lines.Count;

    public IReadOnlyList<string> Files => _files;

    public int AddFunction(string name, string? module)
    {
        if (!_functionIds.TryGetValue((name, module), out var id))
        {
            id = _functionNames.Count;
            _functionNames.Add(name);
            _functionModules.Add(module);
            _functionIds[(name, module)] = id;
        }

        return id;
    }

    /// <summary>A frame calling <paramref name="function"/> at <paramref name="address"/> from <paramref name="parentStack"/> (-1: the stack's root).</summary>
    public int AddStack(int parentStack, int function, long address)
    {
        var parentNode = parentStack < 0 ? -1 : _stackNode[parentStack];
        if (!_nodeIds.TryGetValue((parentNode, function), out var node))
        {
            node = _nodeParent.Count;
            _nodeParent.Add(parentNode);
            _nodeFunction.Add(function);
            _nodeIds[(parentNode, function)] = node;
        }

        _stackParent.Add(parentStack);
        _stackNode.Add(node);
        _stackAddress.Add(address);
        return _stackParent.Count - 1;
    }

    public void AddSample(double time, int thread, int stack, bool external, int weight = 1)
    {
        _samples.Add(new CpuSample(time, thread, stack, external, Math.Max(1, weight)));
        _threadNames.TryAdd(thread, "Thread " + thread.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void NameThread(int thread, string name) => _threadNames[thread] = name;

    /// <summary>Builds the lookup tables once every stack and sample is in.</summary>
    public void Seal()
    {
        var children = new List<int>[_nodeParent.Count + 1];
        var byFunction = new List<int>[_functionNames.Count];
        _recursive = new bool[_nodeParent.Count];
        for (var node = 0; node < _nodeParent.Count; node++)
        {
            var parent = _nodeParent[node];
            (children[parent < 0 ? ^1 : parent] ??= []).Add(node);
            (byFunction[_nodeFunction[node]] ??= []).Add(node);
            for (var up = parent; up >= 0; up = _nodeParent[up])
            {
                if (_nodeFunction[up] == _nodeFunction[node])
                {
                    _recursive[node] = true;
                    break;
                }
            }
        }

        _children = [.. children.Select(c => c?.ToArray() ?? [])];
        _nodesByFunction = [.. byFunction.Select(c => c?.ToArray() ?? [])];
        _stackLine = new int[_stackParent.Count];
        Array.Fill(_stackLine, -1);
        if (Duration <= 0 && _samples.Count > 0)
        {
            Duration = _samples.Max(s => s.Time);
        }
    }

    /// <summary>
    /// Finds every frame's source line through <paramref name="resolve"/> (an address to a file and line, or null),
    /// once per address. Runs after the profile is open; views count lines once it has finished.
    /// </summary>
    public void ResolveLines(Func<long, (string File, int Line)?> resolve, CancellationToken cancellationToken)
    {
        var byAddress = new Dictionary<long, int>();
        var lineIds = new Dictionary<(int File, int Line), int>();
        var fileIds = new Dictionary<string, int>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var stack = 0; stack < _stackAddress.Count; stack++)
        {
            if ((stack & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var address = _stackAddress[stack];
            ref var known = ref CollectionsMarshal.GetValueRefOrAddDefault(byAddress, address, out var exists);
            if (!exists)
            {
                known = -1;
                if (resolve(address) is { } location && location.Line > 0 && location.File.Length > 0)
                {
                    var path = location.File;
                    if (!fileIds.TryGetValue(path, out var file))
                    {
                        file = _files.Count;
                        _files.Add(path);
                        fileIds[path] = file;
                    }

                    if (!lineIds.TryGetValue((file, location.Line), out var line))
                    {
                        line = _lines.Count;
                        _lines.Add((file, location.Line));
                        lineIds[(file, location.Line)] = line;
                    }

                    known = line;
                }
            }

            _stackLine[stack] = known;
        }

        _linesResolved = true;
    }
}
