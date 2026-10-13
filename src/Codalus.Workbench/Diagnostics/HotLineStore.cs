using Codalus.Diagnostics.Protocol;

namespace Codalus.Workbench.Diagnostics;

/// <summary>
/// The hot lines the editor gutter shows (ADR 0045): the samples per source line of the profiling report the user is
/// looking at, by file. Set by the report (for its filter), cleared when it closes. Raised on any thread.
/// </summary>
public sealed class HotLineStore
{
    private readonly Lock _gate = new();
    private Dictionary<string, IReadOnlyList<CpuLine>> _files = new(StringComparer.Ordinal);
    private bool _visible = true;

    public event EventHandler? Changed;

    /// <summary>What the lines come from (the report's title), or null when there are none.</summary>
    public string? Source { get; private set; }

    /// <summary>The samples that make 100%.</summary>
    public int Total { get; private set; }

    /// <summary>Whether editors show them (Show Hot Lines).</summary>
    public bool IsVisible
    {
        get => _visible;
        set
        {
            if (_visible != value)
            {
                _visible = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasLines
    {
        get
        {
            lock (_gate)
            {
                return _files.Count > 0;
            }
        }
    }

    public IReadOnlyCollection<string> Files
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Keys];
            }
        }
    }

    internal static string Key(string path)
    {
        var full = path.Replace('\\', '/');
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    public void Set(string source, int total, IReadOnlyDictionary<string, IReadOnlyList<CpuLine>> files)
    {
        lock (_gate)
        {
            Source = source;
            Total = total;
            _files = files.ToDictionary(f => Key(f.Key), f => f.Value, StringComparer.Ordinal);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears the lines (of <paramref name="source"/> only, when given).</summary>
    public void Clear(string? source = null)
    {
        lock (_gate)
        {
            if (source is not null && Source != source)
            {
                return;
            }

            Source = null;
            Total = 0;
            _files = new(StringComparer.Ordinal);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A file's lines with samples (none when hidden).</summary>
    public IReadOnlyList<CpuLine> LinesOf(string path)
    {
        lock (_gate)
        {
            return _visible && _files.TryGetValue(Key(path), out var lines) ? lines : [];
        }
    }
}
