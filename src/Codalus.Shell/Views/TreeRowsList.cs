using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// A virtualized list of a flattened tree's rows (<see cref="ITreeRow"/>) that a screen reader reads as a tree (M57, the
/// maintainer's "proper peer for all trees"): the Solution Explorer, Git Changes' sections, the Document Outline, the Git
/// Repository tree, a commit's changes and Compare. The list is a <c>Tree</c> and each row a <c>TreeItem</c>
/// (<see cref="TreeRowItemAutomationPeer"/>) with the ExpandCollapse pattern on a folder, its level and its place among its
/// siblings, so a row's name is its name alone (the rows said ", expanded" or ", collapsed" in it, M56). It looks and
/// behaves as the <see cref="ListBox"/> it is: the theme and every <c>ListBoxItem</c> style apply to its rows. Avalonia 12.1
/// passes the role and the expansion to UI Automation, NSAccessibility and AT-SPI, but of the level and the place only
/// AT-SPI's position and size (its <c>level</c> is a heading's). TODO(issue): the level and the place on UI Automation and
/// NSAccessibility once Avalonia's peers carry them; Narrator, VoiceOver and Orca tried on these trees by hand.
/// </summary>
public sealed class TreeRowsList : ListBox
{
    private (int[] Positions, int[] Sizes)? _places; // each row's place among its siblings, from 1, and how many they are
    private bool _hasPeers; // a screen reader asked for a row: only then do the rows carry their place
    private bool _refreshQueued;

    public TreeRowsList() => ItemsView.CollectionChanged += OnRowsChanged;

    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <summary>
    /// Expands or collapses a row as the list's keys and its chevron do (the view's own command): a screen reader's Expand
    /// and Collapse come here. Null leaves the rows' state to be read only.
    /// </summary>
    public Action<ITreeRow, bool>? ExpandRow { get; set; }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new TreeRowItem();

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) =>
        NeedsContainer<TreeRowItem>(item, out recycleKey);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (_hasPeers)
        {
            QueueRefresh();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TreeRowsListAutomationPeer(this);

    /// <summary>
    /// The row at <paramref name="index"/>: its level (from 1), its place among its siblings (from 1) and how many they are;
    /// zeros for no row. One pass over the rows when they've changed, kept until they change again.
    /// </summary>
    public (int Level, int Position, int Size) PlaceOf(int index)
    {
        var rows = ItemsView;
        if (index < 0 || index >= rows.Count || rows[index] is not ITreeRow row)
        {
            return default;
        }

        var (positions, sizes) = _places ??= Places(rows);
        return (row.Depth + 1, positions[index], sizes[index]);
    }

    /// <summary>A row's container got its peer: from now on the rows in view carry their place (AT-SPI reads it there).</summary>
    internal void PeerCreated()
    {
        _hasPeers = true;
        QueueRefresh();
    }

    /// <summary>
    /// The siblings of a row are the rows of its depth between its parent (the row above it of a smaller depth) and the next
    /// row of a smaller depth: a stack of the groups open at each depth finds them all in one pass.
    /// </summary>
    private static (int[] Positions, int[] Sizes) Places(ItemsSourceView rows)
    {
        var positions = new int[rows.Count];
        var groups = new int[rows.Count];
        var counts = new List<int>();
        var open = new Stack<(int Depth, int Group)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var depth = (rows[i] as ITreeRow)?.Depth ?? 0;
            while (open.Count > 0 && open.Peek().Depth > depth)
            {
                open.Pop();
            }

            if (open.Count == 0 || open.Peek().Depth < depth)
            {
                open.Push((depth, counts.Count));
                counts.Add(0);
            }

            var group = open.Peek().Group;
            positions[i] = ++counts[group];
            groups[i] = group;
        }

        for (var i = 0; i < groups.Length; i++)
        {
            groups[i] = counts[groups[i]];
        }

        return (positions, groups);
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A row added or removed changes its siblings' places, whose containers aren't prepared again.
        _places = null;
        if (_hasPeers)
        {
            QueueRefresh();
        }
    }

    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Dispatcher.UIThread.Post(RefreshPlaces, DispatcherPriority.Background);
    }

    /// <summary>
    /// Gives each row in view that a screen reader knows its place (<see cref="AutomationProperties.PositionInSetProperty"/>,
    /// <see cref="AutomationProperties.SizeOfSetProperty"/>), after the rows' changes: Avalonia 12's AT-SPI reads them from
    /// the control, not from its peer.
    /// </summary>
    private void RefreshPlaces()
    {
        _refreshQueued = false;
        foreach (var container in GetRealizedContainers())
        {
            if (container is TreeRowItem item && ControlAutomationPeer.FromElement(item) is not null)
            {
                var (_, position, size) = PlaceOf(IndexFromContainer(item));
                AutomationProperties.SetPositionInSet(item, position);
                AutomationProperties.SetSizeOfSet(item, size);
            }
        }
    }
}

/// <summary>A row of a <see cref="TreeRowsList"/>: a <see cref="ListBoxItem"/> whose automation peer is a tree item's.</summary>
public sealed class TreeRowItem : ListBoxItem
{
    protected override Type StyleKeyOverride => typeof(ListBoxItem);

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        var peer = new TreeRowItemAutomationPeer(this);
        (ItemsControl.ItemsControlFromItemContainer(this) as TreeRowsList)?.PeerCreated();
        return peer;
    }
}

/// <summary>The list of a tree's rows, as a screen reader sees it: a tree (selection and scrolling as a list box's).</summary>
public sealed class TreeRowsListAutomationPeer(TreeRowsList owner) : ListBoxAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;
}

/// <summary>
/// A tree item for a row of a <see cref="TreeRowsList"/> (M57). Its name is the row's (<c>AutomationProperties.Name</c>);
/// a folder has the ExpandCollapse pattern, whose Expand and Collapse are the list's <see cref="TreeRowsList.ExpandRow"/>,
/// and whose state follows the row (a file has none: Avalonia's AT-SPI calls anything with the pattern expandable). Its
/// <see cref="Level"/>, <see cref="PositionInSet"/> and <see cref="SizeOfSet"/> come from the flattened rows; the container
/// is recycled as the list scrolls, so it reads the row it shows now.
/// </summary>
public sealed class TreeRowItemAutomationPeer : ListItemAutomationPeer, IExpandCollapseProvider
{
    private INotifyPropertyChanged? _watched;
    private ExpandCollapseState _reported;

    public TreeRowItemAutomationPeer(TreeRowItem owner)
        : base(owner)
    {
        _reported = StateOf(Row);
        Watch();
        owner.DataContextChanged += (_, _) => Watch();
    }

    public ExpandCollapseState ExpandCollapseState => StateOf(Row);

    public bool ShowsMenu => false;

    /// <summary>The row's level in the tree, from 1 (a root).</summary>
    public int Level => List?.PlaceOf(Index).Level ?? 0;

    /// <summary>The row's place among its siblings (the rows of its parent), from 1.</summary>
    public int PositionInSet => List?.PlaceOf(Index).Position ?? 0;

    /// <summary>How many siblings the row has, itself included.</summary>
    public int SizeOfSet => List?.PlaceOf(Index).Size ?? 0;

    private ITreeRow? Row => Owner.DataContext as ITreeRow;

    private TreeRowsList? List => ItemsControl.ItemsControlFromItemContainer(Owner) as TreeRowsList;

    private int Index => List?.IndexFromContainer(Owner) ?? -1;

    public void Expand() => SetExpanded(true);

    public void Collapse() => SetExpanded(false);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;

    protected override object? GetProviderCore(Type providerType) =>
        providerType == typeof(IExpandCollapseProvider) && Row is not { HasChildren: true } ? null : base.GetProviderCore(providerType);

    private static ExpandCollapseState StateOf(ITreeRow? row) =>
        row is not { HasChildren: true } ? ExpandCollapseState.LeafNode
        : row.IsExpanded ? ExpandCollapseState.Expanded
        : ExpandCollapseState.Collapsed;

    private void SetExpanded(bool expanded)
    {
        EnsureEnabled();
        if (Row is { HasChildren: true } row && row.IsExpanded != expanded)
        {
            List?.ExpandRow?.Invoke(row, expanded);
        }
    }

    /// <summary>Follows the row the container shows: its expansion is announced as it changes, whichever way it changed.</summary>
    private void Watch()
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnRowChanged;
        }

        _watched = Owner.DataContext as INotifyPropertyChanged;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnRowChanged;
        }

        Report();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ITreeRow.IsExpanded) or nameof(ITreeRow.HasChildren) or null or "")
        {
            Report();
        }
    }

    private void Report()
    {
        var state = StateOf(Row);
        if (state != _reported)
        {
            var old = _reported;
            _reported = state;
            RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, old, state);
        }
    }
}
