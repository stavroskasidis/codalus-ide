using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Codalus.Shell.Controls;
using Codalus.Shell.Views;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor;

/// <summary>
/// The preview half of Markdown editors, shared by every one and kept in <c>state.json</c>: collapsing or resizing
/// one applies to the others and to the next one that opens.
/// </summary>
public sealed class MarkdownSplitState
{
    public const double MinRatio = 0.15;
    public const double MaxRatio = 0.85;

    /// <summary>Whether the preview is collapsed to the strip at the editor's right edge.</summary>
    public bool Collapsed { get; set; }

    /// <summary>The editor's share of the width while the preview shows (0.5: half and half).</summary>
    public double Ratio { get; set; } = 0.5;

    /// <summary>A Markdown editor changed <see cref="Collapsed"/> or <see cref="Ratio"/> (on the UI thread).</summary>
    public event EventHandler? Changed;

    public void Set(bool collapsed, double ratio)
    {
        ratio = Math.Clamp(ratio, MinRatio, MaxRatio);
        if (collapsed != Collapsed || ratio != Ratio)
        {
            Collapsed = collapsed;
            Ratio = ratio;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// A Markdown editor's text beside its live preview, half and half (ADR 0016). The divider resizes them, and its chevron
/// collapses the preview to a strip at the right edge; clicking the strip shows it again. Either side scrolls the other
/// to the same proportion of its length.
/// </summary>
public sealed class MarkdownSplit : Grid
{
    /// <summary>The divider's width: the strip a collapsed preview leaves.</summary>
    public const double DividerWidth = 14;

    private readonly MarkdownSplitState _state;
    private readonly Func<TextEditor?> _editor;
    private readonly GridSplitter _splitter;
    private readonly Button _toggle;
    private readonly Icon _chevron = new() { Width = 10, Height = 10 };
    private bool _collapsed;
    private TextEditor? _scrolledEditor;
    private ScrollViewer? _editorScroll;

    /// <summary>The offsets a sync last set on each side: their scroll events are its own, not the user's.</summary>
    private double? _editorTarget;
    private double? _previewTarget;

    public MarkdownSplit(Control editors, CodeDocument source, MarkdownSplitState state, Action<string> openLink, Func<TextEditor?> editor, bool startCollapsed)
    {
        _state = state;
        _editor = editor;
        Preview = new MarkdownLivePreview(source, openLink) { Padding = new Thickness(4, 0, 0, 0) };
        Preview.Bind(BackgroundProperty, this.GetResourceObservable("Shell.Editor.Background"));
        AutomationProperties.SetName(Preview, Localizer.Get("markdown.preview", "Markdown preview"));

        _splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Width = DividerWidth, Background = Avalonia.Media.Brushes.Transparent };
        _splitter.DragCompleted += (_, _) => _state.Set(_collapsed, MeasuredRatio() ?? _state.Ratio);
        _splitter.DoubleTapped += (_, _) => _state.Set(_collapsed, 0.5);
        ToolTip.SetTip(_splitter, Localizer.Get("markdown.splitter", "Drag to resize the preview; double-click for half and half"));

        _toggle = new Button
        {
            Classes = { "toolbar" },
            Padding = new Thickness(0, 4),
            Width = DividerWidth,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top,
            Content = _chevron,
        };
        _toggle.Click += (_, _) => Toggle();

        var divider = new Border { BorderThickness = new Thickness(1, 0) };
        divider.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Shell.Border"));
        divider.Bind(Border.BackgroundProperty, this.GetResourceObservable("Shell.Editor.Background"));

        SetColumn(divider, 1);
        SetColumn(_splitter, 1);
        SetColumn(_toggle, 1);
        SetColumn(Preview, 2);
        Children.AddRange([editors, divider, _splitter, _toggle, Preview]);

        Preview.Scroll.ScrollChanged += OnPreviewScrolled;
        // After a render, once its new length is laid out.
        Preview.RenderedChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(SyncPreview, Avalonia.Threading.DispatcherPriority.Loaded);
        _collapsed = startCollapsed || state.Collapsed;
        state.Changed += OnStateChanged;
        Apply();
    }

    /// <summary>The preview half.</summary>
    public MarkdownLivePreview Preview { get; }

    /// <summary>Whether the preview is collapsed to its strip.</summary>
    public bool IsCollapsed => _collapsed;

    /// <summary>Collapses the preview, or shows it again; the next Markdown editor opens the same way.</summary>
    public void Toggle()
    {
        var ratio = _collapsed ? _state.Ratio : MeasuredRatio() ?? _state.Ratio;
        _collapsed = !_collapsed;
        _state.Set(_collapsed, ratio);
        Apply();
        if (_collapsed)
        {
            _editor()?.TextArea.Focus();
        }
    }

    /// <summary>Stops following the document (the tab closed).</summary>
    public void Release()
    {
        _state.Changed -= OnStateChanged;
        Preview.Release();
        AttachEditorScroll(null);
    }

    /// <summary>Another Markdown editor collapsed, showed or resized its preview: this one follows.</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        _collapsed = _state.Collapsed;
        Apply();
    }

    private void Apply()
    {
        var ratio = Math.Clamp(_state.Ratio, MarkdownSplitState.MinRatio, MarkdownSplitState.MaxRatio);
        ColumnDefinitions = _collapsed
            ? [new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(0, GridUnitType.Pixel)]
            : [new ColumnDefinition(ratio, GridUnitType.Star) { MinWidth = 80 }, new ColumnDefinition(GridLength.Auto), new ColumnDefinition(1 - ratio, GridUnitType.Star) { MinWidth = 80 }];
        Preview.IsVisible = !_collapsed;
        _splitter.IsVisible = !_collapsed;

        // Expanded, the chevron sits at the divider's top and the rest of it drags; collapsed, the whole strip shows the preview.
        _toggle.VerticalAlignment = _collapsed ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        _chevron.Kind = _collapsed ? "chevron-left" : "chevron-right";
        var label = _collapsed
            ? Localizer.Get("markdown.showPreview", "Show the preview")
            : Localizer.Get("markdown.collapsePreview", "Collapse the preview");
        AutomationProperties.SetName(_toggle, label);
        ToolTip.SetTip(_toggle, label);
        if (!_collapsed)
        {
            SyncPreview();
        }
    }

    /// <summary>The editor's share of the width as laid out now, or null before layout.</summary>
    private double? MeasuredRatio()
    {
        var editors = ColumnDefinitions[0].ActualWidth;
        var preview = ColumnDefinitions[2].ActualWidth;
        return editors + preview > 0 ? editors / (editors + preview) : null;
    }

    // ------------------------------------------------------------------ scroll sync

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LayoutUpdated -= OnLayoutUpdated;
    }

    /// <summary>The editor's scroll viewer exists once its template is applied: hooks it then (and again if the editor changes).</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var editor = _editor();
        if (editor == _scrolledEditor && _editorScroll is not null)
        {
            return;
        }

        var scroll = editor?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null)
        {
            return;
        }

        AttachEditorScroll(scroll);
        _scrolledEditor = editor;
        SyncPreview();
    }

    private void AttachEditorScroll(ScrollViewer? scroll)
    {
        if (_editorScroll is { } old)
        {
            old.ScrollChanged -= OnEditorScrolled;
        }

        _editorScroll = scroll;
        _scrolledEditor = null;
        if (scroll is not null)
        {
            scroll.ScrollChanged += OnEditorScrolled;
        }
    }

    private void OnEditorScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y == 0 || IsOwn(ref _editorTarget, _editorScroll!.Offset.Y))
        {
            return;
        }

        SyncPreview();
    }

    private void OnPreviewScrolled(object? sender, ScrollChangedEventArgs e)
    {
        // A render replaces the content (its extent changes and the offset may be clamped): that isn't the user scrolling.
        if (e.OffsetDelta.Y == 0 || e.ExtentDelta.Y != 0 || IsOwn(ref _previewTarget, Preview.Scroll.Offset.Y))
        {
            return;
        }

        if (_editorScroll is { } editor)
        {
            _editorTarget = ScrollTo(editor, Proportion(Preview.Scroll));
        }
    }

    /// <summary>Scrolls the preview to the editor's proportion.</summary>
    private void SyncPreview()
    {
        if (!_collapsed && _editorScroll is { } editor)
        {
            _previewTarget = ScrollTo(Preview.Scroll, Proportion(editor));
        }
    }

    private static bool IsOwn(ref double? target, double offset)
    {
        var own = target is { } expected && Math.Abs(expected - offset) < 1;
        target = null;
        return own;
    }

    /// <summary>How far down a scroll viewer is, from 0 (top) to 1 (bottom).</summary>
    internal static double Proportion(ScrollViewer scroll)
    {
        var range = scroll.Extent.Height - scroll.Viewport.Height;
        return range > 0 ? Math.Clamp(scroll.Offset.Y / range, 0, 1) : 0;
    }

    /// <summary>Scrolls to a proportion; the offset set, or null when it was already there.</summary>
    private static double? ScrollTo(ScrollViewer scroll, double proportion)
    {
        var y = Math.Round(proportion * Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        if (Math.Abs(scroll.Offset.Y - y) < 1)
        {
            return null;
        }

        scroll.Offset = new Vector(scroll.Offset.X, y);
        return y;
    }
}
