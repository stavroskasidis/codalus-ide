using System.Globalization;
using DaedalusStudio.Plugins.Protocol;

namespace DaedalusStudio.Plugins.Host;

internal interface ITreeAdapter
{
    Task<IReadOnlyList<TreeItemDto>> GetChildrenAsync(string? handle, CancellationToken cancellationToken);
}

/// <summary>
/// Maps a plugin's typed tree elements to opaque string handles for the shell. Handles stay stable for an
/// element (by equality) for the lifetime of the provider, so expansion state survives refreshes.
/// </summary>
internal sealed class TreeAdapter<T> : ITreeAdapter, IDisposable
    where T : notnull
{
    private readonly PluginHostServer _server;
    private readonly string _pluginId;
    private readonly string _viewId;
    private readonly ITreeDataProvider<T> _provider;
    private readonly Lock _gate = new();
    private readonly Dictionary<T, string> _handles = [];
    private readonly Dictionary<string, T> _elements = new(StringComparer.Ordinal);
    private long _next;

    public TreeAdapter(PluginHostServer server, string pluginId, string viewId, ITreeDataProvider<T> provider)
    {
        _server = server;
        _pluginId = pluginId;
        _viewId = viewId;
        _provider = provider;
        _provider.Changed += OnChanged;
    }

    public async Task<IReadOnlyList<TreeItemDto>> GetChildrenAsync(string? handle, CancellationToken cancellationToken)
    {
        T? parent = default;
        if (handle is not null)
        {
            lock (_gate)
            {
                if (!_elements.TryGetValue(handle, out parent))
                {
                    return [];
                }
            }
        }

        IReadOnlyList<T> children;
        try
        {
            children = await _provider.GetChildrenAsync(parent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _server.Log(_pluginId, "error", $"Tree '{_viewId}' failed to provide children: {ex}");
            throw;
        }

        var result = new List<TreeItemDto>(children.Count);
        foreach (var child in children)
        {
            var item = _provider.GetTreeItem(child);
            result.Add(new TreeItemDto(
                HandleOf(child),
                item.Label,
                item.Description,
                item.Tooltip,
                item.Icon,
                item.Collapsible switch
                {
                    TreeItemCollapsibleState.Collapsed => "collapsed",
                    TreeItemCollapsibleState.Expanded => "expanded",
                    _ => "none",
                },
                item.Command is { } command
                    ? new TreeItemCommandDto(command.CommandId, [.. command.Args.Select(PluginHostServer.ToJson)])
                    : null));
        }

        return result;
    }

    private string HandleOf(T element)
    {
        lock (_gate)
        {
            if (!_handles.TryGetValue(element, out var handle))
            {
                handle = (++_next).ToString(CultureInfo.InvariantCulture);
                _handles[element] = handle;
                _elements[handle] = element;
            }

            return handle;
        }
    }

    private void OnChanged(object? sender, TreeDataChangedEventArgs<T> e)
    {
        string? handle = null;
        if (e.Element is { } element)
        {
            lock (_gate)
            {
                _handles.TryGetValue(element, out handle);
            }
        }

        _server.Post(PluginMethods.TreeViewRefresh, new TreeViewRefreshParams(_viewId, handle), PluginJsonContext.Default.TreeViewRefreshParams);
    }

    public void Dispose() => _provider.Changed -= OnChanged;
}
