using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;

namespace Codalus.Shell.Controls;

/// <summary>
/// The top edge of the active document's frame (rounded corners, D46, <c>design/controls/RoundedCorners.dc.html</c>): a
/// row under a document group's tabs (<c>DocumentTabStrip.BottomContent</c>) that draws the frame's line from the strip's
/// left end to the selected tab and from the tab to its right end, and under the tab its sides and fill, so the selected
/// tab flows into the frame below. Dock's <c>DocumentControl</c> puts a whole border under the strip, so styles alone can
/// only close the frame under the tab or drop its top edge. Without a selected tab the line is unbroken. A theme turns it
/// off with a height of 0 (<c>Shell.TabStripEdge.Height</c>: every family but Classic).
/// </summary>
public sealed class TabStripEdge : Control
{
    /// <summary>The frame's brush while the group is active (<c>Shell.Well.Frame.Active</c>).</summary>
    public static readonly StyledProperty<IBrush?> ActiveBrushProperty = AvaloniaProperty.Register<TabStripEdge, IBrush?>(nameof(ActiveBrush));

    /// <summary>The frame's brush otherwise (Dock's <c>DockDocumentContentBorderBrush</c>).</summary>
    public static readonly StyledProperty<IBrush?> InactiveBrushProperty = AvaloniaProperty.Register<TabStripEdge, IBrush?>(nameof(InactiveBrush));

    /// <summary>The selected tab's outline while the group is active (<c>Shell.Tab.Active.IndicatorBrush</c>).</summary>
    public static readonly StyledProperty<IBrush?> ActiveTabBrushProperty = AvaloniaProperty.Register<TabStripEdge, IBrush?>(nameof(ActiveTabBrush));

    /// <summary>The selected tab's outline otherwise (<c>Shell.Tab.Selected.IndicatorBrush</c>).</summary>
    public static readonly StyledProperty<IBrush?> InactiveTabBrushProperty = AvaloniaProperty.Register<TabStripEdge, IBrush?>(nameof(InactiveTabBrush));

    private DocumentTabStrip? _strip;

    static TabStripEdge() => AffectsRender<TabStripEdge>(ActiveBrushProperty, InactiveBrushProperty, ActiveTabBrushProperty, InactiveTabBrushProperty);

    public TabStripEdge() => ClipToBounds = false;

    public IBrush? ActiveBrush
    {
        get => GetValue(ActiveBrushProperty);
        set => SetValue(ActiveBrushProperty, value);
    }

    public IBrush? InactiveBrush
    {
        get => GetValue(InactiveBrushProperty);
        set => SetValue(InactiveBrushProperty, value);
    }

    public IBrush? ActiveTabBrush
    {
        get => GetValue(ActiveTabBrushProperty);
        set => SetValue(ActiveTabBrushProperty, value);
    }

    public IBrush? InactiveTabBrush
    {
        get => GetValue(InactiveTabBrushProperty);
        set => SetValue(InactiveTabBrushProperty, value);
    }

    /// <summary>Whether the group's frame is drawn in the active brush.</summary>
    public bool IsGroupActive => _strip?.IsActive == true;

    /// <summary>The brush the line beside the selected tab is drawn in.</summary>
    public IBrush? FrameBrush => IsGroupActive ? ActiveBrush : InactiveBrush;

    /// <summary>The selected tab, when it's shown.</summary>
    public DocumentTabStripItem? SelectedTab =>
        _strip is { SelectedIndex: >= 0 } strip && strip.ContainerFromIndex(strip.SelectedIndex) is DocumentTabStripItem { IsEffectivelyVisible: true } item ? item : null;

    /// <summary>
    /// The span the line leaves open under the selected tab, in this control's coordinates (its left and right ends), or
    /// null when there is no selected tab, the edge isn't drawn, or the tab isn't on the row above it (a pinned tab on
    /// the pinned row, <see cref="DocumentTabPanel.PinnedRow"/>).
    /// </summary>
    public (double Left, double Right)? Gap()
    {
        if (Bounds.Height <= 0 || SelectedTab is not { } tab || tab.TranslatePoint(default, this) is not { } at
            || at.Y + tab.Bounds.Height < -tab.Bounds.Height / 2)
        {
            return null;
        }

        var left = Math.Max(0, at.X);
        var right = Math.Min(Bounds.Width, at.X + tab.Bounds.Width);
        return right > left ? (left, right) : null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _strip = this.FindAncestorOfType<DocumentTabStrip>();
        if (_strip is not null)
        {
            _strip.SelectionChanged += OnStripChanged;
            _strip.PropertyChanged += OnStripPropertyChanged;
            _strip.LayoutUpdated += OnStripLayoutUpdated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_strip is not null)
        {
            _strip.SelectionChanged -= OnStripChanged;
            _strip.PropertyChanged -= OnStripPropertyChanged;
            _strip.LayoutUpdated -= OnStripLayoutUpdated;
            _strip = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnStripChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>The gap and size drawn last, so a layout pass elsewhere in the window doesn't repaint the edge.</summary>
    private ((double Left, double Right)? Gap, Size Size) _drawn;

    /// <summary>
    /// LayoutUpdated comes after every layout pass of the window, typing's included (M51: the Mac's startup profile):
    /// the edge repaints only when the selected tab's gap or its own size moved.
    /// </summary>
    private void OnStripLayoutUpdated(object? sender, EventArgs e)
    {
        if ((Gap(), Bounds.Size) != _drawn)
        {
            InvalidateVisual();
        }
    }

    private void OnStripPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DocumentTabStrip.IsActiveProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        _drawn = (Gap(), bounds.Size);
        if (bounds.Height <= 0 || FrameBrush is not { } frame)
        {
            return;
        }

        if (Gap() is not var (left, right) || SelectedTab is not { } tab)
        {
            context.FillRectangle(frame, new Rect(0, 0, bounds.Width, bounds.Height));
            return;
        }

        // The line on either side of the tab, the tab's fill through the row, and its outline's sides.
        context.FillRectangle(frame, new Rect(0, 0, left, bounds.Height));
        context.FillRectangle(frame, new Rect(right, 0, bounds.Width - right, bounds.Height));
        if (tab.Background is { } fill)
        {
            context.FillRectangle(fill, new Rect(left, 0, right - left, bounds.Height));
        }

        if ((IsGroupActive ? ActiveTabBrush : InactiveTabBrush) is { } outline)
        {
            context.FillRectangle(outline, new Rect(left, 0, 1, bounds.Height));
            context.FillRectangle(outline, new Rect(right - 1, 0, 1, bounds.Height));
        }

        // TODO(issue): the selected tab's curved feet (D46 question 6, the optional part): quarter circles of the tab's
        // radius joining its sides to the frame. They're drawn outside this row, under the strip's tabs, which paint over
        // them; they need the edge above the tabs (an adorner) or the tab panel's help. Square joins ship first.
    }
}
