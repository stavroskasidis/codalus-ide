using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views.DataTips;

/// <summary>The Name and Value columns' widths, shared by a variable window's header and its rows.</summary>
public sealed class VariableColumns
{
    public double Name { get; private set; } = 200;

    public double Value { get; private set; } = 260;

    public event EventHandler? Changed;

    public void Set(double name, double value)
    {
        if (Math.Abs(name - Name) < 0.5 && Math.Abs(value - Value) < 0.5)
        {
            return;
        }

        (Name, Value) = (Math.Max(40, name), Math.Max(40, value));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>What a variable window does with its values: set one typed in place, say why one can't change, remove one (Watch).</summary>
public sealed class VariableRowActions : IDataTipHost
{
    public required Func<VariableNode, string, Task<string?>> SetValue { get; init; }

    public required Func<VariableNode, string?> Reason { get; init; }

    /// <summary>Delete on a row (Watch's own expressions); null where nothing is removed.</summary>
    public Action<VariableNode>? Remove { get; init; }

    public string? ReadOnlyReason(VariableNode node) => Reason(node);

    public Task<string?> SetValueAsync(VariableNode node, string value) => SetValue(node, value);

    public IReadOnlyList<Control> MenuItems(VariableNode node, Action? edit) => VariableTreeSupport.MenuItems(node, edit);

    void IDataTipHost.SetOpen(DataTipPanel? owner, Control anchor, VariableNode node, bool open) => node.IsExpanded = open;

    bool IDataTipHost.IsOpen(VariableNode node) => node.IsExpanded;

    void IDataTipHost.PointerOnRow(DataTipPanel owner, DataTipRow row)
    {
    }

    Action? IDataTipHost.PinAction(VariableNode node) => null;

    void IDataTipHost.Wheel(PointerWheelEventArgs e)
    {
    }

    void IDataTipHost.HeaderGesture(PointerEventArgs e, bool doubleClick)
    {
    }

    void IDataTipHost.CloseAll()
    {
    }

    void IDataTipHost.FocusParent(DataTipPanel owner)
    {
    }
}

/// <summary>
/// Locals, Autos, Watch and QuickWatch (D44, decision 9, C1, C2): one flat list of rows with each level's indent inside
/// the name cell, so Name, Value and Type stay aligned in columns; the data tip's icons, groups, collection views, View
/// button and in-place editing; changed values in red; expansion kept by evaluate name when the rows are built again.
/// Right and Left expand and collapse, F2 or a double-click on a value edits it, Ctrl+C copies it.
/// </summary>
public sealed class VariableRowList : DockPanel
{
    private readonly ObservableCollection<VariableRowItem> _rows = [];
    private readonly HashSet<VariableNode> _followed = [];
    private readonly Grid _header;
    private ObservableCollection<VariableNode>? _roots;
    private VariableRowActions? _actions;
    private ExpansionMemory _memory = new();
    private bool _queued;

    public VariableRowList()
    {
        _header = new Grid
        {
            Classes = { "variableRowsHeader" },
            ColumnDefinitions =
            {
                new ColumnDefinition(Columns.Name, GridUnitType.Pixel),
                new ColumnDefinition(4, GridUnitType.Pixel),
                new ColumnDefinition(Columns.Value, GridUnitType.Pixel),
                new ColumnDefinition(4, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star),
            },
        };
        AddHeader(Localizer.Get("variables.name", "Name"), 0);
        AddHeader(Localizer.Get("variables.value", "Value"), 2);
        AddHeader(Localizer.Get("variables.type", "Type"), 4);
        foreach (var column in new[] { 1, 3 })
        {
            var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent, Classes = { "variableRowsSplitter" } };
            Grid.SetColumn(splitter, column);
            _header.Children.Add(splitter);
        }

        _header.LayoutUpdated += (_, _) => Columns.Set(_header.ColumnDefinitions[0].ActualWidth, _header.ColumnDefinitions[2].ActualWidth);
        SetDock(_header, Avalonia.Controls.Dock.Top);
        Children.Add(_header);
        List = new ListBox
        {
            Classes = { "variableRows" },
            ItemsSource = _rows,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<VariableRowItem?>((row, _) => row is null ? new Panel() : new VariableListRow(row, this), supportsRecycling: false),
        };
        List.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Children.Add(List);
    }

    /// <summary>The window's accessible name is the list's (a screen reader lands on the list).</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AutomationProperties.NameProperty && List is not null)
        {
            AutomationProperties.SetName(List, change.GetNewValue<string?>());
        }
    }

    private void AddHeader(string text, int column)
    {
        var block = new TextBlock { Text = text, Classes = { "muted" }, Margin = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(block, column);
        _header.Children.Add(block);
    }

    public ListBox List { get; }

    public VariableColumns Columns { get; } = new();

    /// <summary>The rows as shown, depth first (tests).</summary>
    public IReadOnlyList<VariableRowItem> Rows => _rows;

    internal VariableRowActions? Actions => _actions;

    /// <summary>Shows <paramref name="roots"/> and what's expanded under them; <paramref name="memory"/> keeps the expansion across rebuilds.</summary>
    public void Attach(ObservableCollection<VariableNode> roots, VariableRowActions actions, ExpansionMemory memory)
    {
        if (_roots is not null)
        {
            _roots.CollectionChanged -= OnRootsChanged;
        }

        _roots = roots;
        _actions = actions;
        _memory = memory;
        roots.CollectionChanged += OnRootsChanged;
        Rebuild();
    }

    private void OnRootsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Queue();

    /// <summary>Builds the rows again once the current changes are done (a reload changes many nodes at once).</summary>
    private void Queue()
    {
        if (_queued)
        {
            return;
        }

        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    /// <summary>The rows: every root, and the children of every expanded node, depth first.</summary>
    public void Rebuild()
    {
        var selected = (List.SelectedItem as VariableRowItem)?.Node;
        var rows = new List<VariableRowItem>();
        void Add(VariableNode node, int depth)
        {
            Follow(node);
            rows.Add(new VariableRowItem(node, depth));
            if (!node.IsExpanded && node.HasChildren && _memory.IsExpanded(node))
            {
                node.IsExpanded = true; // open again what was open (its children load, and the rows follow)
            }

            if (node.IsExpanded)
            {
                foreach (var child in node.Children.ToList())
                {
                    Add(child, depth + 1);
                }
            }
        }

        foreach (var root in _roots?.ToList() ?? [])
        {
            Add(root, 0);
        }

        // Stop following the rows that went (an earlier stop's, a collapsed node's children).
        var shown = rows.Select(r => r.Node).ToHashSet();
        foreach (var gone in _followed.Where(n => !shown.Contains(n)).ToList())
        {
            gone.PropertyChanged -= OnNodeChanged;
            gone.Children.CollectionChanged -= OnChildrenChanged;
            _followed.Remove(gone);
        }

        // The focused row's container goes with the rows, and the keyboard with it: a row that had it gets it back, or
        // keys (Delete, F2, the arrows) go nowhere after every evaluation of the watches and every stop (CI runs
        // 39959543059, 39959550864 and 40346124678: a watch's Delete after an edit's evaluation removed nothing).
        var focused = List.IsKeyboardFocusWithin;
        _rows.Clear();
        foreach (var row in rows)
        {
            _rows.Add(row);
        }

        if (selected is not null && rows.FirstOrDefault(r => ReferenceEquals(r.Node, selected)) is { } again)
        {
            List.SelectedItem = again;
        }

        if (focused && !List.IsKeyboardFocusWithin)
        {
            FocusRow();
        }
    }

    private void Follow(VariableNode node)
    {
        if (!_followed.Add(node))
        {
            return;
        }

        node.PropertyChanged += OnNodeChanged;
        node.Children.CollectionChanged += OnChildrenChanged;
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => Queue();

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VariableNode.IsExpanded) && sender is VariableNode node)
        {
            _memory.Set(node, node.IsExpanded);
            Queue();
        }
        else if (e.PropertyName == nameof(VariableNode.HasChildren))
        {
            Queue();
        }
    }

    /// <summary>Selects <paramref name="item"/> (else keeps the selection, else the first row) and gives its row the keyboard.</summary>
    public void FocusRow(VariableRowItem? item = null)
    {
        if (item is not null)
        {
            List.SelectedItem = item;
        }
        else if (List.SelectedIndex < 0 && _rows.Count > 0)
        {
            List.SelectedIndex = 0;
        }

        if (List.SelectedIndex >= 0)
        {
            List.ScrollIntoView(List.SelectedIndex);
            (List.ContainerFromIndex(List.SelectedIndex) ?? (Control)List).Focus();
        }
        else
        {
            List.Focus();
        }
    }

    /// <summary>The row control of a node, when it's realized.</summary>
    public VariableListRow? RowOf(VariableNode node) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(List).OfType<VariableListRow>().FirstOrDefault(r => ReferenceEquals(r.Item.Node, node));

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || List.SelectedItem is not VariableRowItem item)
        {
            return;
        }

        var node = item.Node;
        switch (e.Key)
        {
            case Key.Right when node.HasChildren && !node.IsExpanded:
                node.IsExpanded = true;
                break;
            case Key.Right when node.IsExpanded && _rows.IndexOf(item) + 1 < _rows.Count:
                List.SelectedIndex = _rows.IndexOf(item) + 1;
                break;
            case Key.Left when node.IsExpanded:
                node.IsExpanded = false;
                break;
            case Key.Left when node.Parent is { } parent && _rows.FirstOrDefault(r => ReferenceEquals(r.Node, parent)) is { } up:
                List.SelectedItem = up;
                break;
            case Key.F2 or Key.Enter when RowOf(node) is { } row && row.Editor.Begin():
                break;
            case Key.Delete when _actions?.Remove is { } remove:
                remove(node);
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = VariableActions.CopyText?.Invoke(node.Value);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}

/// <summary>A row of a variable window: a node and how deep it is.</summary>
public sealed record VariableRowItem(VariableNode Node, int Depth);

/// <summary>A variable window's row: the indent, chevron, icon and name; the View button and the value; the type.</summary>
public sealed class VariableListRow : Grid
{
    public const double IndentPerLevel = 16;

    private readonly VariableRowList _list;
    private readonly TextBlock _value;
    private readonly Icon _chevron;
    private readonly Border _expander;
    private readonly Icon _icon;

    public VariableListRow(VariableRowItem item, VariableRowList list)
    {
        Item = item;
        _list = list;
        var node = item.Node;
        Height = 22;
        Background = Brushes.Transparent;
        ColumnDefinitions =
        [
            new ColumnDefinition(list.Columns.Name, GridUnitType.Pixel),
            new ColumnDefinition(4, GridUnitType.Pixel),
            new ColumnDefinition(list.Columns.Value, GridUnitType.Pixel),
            new ColumnDefinition(4, GridUnitType.Pixel),
            new ColumnDefinition(1, GridUnitType.Star),
        ];

        // The name cell: the indent inside it, so the Value and Type columns don't shift (C1).
        _chevron = new Icon { Width = 16, Height = 16 };
        _expander = new Border { Classes = { "dataTipChevron" }, Child = _chevron, Background = Brushes.Transparent, Width = 16 };
        _expander.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_expander).Properties.IsLeftButtonPressed && node.HasChildren)
            {
                e.Handled = true;
                node.IsExpanded = !node.IsExpanded;
            }
        };
        AutomationProperties.SetName(_expander, Localizer.Format("dataTip.expand", "Expand {0}", node.Name));
        NameBlock = new TextBlock { Text = node.IsPlaceholder ? node.Value : node.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 0) };
        NameBlock.Classes.Set("muted", node.IsPlaceholder);
        if (node.NameTip is { } tip)
        {
            ToolTip.SetTip(NameBlock, tip);
        }

        var icon = _icon = DataTipIcons.For(node);
        icon.IsVisible = !node.IsPlaceholder;
        var name = new DockPanel
        {
            Margin = new Thickness(2 + (item.Depth * IndentPerLevel), 0, 0, 0),
            Children = { _expander, icon, NameBlock },
        };
        DockPanel.SetDock(_expander, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
        Children.Add(name);

        // The value cell: the View button and the value, edited in place.
        _value = new TextBlock { Classes = { "dataTipValue" }, Text = node.IsPlaceholder ? string.Empty : node.Value, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Margin = new Thickness(6, 0, 0, 0), ClipToBounds = true };
        if (!node.IsPlaceholder)
        {
            var view = new ViewButton(node) { Margin = new Thickness(0, 0, 4, 0) };
            cell.Children.Add(view);
        }

        cell.Children.Add(_value);
        if (DataTipIcons.Reevaluate(node) is { } reevaluate)
        {
            cell.Children.Add(reevaluate);
        }

        Editor = new ValueEditor(cell, _value, node, list.Actions!);
        cell.DoubleTapped += (_, e) =>
        {
            if (Editor.Begin())
            {
                e.Handled = true;
            }
        };
        SetColumn(cell, 2);
        Children.Add(cell);

        TypeBlock = new TextBlock { Text = node.Type ?? string.Empty, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
        ToolTip.SetTip(TypeBlock, string.IsNullOrEmpty(node.Type) ? null : node.Type);
        SetColumn(TypeBlock, 4);
        Children.Add(TypeBlock);

        AutomationProperties.SetName(this, node.IsPlaceholder ? node.Value : node.AccessibleName);
        ContextRequested += (_, e) =>
        {
            if (!node.IsPlaceholder && !node.IsError && list.Actions is { } actions)
            {
                e.Handled = true;
                new ContextMenu { ItemsSource = actions.MenuItems(node, actions.ReadOnlyReason(node) is null ? () => Editor.Begin() : null) }.Open(this);
            }
        };
        Update();
        node.PropertyChanged += OnNodeChanged;
        list.Columns.Changed += OnColumnsChanged;
    }

    public VariableRowItem Item { get; }

    public TextBlock NameBlock { get; }

    public TextBlock ValueBlock => _value;

    public TextBlock TypeBlock { get; }

    public ValueEditor Editor { get; }

    private void OnColumnsChanged(object? sender, EventArgs e)
    {
        ColumnDefinitions[0].Width = new GridLength(_list.Columns.Name, GridUnitType.Pixel);
        ColumnDefinitions[2].Width = new GridLength(_list.Columns.Value, GridUnitType.Pixel);
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var node = Item.Node;
        _chevron.Kind = node.IsExpanded ? "chevron-down" : "chevron-right";
        _expander.Opacity = node.HasChildren ? 1 : 0;
        _expander.IsHitTestVisible = node.HasChildren;
        if (!node.IsPlaceholder)
        {
            _value.Text = node.Value;
        }

        TypeBlock.Text = node.Type ?? string.Empty;
        _icon.Kind = node.IsError ? "error" : node.Icon; // a watch that doesn't evaluate (C2)
        _value.Classes.Set("error", node.IsError);
        _value.Classes.Set("changed", node.IsChanged && !node.IsError);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Item.Node.PropertyChanged -= OnNodeChanged;
        _list.Columns.Changed -= OnColumnsChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Item.Node.PropertyChanged -= OnNodeChanged;
        Item.Node.PropertyChanged += OnNodeChanged;
        _list.Columns.Changed -= OnColumnsChanged;
        _list.Columns.Changed += OnColumnsChanged;
        OnColumnsChanged(this, EventArgs.Empty);
        Update();
    }
}
