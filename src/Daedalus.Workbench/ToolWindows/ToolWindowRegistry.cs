using Daedalus.Workbench.Commands;

namespace Daedalus.Workbench.ToolWindows;

public enum ToolWindowLocation
{
    Left,
    Right,
    Bottom,
}

public enum ToolWindowKind
{
    /// <summary>A view implemented by the shell itself (Output, Notifications…).</summary>
    BuiltIn,

    /// <summary>A plugin-provided tree data view.</summary>
    Tree,

    /// <summary>A plugin-provided Markdown view.</summary>
    Markdown,
}

public sealed record ToolWindowDescriptor(string Id, string Title, ToolWindowLocation Location, ToolWindowKind Kind, ContributionSource Source)
{
    public string? Icon { get; init; }

    /// <summary>Opened in the default layout.</summary>
    public bool VisibleByDefault { get; init; }
}

/// <summary>All tool windows known to the shell, by id. Layout persistence resolves content through it.</summary>
public sealed class ToolWindowRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ToolWindowDescriptor> _windows = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    /// <summary>Raised when a command asks to show a tool window.</summary>
    public event EventHandler<ToolWindowDescriptor>? ShowRequested;

    public IReadOnlyList<ToolWindowDescriptor> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _windows.Values.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public ToolWindowDescriptor? Find(string id)
    {
        lock (_gate)
        {
            return _windows.GetValueOrDefault(id);
        }
    }

    public void Register(ToolWindowDescriptor descriptor)
    {
        lock (_gate)
        {
            _windows[descriptor.Id] = descriptor;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveFrom(ContributionSource source)
    {
        lock (_gate)
        {
            foreach (var id in _windows.Values.Where(w => w.Source == source).Select(w => w.Id).ToList())
            {
                _windows.Remove(id);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Show(string id)
    {
        if (Find(id) is not { } descriptor)
        {
            return false;
        }

        ShowRequested?.Invoke(this, descriptor);
        return true;
    }
}
