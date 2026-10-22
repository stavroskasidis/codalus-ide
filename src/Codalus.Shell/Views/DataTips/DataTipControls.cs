using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views.DataTips;

/// <summary>
/// The sizes of a data tip (D44, docs/design/controls/DataTip.dc.html): rows 20 px, the header 22 px, a child tip 24 px
/// right of its parent with its top on the expanded row's bottom edge, the name 42 px in (4 + the 16 px chevron + 2, then
/// the 16 px icon + 4), values trimmed at 400 px, up to 15 rows before a tip scrolls.
/// </summary>
public static class DataTipMetrics
{
    public const double Row = 20;
    public const double Header = 22;
    public const double Indent = 24;
    public const double NameX = 42;
    public const double MaxValueWidth = 400;
    public const int MaxRows = 15;

    /// <summary>A tip's border and the 2 px above its first row.</summary>
    public const double RowsTop = 1 + 2;

    /// <summary>How long the pointer rests on a chevron before its child tip opens.</summary>
    public static readonly TimeSpan HoverOpen = TimeSpan.FromMilliseconds(400);
}

/// <summary>What a tip's header and rows ask of the tips they belong to (the editor's hover, or a pinned tip).</summary>
public interface IDataTipHost
{
    /// <summary>Opens <paramref name="node"/>'s child tip under <paramref name="anchor"/> (a row of <paramref name="owner"/>, or the header when it's null), or closes it.</summary>
    void SetOpen(DataTipPanel? owner, Control anchor, VariableNode node, bool open);

    /// <summary>Whether <paramref name="node"/>'s child tip is open.</summary>
    bool IsOpen(VariableNode node);

    /// <summary>The pointer is on another row of <paramref name="owner"/>: the child tip of the row it left closes.</summary>
    void PointerOnRow(DataTipPanel owner, DataTipRow row);

    /// <summary>Edits a value (F2, Enter, a double-click on it, the menu's Edit Value); null when it can't change.</summary>
    Action? EditAction(VariableNode node);

    /// <summary>Pins a value to its line; null where it can't be pinned.</summary>
    Action? PinAction(VariableNode node);

    /// <summary>The wheel over a tip that doesn't scroll: the editor scrolls, and the tips close.</summary>
    void Wheel(PointerWheelEventArgs e);

    /// <summary>A gesture on the header's name, for the code under it: a double-click, or Ctrl+click.</summary>
    void HeaderGesture(PointerEventArgs e, bool doubleClick);

    /// <summary>Closes every tip (Esc).</summary>
    void CloseAll();

    /// <summary>Focus moves to the tip before <paramref name="owner"/> (Left on a row with nothing open).</summary>
    void FocusParent(DataTipPanel owner);
}

/// <summary>The View drop-down of a row or a header: the magnifier opens the first visualizer, the chevron lists them all.</summary>
public sealed class ViewButton : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    public ViewButton(VariableNode node)
    {
        var choices = VariableTreeSupport.VisualizerChoices(node);
        Choices = choices;
        Classes.Add("dataTipView");
        IsVisible = choices.Count > 0;
        if (choices.Count == 0)
        {
            return;
        }

        var main = new Button
        {
            Classes = { "dataTipViewMain" },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                Children =
                {
                    new Icon { Kind = "search", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Localizer.Get("dataTip.view", "View"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        main.Click += (_, e) =>
        {
            e.Handled = true;
            choices[0].Open();
        };
        ToolTip.SetTip(main, choices[0].Title);
        AutomationProperties.SetName(main, Localizer.Format("dataTip.viewWith", "View: {0}", choices[0].Title));
        var more = new Button
        {
            Classes = { "dataTipViewMore" },
            Content = new Icon { Kind = "chevron-down", Width = 10, Height = 10 },
        };
        AutomationProperties.SetName(more, Localizer.Get("dataTip.visualizers", "Visualizers"));
        more.Click += (_, e) =>
        {
            e.Handled = true;
            Menu().Open(more);
        };
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { main, more } };
    }

    internal IReadOnlyList<VariableTreeSupport.VisualizerChoice> Choices { get; }

    /// <summary>The visualizers, the first checked, the plugins' under a Plugins heading.</summary>
    public ContextMenu Menu()
    {
        var items = new List<Control>();
        var plugins = false;
        for (var i = 0; i < Choices.Count; i++)
        {
            var choice = Choices[i];
            if (choice.IsPlugin && !plugins)
            {
                plugins = true;
                if (items.Count > 0)
                {
                    items.Add(new Separator());
                }

                items.Add(MenuEntry.Caption(Localizer.Get("dataTip.plugins", "Plugins")));
            }

            var item = MenuEntry.Create(choice.Title, i == 0 ? "check" : null, click: choice.Open);
            items.Add(item);
        }

        return new ContextMenu { ItemsSource = items };
    }
}

/// <summary>A tip's row: chevron, icon, name, a separator, the View slot and the value (no type column: it's the name's tooltip).</summary>
public sealed class DataTipRow : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    private readonly IDataTipHost _host;
    private readonly Border _chevron;
    private readonly Icon _chevronIcon;
    private readonly TextBlock _value;
    private DispatcherTimer? _hoverTimer;

    public DataTipRow(DataTipPanel panel, VariableNode node, IDataTipHost host)
    {
        Panel = panel;
        Node = node;
        _host = host;
        Classes.Add("dataTipRow");
        Height = DataTipMetrics.Row;
        Focusable = false;
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(4, GridUnitType.Pixel),
                new ColumnDefinition(16, GridUnitType.Pixel),
                new ColumnDefinition(2, GridUnitType.Pixel),
                new ColumnDefinition(16, GridUnitType.Pixel),
                new ColumnDefinition(4, GridUnitType.Pixel),
                new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "N" },
                new ColumnDefinition(1, GridUnitType.Pixel),
                new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "V" },
                new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "A" },
                new ColumnDefinition(4, GridUnitType.Pixel),
            },
        };
        _chevronIcon = new Icon { Width = 16, Height = 16 };
        _chevron = new Border { Classes = { "dataTipChevron" }, Child = _chevronIcon, Background = Brushes.Transparent, IsVisible = node.HasChildren };
        Grid.SetColumn(_chevron, 1);
        AutomationProperties.SetName(_chevron, Localizer.Format("dataTip.expand", "Expand {0}", node.Name));
        _chevron.PointerEntered += (_, _) => StartHoverOpen();
        _chevron.PointerExited += (_, _) => StopHoverOpen();
        _chevron.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                StopHoverOpen();
                _host.SetOpen(Panel, this, Node, !_host.IsOpen(Node));
            }
        };
        grid.Children.Add(_chevron);

        var icon = new Icon { Kind = node.Icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, IsVisible = !node.IsPlaceholder };
        Grid.SetColumn(icon, 3);
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = node.IsPlaceholder ? node.Value : node.Name,
            Classes = { "dataTipName" },
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.Classes.Set("muted", node.IsPlaceholder);
        if (node.NameTip is { } tip)
        {
            ToolTip.SetTip(name, tip);
        }

        Grid.SetColumn(name, 5);
        grid.Children.Add(name);

        var separator = new Border { Classes = { "dataTipSeparator" } };
        Grid.SetColumn(separator, 6);
        grid.Children.Add(separator);

        if (!node.IsPlaceholder)
        {
            var view = new ViewButton(node) { Margin = new Thickness(4, 0, 0, 0) };
            Grid.SetColumn(view, 7);
            grid.Children.Add(view);
        }

        _value = new TextBlock
        {
            Classes = { "dataTipValue" },
            Text = node.IsPlaceholder ? string.Empty : node.Value,
            MaxWidth = DataTipMetrics.MaxValueWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _value.Classes.Set("error", node.IsError);
        ToolTip.SetTip(_value, node.IsPlaceholder || node.Value.Length == 0 ? null : node.Value);
        _value.DoubleTapped += (_, e) =>
        {
            if (_host.EditAction(Node) is { } edit)
            {
                e.Handled = true;
                edit();
            }
        };
        Grid.SetColumn(_value, 8);
        grid.Children.Add(_value);
        Child = grid;
        AutomationProperties.SetName(this, Accessible(node));
        PointerEntered += (_, _) => _host.PointerOnRow(Panel, this);
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !node.IsPlaceholder)
            {
                Panel.Select(this);
            }
        };
        ContextRequested += (_, e) =>
        {
            if (!node.IsPlaceholder && !node.IsError)
            {
                e.Handled = true;
                Panel.Select(this);
                new ContextMenu { ItemsSource = VariableTreeSupport.MenuItems(node, _host.EditAction(node), _host.PinAction(node)) }.Open(this);
            }
        };
        Node.PropertyChanged += OnNodeChanged;
        UpdateChevron();
    }

    public DataTipPanel Panel { get; }

    public VariableNode Node { get; }

    /// <summary>The value as shown (tests).</summary>
    public TextBlock ValueBlock => _value;

    /// <summary>Whether this row's child tip is open (its chevron points down and the row stays marked).</summary>
    public bool IsOpen
    {
        get => Classes.Contains("open");
        set
        {
            Classes.Set("open", value);
            UpdateChevron();
        }
    }

    private static string Accessible(VariableNode node) =>
        node.IsPlaceholder ? node.Value : node.HasType ? $"{node.Name}, {node.Value}, {node.Type}" : $"{node.Name}, {node.Value}";

    private void OnNodeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VariableNode.Value))
        {
            _value.Text = Node.Value;
            ToolTip.SetTip(_value, Node.Value);
            AutomationProperties.SetName(this, Accessible(Node));
        }
        else if (e.PropertyName is nameof(VariableNode.HasChildren))
        {
            _chevron.IsVisible = Node.HasChildren;
        }
    }

    private void UpdateChevron() => _chevronIcon.Kind = IsOpen ? "chevron-down" : "chevron-right";

    private void StartHoverOpen()
    {
        if (!Node.HasChildren || _host.IsOpen(Node))
        {
            return;
        }

        _hoverTimer ??= new DispatcherTimer(DataTipMetrics.HoverOpen, DispatcherPriority.Input, (_, _) =>
        {
            StopHoverOpen();
            if (IsPointerOver)
            {
                _host.SetOpen(Panel, this, Node, true);
            }
        });
        _hoverTimer.Start();
    }

    private void StopHoverOpen() => _hoverTimer?.Stop();

    /// <summary>The hover-open timer's state (tests).</summary>
    internal bool IsHoverOpenPending => _hoverTimer?.IsEnabled == true;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopHoverOpen();
    }

    /// <summary>The hover-open timer, run now (tests: the pointer rested on the chevron).</summary>
    internal void HoverOpenNow()
    {
        StopHoverOpen();
        _host.SetOpen(Panel, this, Node, true);
    }
}

/// <summary>
/// One tip of the cascade: a node's members as rows, up to 15 before it scrolls, the name column as wide as its widest
/// name. Up, Down, Right (opens the child tip), Left (closes it, or goes back), F2 and Enter (edit), Ctrl+C and Esc.
/// </summary>
public sealed class DataTipPanel : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    private readonly IDataTipHost _host;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private DataTipRow? _selected;

    public DataTipPanel(VariableNode parent, IDataTipHost host, int level)
    {
        Parent = parent;
        _host = host;
        Level = level;
        Classes.Add("dataTip");
        Focusable = true;
        Grid.SetIsSharedSizeScope(_rows, true);
        _scroll = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = DataTipMetrics.MaxRows * DataTipMetrics.Row,
        };
        _scroll.ScrollChanged += (_, _) => ScrollChanged?.Invoke(this, EventArgs.Empty);
        Child = _scroll;
        AutomationProperties.SetName(this, Localizer.Format("dataTip.membersOf", "Members of {0}", parent.Name));
        parent.Children.CollectionChanged += OnChildrenChanged;
        Rebuild();
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        KeyDown += OnKeyDown;
    }

    public new VariableNode Parent { get; }

    /// <summary>The tip's place in the cascade: 1 under the header, 2 under one of its rows…</summary>
    public int Level { get; }

    public IReadOnlyList<DataTipRow> Rows => [.. _rows.Children.OfType<DataTipRow>()];

    public DataTipRow? SelectedRow => _selected;

    /// <summary>How wide a value may be: 400 px, less near the text view's right edge (the tip moved left and still didn't fit).</summary>
    public double ValueMaxWidth
    {
        get;
        set
        {
            field = value;
            foreach (var row in Rows)
            {
                row.ValueBlock.MaxWidth = value;
            }
        }
    } = DataTipMetrics.MaxValueWidth;

    /// <summary>The rows' scroll position.</summary>
    public double ScrollOffset => _scroll.Offset.Y;

    public event EventHandler? ScrollChanged;

    /// <summary>The rows changed (the members loaded): the tip's size, and its children's places, change.</summary>
    public event EventHandler? RowsChanged;

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var selected = _selected?.Node;
        _rows.Children.Clear();
        _selected = null;
        foreach (var child in Parent.Children)
        {
            _rows.Children.Add(new DataTipRow(this, child, _host) { ValueBlock = { MaxWidth = ValueMaxWidth } });
        }

        if (selected is not null && Rows.FirstOrDefault(r => ReferenceEquals(r.Node, selected)) is { } again)
        {
            Select(again);
        }

        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The top of <paramref name="row"/> in the tip's own coordinates (its border and padding included, its scroll applied).</summary>
    public double RowTop(DataTipRow row) => DataTipMetrics.RowsTop + (Rows.ToList().IndexOf(row) * DataTipMetrics.Row) - ScrollOffset;

    public void Select(DataTipRow? row)
    {
        _selected?.Classes.Remove("selected");
        _selected = row;
        if (row is not null)
        {
            row.Classes.Add("selected");
            row.BringIntoView();
        }
    }

    /// <summary>Takes the keyboard focus with a row selected (the first, unless one is).</summary>
    public void FocusRows()
    {
        Select(_selected ?? Rows.FirstOrDefault(r => !r.Node.IsPlaceholder));
        Focus(NavigationMethod.Directional);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // A tip with more rows than it shows scrolls; any other passes the wheel to the editor.
        if (_scroll.Extent.Height <= _scroll.Viewport.Height + 0.5)
        {
            e.Handled = true;
            _host.Wheel(e);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var rows = Rows.Where(r => !r.Node.IsPlaceholder).ToList();
        var index = _selected is null ? -1 : rows.IndexOf(_selected);
        switch (e.Key)
        {
            case Key.Down when rows.Count > 0:
                Select(rows[Math.Min(rows.Count - 1, index + 1)]);
                break;
            case Key.Up when rows.Count > 0:
                Select(rows[Math.Max(0, index - 1)]);
                break;
            case Key.Right when _selected is { Node.HasChildren: true } row:
                _host.SetOpen(this, row, row.Node, true);
                break;
            case Key.Left when _selected is { } row && _host.IsOpen(row.Node):
                _host.SetOpen(this, row, row.Node, false);
                break;
            case Key.Left:
                _host.FocusParent(this);
                break;
            case Key.F2 or Key.Enter when _selected is { } row && _host.EditAction(row.Node) is { } edit:
                edit();
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && _selected is { } row:
                _ = VariableActions.CopyText?.Invoke(row.Node.Value);
                break;
            case Key.F10 when e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selected is { } row:
                new ContextMenu { ItemsSource = VariableTreeSupport.MenuItems(row.Node, _host.EditAction(row.Node), _host.PinAction(row.Node)) }.Open(row);
                break;
            case Key.Escape:
                _host.CloseAll();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Stops following the node (the tip closed).</summary>
    public void Detach() => Parent.Children.CollectionChanged -= OnChildrenChanged;
}

/// <summary>
/// The header over the hovered expression: chevron, icon, the name over the code's, a separator, the View slot and the
/// value in a box. Its name stands for the code's word: a double-click selects it, Ctrl+click goes to its definition and
/// the wheel scrolls the editor, each passed on to the text view at the same point.
/// </summary>
public sealed class DataTipHeader : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    private readonly IDataTipHost _host;
    private readonly Icon _chevronIcon;
    private readonly Border _chevron;
    private readonly TextBlock _value;

    public DataTipHeader(VariableNode node, IDataTipHost host)
    {
        Node = node;
        _host = host;
        Classes.Add("dataTipHeader");
        Height = DataTipMetrics.Header;
        Focusable = true;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        _chevronIcon = new Icon { Kind = "chevron-right", Width = 16, Height = 16 };
        _chevron = new Border
        {
            Classes = { "dataTipChevron" },
            Width = 16,
            Margin = new Thickness(3, 0, 2, 0),
            Background = Brushes.Transparent,
            Child = _chevronIcon,
            Opacity = node.HasChildren ? 1 : 0,
            IsHitTestVisible = node.HasChildren,
        };
        AutomationProperties.SetName(_chevron, Localizer.Format("dataTip.expand", "Expand {0}", node.Name));
        DispatcherTimer? timer = null;
        _chevron.PointerEntered += (_, _) =>
        {
            if (Node.HasChildren && !_host.IsOpen(Node))
            {
                timer ??= new DispatcherTimer(DataTipMetrics.HoverOpen, DispatcherPriority.Input, (_, _) =>
                {
                    timer!.Stop();
                    if (_chevron.IsPointerOver)
                    {
                        _host.SetOpen(null, this, Node, true);
                    }
                });
                timer.Start();
            }
        };
        _chevron.PointerExited += (_, _) => timer?.Stop();
        _chevron.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && Node.HasChildren)
            {
                e.Handled = true;
                timer?.Stop();
                _host.SetOpen(null, this, Node, !_host.IsOpen(Node));
            }
        };
        panel.Children.Add(_chevron);
        panel.Children.Add(new Icon { Kind = node.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        NameBlock = new TextBlock { Text = node.Name, Classes = { "dataTipName" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Background = Brushes.Transparent };
        if (node.NameTip is { } tip)
        {
            ToolTip.SetTip(NameBlock, tip);
        }

        NameBlock.PointerPressed += (_, e) =>
        {
            var properties = e.GetCurrentPoint(NameBlock).Properties;
            if (properties.IsLeftButtonPressed && (e.ClickCount == 2 || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            {
                e.Handled = true;
                _host.HeaderGesture(e, doubleClick: e.ClickCount == 2);
            }
        };
        panel.Children.Add(NameBlock);
        panel.Children.Add(new Border { Classes = { "dataTipSeparator" } });
        View = new ViewButton(node) { Margin = new Thickness(4, 0, 0, 0) };
        panel.Children.Add(View);
        _value = new TextBlock
        {
            Classes = { "dataTipValue" },
            Text = node.Value,
            MaxWidth = DataTipMetrics.MaxValueWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _value.Classes.Set("error", node.IsError);
        ToolTip.SetTip(_value, node.Value);
        var box = new Border { Classes = { "dataTipValueBox" }, Child = _value, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        box.DoubleTapped += (_, e) =>
        {
            if (_host.EditAction(Node) is { } edit)
            {
                e.Handled = true;
                edit();
            }
        };
        panel.Children.Add(box);
        if (_host.PinAction(node) is { } pin)
        {
            var button = new Button { Classes = { "dataTipPin" }, Content = new Icon { Kind = "pin", Width = 14, Height = 14 }, Margin = new Thickness(0, 0, 2, 0) };
            ToolTip.SetTip(button, Localizer.Get("dataTip.pinToSource", "Pin to source"));
            AutomationProperties.SetName(button, Localizer.Get("dataTip.pinToSource", "Pin to source"));
            button.Click += (_, e) =>
            {
                e.Handled = true;
                pin();
            };
            panel.Children.Add(button);
            PinButton = button;
        }

        Child = panel;
        AutomationProperties.SetName(this, node.HasType ? $"{node.Name}, {node.Value}, {node.Type}" : $"{node.Name}, {node.Value}");
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            e.Handled = true;
            _host.Wheel(e);
        }, RoutingStrategies.Tunnel);
        ContextRequested += (_, e) =>
        {
            e.Handled = true;
            new ContextMenu { ItemsSource = VariableTreeSupport.MenuItems(node, _host.EditAction(node), _host.PinAction(node)) }.Open(this);
        };
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Right or Key.Down when Node.HasChildren:
                    _host.SetOpen(null, this, Node, true);
                    break;
                case Key.F2 or Key.Enter when _host.EditAction(Node) is { } edit:
                    edit();
                    break;
                case Key.Escape:
                    _host.CloseAll();
                    break;
                default:
                    return;
            }

            e.Handled = true;
        };
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(VariableNode.Value))
            {
                _value.Text = Node.Value;
                ToolTip.SetTip(_value, Node.Value);
            }
            else if (e.PropertyName is nameof(VariableNode.HasChildren))
            {
                _chevron.Opacity = Node.HasChildren ? 1 : 0;
                _chevron.IsHitTestVisible = Node.HasChildren;
            }
        };
    }

    public VariableNode Node { get; }

    public TextBlock NameBlock { get; }

    public ViewButton View { get; }

    public Button? PinButton { get; }

    /// <summary>The value as shown (tests).</summary>
    public TextBlock ValueBlock => _value;

    /// <summary>How wide the value may be: 400 px, less near the text view's right edge.</summary>
    public double ValueMaxWidth
    {
        get => _value.MaxWidth;
        set => _value.MaxWidth = value;
    }

    /// <summary>Whether the header's child tip is open (its chevron points down).</summary>
    public bool IsOpen
    {
        get => _chevronIcon.Kind == "chevron-down";
        set => _chevronIcon.Kind = value ? "chevron-down" : "chevron-right";
    }
}
