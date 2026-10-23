using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Codalus.Shell.Controls;

/// <summary>The part a child of <see cref="TitleBarPanel"/> plays in the title row.</summary>
public enum TitleBarRole
{
    None,
    Icon,
    Menu,
    Search,
    Name,
    DragArea,
    Caption,
}

/// <summary>
/// The title row (M29; 0.6.0 for narrow windows): the icon, the menu bar, the search box, the solution's name, the drag
/// area and the room for the caption buttons, each child marked with its <see cref="RoleProperty"/>. The caption buttons'
/// room and <see cref="MinDragWidth"/> of drag area are always kept at the first row's right end. As the window narrows,
/// like the classic IDE's: the solution's name trims (down to <see cref="MinNameWidth"/>), the search box shrinks to its
/// icon, then the menu wraps onto a second row (<see cref="TitleMenuPanel"/>) with the search box at that row's end,
/// while the name stays on the first row, before the caption buttons.
/// </summary>
public sealed class TitleBarPanel : Panel
{
    public static readonly AttachedProperty<TitleBarRole> RoleProperty =
        AvaloniaProperty.RegisterAttached<TitleBarPanel, Control, TitleBarRole>("Role");

    /// <summary>The first row's height: the caption buttons' (<c>ExtendClientAreaTitleBarHeightHint</c> on Windows).</summary>
    public static readonly StyledProperty<double> FirstRowHeightProperty =
        AvaloniaProperty.Register<TitleBarPanel, double>(nameof(FirstRowHeight), 32);

    /// <summary>The drag area the first row always keeps before the caption buttons.</summary>
    public static readonly StyledProperty<double> MinDragWidthProperty =
        AvaloniaProperty.Register<TitleBarPanel, double>(nameof(MinDragWidth), 40);

    /// <summary>The narrowest the solution's name trims to before the search box shrinks.</summary>
    public static readonly StyledProperty<double> MinNameWidthProperty =
        AvaloniaProperty.Register<TitleBarPanel, double>(nameof(MinNameWidth), 72);

    /// <summary>The search box's class while it is shrunk to its icon (its text is hidden).</summary>
    public const string CompactClass = "compact";

    private double _searchFull = -1;
    private double _menuWidth;
    private double _searchWidth;
    private double _nameWidth;
    private double _menuTop;

    static TitleBarPanel()
    {
        AffectsMeasure<TitleBarPanel>(FirstRowHeightProperty, MinDragWidthProperty, MinNameWidthProperty);
        AffectsParentMeasure<TitleBarPanel>(RoleProperty);
    }

    public static TitleBarRole GetRole(Control control) => control.GetValue(RoleProperty);

    public static void SetRole(Control control, TitleBarRole value) => control.SetValue(RoleProperty, value);

    public double FirstRowHeight
    {
        get => GetValue(FirstRowHeightProperty);
        set => SetValue(FirstRowHeightProperty, value);
    }

    public double MinDragWidth
    {
        get => GetValue(MinDragWidthProperty);
        set => SetValue(MinDragWidthProperty, value);
    }

    public double MinNameWidth
    {
        get => GetValue(MinNameWidthProperty);
        set => SetValue(MinNameWidthProperty, value);
    }

    /// <summary>Whether the menu wraps onto more than one row (the search box is then at the end of its last).</summary>
    public bool IsWrapped { get; private set; }

    /// <summary>Whether the search box is shrunk to its icon.</summary>
    public bool IsSearchCompact { get; private set; }

    private Control? Child(TitleBarRole role) => Children.FirstOrDefault(c => GetRole(c) == role);

    private static double WidthOf(Control? control) => control is { IsVisible: true } ? control.DesiredSize.Width : 0;

    private static double HeightOf(Control? control) => control is { IsVisible: true } ? control.DesiredSize.Height : 0;

    private static TitleMenuPanel? RowsOf(Control? menu) => (menu as ItemsControl)?.ItemsPanelRoot as TitleMenuPanel;

    protected override Size MeasureOverride(Size availableSize)
    {
        var (icon, menu, search, name, drag, caption) =
            (Child(TitleBarRole.Icon), Child(TitleBarRole.Menu), Child(TitleBarRole.Search), Child(TitleBarRole.Name), Child(TitleBarRole.DragArea), Child(TitleBarRole.Caption));
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
        }

        var iconWidth = WidthOf(icon);
        var captionWidth = WidthOf(caption);
        var nameFull = WidthOf(name);
        var nameMin = Math.Min(nameFull, MinNameWidth);

        // The search box's two widths: full (measured while it is), and shrunk to its icon.
        var searchFull = 0.0;
        var searchCompact = 0.0;
        if (search is { IsVisible: true })
        {
            if (!IsSearchCompact || _searchFull < 0)
            {
                _searchFull = search.DesiredSize.Width;
            }

            searchFull = _searchFull;
            searchCompact = CompactWidth(search);
        }

        // The menu on one row.
        var rows = RowsOf(menu);
        if (rows is not null && Math.Abs(rows.TrailingSpace - searchCompact) > 0.5)
        {
            rows.TrailingSpace = searchCompact;
            InvalidateBetween(rows, menu!);
            menu!.Measure(Size.Infinity);
        }

        var menuOneRow = WidthOf(menu);
        var room = availableSize.Width - iconWidth - captionWidth - MinDragWidth;
        bool compact;
        if (double.IsInfinity(availableSize.Width) || menuOneRow + searchFull + nameFull <= room)
        {
            (IsWrapped, compact, _nameWidth) = (false, false, nameFull);
        }
        else if (menuOneRow + searchFull + nameMin <= room)
        {
            (IsWrapped, compact, _nameWidth) = (false, false, room - menuOneRow - searchFull);
        }
        else if (menuOneRow + searchCompact + nameMin <= room)
        {
            (IsWrapped, compact, _nameWidth) = (false, true, Math.Min(nameFull, room - menuOneRow - searchCompact));
        }
        else
        {
            IsWrapped = true;
            compact = true;
            _nameWidth = nameMin;
        }

        _menuWidth = menuOneRow;
        if (IsWrapped && menu is not null)
        {
            // The rows with the name at its narrowest, then the name takes what the widest of them leaves (up to its width).
            // Not just what the first row leaves: a later row wider than it (or the last with the search box at its end)
            // would wrap again.
            menu.Measure(new Size(Math.Max(0, room - nameMin), double.PositiveInfinity));
            var chrome = menu.DesiredSize.Width - (rows?.DesiredSize.Width ?? menu.DesiredSize.Width);
            var widest = (rows is null ? menu.DesiredSize.Width - chrome : Math.Max(rows.DesiredSize.Width, rows.LastRowEnd + rows.TrailingSpace)) + chrome;
            _nameWidth = Math.Clamp(room - widest, nameMin, nameFull);
            _menuWidth = Math.Max(0, room - _nameWidth);
            menu.Measure(new Size(_menuWidth, double.PositiveInfinity));

            // The search box at the last row's end: full when that row has room for it.
            var free = _menuWidth - chrome - (rows?.LastRowEnd ?? menu.DesiredSize.Width);
            compact = free < searchFull - 0.5;
        }

        if (search is not null)
        {
            IsSearchCompact = compact;
            if (search.Classes.Contains(CompactClass) != compact)
            {
                // Its text shows or hides: measured again in this pass, from the text up.
                search.Classes.Set(CompactClass, compact);
                foreach (var part in search.GetVisualDescendants().OfType<Layoutable>().Prepend(search))
                {
                    part.InvalidateMeasure();
                }
            }

            search.Measure(Size.Infinity);
            _searchWidth = WidthOf(search);
        }

        name?.Measure(new Size(Math.Max(0, _nameWidth), double.PositiveInfinity));
        drag?.Measure(Size.Infinity);

        // The rows' height: the first row is at least the caption buttons' height, and the menu's first row centers in it.
        double height;
        if (IsWrapped && menu is not null)
        {
            var firstRowHeight = (rows?.FirstRowHeight ?? 0) + (menu.DesiredSize.Height - (rows?.DesiredSize.Height ?? menu.DesiredSize.Height));
            _menuTop = Math.Max(0, (FirstRowHeight - firstRowHeight) / 2);
            var searchBottom = _menuTop + (rows is null ? menu.DesiredSize.Height : rows.LastRowTop + (rows.LastRowHeight + HeightOf(search)) / 2);
            height = Math.Max(_menuTop * 2 + menu.DesiredSize.Height, searchBottom + _menuTop);
        }
        else
        {
            _menuTop = 0;
            height = new[] { FirstRowHeight, HeightOf(menu), HeightOf(search), HeightOf(name), HeightOf(icon) }.Max();
        }

        var width = double.IsInfinity(availableSize.Width)
            ? iconWidth + menuOneRow + searchFull + nameFull + MinDragWidth + captionWidth
            : availableSize.Width;
        return new Size(width, Math.Max(FirstRowHeight, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (icon, menu, search, name, drag, caption) =
            (Child(TitleBarRole.Icon), Child(TitleBarRole.Menu), Child(TitleBarRole.Search), Child(TitleBarRole.Name), Child(TitleBarRole.DragArea), Child(TitleBarRole.Caption));
        var width = finalSize.Width;
        var height = finalSize.Height;
        var captionWidth = WidthOf(caption);
        var firstRow = IsWrapped ? Math.Min(FirstRowHeight, height) : height;

        // The caption buttons' room: the first row's right end only.
        caption?.Arrange(new Rect(width - captionWidth, 0, captionWidth, IsWrapped ? firstRow : height));

        var x = 0.0;
        if (icon is { IsVisible: true })
        {
            icon.Arrange(new Rect(0, (firstRow - icon.DesiredSize.Height) / 2, icon.DesiredSize.Width, icon.DesiredSize.Height));
            x = icon.DesiredSize.Width;
        }

        double end;
        if (!IsWrapped)
        {
            menu?.Arrange(new Rect(x, (height - HeightOf(menu)) / 2, _menuWidth, HeightOf(menu)));
            x += WidthOf(menu) > 0 ? _menuWidth : 0;
            search?.Arrange(new Rect(x, (height - HeightOf(search)) / 2, _searchWidth, HeightOf(search)));
            x += _searchWidth;
            name?.Arrange(new Rect(x, (height - HeightOf(name)) / 2, Math.Max(0, _nameWidth), HeightOf(name)));
            end = x + (WidthOf(name) > 0 ? Math.Max(0, _nameWidth) : 0);
        }
        else
        {
            menu!.Arrange(new Rect(x, _menuTop, _menuWidth, menu.DesiredSize.Height));
            var rows = RowsOf(menu);

            // The search box after the last row's items; the name after the first row's.
            var lastRow = rows?.TranslatePoint(new Point(rows.LastRowEnd, rows.LastRowTop + rows.LastRowHeight / 2), this)
                ?? new Point(x + _menuWidth, _menuTop + menu.DesiredSize.Height / 2);
            search?.Arrange(new Rect(lastRow.X, lastRow.Y - HeightOf(search) / 2, _searchWidth, HeightOf(search)));
            var firstRowEnd = rows?.TranslatePoint(new Point(rows.FirstRowWidth, 0), this)?.X ?? x + _menuWidth;
            var chromeRight = x + _menuWidth - (rows?.TranslatePoint(new Point(rows.Bounds.Width, 0), this)?.X ?? x + _menuWidth);
            var nameX = firstRowEnd + Math.Max(0, chromeRight);
            name?.Arrange(new Rect(nameX, (firstRow - HeightOf(name)) / 2, Math.Max(0, _nameWidth), HeightOf(name)));
            end = WidthOf(name) > 0 ? nameX + Math.Max(0, _nameWidth) : nameX;
        }

        // The drag area: what is left of the first row before the caption buttons, the whole height down.
        drag?.Arrange(new Rect(end, 0, Math.Max(0, width - captionWidth - end), height));
        return finalSize;
    }

    /// <summary>The search box shrunk to its icon: its margin, border and padding around its icon.</summary>
    private static double CompactWidth(Control search)
    {
        var icon = search.GetVisualDescendants().OfType<Icon>().FirstOrDefault();
        var iconWidth = icon is null ? 16 : Math.Max(icon.DesiredSize.Width, double.IsNaN(icon.Width) ? 16 : icon.Width);
        var (padding, border) = search is TemplatedControl t ? (t.Padding, t.BorderThickness) : (default, default);
        return search.Margin.Left + search.Margin.Right + padding.Left + padding.Right + border.Left + border.Right + iconWidth;
    }

    /// <summary>
    /// Marks the menu and the controls between it and its items panel for measuring again, so a change to the panel's
    /// trailing space reaches the panel in this measure pass.
    /// </summary>
    private static void InvalidateBetween(Visual panel, Visual menu)
    {
        for (Visual? visual = panel; visual is not null; visual = visual.GetVisualParent())
        {
            (visual as Layoutable)?.InvalidateMeasure();
            if (visual == menu)
            {
                break;
            }
        }
    }
}
