namespace Codalus.Workbench.StatusBar;

public enum StatusBarAlignment
{
    Left,
    Right,
}

/// <summary>A status bar entry. Higher <see cref="Priority"/> sits closer to the window edge.</summary>
public sealed record StatusBarItem(string Id, string Text, StatusBarAlignment Alignment, int Priority, string SourceId)
{
    public string? Tooltip { get; init; }

    public string? Command { get; init; }

    public bool Visible { get; init; } = true;

    /// <summary>Accessible name; defaults to the text.</summary>
    public string? AccessibleName { get; init; }
}

public sealed class StatusBarService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StatusBarItem> _items = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public IReadOnlyList<StatusBarItem> GetItems(StatusBarAlignment alignment)
    {
        lock (_gate)
        {
            var items = _items.Values.Where(i => i.Visible && i.Alignment == alignment);
            return alignment == StatusBarAlignment.Left
                ? [.. items.OrderByDescending(i => i.Priority).ThenBy(i => i.Id, StringComparer.Ordinal)]
                : [.. items.OrderBy(i => i.Priority).ThenBy(i => i.Id, StringComparer.Ordinal)];
        }
    }

    public void Set(StatusBarItem item)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(item.Id, out var existing) && existing == item)
            {
                return;
            }

            _items[item.Id] = item;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.Remove(id);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemoveFrom(string sourceId)
    {
        lock (_gate)
        {
            foreach (var id in _items.Values.Where(i => i.SourceId == sourceId).Select(i => i.Id).ToList())
            {
                _items.Remove(id);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
