using Codalus.Debugging;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// Which exceptions break when thrown (ADR 0021): a setting per listed type, and one for every type not in the list.
/// They become a condition on netcoredbg's <c>all</c> filter: <c>A,B</c> when only listed types break, <c>!A,B</c>
/// when every type but the listed ones does. Types match exactly (a base type doesn't cover derived ones).
/// User-unhandled exceptions always break. <see cref="Changed"/> may be raised on any thread.
/// </summary>
public sealed class ExceptionSettings
{
    /// <summary>The types listed by default, unchecked: the common runtime exceptions.</summary>
    public static readonly IReadOnlyList<string> CommonTypes =
    [
        "System.AggregateException",
        "System.ArgumentException",
        "System.ArgumentNullException",
        "System.ArgumentOutOfRangeException",
        "System.Collections.Generic.KeyNotFoundException",
        "System.DivideByZeroException",
        "System.FormatException",
        "System.IndexOutOfRangeException",
        "System.InvalidCastException",
        "System.InvalidOperationException",
        "System.IO.DirectoryNotFoundException",
        "System.IO.FileNotFoundException",
        "System.IO.IOException",
        "System.Net.Http.HttpRequestException",
        "System.NotImplementedException",
        "System.NotSupportedException",
        "System.NullReferenceException",
        "System.ObjectDisposedException",
        "System.OperationCanceledException",
        "System.OverflowException",
        "System.Text.Json.JsonException",
        "System.Threading.Tasks.TaskCanceledException",
        "System.TimeoutException",
        "System.UnauthorizedAccessException",
    ];

    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, bool> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _conditions = new(StringComparer.Ordinal);
    private bool _others;

    public ExceptionSettings() => Reset();

    public event EventHandler? Changed;

    /// <summary>Whether types that aren't listed break when thrown.</summary>
    public bool BreakOnOthers
    {
        get
        {
            lock (_gate)
            {
                return _others;
            }
        }
    }

    /// <summary>The listed types and whether each breaks when thrown, sorted by name.</summary>
    public IReadOnlyList<KeyValuePair<string, bool>> Types
    {
        get
        {
            lock (_gate)
            {
                return [.. _types];
            }
        }
    }

    /// <summary>
    /// The module condition of <paramref name="type"/> (M14b): module names, comma-separated, with <c>*</c> and <c>?</c>
    /// wildcards; a <c>!</c> prefix excludes. Null: every module.
    /// </summary>
    public string? ConditionOf(string type)
    {
        lock (_gate)
        {
            return _conditions.GetValueOrDefault(type);
        }
    }

    /// <summary>The module conditions by type (what a solution saves).</summary>
    public Dictionary<string, string> Conditions()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_conditions, StringComparer.Ordinal);
        }
    }

    /// <summary>Sets <paramref name="type"/>'s module condition (null or empty: every module).</summary>
    public void SetCondition(string type, string? condition)
    {
        condition = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();
        lock (_gate)
        {
            if (_conditions.GetValueOrDefault(type) == condition)
            {
                return;
            }

            if (condition is null)
            {
                _conditions.Remove(type);
            }
            else
            {
                _conditions[type] = condition;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether an exception of <paramref name="type"/> thrown in <paramref name="module"/> passes the type's module
    /// condition: the module matches one of its names (when it has any without <c>!</c>) and none of its <c>!</c> names.
    /// </summary>
    public bool ModuleAllows(string type, string? module)
    {
        if (ConditionOf(type) is not { } condition)
        {
            return true;
        }

        var patterns = condition.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var included = patterns.Where(p => !p.StartsWith('!')).ToList();
        var excluded = patterns.Where(p => p.StartsWith('!')).Select(p => p[1..].Trim()).ToList();
        var name = module ?? string.Empty;
        return (included.Count == 0 || included.Any(p => Wildcard(p, name))) && !excluded.Any(p => Wildcard(p, name));
    }

    private static bool Wildcard(string pattern, string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Whether <paramref name="type"/> (a full type name) breaks when thrown.</summary>
    public bool Breaks(string type)
    {
        lock (_gate)
        {
            return _types.TryGetValue(type, out var breaks) ? breaks : _others;
        }
    }

    /// <summary>Sets one type, adding it to the list if needed.</summary>
    public void Set(string type, bool breakWhenThrown)
    {
        type = type.Trim();
        if (type.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_types.TryGetValue(type, out var current) && current == breakWhenThrown)
            {
                return;
            }

            _types[type] = breakWhenThrown;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a type from the list: it follows <see cref="BreakOnOthers"/> again.</summary>
    public void Remove(string type)
    {
        bool removed;
        lock (_gate)
        {
            removed = _types.Remove(type);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetBreakOnOthers(bool value)
    {
        lock (_gate)
        {
            if (_others == value)
            {
                return;
            }

            _others = value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every type breaks when thrown (true) or none does (false): the whole list follows.</summary>
    public void SetAll(bool value)
    {
        lock (_gate)
        {
            _others = value;
            foreach (var type in _types.Keys.ToList())
            {
                _types[type] = value;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Back to the defaults: the common types listed, none of them breaking when thrown.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _others = false;
            _types.Clear();
            _conditions.Clear();
            foreach (var type in CommonTypes)
            {
                _types[type] = false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the settings (a solution's saved ones); types missing from <paramref name="types"/> keep their defaults.</summary>
    public void Load(bool breakOnOthers, IReadOnlyDictionary<string, bool>? types, IReadOnlyDictionary<string, string>? conditions = null)
    {
        lock (_gate)
        {
            _others = breakOnOthers;
            _types.Clear();
            _conditions.Clear();
            foreach (var (type, condition) in conditions ?? new Dictionary<string, string>())
            {
                _conditions[type] = condition;
            }

            foreach (var type in CommonTypes)
            {
                _types[type] = false;
            }

            foreach (var (type, breaks) in types ?? new Dictionary<string, bool>())
            {
                _types[type] = breaks;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The types whose setting differs from the defaults (what a solution saves).</summary>
    public Dictionary<string, bool> Changes()
    {
        lock (_gate)
        {
            return _types.Where(t => t.Value || !CommonTypes.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        }
    }

    /// <summary>The filters and filter options to send with <c>setExceptionBreakpoints</c>.</summary>
    public (string[] Filters, ExceptionFilterOptions[] Options) ToFilters()
    {
        lock (_gate)
        {
            if (_others)
            {
                var excluded = _types.Where(t => !t.Value).Select(t => t.Key).ToList();
                return excluded.Count == 0
                    ? ([DebugService.ExceptionsAll, DebugService.ExceptionsUserUnhandled], [])
                    : ([DebugService.ExceptionsUserUnhandled], [new ExceptionFilterOptions(DebugService.ExceptionsAll, "!" + string.Join(",", excluded))]);
            }

            var included = _types.Where(t => t.Value).Select(t => t.Key).ToList();
            return included.Count == 0
                ? ([DebugService.ExceptionsUserUnhandled], [])
                : ([DebugService.ExceptionsUserUnhandled], [new ExceptionFilterOptions(DebugService.ExceptionsAll, string.Join(",", included))]);
        }
    }
}
