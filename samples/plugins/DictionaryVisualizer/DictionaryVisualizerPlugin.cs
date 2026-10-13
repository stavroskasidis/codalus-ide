using System.Globalization;
using Codalus.Plugins;
using Codalus.Plugins.Proposed;

namespace DictionaryVisualizer;

/// <summary>
/// A debugger visualizer (proposed API 0.2.7): a <c>Dictionary&lt;TKey, TValue&gt;</c> as a table. netcoredbg applies no
/// type proxies, so the entries are read from the dictionary's own fields: <c>_count</c> slots of <c>_entries</c>, where
/// a free slot has <c>next</c> below -1.
/// </summary>
public sealed class DictionaryVisualizerPlugin : ICodalusPlugin
{
    private const int MaxEntries = 200;

    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Subscriptions.Add(context.Proposed().Debug.RegisterVisualizer(
            new DebugVisualizerOptions("dictionary", "Dictionary Visualizer", ["System.Collections.Generic.Dictionary<*"]),
            VisualizeAsync));
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<VisualizerView> VisualizeAsync(IDebugVisualizerContext value, CancellationToken cancellationToken)
    {
        var target = $"({value.Expression})";
        var count = int.Parse((await value.EvaluateAsync(target + "._count", cancellationToken)).Value, CultureInfo.InvariantCulture);
        var rows = new List<IReadOnlyList<string>>();
        for (var i = 0; i < count && rows.Count < MaxEntries; i++)
        {
            var entry = $"{target}._entries[{i.ToString(CultureInfo.InvariantCulture)}]";
            if (int.Parse((await value.EvaluateAsync(entry + ".next", cancellationToken)).Value, CultureInfo.InvariantCulture) < -1)
            {
                continue; // a removed entry
            }

            var key = await value.EvaluateAsync(entry + ".key", cancellationToken);
            var item = await value.EvaluateAsync(entry + ".value", cancellationToken);
            rows.Add([key.Value, item.Value]);
        }

        return VisualizerView.FromTable(["Key", "Value"], rows);
    }
}
