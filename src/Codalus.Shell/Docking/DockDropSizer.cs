using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using DockOrientation = Dock.Model.Core.Orientation;

namespace Codalus.Shell.Docking;

/// <summary>
/// A tool window dragged to a new place beside others (M50, the maintainer's screenshots 1–3: Solution Explorer dragged
/// to the window's left edge became an invisible column) lands at the size it had before the drag, or a quarter of the
/// window when it never had one, and never under <see cref="MinimumSize"/>. Dock gives the new pane a proportion of its
/// own choosing (half its neighbour's, or <c>DockSettings.GlobalDockingProportion</c> at the window's edges) next to
/// siblings whose proportions came from a saved layout, so the pane could get nothing. Its size is set once the drop's
/// layout has run, from the sizes each tool window's frame had before it.
/// </summary>
internal sealed class DockDropSizer
{
    /// <summary>The smallest size a dropped tool window gets along its pane's direction, in pixels.</summary>
    internal const double MinimumSize = 200;

    private readonly TopLevel _window;
    private readonly Dictionary<string, Size> _sizes = new(StringComparer.Ordinal);
    private readonly IDisposable _bounds;
    private int _pending;

    private DockDropSizer(TopLevel window, ShellDockFactory factory)
    {
        _window = window;
        _bounds = Visual.BoundsProperty.Changed.AddClassHandler<ToolChromeControl>((chrome, _) => Remember(chrome));
        factory.DockableDocked += OnDocked;
        window.Closed += (_, _) =>
        {
            factory.DockableDocked -= OnDocked;
            _bounds.Dispose();
        };
    }

    public static void Attach(Window window, ShellDockFactory factory) => _ = new DockDropSizer(window, factory);

    /// <summary>Each tool window's last size in its frame, while no drop is being laid out.</summary>
    private void Remember(ToolChromeControl chrome)
    {
        if (_pending > 0 || chrome.DataContext is not IDock { VisibleDockables: { } tools } || chrome.Bounds.Width < 1 || chrome.Bounds.Height < 1
            || TopLevel.GetTopLevel(chrome) != _window)
        {
            return;
        }

        foreach (var tool in tools)
        {
            if (tool is ToolWindowDockable)
            {
                _sizes[tool.Id] = chrome.Bounds.Size;
            }
        }
    }

    private void OnDocked(object? sender, DockableDockedEventArgs e)
    {
        if (e.Operation is not (DockOperation.Left or DockOperation.Right or DockOperation.Top or DockOperation.Bottom) || ToolDockOf(e.Dockable) is not { } dock)
        {
            return;
        }

        // Before the drop's layout: the sizes are still the ones the tools had where they came from.
        var before = (dock.ActiveDockable is { } active && _sizes.TryGetValue(active.Id, out var size)) ? size : (Size?)null;
        before ??= dock.VisibleDockables?.Select(t => _sizes.TryGetValue(t.Id, out var s) ? s : (Size?)null).FirstOrDefault(s => s is not null);
        _pending++;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Size(dock, before);
            }
            finally
            {
                _pending--;
            }
        }, DispatcherPriority.Background);
    }

    private static IToolDock? ToolDockOf(IDockable? dockable) => dockable switch
    {
        IToolDock tool => tool,
        ToolWindowDockable { Owner: IToolDock owner } => owner,
        IProportionalDock { VisibleDockables: { } children } => children.OfType<IToolDock>().FirstOrDefault(),
        _ => null,
    };

    private void Size(IToolDock dock, Size? before)
    {
        if (dock.Owner is not IProportionalDock { VisibleDockables: { } siblings } owner)
        {
            return;
        }

        _window.UpdateLayout();
        if (_window.GetVisualDescendants().OfType<ProportionalDockControl>().FirstOrDefault(c => c.DataContext == owner && c.IsEffectivelyVisible) is not { } ownerControl)
        {
            return;
        }

        var horizontal = owner.Orientation == DockOrientation.Horizontal;
        var available = horizontal ? ownerControl.Bounds.Width : ownerControl.Bounds.Height;
        var others = siblings.Where(d => d != dock && d is not IProportionalDockSplitter).ToList();
        if (available < 1 || others.Count == 0)
        {
            return;
        }

        var window = horizontal ? _window.Bounds.Width : _window.Bounds.Height;
        var wanted = before is { } had && (horizontal ? had.Width : had.Height) is var length && length >= MinimumSize ? length : window / 4;
        wanted = Math.Max(wanted, MinimumSize);

        // Its neighbours keep at least the minimum too, where the pane has room for both.
        wanted = Math.Min(wanted, Math.Max(available / 2, available - (MinimumSize * others.Count)));
        var share = Math.Clamp(wanted / available, 0.05, 0.95);

        // The others keep their proportions to each other: unassigned ones share what the assigned ones leave.
        var assigned = others.Where(d => !double.IsNaN(d.Proportion) && d.Proportion > 0).Sum(d => d.Proportion);
        var unassigned = others.Count(d => double.IsNaN(d.Proportion) || d.Proportion <= 0);
        var each = unassigned > 0 ? Math.Max(1 - assigned, 0.1 * unassigned) / unassigned : 0;
        var total = assigned + (each * unassigned);
        foreach (var other in others)
        {
            var current = double.IsNaN(other.Proportion) || other.Proportion <= 0 ? each : other.Proportion;
            other.Proportion = current / total * (1 - share);
            other.CollapsedProportion = other.Proportion;
        }

        dock.Proportion = share;
        dock.CollapsedProportion = share;
    }
}
