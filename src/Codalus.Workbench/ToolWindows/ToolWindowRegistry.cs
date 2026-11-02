using Codalus.Workbench.Commands;

namespace Codalus.Workbench.ToolWindows;

/// <summary>Where a tool window opens the first time it is shown (after that, in the group it was last in).</summary>
public enum ToolWindowLocation
{
    Left,
    Right,
    Bottom,

    /// <summary>
    /// A tab among the documents, in the active document group: a page that wants the window's middle (the Plugins page,
    /// Aspire's dashboard). Shown without the focus, it joins the group behind the document the user is in.
    /// </summary>
    Document,

    /// <summary>
    /// A document group of its own beside the active one (to its right), or the other group when there are two: a view of
    /// the document's results (the HTTP Response beside its <c>.http</c> file).
    /// </summary>
    DocumentBeside,
}

public enum ToolWindowKind
{
    /// <summary>A view implemented by the shell itself (Output, Notifications…).</summary>
    BuiltIn,

    /// <summary>A plugin-provided tree data view.</summary>
    Tree,

    /// <summary>A plugin-provided Markdown view.</summary>
    Markdown,

    /// <summary>A plugin's declarative view (D2, API 1.0): elements the shell renders.</summary>
    View,
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
            _windows[descriptor.Id] = descriptor.Source == Commands.ContributionSource.Shell
                ? descriptor with { Title = Localization.Localizer.Get($"toolWindow.{descriptor.Id}", descriptor.Title) }
                : descriptor;
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
