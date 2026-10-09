using System.Globalization;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Debugging;

/// <summary>
/// A source breakpoint (ADR 0007). <see cref="HitCondition"/> is <c>n</c> or <c>== n</c> (break on the n-th hit),
/// <c>&gt;= n</c> (from the n-th hit on) or <c>% n</c> (every n-th hit). A <see cref="LogMessage"/> makes it a
/// logpoint: the message (with <c>{expressions}</c>) is written and the program goes on.
/// </summary>
public sealed record BreakpointSpec(string Path, int Line, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool Enabled = true)
{
    public bool IsLogpoint => LogMessage is not null;

    /// <summary>Whether the debugger has to stop to emulate this breakpoint's behavior (netcoredbg has no hit counts or logpoints).</summary>
    public bool NeedsClientHandling => HitCondition is not null || LogMessage is not null;
}

/// <summary>What the debug adapter said about a breakpoint.</summary>
public sealed record BreakpointState(bool Verified, string? Message);

/// <summary>The breakpoints of the open solution, by file. <see cref="Changed"/> may be raised on any thread.</summary>
public sealed class BreakpointStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<BreakpointSpec>> _files = new(SolutionService.PathComparer);
    private readonly Dictionary<(string Path, int Line), BreakpointState> _states = new(new LocationComparer());

    /// <summary>A file's breakpoints changed (the path), or every file's (null).</summary>
    public event EventHandler<string?>? Changed;

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
        lock (_gate)
        {
            _files.Clear();
            _states.Clear();
        }

        Changed?.Invoke(this, null);
    }

    /// <summary>Replaces every breakpoint (a solution's saved ones).</summary>
    public void Load(IEnumerable<BreakpointSpec> breakpoints)
    {
        lock (_gate)
        {
            _files.Clear();
            _states.Clear();
            foreach (var group in breakpoints.GroupBy(b => b.Path, SolutionService.PathComparer))
            {
                _files[group.Key] = [.. group.DistinctBy(b => b.Line).OrderBy(b => b.Line)];
            }
        }

        Changed?.Invoke(this, null);
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

        Changed?.Invoke(this, path);
    }

    internal void ClearStates()
    {
        lock (_gate)
        {
            if (_states.Count == 0)
            {
                return;
            }

            _states.Clear();
        }

        Changed?.Invoke(this, null);
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
