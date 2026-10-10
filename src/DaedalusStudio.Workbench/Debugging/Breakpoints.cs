using System.Globalization;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Debugging;

/// <summary>
/// A source breakpoint (ADR 0007). <see cref="HitCondition"/> is <c>n</c> or <c>== n</c> (break on the n-th hit),
/// <c>&gt;= n</c> (from the n-th hit on) or <c>% n</c> (every n-th hit). A <see cref="LogMessage"/> makes it a
/// logpoint: the message (with <c>{expressions}</c>) is written and the program goes on.
/// A <see cref="Temporary"/> breakpoint is removed after it stops the program once; one that
/// <see cref="DependsOn"/> another stays inactive until that one is hit in the session (ADR 0021).
/// <see cref="Labels"/> is a comma-separated list.
/// </summary>
public sealed record BreakpointSpec(
    string Path,
    int Line,
    string? Condition = null,
    string? HitCondition = null,
    string? LogMessage = null,
    bool Enabled = true,
    bool Temporary = false,
    BreakpointLocation? DependsOn = null,
    string? Labels = null)
{
    public bool IsLogpoint => LogMessage is not null;

    public BreakpointLocation Location => new(Path, Line);

    /// <summary>Whether the debugger has to stop to emulate this breakpoint's behavior (netcoredbg has no hit counts, logpoints or dependencies).</summary>
    public bool NeedsClientHandling => HitCondition is not null || LogMessage is not null || DependsOn is not null;

    /// <summary>The labels, trimmed, without empty ones.</summary>
    public IReadOnlyList<string> LabelList => BreakpointStore.SplitLabels(Labels);
}

/// <summary>A source location a breakpoint can depend on.</summary>
public sealed record BreakpointLocation(string Path, int Line)
{
    public bool Matches(BreakpointLocation other) => Line == other.Line && SolutionService.PathComparer.Equals(Path, other.Path);
}

/// <summary>A breakpoint on a function by name (<c>Method</c>, <c>Type.Method</c> or <c>Namespace.Type.Method</c>), set in netcoredbg directly.</summary>
public sealed record FunctionBreakpointSpec(string Name, string? Condition = null, bool Enabled = true, string? Labels = null)
{
    public IReadOnlyList<string> LabelList => BreakpointStore.SplitLabels(Labels);
}

/// <summary>What the debug adapter said about a breakpoint.</summary>
public sealed record BreakpointState(bool Verified, string? Message);

/// <summary>The breakpoints of the open solution, by file. <see cref="Changed"/> may be raised on any thread.</summary>
public sealed class BreakpointStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<BreakpointSpec>> _files = new(SolutionService.PathComparer);
    private readonly Dictionary<(string Path, int Line), BreakpointState> _states = new(new LocationComparer());
    private readonly List<FunctionBreakpointSpec> _functions = [];
    private readonly Dictionary<string, BreakpointState> _functionStates = new(StringComparer.Ordinal);

    /// <summary>A file's breakpoints changed (the path), or every file's (null).</summary>
    public event EventHandler<string?>? Changed;

    /// <summary>The function breakpoints changed.</summary>
    public event EventHandler? FunctionsChanged;

    /// <summary>
    /// What the debugger said about breakpoints changed: a file's (the path), or every file's and the functions' (null).
    /// Separate from <see cref="Changed"/>, which makes the debugger send the breakpoints again.
    /// </summary>
    public event EventHandler<string?>? StatesChanged;

    public IReadOnlyList<FunctionBreakpointSpec> Functions
    {
        get
        {
            lock (_gate)
            {
                return [.. _functions];
            }
        }
    }

    public BreakpointState? StateOf(FunctionBreakpointSpec breakpoint)
    {
        lock (_gate)
        {
            return _functionStates.GetValueOrDefault(breakpoint.Name);
        }
    }

    /// <summary>Adds or replaces the function breakpoint with this name.</summary>
    public void SetFunction(FunctionBreakpointSpec breakpoint)
    {
        lock (_gate)
        {
            var index = _functions.FindIndex(f => f.Name == breakpoint.Name);
            if (index >= 0)
            {
                _functions[index] = breakpoint;
            }
            else
            {
                _functions.Add(breakpoint);
            }
        }

        FunctionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveFunction(FunctionBreakpointSpec breakpoint)
    {
        lock (_gate)
        {
            _functions.RemoveAll(f => f.Name == breakpoint.Name);
            _functionStates.Remove(breakpoint.Name);
        }

        FunctionsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SetFunctionStates(IReadOnlyList<(string Name, BreakpointState State)> states)
    {
        lock (_gate)
        {
            _functionStates.Clear();
            foreach (var (name, state) in states)
            {
                _functionStates[name] = state;
            }
        }

        StatesChanged?.Invoke(this, null);
    }

    internal void SetFunctionState(string name, BreakpointState state)
    {
        lock (_gate)
        {
            _functionStates[name] = state;
        }

        StatesChanged?.Invoke(this, null);
    }

    /// <summary>Every label in use, sorted.</summary>
    public IReadOnlyList<string> AllLabels
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Values.SelectMany(f => f).SelectMany(b => b.LabelList).Concat(_functions.SelectMany(f => f.LabelList))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    public static IReadOnlyList<string> SplitLabels(string? labels) =>
        string.IsNullOrWhiteSpace(labels) ? [] : [.. labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Normalizes a comma-separated label list (null when empty).</summary>
    public static string? JoinLabels(IEnumerable<string> labels) =>
        string.Join(", ", labels.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) is { Length: > 0 } joined ? joined : null;

    public IReadOnlyList<BreakpointSpec> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Values.SelectMany(f => f).OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Line)];
            }
        }
    }

    public IReadOnlyList<string> Files
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Keys];
            }
        }
    }

    public IReadOnlyList<BreakpointSpec> For(string path)
    {
        lock (_gate)
        {
            return _files.TryGetValue(path, out var list) ? [.. list] : [];
        }
    }

    public BreakpointSpec? At(string path, int line)
    {
        lock (_gate)
        {
            return _files.TryGetValue(path, out var list) ? list.FirstOrDefault(b => b.Line == line) : null;
        }
    }

    public BreakpointState? StateOf(BreakpointSpec breakpoint)
    {
        lock (_gate)
        {
            return _states.GetValueOrDefault((breakpoint.Path, breakpoint.Line));
        }
    }

    /// <summary>Adds a breakpoint on the line, or removes the one there.</summary>
    public void Toggle(string path, int line)
    {
        if (At(path, line) is { } existing)
        {
            Remove(existing);
        }
        else
        {
            Set(new BreakpointSpec(path, line));
        }
    }

    /// <summary>Adds or replaces the breakpoint at its file and line.</summary>
    public void Set(BreakpointSpec breakpoint)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(breakpoint.Path, out var list))
            {
                _files[breakpoint.Path] = list = [];
            }

            list.RemoveAll(b => b.Line == breakpoint.Line);
            list.Add(breakpoint);
            list.Sort((a, b) => a.Line.CompareTo(b.Line));
        }

        Changed?.Invoke(this, breakpoint.Path);
    }

    public void Remove(BreakpointSpec breakpoint)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(breakpoint.Path, out var list))
            {
                list.RemoveAll(b => b.Line == breakpoint.Line);
                if (list.Count == 0)
                {
                    _files.Remove(breakpoint.Path);
                }
            }

            _states.Remove((breakpoint.Path, breakpoint.Line));
        }

        Changed?.Invoke(this, breakpoint.Path);
    }

    /// <summary>Moves a file's breakpoints after an edit (old line → new line; dropped when the line is gone).</summary>
    public void Relocate(string path, Func<int, int?> map)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var list))
            {
                return;
            }

            var moved = list
                .Select(b => map(b.Line) is { } line ? b with { Line = line } : null)
                .OfType<BreakpointSpec>()
                .DistinctBy(b => b.Line)
                .OrderBy(b => b.Line)
                .ToList();
            if (moved.SequenceEqual(list))
            {
                return;
            }

            _files[path] = moved;
            foreach (var key in _states.Keys.Where(k => SolutionService.PathComparer.Equals(k.Path, path)).ToList())
            {
                _states.Remove(key);
            }
        }

        Changed?.Invoke(this, path);
    }

    public void Clear()
    {
        bool hadFunctions;
        lock (_gate)
        {
            _files.Clear();
            _states.Clear();
            hadFunctions = _functions.Count > 0;
            _functions.Clear();
            _functionStates.Clear();
        }

        Changed?.Invoke(this, null);
        if (hadFunctions)
        {
            FunctionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Replaces every breakpoint (a solution's saved ones).</summary>
    public void Load(IEnumerable<BreakpointSpec> breakpoints, IEnumerable<FunctionBreakpointSpec>? functions = null)
    {
        lock (_gate)
        {
            _files.Clear();
            _states.Clear();
            foreach (var group in breakpoints.GroupBy(b => b.Path, SolutionService.PathComparer))
            {
                _files[group.Key] = [.. group.DistinctBy(b => b.Line).OrderBy(b => b.Line)];
            }

            _functions.Clear();
            _functionStates.Clear();
            _functions.AddRange((functions ?? []).DistinctBy(f => f.Name));
        }

        Changed?.Invoke(this, null);
        FunctionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds breakpoints (an import), replacing those at the same locations or with the same names.</summary>
    public void Add(IEnumerable<BreakpointSpec> breakpoints, IEnumerable<FunctionBreakpointSpec> functions)
    {
        lock (_gate)
        {
            foreach (var breakpoint in breakpoints)
            {
                if (!_files.TryGetValue(breakpoint.Path, out var list))
                {
                    _files[breakpoint.Path] = list = [];
                }

                list.RemoveAll(b => b.Line == breakpoint.Line);
                list.Add(breakpoint);
                list.Sort((a, b) => a.Line.CompareTo(b.Line));
            }

            foreach (var function in functions)
            {
                _functions.RemoveAll(f => f.Name == function.Name);
                _functions.Add(function);
            }
        }

        Changed?.Invoke(this, null);
        FunctionsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SetStates(string path, IReadOnlyList<(int Line, BreakpointState State)> states)
    {
        lock (_gate)
        {
            foreach (var (line, state) in states)
            {
                _states[(path, line)] = state;
            }
        }

        StatesChanged?.Invoke(this, path);
    }

    internal void ClearStates()
    {
        bool files, functions;
        lock (_gate)
        {
            files = _states.Count > 0;
            functions = _functionStates.Count > 0;
            _states.Clear();
            _functionStates.Clear();
        }

        if (files)
        {
            StatesChanged?.Invoke(this, null);
        }

        if (functions)
        {
            StatesChanged?.Invoke(this, null);
        }
    }

    /// <summary>Whether a breakpoint with <paramref name="hitCondition"/> breaks on its <paramref name="hits"/>-th hit.</summary>
    public static bool HitConditionMet(string hitCondition, int hits)
    {
        var text = hitCondition.Trim();
        static int Number(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
        if (text.StartsWith(">=", StringComparison.Ordinal))
        {
            return hits >= Number(text[2..]);
        }

        if (text.StartsWith('%'))
        {
            var n = Number(text[1..]);
            return n > 0 && hits % n == 0;
        }

        if (text.StartsWith("==", StringComparison.Ordinal))
        {
            return hits == Number(text[2..]);
        }

        return hits == Number(text.TrimStart('='));
    }

    private sealed class LocationComparer : IEqualityComparer<(string Path, int Line)>
    {
        public bool Equals((string Path, int Line) x, (string Path, int Line) y) => x.Line == y.Line && SolutionService.PathComparer.Equals(x.Path, y.Path);

        public int GetHashCode((string Path, int Line) obj) => HashCode.Combine(SolutionService.PathComparer.GetHashCode(obj.Path), obj.Line);
    }
}
