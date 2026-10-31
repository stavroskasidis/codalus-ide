using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    /// <summary>Why a value can't be changed (its tooltip says so); empty when there's nothing to change; null when it can, in place.</summary>
    string? ReadOnlyReason(VariableNode node);

    /// <summary>Sets a value typed in its box; the engine's error, or null.</summary>
    Task<string?> SetValueAsync(VariableNode node, string value);

    /// <summary>Pins a value to its line; null where it can't be pinned.</summary>
    Action? PinAction(VariableNode node);

    /// <summary>A value's menu (Shift+F10, a right-click); <paramref name="edit"/> opens its box, null when it can't change.</summary>
    IReadOnlyList<Control> MenuItems(VariableNode node, Action? edit);

    /// <summary>The wheel over a tip that doesn't scroll: the editor scrolls, and the tips close.</summary>
    void Wheel(PointerWheelEventArgs e);

    /// <summary>A gesture on the header's name, for the code under it: a double-click, or Ctrl+click.</summary>
    void HeaderGesture(PointerEventArgs e, bool doubleClick);

    /// <summary>Closes every tip (Esc).</summary>
    void CloseAll();

    /// <summary>Focus moves to the tip before <paramref name="owner"/> (Left on a row with nothing open).</summary>
    void FocusParent(DataTipPanel owner);
}

/// <summary>A row's icon (with the lock badge on a non-public member of your code) and its Re-evaluate button.</summary>
internal static class DataTipIcons
{
    public static Icon For(VariableNode node)
    {
        var icon = new Icon { Kind = node.Icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Badge = node.HasBadge ? "lock" : null };
        icon.Bind(Icon.BadgeBackgroundProperty, icon.GetResourceObservable("Shell.Overlay"));
        return icon;
    }

    /// <summary>Re-evaluate, for a value netcoredbg couldn't evaluate (its <c>&lt;error&gt;</c>).</summary>
    public static Button? Reevaluate(VariableNode node)
    {
        if (!node.CanReevaluate)
        {
            return null;
        }

        var button = new Button { Classes = { "dataTipPin" }, Content = new Icon { Kind = "refresh", Width = 14, Height = 14 }, Margin = new Thickness(4, 0, 0, 0) };
        ToolTip.SetTip(button, Localizer.Get("dataTip.reevaluate", "Re-evaluate"));
        AutomationProperties.SetName(button, Localizer.Get("dataTip.reevaluate", "Re-evaluate"));
        button.Click += (_, e) =>
        {
            e.Handled = true;
            _ = node.ReevaluateAsync();
        };
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VariableNode.CanReevaluate))
            {
                button.IsVisible = node.CanReevaluate;
            }
        }

        // Followed while shown: a row that went (scrolled out, a tip closed) doesn't stay reachable from its node.
        button.AttachedToVisualTree += (_, _) =>
        {
            node.PropertyChanged -= OnChanged;
            node.PropertyChanged += OnChanged;
            button.IsVisible = node.CanReevaluate;
        };
        button.DetachedFromVisualTree += (_, _) => node.PropertyChanged -= OnChanged;
        return button;
    }
}

/// <summary>
/// A value edited in place (D44, decision 6, A11, A12, A14): a click on it, F2 or Enter swaps it for a box with the value
/// selected; Enter sets it, Esc puts it back, and the engine's error keeps the box open, its edge red and the message
/// under it. A value that can't change opens no box: its tooltip says why.
/// </summary>
public sealed class ValueEditor
{
    private readonly Panel _cell;
    private readonly TextBlock _display;
    private readonly VariableNode _node;
    private readonly IDataTipHost _host;
    private Popup? _message;
    private bool _committing;

    public ValueEditor(Panel cell, TextBlock display, VariableNode node, IDataTipHost host)
    {
        _cell = cell;
        _display = display;
        _node = node;
        _host = host;
        Tooltip();

        // Followed while shown: a row that went (scrolled out, a tip closed) doesn't stay reachable from its node.
        cell.AttachedToVisualTree += (_, _) =>
        {
            node.PropertyChanged -= OnNodeChanged;
            node.PropertyChanged += OnNodeChanged;
            Tooltip();
        };
        cell.DetachedFromVisualTree += (_, _) => node.PropertyChanged -= OnNodeChanged;
    }

    private void OnNodeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VariableNode.Value) or nameof(VariableNode.IsError))
        {
            Tooltip();
        }
    }

    /// <summary>The box while the value is edited (tests).</summary>
    public TextBox? Box { get; private set; }

    public bool IsEditing => Box is not null;

    /// <summary>The engine's message for the last value it refused, while the box is open.</summary>
    public string? Error { get; private set; }

    /// <summary>The value in the tooltip, and why it can't be changed when it can't (A14).</summary>
    private void Tooltip()
    {
        var reason = _host.ReadOnlyReason(_node);
        var value = _node.IsPlaceholder || _node.Value.Length == 0 ? null : _node.Value;
        ToolTip.SetTip(_display, reason is { Length: > 0 } ? (value is null ? reason : value + "\n" + reason) : value);
    }

    /// <summary>Opens the box, unless the value can't change.</summary>
    public bool Begin()
    {
        if (Box is not null)
        {
            Box.Focus();
            return true;
        }

        if (_host.ReadOnlyReason(_node) is not null)
        {
            return false;
        }

        var box = new TextBox
        {
            Classes = { "dataTipEdit" },
            Text = _node.Value,
            MinWidth = Math.Max(80, _display.Bounds.Width + 24),
            MaxWidth = DataTipMetrics.MaxValueWidth,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, Localizer.Format("dataTip.valueOf", "Value of {0}", _node.Name));
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    _ = CommitAsync();
                    break;
                case Key.Escape:
                    e.Handled = true;
                    Cancel();
                    break;
            }
        }, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) =>
        {
            if (!_committing && Error is null)
            {
                Cancel();
            }
        };
        Box = box;
        _display.IsVisible = false;
        _cell.Children.Insert(_cell.Children.IndexOf(_display) + 1, box);
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
        return true;
    }

    /// <summary>Sets the typed value (Enter).</summary>
    public async Task CommitAsync()
    {
        if (Box is not { } box)
        {
            return;
        }

        _committing = true;
        try
        {
            if (await _host.SetValueAsync(_node, box.Text ?? string.Empty).ConfigureAwait(true) is { } error)
            {
                ShowError(error);
            }
            else
            {
                End();
            }
        }
        finally
        {
            _committing = false;
        }
    }

    /// <summary>Puts the value back (Esc).</summary>
    public void Cancel() => End();

    private void End()
    {
        if (_message is { } message)
        {
            message.IsOpen = false;
            _cell.Children.Remove(message);
            _message = null;
        }

        if (Box is { } box)
        {
            Box = null;
            _cell.Children.Remove(box);
        }

        Error = null;
        _display.IsVisible = true;
    }

    private void ShowError(string error)
    {
        Error = error;
        if (Box is not { } box)
        {
            return;
        }

        box.Classes.Add("error");
        if (_message is null)
        {
            _message = new Popup
            {
                PlacementTarget = box,
                Placement = PlacementMode.Bottom,
                IsLightDismissEnabled = false,
                Focusable = false,
                Child = new Border
                {
                    Classes = { "editorHover", "dataTipError" },
                    Padding = new Thickness(6, 3),
                    Child = new TextBlock { Classes = { "error" }, TextWrapping = TextWrapping.Wrap, MaxWidth = DataTipMetrics.MaxValueWidth },
                },
            };
            _cell.Children.Add(_message);
        }

        ((TextBlock)((Border)_message.Child!).Child!).Text = error;
        _message.IsOpen = true;
        box.Focus();
        box.SelectAll();
    }
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
                    new Icon { Kind = "visualizer", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
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
    private readonly Grid _grid;
    private DispatcherTimer? _hoverTimer;

    /// <summary>How many rows were built (tests: a tip builds the rows it shows, not one per member, M56).</summary>
    internal static int Built;

    public DataTipRow(DataTipPanel panel, VariableNode node, IDataTipHost host)
    {
        Interlocked.Increment(ref Built);
        Panel = panel;
        Node = node;
        _host = host;
        Classes.Add("dataTipRow");
        Height = DataTipMetrics.Row;
        Focusable = false;

        // The name, View and value columns are as wide as the tip's widest, measured from all its members' texts: the
        // rows are virtualized (M56), and every row has them, so they stay put as it scrolls.
        var grid = _grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(4, GridUnitType.Pixel),
                new ColumnDefinition(16, GridUnitType.Pixel),
                new ColumnDefinition(2, GridUnitType.Pixel),
                new ColumnDefinition(16, GridUnitType.Pixel),
                new ColumnDefinition(4, GridUnitType.Pixel),
                new ColumnDefinition(panel.NameColumn, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Pixel),
                new ColumnDefinition(panel.ViewColumn, GridUnitType.Pixel),
                new ColumnDefinition(GridLength.Auto) { MinWidth = panel.ValueColumn }, // a value's box widens its row while it's edited
                new ColumnDefinition(22, GridUnitType.Pixel),
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

        var icon = DataTipIcons.For(node);
        icon.IsVisible = !node.IsPlaceholder;
        Grid.SetColumn(icon, 3);
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = node.IsPlaceholder ? node.Value : node.Name,
            Classes = { "dataTipName" },
            Margin = new Thickness(0, 0, DataTipPanel.NameMargin, 0),
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
            var view = new ViewButton(node) { Margin = new Thickness(DataTipPanel.ViewMargin, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(view, 7);
            grid.Children.Add(view);
            View = view;
        }

        _value = new TextBlock
        {
            Classes = { "dataTipValue" },
            Text = node.IsPlaceholder ? string.Empty : node.Value,
            MaxWidth = panel.ValueMaxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(DataTipPanel.ValueMargin, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _value.Classes.Set("error", node.IsError);
        _value.Classes.Set("muted", node.IsDisplayPending);
        _value.Classes.Set("changed", node.IsChanged && !node.IsError);
        var valueCell = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Children = { _value } };
        Editor = new ValueEditor(valueCell, _value, node, host);
        valueCell.PointerPressed += (_, e) =>
        {
            // A click on the value edits it, where it can change (A11).
            if (e.GetCurrentPoint(valueCell).Properties.IsLeftButtonPressed && !node.IsPlaceholder && !Editor.IsEditing && Editor.Begin())
            {
                e.Handled = true;
                Panel.Select(this);
            }
        };
        if (DataTipIcons.Reevaluate(node) is { } reevaluate)
        {
            valueCell.Children.Add(reevaluate);
        }

        Grid.SetColumn(valueCell, 8);
        grid.Children.Add(valueCell);
        if (host.PinAction(node) is { } pin)
        {
            // Any row with an evaluate name pins (decision 7); the button shows on the row under the pointer.
            PinButton = new Button { Classes = { "dataTipPin", "dataTipRowPin" }, Content = new Icon { Kind = "pin", Width = 14, Height = 14 } };
            ToolTip.SetTip(PinButton, Localizer.Get("dataTip.pinToSource", "Pin to source"));
            AutomationProperties.SetName(PinButton, Localizer.Format("dataTip.pin", "Pin {0} to source", node.EvaluateName ?? node.Name));
            PinButton.Click += (_, e) =>
            {
                e.Handled = true;
                pin();
            };
            Grid.SetColumn(PinButton, 9);
            grid.Children.Add(PinButton);
        }

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
                OpenMenu();
            }
        };

        // A row built again for a member (scrolled back into view) shows what's open and selected as the last one did.
        Classes.Set("open", host.IsOpen(node) && node.HasChildren);
        Classes.Set("selected", panel.IsSelected(node));
        UpdateChevron();
    }

    public DataTipPanel Panel { get; }

    public VariableNode Node { get; }

    /// <summary>The row's View button (null for a placeholder).</summary>
    public ViewButton? View { get; }

    /// <summary>The value as shown (tests).</summary>
    public TextBlock ValueBlock => _value;

    /// <summary>The value's box, in place (F2, Enter, a click on it).</summary>
    public ValueEditor Editor { get; }

    /// <summary>Pin to source, on the row under the pointer; null for a row that can't be pinned.</summary>
    public Button? PinButton { get; }

    /// <summary>The row's menu (a right-click, Shift+F10).</summary>
    public void OpenMenu() =>
        new ContextMenu { ItemsSource = _host.MenuItems(Node, _host.ReadOnlyReason(Node) is null ? () => Editor.Begin() : null) }.Open(this);

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
        node.IsPlaceholder ? node.Value
        : (node.HasType ? $"{node.Name}, {node.Value}, {node.Type}" : $"{node.Name}, {node.Value}")
          + (node.HasBadge ? ", " + Localizer.Get("dataTip.nonPublic", "non-public") : string.Empty)
          + (node.IsError ? ", " + Localizer.Get("dataTip.error", "error") : string.Empty);

    private void OnNodeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VariableNode.Value))
        {
            _value.Text = Node.Value;
            AutomationProperties.SetName(this, Accessible(Node));
            Panel.FitValue(Node);
        }
        else if (e.PropertyName is nameof(VariableNode.IsError) or nameof(VariableNode.IsChanged))
        {
            _value.Classes.Set("error", Node.IsError);
            _value.Classes.Set("changed", Node.IsChanged && !Node.IsError);
        }
        else if (e.PropertyName is nameof(VariableNode.IsDisplayPending))
        {
            _value.Classes.Set("muted", Node.IsDisplayPending);
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

    /// <summary>The tip's columns changed (its members loaded, its values trimmed sooner): the row's follow.</summary>
    internal void SetColumns(double name, double view, double value, double valueMaxWidth)
    {
        _grid.ColumnDefinitions[5].Width = new GridLength(name, GridUnitType.Pixel);
        _grid.ColumnDefinitions[7].Width = new GridLength(view, GridUnitType.Pixel);
        _grid.ColumnDefinitions[8].MinWidth = value;
        _value.MaxWidth = valueMaxWidth;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Followed while shown (M56): rows are built as they scroll into view, and one that went doesn't stay reachable.
        Node.PropertyChanged -= OnNodeChanged;
        Node.PropertyChanged += OnNodeChanged;
        _value.Text = Node.IsPlaceholder ? string.Empty : Node.Value;
        _value.Classes.Set("error", Node.IsError);
        _value.Classes.Set("changed", Node.IsChanged && !Node.IsError);
        _value.Classes.Set("muted", Node.IsDisplayPending);
        _chevron.IsVisible = Node.HasChildren;
        Panel.FitValue(Node); // its value may have changed since the columns were measured
        SetColumns(Panel.NameColumn, Panel.ViewColumn, Panel.ValueColumn, Panel.ValueMaxWidth);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Node.PropertyChanged -= OnNodeChanged;
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
/// The rows are virtualized (M56): only those in view, and a few around them, are built. The name, View and value columns
/// are measured from every member's text, so each row has them as wide, and they stay put as the tip scrolls.
/// </summary>
/// <remarks>
/// TODO(issue): the keyboard's selection is a class only: give the rows selection-item automation (or automation focus)
/// so a screen reader says what Up and Down selected, now that only the rows in view are in the automation tree.
/// </remarks>
public sealed class DataTipPanel : Border
{
    /// <summary>The space after a name, before the separator.</summary>
    internal const double NameMargin = 8;

    /// <summary>The space before the View button.</summary>
    internal const double ViewMargin = 4;

    /// <summary>The space before the value.</summary>
    internal const double ValueMargin = 6;

    /// <summary>The View button around its text (DataTip styles): its icon and spacing, its chevron, their paddings.</summary>
    private const double ViewChrome = 2 + 14 + 3 + 2 + 2 + 10 + 2 + 1;

    /// <summary>Re-evaluate after a value: its margin and its 20 px button.</summary>
    private const double ReevaluateWidth = 4 + 20;

    /// <summary>A value is measured up to this many characters: past them it's trimmed at 400 px anyway.</summary>
    private const int MeasuredValueLength = 256;

    protected override Type StyleKeyOverride => typeof(Border);

    private readonly IDataTipHost _host;
    private readonly ItemsControl _rows;
    private readonly ScrollViewer _scroll;
    private VariableNode? _selected;
    private double _names;
    private double _values;
    private double _view;
    private bool _reevaluates;
    private bool _refitQueued;

    public DataTipPanel(VariableNode parent, IDataTipHost host, int level)
    {
        Parent = parent;
        _host = host;
        Level = level;
        Classes.Add("dataTip");
        Focusable = true;
        _rows = new ItemsControl
        {
            ItemsSource = parent.Children,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<VariableNode?>((node, _) => node is null ? new Panel() : new DataTipRow(this, node, host), supportsRecycling: false),
        };
        _scroll = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = DataTipMetrics.MaxRows * DataTipMetrics.Row,
        };
        _scroll.ScrollChanged += (_, _) =>
        {
            ResolveDisplays();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        };
        Child = _scroll;
        AutomationProperties.SetName(this, Localizer.Format("dataTip.membersOf", "Members of {0}", parent.Name));
        parent.Children.CollectionChanged += OnChildrenChanged;
        MeasureColumns();
        ResolveDisplays();
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        KeyDown += OnKeyDown;
    }

    public new VariableNode Parent { get; }

    /// <summary>The tip's place in the cascade: 1 under the header, 2 under one of its rows…</summary>
    public int Level { get; }

    /// <summary>The members, in order: every one, though only those in view have rows built.</summary>
    public IReadOnlyList<VariableNode> Nodes => Parent.Children;

    /// <summary>The rows built now, in order: those in view and a few around them (M56).</summary>
    public IReadOnlyList<DataTipRow> Rows =>
        [.. _rows.GetRealizedContainers()
            .Select(c => (Index: _rows.IndexFromContainer(c), Row: (c as ContentPresenter)?.Child as DataTipRow ?? c as DataTipRow))
            .Where(r => r.Index >= 0 && r.Row is not null)
            .OrderBy(r => r.Index)
            .Select(r => r.Row!)];

    /// <summary>A member's row, while it's built (in view).</summary>
    public DataTipRow? RowOf(VariableNode node) => Rows.FirstOrDefault(r => ReferenceEquals(r.Node, node));

    public DataTipRow? SelectedRow => _selected is { } node ? RowOf(node) : null;

    /// <summary>The selected member (its row may be out of view).</summary>
    public VariableNode? SelectedNode => _selected;

    internal bool IsSelected(VariableNode node) => ReferenceEquals(_selected, node);

    /// <summary>How wide a value may be: 400 px, less near the text view's right edge (the tip moved left and still didn't fit).</summary>
    public double ValueMaxWidth
    {
        get;
        set
        {
            field = value;
            ApplyColumns();
        }
    } = DataTipMetrics.MaxValueWidth;

    /// <summary>The name column: the widest name, as text.</summary>
    internal double NameColumn => _names + NameMargin;

    /// <summary>The View column: a View button's width when any member has one.</summary>
    internal double ViewColumn => _view;

    /// <summary>The value column: the widest value, as text, up to <see cref="ValueMaxWidth"/>, and Re-evaluate when a member has it.</summary>
    internal double ValueColumn => ValueMargin + Math.Min(_values, ValueMaxWidth) + (_reevaluates ? ReevaluateWidth : 0);

    /// <summary>The rows' scroll position.</summary>
    public double ScrollOffset => _scroll.Offset.Y;

    public event EventHandler? ScrollChanged;

    /// <summary>The rows changed (the members loaded): the tip's size, and its children's places, change.</summary>
    public event EventHandler? RowsChanged;

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The rows follow the members by themselves (one reset per load); the selection stays when its member does.
        if (_selected is { } selected && !Parent.Children.Contains(selected))
        {
            _selected = null;
        }

        MeasureColumns();
        ResolveDisplays();
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MeasureColumns(); // in its window now: the popup's font
    }

    /// <summary>
    /// The columns' widths, from every member's name and value as text in the tip's font (M56, the maintainer's choice):
    /// the same for each row, built or not, so they don't change as the tip scrolls. Measured once the tip is in its
    /// window (in its font: before, the text was measured twice, once in the default font).
    /// </summary>
    private void MeasureColumns()
    {
        if (!this.IsAttachedToVisualTree())
        {
            return;
        }

        var typeface = FontTypeface();
        var size = TextElement.GetFontSize(this);
        double names = 0, values = 0;
        bool views = false, reevaluates = false;
        foreach (var node in Parent.Children)
        {
            names = Math.Max(names, TextWidth(node.IsPlaceholder ? node.Value : node.Name, typeface, size));
            if (!node.IsPlaceholder)
            {
                values = Math.Max(values, TextWidth(node.Value, typeface, size));
                views = views || VariableTreeSupport.HasVisualizers(node);
            }

            reevaluates |= node.CanReevaluate;
        }

        (_names, _values, _reevaluates) = (names, values, reevaluates);
        _view = views ? ViewMargin + TextWidth(Localizer.Get("dataTip.view", "View"), typeface, size) + ViewChrome : 0;
        ApplyColumns();
    }

    private Typeface FontTypeface() => new(TextElement.GetFontFamily(this), TextElement.GetFontStyle(this), TextElement.GetFontWeight(this), TextElement.GetFontStretch(this));

    private static double TextWidth(string text, Typeface typeface, double size)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        using var layout = new TextLayout(text.Length > MeasuredValueLength ? text[..MeasuredValueLength] : text, typeface, size, null);
        return Math.Ceiling(layout.WidthIncludingTrailingWhitespace) + 1;
    }

    /// <summary>
    /// A member's value changed after the columns were measured (its [DebuggerDisplay] applied as it scrolled into view,
    /// Re-evaluate's answer), or its row was built: a wider value widens the value column for every row, so the rows keep
    /// one width as the tip scrolls, and only a value's box widens its own row.
    /// </summary>
    internal void FitValue(VariableNode node)
    {
        if (node.IsPlaceholder || !this.IsAttachedToVisualTree())
        {
            return;
        }

        var width = TextWidth(node.Value, FontTypeface(), TextElement.GetFontSize(this));
        if (width <= _values)
        {
            return;
        }

        _values = width;
        ApplyColumns();
        if (_refitQueued)
        {
            return;
        }

        // Once the rows built in this layout pass are in: theirs too, and the tip, wider, is placed again.
        _refitQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refitQueued = false;
            if (this.IsAttachedToVisualTree())
            {
                ApplyColumns();
                RowsChanged?.Invoke(this, EventArgs.Empty);
            }
        }, DispatcherPriority.Background);
    }

    private void ApplyColumns()
    {
        foreach (var row in Rows)
        {
            row.SetColumns(NameColumn, ViewColumn, ValueColumn, ValueMaxWidth);
        }
    }

    /// <summary>The [DebuggerDisplay] of the members in view that don't have it yet (A10).</summary>
    private void ResolveDisplays()
    {
        var nodes = Parent.Children;
        var first = Math.Max(0, (int)(ScrollOffset / DataTipMetrics.Row));
        for (var i = first; i < nodes.Count && i < first + DataTipMetrics.MaxRows + 1; i++)
        {
            _ = nodes[i].ResolveDisplayAsync();
        }
    }

    /// <summary>The top of <paramref name="node"/>'s row in the tip's own coordinates (its border and padding included, its scroll applied).</summary>
    public double RowTop(VariableNode node) => DataTipMetrics.RowsTop + (Math.Max(0, Parent.Children.IndexOf(node)) * DataTipMetrics.Row) - ScrollOffset;

    /// <summary>The top of <paramref name="row"/> in the tip's own coordinates.</summary>
    public double RowTop(DataTipRow row) => RowTop(row.Node);

    public void Select(DataTipRow? row) => Select(row?.Node);

    /// <summary>Selects a member, and brings its row into view.</summary>
    public void Select(VariableNode? node)
    {
        if (_selected is { } old && RowOf(old) is { } before)
        {
            before.Classes.Remove("selected");
        }

        _selected = node;
        if (node is null)
        {
            return;
        }

        if (Parent.Children.IndexOf(node) is >= 0 and var index)
        {
            _rows.ScrollIntoView(index);
        }

        RowOf(node)?.Classes.Add("selected");
    }

    /// <summary>The selected member's row, brought into view first: the wheel may have scrolled it out, and only the rows in view are built.</summary>
    private DataTipRow? SelectedRowInView()
    {
        Select(_selected);
        return SelectedRow;
    }

    /// <summary>Takes the keyboard focus with a row selected (the first, unless one is).</summary>
    public void FocusRows()
    {
        Select(_selected ?? Parent.Children.FirstOrDefault(n => !n.IsPlaceholder));
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
        if (e.Source is TextBox)
        {
            return; // a value's box takes its own keys
        }

        var nodes = Parent.Children.Where(n => !n.IsPlaceholder).ToList();
        var index = _selected is null ? -1 : nodes.IndexOf(_selected);
        switch (e.Key)
        {
            case Key.Down when nodes.Count > 0:
                Select(nodes[Math.Min(nodes.Count - 1, index + 1)]);
                break;
            case Key.Up when nodes.Count > 0:
                Select(nodes[Math.Max(0, index - 1)]);
                break;
            case Key.Right when _selected is { HasChildren: true } node:
                _host.SetOpen(this, RowOf(node) ?? (Control)this, node, true);
                break;
            case Key.Left when _selected is { } node && _host.IsOpen(node):
                _host.SetOpen(this, RowOf(node) ?? (Control)this, node, false);
                break;
            case Key.Left:
                _host.FocusParent(this);
                break;
            case Key.F2 or Key.Enter when SelectedRowInView() is { } row && row.Editor.Begin():
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && _selected is { } node:
                _ = VariableActions.CopyText?.Invoke(node.Value);
                break;
            case Key.F10 when e.KeyModifiers.HasFlag(KeyModifiers.Shift) && SelectedRowInView() is { } row:
                row.OpenMenu();
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
    public void Detach()
    {
        Parent.Children.CollectionChanged -= OnChildrenChanged;
        _rows.ItemsSource = null;
    }
}

/// <summary>
/// The header over the hovered expression: chevron, icon, the name over the code's, a separator, the View slot and the
/// value in a box. Its name stands for the code's word: a double-click selects it, Ctrl+click goes to its definition and
/// the wheel scrolls the editor, each passed on to the text view at the same point. A pinned tip (A15, A16) is the same
/// header at the end of its line, with a strip to unpin it on its left; muted, and not expanding, while the program runs.
/// </summary>
public sealed class DataTipHeader : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    private readonly IDataTipHost _host;
    private readonly Icon _chevronIcon;
    private readonly Border _chevron;
    private readonly TextBlock _value;
    private readonly bool _muted;

    /// <param name="unpin">For a pinned tip: its strip's action.</param>
    /// <param name="muted">A pinned tip while the program runs: the last stop's value, muted, nothing expands.</param>
    public DataTipHeader(VariableNode node, IDataTipHost host, Action? unpin = null, bool muted = false)
    {
        Node = node;
        _host = host;
        _muted = muted;
        Classes.Add("dataTipHeader");
        Classes.Set("pinned", unpin is not null);
        Height = DataTipMetrics.Header;
        Focusable = true;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (unpin is not null)
        {
            var strip = new Button { Classes = { "dataTipPin", "dataTipUnpin" }, Content = new Icon { Kind = "pin", Width = 14, Height = 14 } };
            ToolTip.SetTip(strip, Localizer.Get("dataTip.unpin", "Unpin"));
            AutomationProperties.SetName(strip, Localizer.Format("dataTip.unpinExpression", "Unpin {0}", node.Name));
            strip.Click += (_, e) =>
            {
                e.Handled = true;
                unpin();
            };
            panel.Children.Add(strip);
            panel.Children.Add(new Border { Classes = { "dataTipSeparator" } });
            UnpinButton = strip;
        }

        _chevronIcon = new Icon { Kind = "chevron-right", Width = 16, Height = 16 };
        _chevron = new Border
        {
            Classes = { "dataTipChevron" },
            Width = 16,
            Margin = new Thickness(3, 0, 2, 0),
            Background = Brushes.Transparent,
            Child = _chevronIcon,
        };
        UpdateChevron();
        AutomationProperties.SetName(_chevron, Localizer.Format("dataTip.expand", "Expand {0}", node.Name));
        DispatcherTimer? timer = null;
        _chevron.PointerEntered += (_, _) =>
        {
            if (CanExpand && !_host.IsOpen(Node))
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
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && CanExpand)
            {
                e.Handled = true;
                timer?.Stop();
                _host.SetOpen(null, this, Node, !_host.IsOpen(Node));
            }
        };
        panel.Children.Add(_chevron);
        var headerIcon = DataTipIcons.For(node);
        headerIcon.Margin = new Thickness(0, 0, 4, 0);
        panel.Children.Add(headerIcon);
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
        UpdateValueClasses();
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Children = { _value } };
        var box = new Border { Classes = { "dataTipValueBox" }, Child = cell, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        Editor = new ValueEditor(cell, _value, node, host);
        box.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(box).Properties.IsLeftButtonPressed && !Editor.IsEditing && Editor.Begin())
            {
                e.Handled = true;
            }
        };
        panel.Children.Add(box);
        if (DataTipIcons.Reevaluate(node) is { } reevaluate)
        {
            panel.Children.Add(reevaluate);
        }

        if (unpin is null && _host.PinAction(node) is { } pin)
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
            OpenMenu();
        };
        KeyDown += (_, e) =>
        {
            if (e.Source is TextBox)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Right or Key.Down when CanExpand:
                    _host.SetOpen(null, this, Node, true);
                    break;
                case Key.F2 or Key.Enter when Editor.Begin():
                    break;
                case Key.F10 when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    OpenMenu();
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
            }
            else if (e.PropertyName is nameof(VariableNode.HasChildren))
            {
                UpdateChevron();
            }
            else if (e.PropertyName is nameof(VariableNode.IsChanged) or nameof(VariableNode.IsError))
            {
                UpdateValueClasses();
            }
        };
    }

    public VariableNode Node { get; }

    public TextBlock NameBlock { get; }

    public ViewButton View { get; }

    public Button? PinButton { get; }

    /// <summary>A pinned tip's strip, which unpins it.</summary>
    public Button? UnpinButton { get; }

    /// <summary>The value's box, in place (F2, Enter, a click on it).</summary>
    public ValueEditor Editor { get; }

    /// <summary>Whether the value has members to show now (a pinned tip's don't while the program runs).</summary>
    private bool CanExpand => Node.HasChildren && !_muted;

    private void UpdateChevron()
    {
        // No chevron when there's nothing to expand; a pinned tip's is dimmed while the program runs (A16).
        _chevron.Opacity = Node.HasChildren ? (_muted ? 0.4 : 1) : 0;
        _chevron.IsHitTestVisible = CanExpand;
    }

    private void UpdateValueClasses()
    {
        _value.Classes.Set("error", Node.IsError && !_muted);
        _value.Classes.Set("changed", Node.IsChanged && !Node.IsError && !_muted);
        _value.Classes.Set("muted", _muted);
    }

    public void OpenMenu() =>
        new ContextMenu { ItemsSource = _host.MenuItems(Node, _host.ReadOnlyReason(Node) is null ? () => Editor.Begin() : null) }.Open(this);

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
