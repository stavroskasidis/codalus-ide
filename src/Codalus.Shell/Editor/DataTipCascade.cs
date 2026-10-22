using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Shell.Views.DataTips;

namespace Codalus.Shell.Editor;

/// <summary>What the editor does for a data tip's cascade: the code's gestures passed on, and the tip's actions.</summary>
internal sealed class DataTipActions
{
    /// <summary>The wheel over a tip: the editor scrolls, and the tips close.</summary>
    public required Action<PointerWheelEventArgs> Wheel { get; init; }

    /// <summary>A double-click (true) or Ctrl+click on the header's name, at a point of the text view.</summary>
    public required Action<Point, bool> NameGesture { get; init; }

    /// <summary>The pointer over a faded tip (Ctrl held), at a point of the text view: the Ctrl+hover link there.</summary>
    public required Action<Point, KeyModifiers> FadedMove { get; init; }

    /// <summary>A click on a faded tip, at a point of the text view: Ctrl+click on the code under it.</summary>
    public required Action<Point, KeyModifiers> FadedPress { get; init; }

    /// <summary>Sets a value typed in place; the engine's error, or null.</summary>
    public required Func<VariableNode, string, Task<string?>> SetValue { get; init; }

    /// <summary>Why a value can't be changed (empty: nothing to say), or null.</summary>
    public required Func<VariableNode, string?> ReadOnlyReason { get; init; }

    public required Func<VariableNode, Action?> Pin { get; init; }

    public required Action Close { get; init; }

    /// <summary>A value's menu; null for the variable windows' (copy, Add Watch, QuickWatch, Pin to Source, View).</summary>
    public Func<VariableNode, Action?, IReadOnlyList<Control>?>? Menu { get; init; }

    /// <summary>A pinned tip's unpin (its strip); null for the hover's header.</summary>
    public Action? Unpin { get; init; }

    /// <summary>A pinned tip while the program runs: muted, nothing expands.</summary>
    public bool Muted { get; init; }
}

/// <summary>
/// A data tip's cascade (D44): the header over the hovered expression, in the editor's hover popup, and a tip of its
/// own for each expanded level, its top on the expanded row's bottom edge and 24 px in. The tips are placed against
/// the text view, as one: near its bottom edge they all open upwards, their bottoms on the row's top edge (A18), and
/// near its right edge each moves left. The child tips are popups inside the header's, so a click in one doesn't
/// light-dismiss it. While Ctrl is held they fade and let the pointer through to the code (A19). A pinned tip's header
/// is in the editor's pinned layer instead, and its child tips open from it the same way (A15).
/// </summary>
internal sealed class DataTipCascade : IDataTipHost
{
    private readonly TextView _view;
    private readonly Popup? _popup;
    private readonly DataTipActions _actions;
    private readonly Panel _root;
    private readonly List<Level> _levels = [];
    private Rect _header;
    private Point _name;
    private double _lineHeight;
    private bool _flip;
    private bool _faded;
    private readonly List<TopLevel> _forwarding = [];
    private Popup? _strip;

    private sealed record Level(Popup Popup, DataTipPanel Panel, Control Anchor)
    {
        public Rect Bounds { get; set; }
    }

    /// <summary>The hover's tip: its header in <paramref name="popup"/>, the editor's hover popup.</summary>
    public DataTipCascade(TextView view, Popup popup, VariableNode root, DataTipActions actions)
    {
        _view = view;
        _popup = popup;
        _actions = actions;
        _root = new Panel();
        Root = root;
        Header = new DataTipHeader(root, this);
        _root.Children.Add(Header);
        _popup.Closed += OnClosed;
    }

    /// <summary>A pinned tip: its header is placed by the pinned layer, and its child tips' popups go in <paramref name="popups"/>.</summary>
    public DataTipCascade(TextView view, Panel popups, VariableNode root, DataTipActions actions)
    {
        _view = view;
        _actions = actions;
        _root = popups;
        Root = root;
        Header = new DataTipHeader(root, this, actions.Unpin, actions.Muted);

        // The pinned layer moves its tips as the text scrolls and lines change: the child tips follow their header.
        Header.LayoutUpdated += (_, _) =>
        {
            if (_levels.Count > 0 && HeaderBounds != _arrangedFrom)
            {
                Arrange();
            }
        };
    }

    /// <summary>The header's bounds the child tips were last placed from.</summary>
    private Rect _arrangedFrom;

    public VariableNode Root { get; }

    public DataTipHeader Header { get; }

    /// <summary>What the header's popup shows: the header, and the child tips' popups inside it.</summary>
    public Control Content => _root;

    /// <summary>The open child tips, outermost first.</summary>
    public IReadOnlyList<DataTipPanel> Panels => [.. _levels.Select(l => l.Panel)];

    /// <summary>Whether the tips open upwards (near the text view's bottom edge).</summary>
    public bool IsFlipped => _flip;

    public bool IsFaded => _faded;

    /// <summary>
    /// Places the header with its name over the code's: <paramref name="name"/> is where the expression starts, at its
    /// line's top, in the text view's coordinates.
    /// </summary>
    public void Place(Point name, double lineHeight)
    {
        _name = name;
        _lineHeight = lineHeight;
        var size = Fit(Header, w => Header.ValueMaxWidth = w);
        var x = Math.Max(0, Math.Min(name.X - DataTipMetrics.NameX, _view.Bounds.Width - size.Width));
        var y = name.Y + ((lineHeight - DataTipMetrics.Header) / 2);
        _header = new Rect(x, y, size.Width, size.Height);
        if (_popup is null)
        {
            return;
        }

        _popup.Placement = PlacementMode.AnchorAndGravity;
        _popup.PlacementTarget = _view;
        _popup.PlacementAnchor = PopupAnchor.TopLeft;
        _popup.PlacementGravity = PopupGravity.BottomRight;
        _popup.PlacementRect = new Rect(0, 0, 1, 1);
        _popup.PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY;
        _popup.HorizontalOffset = x;
        _popup.VerticalOffset = y;
        Arrange();
    }

    /// <summary>The widest the diagnostics strip gets.</summary>
    public const double StripMaxWidth = 470;

    /// <summary>
    /// The hover's diagnostics (A3, decision 17): a strip of their own right above the header, 2 px from it, with the
    /// code's help link, the message, Fix and Other Fixes as Quick Info shows them; under the header when the tips open
    /// upwards (A18), so it stays clear of them.
    /// </summary>
    public void SetStrip(Control diagnostics)
    {
        _strip = new Popup
        {
            Child = new Border { Classes = { "editorHover", "intelliSense", "dataTipStrip" }, MaxWidth = StripMaxWidth, Child = diagnostics },
            IsLightDismissEnabled = false,
            Focusable = false,
            WindowManagerAddShadowHint = true,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementTarget = _view,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            PlacementRect = new Rect(0, 0, 1, 1),
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY,
        };
        _root.Children.Add(_strip);
        _strip.Child.SizeChanged += (_, _) => Arrange(); // measured again in its window, with its styles
    }

    /// <summary>Every popup the tip shows: the header's, the diagnostics' and each child tip's (the smoke test's screenshot).</summary>
    public IReadOnlyList<Popup> Popups => [.. _popup is null ? [] : new[] { _popup }, .. _strip is null ? [] : new[] { _strip }, .. _levels.Select(l => l.Popup)];

    /// <summary>The diagnostics' popup (tests).</summary>
    public Popup? Strip => _strip;

    /// <summary>The header's and each open tip's bounds, in the text view's coordinates.</summary>
    public IEnumerable<Rect> Bounds => [HeaderBounds, .. _levels.Select(l => l.Bounds), .. StripBounds()];

    /// <summary>The header's bounds in the text view's coordinates: where the hover placed it, or where the pinned layer did.</summary>
    private Rect HeaderBounds => _popup is null && Header.Bounds.Width > 0 && TopLevel.GetTopLevel(Header) is not null
        ? new Rect(_view.PointToClient(Header.PointToScreen(default)), Header.Bounds.Size)
        : _header;

    private IEnumerable<Rect> StripBounds()
    {
        if (_strip is { IsOpen: true, Child: { } strip } popup)
        {
            yield return new Rect(new Point(popup.HorizontalOffset, popup.VerticalOffset), strip.Bounds.Height > 0 ? strip.Bounds.Size : strip.DesiredSize);
        }
    }

    public bool IsOpen(VariableNode node) => _levels.Any(l => ReferenceEquals(l.Panel.Parent, node));

    public void SetOpen(DataTipPanel? owner, Control anchor, VariableNode node, bool open)
    {
        var depth = owner?.Level ?? 0;
        var focused = anchor.IsKeyboardFocusWithin || owner?.IsKeyboardFocusWithin == true;
        CloseFrom(depth);
        if (!open || !node.HasChildren)
        {
            if (focused)
            {
                (owner as InputElement ?? Header).Focus(NavigationMethod.Directional);
            }

            return;
        }

        node.IsExpanded = true;
        var panel = new DataTipPanel(node, this, depth + 1);
        var popup = new Popup
        {
            Child = panel,
            IsLightDismissEnabled = false,
            Focusable = false,
            WindowManagerAddShadowHint = true,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementTarget = _view,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            PlacementRect = new Rect(0, 0, 1, 1),
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY,
        };
        var level = new Level(popup, panel, anchor);
        _levels.Add(level);
        _root.Children.Add(popup);
        SetAnchorOpen(anchor, true);
        panel.RowsChanged += (_, _) => Arrange();
        panel.ScrollChanged += (_, _) => Arrange();
        panel.Opacity = _faded ? 0.15 : 1;
        panel.IsHitTestVisible = !_faded;
        Arrange();
        popup.IsOpen = true;
        Arrange(); // measured again in its window, with its styles
        Forward(panel);
        if (focused)
        {
            panel.FocusRows();
        }
    }

    public void PointerOnRow(DataTipPanel owner, DataTipRow row)
    {
        // Another row of the same tip: the child tip of the row the pointer left closes.
        if (_levels.Count > owner.Level && !ReferenceEquals(_levels[owner.Level].Anchor, row))
        {
            CloseFrom(owner.Level);
        }
    }

    public string? ReadOnlyReason(VariableNode node) => _actions.ReadOnlyReason(node);

    public Task<string?> SetValueAsync(VariableNode node, string value) => _actions.SetValue(node, value);

    public IReadOnlyList<Control> MenuItems(VariableNode node, Action? edit) =>
        _actions.Menu?.Invoke(node, edit) ?? VariableTreeSupport.MenuItems(node, edit, _actions.Pin(node));

    public Action? PinAction(VariableNode node) => _actions.Pin(node);

    public void Wheel(PointerWheelEventArgs e) => _actions.Wheel(e);

    public void HeaderGesture(PointerEventArgs e, bool doubleClick) => _actions.NameGesture(ToView(Header.NameBlock, e.GetPosition(Header.NameBlock)), doubleClick);

    public void CloseAll() => _actions.Close();

    public void FocusParent(DataTipPanel owner)
    {
        if (owner.Level <= 1)
        {
            Header.Focus(NavigationMethod.Directional);
        }
        else
        {
            _levels[owner.Level - 2].Panel.FocusRows();
        }
    }

    /// <summary>Closes the tips below level <paramref name="depth"/> (0: every child tip, the header stays).</summary>
    public void CloseFrom(int depth)
    {
        while (_levels.Count > depth)
        {
            var level = _levels[^1];
            _levels.RemoveAt(_levels.Count - 1);
            level.Popup.IsOpen = false;
            _root.Children.Remove(level.Popup);
            level.Panel.Detach();
            SetAnchorOpen(level.Anchor, false);
        }
    }

    private static void SetAnchorOpen(Control anchor, bool open)
    {
        switch (anchor)
        {
            case DataTipRow row:
                row.IsOpen = open;
                break;
            case DataTipHeader header:
                header.IsOpen = open;
                break;
        }
    }

    /// <summary>
    /// Places every child tip: each 24 px right of its parent, under its row, or above it once the first doesn't fit
    /// under the header; moved left where it would pass the text view's right edge.
    /// </summary>
    public void Arrange()
    {
        var width = _view.Bounds.Width;
        var height = _view.Bounds.Height;
        var header = HeaderBounds;
        _arrangedFrom = header;
        Rect parent = header;
        for (var i = 0; i < _levels.Count; i++)
        {
            var level = _levels[i];
            var size = Fit(level.Panel, w => level.Panel.ValueMaxWidth = w);
            double top, bottom;
            if (i == 0)
            {
                (top, bottom) = (header.Top, header.Bottom);
                _flip = bottom + size.Height > height && top - size.Height >= 0;
            }
            else
            {
                var owner = _levels[i - 1];
                var row = owner.Panel.Rows.FirstOrDefault(r => ReferenceEquals(r, level.Anchor));
                top = owner.Bounds.Top + (row is null ? 0 : owner.Panel.RowTop(row));
                bottom = top + DataTipMetrics.Row;
            }

            var x = Math.Max(0, Math.Min(parent.Left + DataTipMetrics.Indent, width - size.Width));
            var y = _flip ? top - size.Height : bottom;
            level.Bounds = new Rect(x, y, size.Width, size.Height);
            level.Popup.HorizontalOffset = x;
            level.Popup.VerticalOffset = y;
            parent = level.Bounds;
        }

        if (_strip is { Child: { } strip } popup)
        {
            strip.Measure(Size.Infinity);
            var under = _levels.Count > 0 && _flip;
            var size = strip.Bounds.Height > 0 ? strip.Bounds.Size : strip.DesiredSize;
            popup.HorizontalOffset = Math.Max(0, Math.Min(header.Left, _view.Bounds.Width - size.Width));
            popup.VerticalOffset = under ? header.Bottom + 2 : header.Top - size.Height - 2;
        }
    }

    /// <summary>A tip's size, its values trimmed sooner when it's wider than the text view (it moves left first).</summary>
    private Size Fit(Control tip, Action<double> valueMaxWidth)
    {
        valueMaxWidth(DataTipMetrics.MaxValueWidth);
        tip.Measure(Size.Infinity);
        var over = tip.DesiredSize.Width - _view.Bounds.Width;
        if (over > 0 && _view.Bounds.Width > 0)
        {
            valueMaxWidth(Math.Max(40, DataTipMetrics.MaxValueWidth - over));
            tip.Measure(Size.Infinity);
        }

        return tip.DesiredSize;
    }

    /// <summary>Fades the tips and lets the pointer through to the code while Ctrl is held (A19), or brings them back.</summary>
    public void SetFaded(bool faded)
    {
        if (_faded == faded)
        {
            return;
        }

        _faded = faded;
        foreach (var control in new Control[] { Header }.Concat(_levels.Select(l => l.Panel)))
        {
            control.Opacity = faded ? 0.15 : 1;
            control.IsHitTestVisible = !faded;
        }
    }

    /// <summary>A faded tip in a window of its own still gets the pointer: what it gets goes to the text view under it.</summary>
    private void Forward(Control content)
    {
        if (TopLevel.GetTopLevel(content) is not { } top || ReferenceEquals(top, TopLevel.GetTopLevel(_view)) || _forwarding.Contains(top))
        {
            return; // in the window's overlay layer, a control that isn't hit-test visible lets the pointer through by itself
        }

        _forwarding.Add(top);
        top.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (_faded)
            {
                _actions.FadedMove(ToView(top, e.GetPosition(top)), e.KeyModifiers);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (_faded)
            {
                e.Handled = true;
                _actions.FadedPress(ToView(top, e.GetPosition(top)), e.KeyModifiers);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Starts passing a faded header's pointer on, once its popup is open.</summary>
    public void Opened()
    {
        // Measured again in its window, with its styles: the header's border and paddings count.
        Place(_name, _lineHeight);
        Forward(Header);
        if (_strip is { } strip)
        {
            strip.IsOpen = true;
        }
    }

    private Point ToView(Visual from, Point point) => _view.PointToClient(from.PointToScreen(point));

    private void OnClosed(object? sender, EventArgs e)
    {
        _popup!.Closed -= OnClosed;
        CloseFrom(0);
        if (_strip is { } strip)
        {
            strip.IsOpen = false;
        }
    }

    /// <summary>The tip's own coordinates of a point in the text view (tests: hit testing).</summary>
    internal static Point? Translate(Visual from, Visual to, Point point) => from.TranslatePoint(point, to);
}
