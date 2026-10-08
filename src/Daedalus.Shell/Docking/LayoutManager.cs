using Daedalus.Shell.Infrastructure;
using Daedalus.Workbench.ToolWindows;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;

namespace Daedalus.Shell.Docking;

/// <summary>
/// Owns the dock layout: the default arrangement, showing tool windows by id, documents, Reset Layout, and
/// saving/restoring the layout as a <see cref="LayoutSnapshot"/>.
/// </summary>
/// <remarks>
/// Default layout: a horizontal "Main" dock of [left tools | "Center" | right tools], where "Center" is a
/// vertical dock of [documents | bottom tools]. Empty tool panes collapse; showing a tool window recreates its
/// pane in the right place. Floating windows are not persisted yet. TODO(issue): persist floating windows and
/// reopen documents.
/// </remarks>
public sealed class LayoutManager : ObservableObject
{
    public const string DocumentsId = "Documents";
    private const string MainId = "Main";
    private const string CenterId = "Center";
    private const string LeftId = "LeftPane";
    private const string RightId = "RightPane";
    private const string BottomId = "BottomPane";

    private readonly ToolWindowRegistry _registry;
    private readonly Func<ToolWindowDescriptor, object?> _createContent;
    private readonly Dictionary<string, ToolWindowDockable> _tools = new(StringComparer.Ordinal);
    private IRootDock _root;
    private IDocumentDock? _lastDocumentDock;
    private ShellDocument? _activeDocument;

    public LayoutManager(ToolWindowRegistry registry, Func<ToolWindowDescriptor, object?> createContent)
    {
        _registry = registry;
        _createContent = createContent;
        Factory = new ShellDockFactory();
        Factory.ActiveDockableChanged += OnActiveDockableChanged;
        Factory.FocusedDockableChanged += OnFocusedDockableChanged;
        Factory.DockableClosed += OnDockableClosed;
        _root = Build(DefaultTree())!;
    }

    public ShellDockFactory Factory { get; }

    public IRootDock Root
    {
        get => _root;
        private set => SetField(ref _root, value);
    }

    public ShellDocument? ActiveDocument
    {
        get => _activeDocument;
        private set
        {
            if (SetField(ref _activeDocument, value))
            {
                ActiveDocumentChanged?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<ShellDocument?>? ActiveDocumentChanged;

    /// <summary>Raised when a tool window should receive keyboard focus once its view exists.</summary>
    public event EventHandler<IDockable>? FocusRequested;

    /// <summary>Tool windows in the layout, including pinned and floating ones.</summary>
    public IEnumerable<ToolWindowDockable> OpenToolWindows => AllDockables().OfType<ToolWindowDockable>();

    public IEnumerable<ShellDocument> Documents => AllDockables().OfType<ShellDocument>();

    // ------------------------------------------------------------------ tool windows

    /// <summary>Shows (adding it if needed) the tool window <paramref name="id"/>. Returns null for unknown ids.</summary>
    public ToolWindowDockable? ShowToolWindow(string id, bool focus = true)
    {
        if (_registry.Find(id) is not { } descriptor)
        {
            return null;
        }

        var tool = GetOrCreateTool(descriptor);
        if (IsPinned(tool))
        {
            Factory.PreviewPinnedDockable(tool);
        }
        else if (!AllDockables().Contains(tool))
        {
            var pane = EnsurePane(descriptor.Location);
            Factory.AddDockable(pane, tool);
            Factory.SetActiveDockable(tool);
        }
        else
        {
            Factory.SetActiveDockable(tool);
        }

        if (focus)
        {
            if (tool.Owner is IDock owner)
            {
                Factory.SetFocusedDockable(owner, tool);
            }

            FocusRequested?.Invoke(this, tool);
        }

        return tool;
    }

    public bool IsVisible(string toolWindowId) => OpenToolWindows.Any(t => t.Id == toolWindowId);

    public void CloseToolWindow(string id)
    {
        if (OpenToolWindows.FirstOrDefault(t => t.Id == id) is { } tool)
        {
            Factory.CloseDockable(tool);
        }
    }

    private ToolWindowDockable GetOrCreateTool(ToolWindowDescriptor descriptor)
    {
        if (!_tools.TryGetValue(descriptor.Id, out var tool) || tool.Descriptor != descriptor)
        {
            tool = new ToolWindowDockable(descriptor, _createContent(descriptor));
            _tools[descriptor.Id] = tool;
        }

        return tool;
    }

    private ToolWindowDockable? ResolveTool(string id) => _registry.Find(id) is { } descriptor ? GetOrCreateTool(descriptor) : null;

    private bool IsPinned(IDockable dockable) =>
        new[] { _root.LeftPinnedDockables, _root.RightPinnedDockables, _root.TopPinnedDockables, _root.BottomPinnedDockables }
            .Any(list => list?.Contains(dockable) == true);

    private IToolDock EnsurePane(ToolWindowLocation location)
    {
        var (id, alignment, proportion) = location switch
        {
            ToolWindowLocation.Left => (LeftId, Alignment.Left, 0.22),
            ToolWindowLocation.Right => (RightId, Alignment.Right, 0.22),
            _ => (BottomId, Alignment.Bottom, 0.28),
        };

        if (Find<IToolDock>(id) is { } existing)
        {
            return existing;
        }

        var pane = CreateToolDock(id, alignment, proportion);
        if (location == ToolWindowLocation.Bottom)
        {
            var center = EnsureContainer(CenterId, Orientation.Vertical, DocumentsDock());
            Factory.AddDockable(center, Factory.CreateProportionalDockSplitter());
            Factory.AddDockable(center, pane);
            MakeRoomFor(center, pane);
        }
        else
        {
            var main = EnsureContainer(MainId, Orientation.Horizontal, (IDockable)_root.VisibleDockables![0]);
            if (location == ToolWindowLocation.Left)
            {
                Factory.InsertDockable(main, Factory.CreateProportionalDockSplitter(), 0);
                Factory.InsertDockable(main, pane, 0);
            }
            else
            {
                Factory.AddDockable(main, Factory.CreateProportionalDockSplitter());
                Factory.AddDockable(main, pane);
            }

            MakeRoomFor(main, pane);
        }

        return pane;
    }

    /// <summary>Scales the other children of <paramref name="dock"/> so <paramref name="pane"/> gets its proportion.</summary>
    private static void MakeRoomFor(IDock dock, IDockable pane)
    {
        var others = dock.VisibleDockables!.Where(d => d != pane && d is not IProportionalDockSplitter).ToList();
        var known = others.Where(d => !double.IsNaN(d.Proportion)).Sum(d => d.Proportion);
        var rest = 1 - pane.Proportion;
        foreach (var other in others)
        {
            other.Proportion = double.IsNaN(other.Proportion) || known <= 0 ? rest / others.Count : other.Proportion / known * rest;
        }
    }

    /// <summary>
    /// The proportional dock <paramref name="id"/>; if the user's rearranging removed it, <paramref name="inner"/>
    /// is wrapped in a new one in place.
    /// </summary>
    private IProportionalDock EnsureContainer(string id, Orientation orientation, IDockable inner)
    {
        if (Find<IProportionalDock>(id) is { } existing)
        {
            return existing;
        }

        if (inner.Owner is IProportionalDock { } owner && owner.Orientation == orientation)
        {
            return owner;
        }

        var wrapper = Factory.CreateProportionalDock();
        wrapper.Id = id;
        wrapper.Orientation = orientation;
        wrapper.Proportion = inner.Proportion;
        inner.Proportion = double.NaN;
        var parent = (IDock)inner.Owner!;
        var index = parent.VisibleDockables!.IndexOf(inner);
        wrapper.VisibleDockables = Factory.CreateList(inner);
        wrapper.ActiveDockable = inner;
        parent.VisibleDockables[index] = wrapper;
        if (parent.ActiveDockable == inner)
        {
            parent.ActiveDockable = wrapper;
        }

        if (parent.DefaultDockable == inner)
        {
            parent.DefaultDockable = wrapper;
        }

        Factory.InitDockable(wrapper, parent);
        return wrapper;
    }

    // ------------------------------------------------------------------ documents

    public void OpenDocument(ShellDocument document)
    {
        var existing = Documents.FirstOrDefault(d => d.Id == document.Id);
        if (existing is null)
        {
            var dock = _lastDocumentDock is { Owner: not null } last && AllDockables().Contains(last) ? last : DocumentsDock();
            Factory.AddDockable(dock, document);
            existing = document;
        }

        Factory.SetActiveDockable(existing);
        if (existing.Owner is IDock owner)
        {
            Factory.SetFocusedDockable(owner, existing);
        }

        UpdateActiveDocument();
    }

    public T? FindDocument<T>(string id)
        where T : ShellDocument => Documents.OfType<T>().FirstOrDefault(d => d.Id == id);

    public void CloseDocument(ShellDocument document) => Factory.CloseDockable(document);

    private IDocumentDock DocumentsDock()
    {
        if ((Find<IDocumentDock>(DocumentsId) ?? AllDockables().OfType<IDocumentDock>().FirstOrDefault()) is { } dock)
        {
            return dock;
        }

        // Unreachable with a valid layout (it always has a documents dock); rebuild rather than fail.
        ResetLayout();
        return Find<IDocumentDock>(DocumentsId)!;
    }

    private void OnActiveDockableChanged(object? sender, ActiveDockableChangedEventArgs e) => UpdateActiveDocument(e.Dockable);

    private void OnFocusedDockableChanged(object? sender, FocusedDockableChangedEventArgs e) => UpdateActiveDocument(e.Dockable);

    private void OnDockableClosed(object? sender, DockableClosedEventArgs e) => UpdateActiveDocument();

    private void UpdateActiveDocument(IDockable? changed = null)
    {
        // Dock raises events while InitLayout runs, before the new root is assigned.
        if (_root is null)
        {
            return;
        }

        if (changed?.Owner is IDocumentDock owner)
        {
            _lastDocumentDock = owner;
        }

        var dock = _lastDocumentDock is { } last && AllDockables().Contains(last) ? last : Find<IDocumentDock>(DocumentsId);
        ActiveDocument = dock?.ActiveDockable as ShellDocument ?? Documents.FirstOrDefault();
    }

    // ------------------------------------------------------------------ reset, save and restore

    public void ResetLayout()
    {
        var documents = Documents.ToList();
        Root = Build(DefaultTree())!;
        foreach (var document in documents)
        {
            Factory.AddDockable(Find<IDocumentDock>(DocumentsId)!, document);
        }

        UpdateActiveDocument();
    }

    public LayoutSnapshot Capture(WindowPlacement? window) => new(LayoutSnapshot.CurrentVersion, CaptureNode((IDockable)_root.VisibleDockables![0])!)
    {
        LeftPinned = PinnedIds(_root.LeftPinnedDockables),
        RightPinned = PinnedIds(_root.RightPinnedDockables),
        TopPinned = PinnedIds(_root.TopPinnedDockables),
        BottomPinned = PinnedIds(_root.BottomPinnedDockables),
        Window = window,
    };

    /// <summary>Replaces the layout with <paramref name="snapshot"/>; returns false (keeping the current layout) if it is unusable.</summary>
    public bool Restore(LayoutSnapshot snapshot)
    {
        if (snapshot.Version != LayoutSnapshot.CurrentVersion || Build(snapshot.Root) is not { } root)
        {
            return false;
        }

        var documents = Documents.ToList();
        Root = root;
        foreach (var document in documents)
        {
            Factory.AddDockable(DocumentsDock(), document);
        }

        foreach (var (ids, location) in new[]
                 {
                     (snapshot.LeftPinned, ToolWindowLocation.Left), (snapshot.RightPinned, ToolWindowLocation.Right),
                     (snapshot.TopPinned, ToolWindowLocation.Bottom), (snapshot.BottomPinned, ToolWindowLocation.Bottom),
                 })
        {
            foreach (var tool in (ids ?? []).Select(ResolveTool).OfType<ToolWindowDockable>())
            {
                Factory.AddDockable(EnsurePane(location), tool);
                Factory.PinDockable(tool);
            }
        }

        UpdateActiveDocument();
        return true;
    }

    private static List<string>? PinnedIds(IList<IDockable>? pinned) =>
        pinned is { Count: > 0 } ? [.. pinned.OfType<ToolWindowDockable>().Select(t => t.Id)] : null;

    private static LayoutNode? CaptureNode(IDockable dockable) => dockable switch
    {
        IProportionalDock p => new LayoutNode("proportional")
        {
            Id = p.Id,
            Orientation = p.Orientation == Orientation.Horizontal ? "horizontal" : "vertical",
            Proportion = Proportion(p),
            Children = [.. (p.VisibleDockables ?? []).Select(CaptureNode).OfType<LayoutNode>()],
        },
        IProportionalDockSplitter => new LayoutNode("splitter"),
        IToolDock t => new LayoutNode("tools")
        {
            Id = t.Id,
            Alignment = t.Alignment.ToString().ToLowerInvariant(),
            Proportion = Proportion(t),
            Active = t.ActiveDockable?.Id,
            Tools = [.. (t.VisibleDockables ?? []).OfType<ToolWindowDockable>().Select(tool => tool.Id)],
        },
        IDocumentDock d => new LayoutNode("documents") { Id = d.Id, Proportion = Proportion(d) },
        _ => null,
    };

    private static double? Proportion(IDockable dockable) => double.IsNaN(dockable.Proportion) ? null : dockable.Proportion;

    private IRootDock? Build(LayoutNode tree)
    {
        var top = BuildNode(tree);
        if (top is null || !Flatten(top).OfType<IDocumentDock>().Any())
        {
            return null;
        }

        var root = Factory.CreateRootDock();
        root.Id = "Root";
        root.IsCollapsable = false;
        root.VisibleDockables = Factory.CreateList(top);
        root.ActiveDockable = top;
        root.DefaultDockable = top;
        root.HiddenDockables ??= Factory.CreateList<IDockable>();
        root.LeftPinnedDockables ??= Factory.CreateList<IDockable>();
        root.RightPinnedDockables ??= Factory.CreateList<IDockable>();
        root.TopPinnedDockables ??= Factory.CreateList<IDockable>();
        root.BottomPinnedDockables ??= Factory.CreateList<IDockable>();
        Factory.InitLayout(root);
        _lastDocumentDock = null;
        return root;
    }

    private IDockable? BuildNode(LayoutNode node)
    {
        switch (node.Kind)
        {
            case "proportional":
            {
                var children = NormalizeSplitters((node.Children ?? []).Select(BuildNode).OfType<IDockable>().ToList());
                if (children.Count == 0)
                {
                    return null;
                }

                var dock = Factory.CreateProportionalDock();
                dock.Id = node.Id ?? string.Empty;
                dock.Orientation = node.Orientation == "vertical" ? Orientation.Vertical : Orientation.Horizontal;
                dock.Proportion = node.Proportion ?? double.NaN;
                dock.VisibleDockables = Factory.CreateList([.. children]);
                dock.ActiveDockable = children.FirstOrDefault(c => c is not IProportionalDockSplitter);
                return dock;
            }

            case "splitter":
                return Factory.CreateProportionalDockSplitter();

            case "tools":
            {
                var tools = (node.Tools ?? []).Select(ResolveTool).OfType<ToolWindowDockable>().Distinct().ToList();
                if (tools.Count == 0)
                {
                    return null;
                }

                var alignment = node.Alignment switch
                {
                    "left" => Alignment.Left,
                    "right" => Alignment.Right,
                    "top" => Alignment.Top,
                    _ => Alignment.Bottom,
                };
                var dock = CreateToolDock(node.Id ?? string.Empty, alignment, node.Proportion ?? double.NaN);
                dock.VisibleDockables = Factory.CreateList<IDockable>([.. tools]);
                dock.ActiveDockable = tools.FirstOrDefault(t => t.Id == node.Active) ?? tools[0];
                return dock;
            }

            case "documents":
            {
                var dock = Factory.CreateDocumentDock();
                dock.Id = node.Id ?? DocumentsId;
                dock.IsCollapsable = false;
                dock.CanCreateDocument = false;
                dock.Proportion = node.Proportion ?? double.NaN;
                dock.VisibleDockables = Factory.CreateList<IDockable>();
                return dock;
            }

            default:
                return null;
        }
    }

    private IToolDock CreateToolDock(string id, Alignment alignment, double proportion)
    {
        var dock = Factory.CreateToolDock();
        dock.Id = id;
        dock.Alignment = alignment;
        dock.Proportion = proportion;
        dock.VisibleDockables = Factory.CreateList<IDockable>();
        return dock;
    }

    /// <summary>Drops splitters at either end and between removed siblings.</summary>
    private static List<IDockable> NormalizeSplitters(List<IDockable> children)
    {
        var result = new List<IDockable>();
        foreach (var child in children)
        {
            if (child is IProportionalDockSplitter && (result.Count == 0 || result[^1] is IProportionalDockSplitter))
            {
                continue;
            }

            result.Add(child);
        }

        while (result.Count > 0 && result[^1] is IProportionalDockSplitter)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private LayoutNode DefaultTree()
    {
        LayoutNode Tools(string id, string alignment, double proportion, ToolWindowLocation location) => new("tools")
        {
            Id = id,
            Alignment = alignment,
            Proportion = proportion,
            Tools = [.. _registry.All.Where(w => w.VisibleByDefault && w.Location == location).Select(w => w.Id)],
        };

        return new LayoutNode("proportional")
        {
            Id = MainId,
            Orientation = "horizontal",
            Children =
            [
                Tools(LeftId, "left", 0.22, ToolWindowLocation.Left),
                new LayoutNode("splitter"),
                new LayoutNode("proportional")
                {
                    Id = CenterId,
                    Orientation = "vertical",
                    Children =
                    [
                        new LayoutNode("documents") { Id = DocumentsId },
                        new LayoutNode("splitter"),
                        Tools(BottomId, "bottom", 0.28, ToolWindowLocation.Bottom),
                    ],
                },
                new LayoutNode("splitter"),
                Tools(RightId, "right", 0.22, ToolWindowLocation.Right),
            ],
        };
    }

    // ------------------------------------------------------------------ traversal

    private T? Find<T>(string id)
        where T : class, IDockable => AllDockables().OfType<T>().FirstOrDefault(d => d.Id == id);

    /// <summary>Everything in the layout: the main tree, pinned dockables and floating windows.</summary>
    private IEnumerable<IDockable> AllDockables()
    {
        foreach (var dockable in Flatten(_root))
        {
            yield return dockable;
        }

        foreach (var list in new[] { _root.LeftPinnedDockables, _root.RightPinnedDockables, _root.TopPinnedDockables, _root.BottomPinnedDockables })
        {
            foreach (var dockable in list ?? [])
            {
                yield return dockable;
            }
        }

        foreach (var window in _root.Windows ?? [])
        {
            if (window.Layout is { } layout)
            {
                foreach (var dockable in Flatten(layout))
                {
                    yield return dockable;
                }
            }
        }
    }

    private static IEnumerable<IDockable> Flatten(IDockable dockable)
    {
        yield return dockable;
        if (dockable is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children.ToList())
            {
                foreach (var descendant in Flatten(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
