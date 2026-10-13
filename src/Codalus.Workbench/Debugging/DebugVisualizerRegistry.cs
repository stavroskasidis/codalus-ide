using Codalus.Plugins.Protocol;

namespace Codalus.Workbench.Debugging;

/// <summary>A visualizer a plugin offers (proposed API 0.2.7): its title, the types it takes, and the call into the plugin.</summary>
public sealed record PluginDebugVisualizer(
    string Id,
    string PluginId,
    string Title,
    IReadOnlyList<string> Types,
    Func<DebugVisualizeParams, CancellationToken, Task<DebugVisualizerViewDto>> Visualize)
{
    /// <summary>Whether the visualizer takes a value of <paramref name="type"/> (a pattern ending in <c>*</c> is a prefix).</summary>
    public bool Accepts(string? type) => type is not null && Types.Any(pattern => pattern.EndsWith('*')
        ? type.StartsWith(pattern[..^1], StringComparison.Ordinal)
        : string.Equals(type, pattern, StringComparison.Ordinal));
}

/// <summary>The plugins' debugger visualizers (M14b, ADR 0032), offered in the variable trees' View menu.</summary>
public sealed class DebugVisualizerRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginDebugVisualizer> _visualizers = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public IReadOnlyList<PluginDebugVisualizer> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _visualizers.Values.OrderBy(v => v.Title, StringComparer.CurrentCulture)];
            }
        }
    }

    public IReadOnlyList<PluginDebugVisualizer> For(string? type) => [.. All.Where(v => v.Accepts(type))];

    public PluginDebugVisualizer? Find(string id)
    {
        lock (_gate)
        {
            return _visualizers.GetValueOrDefault(id);
        }
    }

    public void Register(PluginDebugVisualizer visualizer)
    {
        lock (_gate)
        {
            _visualizers[visualizer.Id] = visualizer;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Unregister(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _visualizers.Remove(id);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
