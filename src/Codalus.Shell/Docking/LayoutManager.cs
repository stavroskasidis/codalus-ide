using Codalus.Shell.Infrastructure;
using Codalus.Workbench.ToolWindows;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;

namespace Codalus.Shell.Docking;

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
        // Every document's tab has its pin button, wherever it was added (opened, dragged to another group, restored).
        Factory.DockableAdded += (_, e) =>
        {
            if (e.Dockable is ShellDocument document)
            {
                document.PinCommand ??= new Infrastructure.RelayCommand(_ => SetPinned(document, !document.IsPinned));
            }
        };
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

    /// <summary>A document tab was closed.</summary>
    public event EventHandler<ShellDocument>? DocumentClosed;

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

        // Dock focuses whatever it makes active (InitActiveDockable calls SetFocusedDockable): without this, a tool window
        // that shows itself (Output, Error List, Notifications) would take the active frame from the group the user is in.
        var focused = focus ? null : _root.FocusedDockable;
        if (IsPinned(tool))
        {
            Factory.PreviewPinnedDockable(tool);
        }
        else if (!AllDockables().Contains(tool))
        {
            Factory.AddDockable(GroupFor(tool), tool);
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
        else if (focused is { Owner: IDock focusedOwner } && !ReferenceEquals(focused, tool) && !ReferenceEquals(_root.FocusedDockable, focused))
        {
            Factory.SetFocusedDockable(focusedOwner, focused);
        }

        return tool;
    }

    public bool IsVisible(string toolWindowId) => OpenToolWindows.Any(t => t.Id == toolWindowId);

    /// <summary>A tool window <see cref="ShowExtraToolWindow"/> opened was closed: it is gone for good.</summary>
    public event EventHandler<ToolWindowDockable>? ExtraToolWindowClosed;

    /// <summary>
    /// Opens another instance of a tool window (a References window Keep Results left, 0.6): <paramref name="descriptor"/>
    /// isn't registered, so the View menu doesn't list it and a restart doesn't restore it. It opens as a tab beside
    /// <paramref name="besideId"/>'s window when that is open, else where the descriptor's location says.
    /// </summary>
    public ToolWindowDockable ShowExtraToolWindow(ToolWindowDescriptor descriptor, object content, string besideId, bool focus = false)
    {
        if (!_tools.TryGetValue(descriptor.Id, out var tool))
        {
            tool = new ToolWindowDockable(descriptor, content);
            _tools[descriptor.Id] = tool;
        }

        var focused = focus ? null : _root.FocusedDockable;
        if (!AllDockables().Contains(tool))
        {
            var beside = OpenToolWindows.FirstOrDefault(t => t.Id == besideId && !IsPinned(t));
            Factory.AddDockable(beside?.Owner is IToolDock group && AllDockables().Contains(group) ? group : GroupFor(tool), tool);
        }

        Factory.SetActiveDockable(tool);
        if (focus && tool.Owner is IDock owner)
        {
            Factory.SetFocusedDockable(owner, tool);
            FocusRequested?.Invoke(this, tool);
        }
        else if (focused is { Owner: IDock focusedOwner } && !ReferenceEquals(focused, tool) && !ReferenceEquals(_root.FocusedDockable, focused))
        {
            Factory.SetFocusedDockable(focusedOwner, focused);
        }

        return tool;
    }

    /// <summary>The tool window <paramref name="id"/>'s tab, open or not (registered or extra), or null.</summary>
    public ToolWindowDockable? ToolWindow(string id) => _tools.GetValueOrDefault(id);

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
            // Its content when Dock first presents the tab, not now (M56): a restored layout made every saved tool window's
            // before the first frame, the tabs behind others' and the auto-hidden ones' too.
            tool = new ToolWindowDockable(descriptor, () => _createContent(descriptor));
            _tools[descriptor.Id] = tool;
        }

        return tool;
    }

    private ToolWindowDockable? ResolveTool(string id) => _registry.Find(id) is { } descriptor ? GetOrCreateTool(descriptor) : null;

    private bool IsPinned(IDockable dockable) =>
        new[] { _root.LeftPinnedDockables, _root.RightPinnedDockables, _root.TopPinnedDockables, _root.BottomPinnedDockables }
            .Any(list => list?.Contains(dockable) == true);

    /// <summary>
    /// The group a tool window that isn't in the layout opens in, as a tab: the group it was in when it closed, if that
    /// group is still open (Dock leaves a closed dockable's owner set); else the group of its default location.
    /// </summary>
    private IDock GroupFor(ToolWindowDockable tool) =>
        tool.Owner is IDock last and (IToolDock or IDocumentDock) && AllDockables().Contains(last) ? last : EnsurePane(tool.Descriptor.Location);

    /// <summary>
    /// The main window's group for <paramref name="location"/>: the pane with its id; or, once the user's rearranging
    /// has emptied and removed that pane, the group that holds most of the location's tool windows, such as one Dock
    /// made when its tabs were dragged to the window's edge; or else a new pane in its place.
    /// </summary>
    /// <remarks>
    /// Dock's groups have no id of ours, and a floating window's group takes the id of the group it came from, so only
    /// the main window's groups count, and not by id alone. Looking for the id only opened every tool window not yet
    /// shown in a new group stacked above the bottom tool windows the user had moved (the maintainer's report).
    /// </remarks>
    private IToolDock EnsurePane(ToolWindowLocation location)
    {
        var (id, alignment, proportion) = location switch
        {
            ToolWindowLocation.Left => (LeftId, Alignment.Left, 0.22),
            ToolWindowLocation.Right => (RightId, Alignment.Right, 0.22),
            _ => (BottomId, Alignment.Bottom, 0.28),
        };

        var groups = Flatten(_root).OfType<IToolDock>().ToList();
        var existing = groups.FirstOrDefault(g => g.Id == id)
            ?? groups.Select(g => (Group: g, Count: (g.VisibleDockables ?? []).OfType<ToolWindowDockable>().Count(t => t.Descriptor.Location == location)))
                .Where(g => g.Count > 0).OrderByDescending(g => g.Count).Select(g => g.Group).FirstOrDefault();
        if (existing is not null)
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
    /// <remarks>
    /// A child without a proportion (NaN: the default layout's center, a restored node saved without one) holds the
    /// share the others leave, which the dock panel writes into it only when it next arranges. It is scaled with that
    /// share: scaling the known children up to the whole rest and giving the unknown ones more on top made the
    /// proportions add up to more than 1, and the panel's normalizing them squeezed the documents (the default layout's
    /// right pane at 0.78 beside a center at 0.39 when the first left tool window opened).
    /// </remarks>
    private static void MakeRoomFor(IDock dock, IDockable pane)
    {
        var others = dock.VisibleDockables!.Where(d => d != pane && d is not IProportionalDockSplitter).ToList();
        if (others.Count == 0)
        {
            return;
        }

        var known = others.Where(d => !double.IsNaN(d.Proportion)).Sum(d => d.Proportion);
        var unknown = others.Count(d => double.IsNaN(d.Proportion));
        var unassigned = unknown == 0 ? 0 : known < 1 ? (1 - known) / unknown : 1.0 / others.Count;
        var total = known + (unassigned * unknown);
        var rest = 1 - pane.Proportion;
        foreach (var other in others)
        {
            var share = double.IsNaN(other.Proportion) ? unassigned : other.Proportion;
            other.Proportion = total > 0 ? share / total * rest : rest / others.Count;
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

    /// <summary>
    /// Shows <paramref name="document"/> (or the open document with its id). A <paramref name="preview"/> document
    /// takes the place of the group's current preview tab, unless that one was modified.
    /// </summary>
    /// <param name="activate">
    /// False to add the tab behind the active one (a session's documents being restored): its view is made when it's
    /// first shown, not now.
    /// </param>
    public void OpenDocument(ShellDocument document, bool preview = false, bool focus = false, bool activate = true)
    {
        var existing = Documents.FirstOrDefault(d => d.Id == document.Id);
        if (existing is null)
        {
            // The preview tab sits at the right end of its group (M34d): a new preview takes its place there, and other
            // documents open to its left.
            var dock = ActiveDocumentDock();
            var current = dock.VisibleDockables?.OfType<ShellDocument>().FirstOrDefault(d => d.IsPreview && !d.IsModified);
            if (preview && current is not null)
            {
                Factory.AddDockable(dock, document);
                Factory.CloseDockable(current);
            }
            else if (!preview && current is not null && dock.VisibleDockables is { } tabs)
            {
                Factory.InsertDockable(dock, document, tabs.IndexOf(current));
            }
            else
            {
                Factory.AddDockable(dock, document);
            }

            document.IsPreview = preview;
            existing = document;
        }
        else if (!preview)
        {
            existing.IsPreview = false;
        }

        if (!activate)
        {
            return;
        }

        if (focus)
        {
            FocusRequested?.Invoke(this, existing);
        }

        Factory.SetActiveDockable(existing);
        if (existing.Owner is IDock owner)
        {
            Factory.SetFocusedDockable(owner, existing);
        }

        UpdateActiveDocument();
    }

    /// <summary>
    /// Pins or unpins <paramref name="document"/> (M34d). Pinned tabs stay at the left of their group in the order they
    /// were pinned; an unpinned tab moves to the first place after them. Pinning keeps a preview tab open.
    /// </summary>
    public void SetPinned(ShellDocument document, bool pinned)
    {
        document.IsPinned = pinned;
        if (pinned)
        {
            document.IsPreview = false;
        }

        if (document.Owner is IDock { VisibleDockables: { } tabs } dock)
        {
            var target = tabs.OfType<ShellDocument>().Count(d => d.IsPinned && d != document);
            if (target < tabs.Count && tabs[target] != document)
            {
                Factory.MoveDockable(dock, document, tabs[target]);
            }
        }
    }

    /// <summary>Adds <paramref name="view"/> next to the active document in a new group, to the right or below.</summary>
    public void OpenInNewGroup(ShellDocument view, bool below)
    {
        Factory.AddDockable(ActiveDocumentDock(), view);
        if (below)
        {
            Factory.NewVerticalDocumentDock(view);
        }
        else
        {
            Factory.NewHorizontalDocumentDock(view);
        }

        OpenDocument(view);
    }

    /// <summary>The document dock that has the active document (or the main one).</summary>
    private IDocumentDock ActiveDocumentDock() =>
        _lastDocumentDock is { Owner: not null } last && AllDockables().Contains(last) ? last : DocumentsDock();

    public T? FindDocument<T>(string id)
        where T : ShellDocument => Documents.OfType<T>().FirstOrDefault(d => d.Id == id);

    public void CloseDocument(ShellDocument document) => Factory.CloseDockable(document);

    /// <summary>Deferrals open (<see cref="DeferActiveDocument"/>): the active document is worked out after them.</summary>
    private int _deferred;

    /// <summary>
    /// Until disposed, tabs that open, close or become active don't change <see cref="ActiveDocument"/>: it's worked out
    /// once, after (M56). Tabs opened or closed one by one each became the active one in turn (a group's first tab, the
    /// neighbor of a closed active one), and each activation ran every reaction to a tab switch.
    /// </summary>
    public IDisposable DeferActiveDocument()
    {
        _deferred++;
        return new Deferral(this);
    }

    /// <summary>
    /// Closes <paramref name="documents"/> as one change (M56): each still closes as a tab does (one that has unsaved
    /// changes asks, <see cref="DocumentClosed"/> follows each), but no tab becomes active on the way, and the active
    /// document is worked out once, after the last (Close All, closing a solution).
    /// </summary>
    public void CloseDocuments(IEnumerable<ShellDocument> documents)
    {
        // Each group's active tab last, and the active document after everything: Dock makes a group's neighbor active
        // when its active tab closes.
        var closing = documents.OrderBy(d => d == _activeDocument ? 2 : d.Owner is IDock { ActiveDockable: var active } && active == d ? 1 : 0).ToList();
        using (DeferActiveDocument())
        {
            foreach (var document in closing)
            {
                Factory.CloseDockable(document);
            }
        }
    }

    private sealed class Deferral(LayoutManager layout) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (--layout._deferred == 0)
            {
                layout.UpdateActiveDocument();
            }
        }
    }

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

    private void OnDockableClosed(object? sender, DockableClosedEventArgs e)
    {
        if (e.Dockable is ShellDocument document)
        {
            DocumentClosed?.Invoke(this, document);
        }
        else if (e.Dockable is ToolWindowDockable tool && _registry.Find(tool.Id) is null && _tools.Remove(tool.Id))
        {
            ExtraToolWindowClosed?.Invoke(this, tool);
        }

        UpdateActiveDocument();
    }

    private void UpdateActiveDocument(IDockable? changed = null)
    {
        // Dock raises events while InitLayout runs, before the new root is assigned; and while documents close together.
        if (_root is null || _deferred > 0)
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
