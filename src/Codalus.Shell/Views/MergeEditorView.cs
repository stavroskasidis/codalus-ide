using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>The merge editor's three panes.</summary>
internal enum MergePane
{
    Current,
    Incoming,
    Result,
}

/// <summary>
/// Draws each conflict's block in a merge editor pane: a muted red fill with a dashed outline while it is unresolved
/// (<c>Shell.Merge.Conflict</c>, <c>.Border</c>), a solid outline once resolved where the result took the block
/// (<c>Shell.Merge.Resolved.Border</c>), and a quiet dashed outline on the side it didn't take.
/// </summary>
internal sealed class ConflictBlockRenderer(MergeEditorViewModel viewModel, MergePane pane) : IBackgroundRenderer
{
    private static readonly DashStyle s_dash = new([4, 3], 0);

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.Document is not { } document)
        {
            return;
        }

        var fill = MergeEditorView.Brush(textView, "Shell.Merge.Conflict");
        var conflict = new Pen(MergeEditorView.Brush(textView, "Shell.Merge.Conflict.Border"), 1, s_dash);
        var resolved = new Pen(MergeEditorView.Brush(textView, "Shell.Merge.Resolved.Border"), 1);
        var untaken = new Pen(MergeEditorView.Brush(textView, "Shell.Border"), 1, s_dash);
        var width = textView.Bounds.Width;
        foreach (var block in viewModel.Conflicts)
        {
            var range = MergeEditorView.RangeOf(block, pane);
            if (range.End > document.TextLength || MergeEditorView.LinesOf(textView, document, range) is not var (top, bottom) || bottom < 0 || top > textView.Bounds.Height)
            {
                continue;
            }

            var rect = new Rect(0.5, Math.Floor(top) + 0.5, Math.Max(0, width - 1), Math.Max(2, Math.Round(bottom - top) - 1));
            if (!block.IsResolved)
            {
                drawingContext.DrawRectangle(fill, conflict, rect);
            }
            else if (pane == MergePane.Result || block.Takes(pane == MergePane.Current ? MergeSide.Current : MergeSide.Incoming))
            {
                drawingContext.DrawRectangle(null, resolved, rect);
            }
            else
            {
                drawingContext.DrawRectangle(null, untaken, rect);
            }
        }
    }
}

/// <summary>
/// The check boxes in a source pane's gutter, one beside each conflict's block: checking it takes that side's block into
/// the result, unchecking takes it out again.
/// </summary>
internal sealed class ConflictCheckMargin : Canvas, ITextViewConnect
{
    private const double BoxSize = 16;
    private readonly MergeEditorViewModel _viewModel;
    private readonly MergeSide _side;
    private readonly List<(MergeConflictBlock Block, CheckBox Box)> _boxes = [];
    private TextView? _textView;
    private double _height;

    public ConflictCheckMargin(MergeEditorViewModel viewModel, MergeSide side)
    {
        _viewModel = viewModel;
        _side = side;
        Width = BoxSize + 8;
        ClipToBounds = true;
        foreach (var block in viewModel.Conflicts)
        {
            var name = Localizer.Format("mergeEditor.takeConflictFrom", "Take conflict {0} from {1}", block.Index + 1, viewModel.TitleFor(side));
            var box = new CheckBox { Width = BoxSize, Height = BoxSize, MinHeight = 0, MinWidth = 0, Padding = default, IsChecked = block.Takes(side) };
            AutomationProperties.SetName(box, name);
            ToolTip.SetTip(box, name);
            box.Click += (_, _) =>
            {
                viewModel.SetTake(block, side, box.IsChecked == true);
                box.IsChecked = block.Takes(side);
            };
            _boxes.Add((block, box));
            Children.Add(box);
        }
    }

    /// <summary>Each conflict's check box (tests).</summary>
    public IReadOnlyList<CheckBox> Boxes => [.. _boxes.Select(b => b.Box)];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (var (block, _) in _boxes)
        {
            block.PropertyChanged += OnBlockChanged;
        }

        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var (block, _) in _boxes)
        {
            block.PropertyChanged -= OnBlockChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnBlockChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MergeConflictBlock.TakeCurrent) or nameof(MergeConflictBlock.TakeIncoming))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        foreach (var (block, box) in _boxes)
        {
            box.IsChecked = block.Takes(_side);
        }
    }

    void ITextViewConnect.AddToTextView(TextView textView)
    {
        _textView = textView;
        textView.VisualLinesChanged += OnVisualLinesChanged;
        textView.ScrollOffsetChanged += OnVisualLinesChanged;
    }

    void ITextViewConnect.RemoveFromTextView(TextView textView)
    {
        textView.VisualLinesChanged -= OnVisualLinesChanged;
        textView.ScrollOffsetChanged -= OnVisualLinesChanged;
        _textView = null;
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => Reposition();

    protected override Size ArrangeOverride(Size finalSize)
    {
        _height = finalSize.Height; // Bounds isn't updated until the arrange is done
        Reposition();
        return base.ArrangeOverride(finalSize);
    }

    private void Reposition()
    {
        if (_textView?.Document is not { } document)
        {
            return;
        }

        var lineHeight = _textView.DefaultLineHeight;
        foreach (var (block, box) in _boxes)
        {
            var range = block.RangeIn(_side);
            if (range.End > document.TextLength)
            {
                box.IsVisible = false;
                continue;
            }

            var line = document.GetLineByOffset(range.Start);
            var top = _textView.GetVisualTopByDocumentLine(line.LineNumber) - _textView.ScrollOffset.Y;
            box.IsVisible = top + lineHeight > 0 && top < Math.Max(_height, Bounds.Height);
            SetLeft(box, 4);
            SetTop(box, top + Math.Max(0, (lineHeight - BoxSize) / 2));
        }
    }
}

/// <summary>
/// The merge editor (M13; redesigned 2026-10-19 after a classic IDE's): the incoming and current versions side by side
/// with a check box beside each conflict's block, and the editable result below them (or three columns), which shows the
/// merge base's text for each conflict still to resolve. A compact toolbar moves between conflicts, counts them and
/// accepts the merge; its gear menu holds the layout and the bulk actions. All three panes scroll together, and a status
/// bar under them shows the zoom, the caret, the indentation, the line endings and the encoding.
/// </summary>
public sealed class MergeEditorView : UserControl, IPaneView
{
    private static readonly double[] s_zooms = [0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    private readonly MergeEditorViewModel _viewModel;
    private readonly EditorFont _font;
    private readonly TextEditor _current;
    private readonly TextEditor _incoming;
    private readonly TextEditor _result;
    private readonly Control _currentPane;
    private readonly Control _incomingPane;
    private readonly Control _resultPane;
    private readonly CheckBox _currentHeader;
    private readonly CheckBox _incomingHeader;
    private readonly ConflictCheckMargin _currentMargin;
    private readonly ConflictCheckMargin _incomingMargin;
    private readonly Grid _body = new();
    private readonly TextBlock _summary;
    private readonly TextBlock _position;
    private readonly Button _accept;
    private readonly ComboBox _zoom;
    private bool _syncing;

    public MergeEditorView(MergeEditorViewModel viewModel, EditorFont font)
    {
        _viewModel = viewModel;
        _font = font;
        _current = Editor(font, readOnly: true, viewModel.CurrentTitle);
        _current.Document = new TextDocument(viewModel.CurrentText);
        _incoming = Editor(font, readOnly: true, viewModel.IncomingTitle);
        _incoming.Document = new TextDocument(viewModel.IncomingText);
        _result = Editor(font, readOnly: false, viewModel.ResultTitle);
        _result.Document = viewModel.Result;
        _current.TextArea.TextView.BackgroundRenderers.Add(new ConflictBlockRenderer(viewModel, MergePane.Current));
        _incoming.TextArea.TextView.BackgroundRenderers.Add(new ConflictBlockRenderer(viewModel, MergePane.Incoming));
        _result.TextArea.TextView.BackgroundRenderers.Add(new ConflictBlockRenderer(viewModel, MergePane.Result));
        _currentMargin = new ConflictCheckMargin(viewModel, MergeSide.Current);
        _incomingMargin = new ConflictCheckMargin(viewModel, MergeSide.Incoming);
        _current.TextArea.LeftMargins.Add(_currentMargin);
        _incoming.TextArea.LeftMargins.Add(_incomingMargin);
        foreach (var editor in (TextEditor[])[_current, _incoming, _result])
        {
            var from = editor;
            editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(from);
            editor.TextArea.Caret.PositionChanged += (_, _) => UpdatePosition(from);
            editor.TextArea.GotFocus += (_, _) => UpdatePosition(from);
        }

        _result.TextArea.Caret.PositionChanged += (_, _) => viewModel.CaretOffset = _result.CaretOffset;

        _currentHeader = Header(MergeSide.Current);
        _incomingHeader = Header(MergeSide.Incoming);
        var resultTitle = new TextBlock { Text = viewModel.ResultTitle };
        ToolTip.SetTip(resultTitle, viewModel.Path);
        _currentPane = Pane(_currentHeader, _current);
        _incomingPane = Pane(_incomingHeader, _incoming);
        _resultPane = Pane(resultTitle, _result);

        // The toolbar: previous and next conflict and the count; Accept Merge and the gear menu at the right end.
        _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        AutomationProperties.SetLiveSetting(_summary, AutomationLiveSetting.Polite);
        _accept = new Button { Content = Localizer.Get("mergeEditor.acceptMerge", "Accept Merge"), Margin = new Thickness(4, 0) };
        _accept.Classes.AddRange(["accent", "iconText", "compact"]);
        ButtonIcon.SetKind(_accept, "check");
        ToolTip.SetTip(_accept, WithShortcut(Localizer.Get("mergeEditor.acceptMergeTip", "Save the result and mark the file resolved"), "git.mergeEditor.acceptMerge"));
        _accept.Click += (_, _) => _ = AcceptMergeAsync();
        var spacer = new Border();
        spacer.Classes.Add("toolbarSpacer");
        var toolbar = new ToolbarPanel();
        toolbar.Children.Add(IconButton("arrow-up", Localizer.Get("mergeEditor.previousConflict", "Previous Conflict"), "git.mergeEditor.previousConflict", () => GoTo(-1)));
        toolbar.Children.Add(IconButton("arrow-down", Localizer.Get("mergeEditor.nextConflict", "Next Conflict"), "git.mergeEditor.nextConflict", () => GoTo(1)));
        toolbar.Children.Add(_summary);
        toolbar.Children.Add(spacer);
        toolbar.Children.Add(_accept);
        toolbar.Children.Add(GearMenu());

        // The status bar under the panes.
        _zoom = new ComboBox { ItemsSource = s_zooms.Select(z => $"{z * 100:0} %").ToList(), MinWidth = 76 };
        _zoom.Classes.Add("toolbar");
        AutomationProperties.SetName(_zoom, Localizer.Get("mergeEditor.zoom", "Zoom"));
        ToolTip.SetTip(_zoom, Localizer.Get("mergeEditor.zoom", "Zoom"));
        _zoom.SelectionChanged += (_, _) =>
        {
            if (_zoom.SelectedIndex >= 0)
            {
                viewModel.Zoom = s_zooms[_zoom.SelectedIndex];
            }
        };
        _position = Status(string.Empty, Localizer.Get("mergeEditor.caret", "Line and character of the caret"));
        var status = new DockPanel { Margin = new Thickness(4, 1) };
        DockPanel.SetDock(_zoom, Avalonia.Controls.Dock.Left);
        status.Children.Add(_zoom);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0) };
        right.Children.Add(_position);
        right.Children.Add(Status(viewModel.IndentationName, Localizer.Get("mergeEditor.indentation", "Indentation: spaces (SPC) or tabs (TABS)")));
        right.Children.Add(Status(viewModel.LineEndingName, Localizer.Get("mergeEditor.lineEndings", "Line endings")));
        right.Children.Add(Status(viewModel.EncodingName, Localizer.Get("mergeEditor.encoding", "Encoding")));
        status.Children.Add(right);
        var statusBar = new Border { Child = status, BorderThickness = new Thickness(0, 1, 0, 0) };
        statusBar.Bind(Border.BackgroundProperty, statusBar.GetResourceObservable("Shell.Panel"));
        statusBar.Bind(Border.BorderBrushProperty, statusBar.GetResourceObservable("Shell.Border"));

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(statusBar, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(statusBar);
        root.Children.Add(_body);
        Content = root;

        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        Arrange();
        ApplyZoom();
        Refresh();
        UpdatePosition(_result);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if ((viewModel.Conflicts.FirstOrDefault(b => !b.IsResolved) ?? (viewModel.Conflicts.Count > 0 ? viewModel.Conflicts[0] : null)) is { } first)
                {
                    Show(first);
                }
            },
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Reports a failed accept (set by the shell).</summary>
    public static Action<Exception>? ReportError { get; set; }

    public MergeEditorViewModel ViewModel => _viewModel;

    /// <summary>The result text (tests).</summary>
    public string ResultText => _result.Text;

    public TextEditor Result => _result;

    public TextEditor EditorFor(MergeSide side) => side == MergeSide.Current ? _current : _incoming;

    /// <summary>A header's check box, which takes every conflict from its side.</summary>
    public CheckBox HeaderFor(MergeSide side) => side == MergeSide.Current ? _currentHeader : _incomingHeader;

    /// <summary>The check boxes beside each conflict's block in a side's pane.</summary>
    public IReadOnlyList<CheckBox> CheckBoxesFor(MergeSide side) => (side == MergeSide.Current ? _currentMargin : _incomingMargin).Boxes;

    /// <summary>The toolbar's count (tests).</summary>
    public string SummaryText => _summary.Text ?? string.Empty;

    public bool CanAcceptMerge => _accept.IsEnabled;

    public void FocusPrimary() => _result.TextArea.Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.NavigateRequested += OnNavigateRequested;
        _font.PropertyChanged += OnFontChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.NavigateRequested -= OnNavigateRequested;
        _font.PropertyChanged -= OnFontChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MergeEditorViewModel.Summary) or nameof(MergeEditorViewModel.CanAccept) or nameof(MergeEditorViewModel.Conflicts):
                Refresh();
                break;
            case nameof(MergeEditorViewModel.Zoom):
                ApplyZoom();
                break;
            case nameof(MergeEditorViewModel.Layout) or nameof(MergeEditorViewModel.IncomingOnLeft):
                Arrange();
                break;
        }
    }

    private void OnNavigateRequested(object? sender, MergeConflictBlock block) => Show(block);

    private void OnFontChanged(object? sender, PropertyChangedEventArgs e) => ApplyZoom();

    private void OnThemeChanged(object? sender, EventArgs e) => Invalidate();

    /// <summary>The count, Accept Merge, the headers' boxes, and the blocks drawn again.</summary>
    private void Refresh()
    {
        _summary.Text = _viewModel.Summary;
        _accept.IsEnabled = _viewModel.CanAccept;
        _currentHeader.IsChecked = _viewModel.AllTake(MergeSide.Current);
        _incomingHeader.IsChecked = _viewModel.AllTake(MergeSide.Incoming);
        Invalidate();
    }

    private void Invalidate()
    {
        foreach (var editor in (TextEditor[])[_current, _incoming, _result])
        {
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
    }

    /// <summary>Lays the panes out: the two versions above the result, or three columns with the result in the middle.</summary>
    private void Arrange()
    {
        _body.Children.Clear();
        var left = _viewModel.LeftSide == MergeSide.Current ? _currentPane : _incomingPane;
        var right = _viewModel.LeftSide == MergeSide.Current ? _incomingPane : _currentPane;
        if (_viewModel.Layout == MergeEditorViewModel.LayoutThreeColumns)
        {
            _body.RowDefinitions = [];
            _body.ColumnDefinitions = new ColumnDefinitions("*,4,*,4,*");
            Place(left, 0, 0);
            Place(Splitter(GridResizeDirection.Columns), 0, 1);
            Place(_resultPane, 0, 2);
            Place(Splitter(GridResizeDirection.Columns), 0, 3);
            Place(right, 0, 4);
        }
        else
        {
            _body.RowDefinitions = new RowDefinitions("*,4,*");
            _body.ColumnDefinitions = new ColumnDefinitions("*,4,*");
            Place(left, 0, 0);
            Place(Splitter(GridResizeDirection.Columns), 0, 1);
            Place(right, 0, 2);
            Place(Splitter(GridResizeDirection.Rows), 1, 0, 3);
            Place(_resultPane, 2, 0, 3);
        }
    }

    private void Place(Control control, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, columnSpan);
        _body.Children.Add(control);
    }

    private static GridSplitter Splitter(GridResizeDirection direction) => new() { ResizeDirection = direction };

    private void ApplyZoom()
    {
        foreach (var editor in (TextEditor[])[_current, _incoming, _result])
        {
            editor.FontFamily = _font.Family;
            editor.FontSize = _font.Size * _viewModel.Zoom;
        }

        var index = Array.IndexOf(s_zooms, _viewModel.Zoom);
        if (index >= 0 && _zoom.SelectedIndex != index)
        {
            _zoom.SelectedIndex = index;
        }
    }

    /// <summary>Ctrl+wheel zooms all three panes, a step at a time.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) || e.Delta.Y == 0)
        {
            return;
        }

        var index = Array.IndexOf(s_zooms, _viewModel.Zoom);
        index = index < 0 ? Array.IndexOf(s_zooms, 1.0) : Math.Clamp(index + (e.Delta.Y > 0 ? 1 : -1), 0, s_zooms.Length - 1);
        _viewModel.Zoom = s_zooms[index];
        e.Handled = true;
    }

    private void UpdatePosition(TextEditor editor)
    {
        if (editor != _result && !editor.TextArea.IsKeyboardFocusWithin)
        {
            return;
        }

        var caret = editor.TextArea.Caret;
        _position.Text = Localizer.Format("mergeEditor.position", "Ln: {0}   Ch: {1}", caret.Line, caret.Column);
    }

    // ------------------------------------------------------------------ resolving and moving

    /// <summary>Resolves the conflict at the result's caret: <c>current</c>, <c>incoming</c>, <c>both</c> or <c>base</c>.</summary>
    public void Accept(string choice)
    {
        _viewModel.CaretOffset = _result.CaretOffset;
        _viewModel.Accept(choice);
    }

    public void AcceptAll(string choice) => _viewModel.AcceptAll(choice);

    public void GoTo(int direction)
    {
        _viewModel.CaretOffset = _result.CaretOffset;
        _viewModel.GoTo(direction);
    }

    /// <summary>Shows a conflict: the result's caret at its block, scrolled into view (the other panes follow).</summary>
    private void Show(MergeConflictBlock block)
    {
        if (block.ResultStart > _result.Document.TextLength)
        {
            return;
        }

        _result.CaretOffset = block.ResultStart;
        var location = _result.Document.GetLocation(block.ResultStart);
        _result.ScrollTo(location.Line, location.Column);
    }

    public async Task AcceptMergeAsync()
    {
        try
        {
            await _viewModel.AcceptAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is Codalus.Workbench.Git.GitException or InvalidOperationException or IOException)
        {
            ReportError?.Invoke(ex);
        }
    }

    // ------------------------------------------------------------------ scrolling together

    /// <summary>Scrolls the other panes with <paramref name="from"/>: the same column, and the matching line (conflict blocks line up).</summary>
    private void Sync(TextEditor from)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            var view = from.TextArea.TextView;
            var lineHeight = view.DefaultLineHeight;
            if (lineHeight <= 0 || view.Document is null)
            {
                return;
            }

            var line = view.ScrollOffset.Y / lineHeight;
            var fromAnchors = Anchors(from);
            foreach (var editor in (TextEditor[])[_current, _incoming, _result])
            {
                if (editor == from || editor.TextArea.TextView is not { Document: not null } target)
                {
                    continue;
                }

                var scrollable = (IScrollable)target;
                var y = MapLine(fromAnchors, Anchors(editor), line) * target.DefaultLineHeight;
                var maxX = Math.Max(0, scrollable.Extent.Width - scrollable.Viewport.Width);
                var maxY = Math.Max(0, scrollable.Extent.Height - scrollable.Viewport.Height);
                scrollable.Offset = new Vector(Math.Clamp(view.ScrollOffset.X, 0, maxX), Math.Clamp(y, 0, maxY));
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>A pane's lines where conflict blocks start and end: 0, then each block's first and after-last line, then the line count.</summary>
    private double[] Anchors(TextEditor editor)
    {
        var pane = editor == _current ? MergePane.Current : editor == _incoming ? MergePane.Incoming : MergePane.Result;
        var document = editor.Document;
        var anchors = new List<double> { 0 };
        foreach (var block in _viewModel.Conflicts)
        {
            var range = RangeOf(block, pane);
            if (range.End > document.TextLength)
            {
                continue;
            }

            var first = document.GetLineByOffset(range.Start).LineNumber - 1;
            var last = range.Length == 0 ? first : document.GetLineByOffset(range.End - 1).LineNumber;
            anchors.Add(Math.Max(first, anchors[^1]));
            anchors.Add(Math.Max(last, anchors[^1]));
        }

        anchors.Add(Math.Max(document.LineCount, anchors[^1]));
        return [.. anchors];
    }

    /// <summary>Maps a line between two panes' anchors, proportionally within each stretch.</summary>
    internal static double MapLine(double[] from, double[] to, double line)
    {
        if (from.Length != to.Length)
        {
            return line;
        }

        for (var i = 0; i < from.Length - 1; i++)
        {
            if (line < from[i + 1] || i == from.Length - 2)
            {
                var span = from[i + 1] - from[i];
                var t = span <= 0 ? 0 : Math.Clamp((line - from[i]) / span, 0, 1);
                return to[i] + (t * (to[i + 1] - to[i]));
            }
        }

        return line;
    }

    // ------------------------------------------------------------------ pieces

    internal static MergeRange RangeOf(MergeConflictBlock block, MergePane pane) => pane switch
    {
        MergePane.Current => block.Current,
        MergePane.Incoming => block.Incoming,
        _ => block.Result,
    };

    /// <summary>The top and bottom of a block's lines in a text view (an empty block: a thin band at its place).</summary>
    internal static (double Top, double Bottom)? LinesOf(TextView textView, TextDocument document, MergeRange range)
    {
        if (range.Start > document.TextLength)
        {
            return null;
        }

        var lineHeight = textView.DefaultLineHeight;
        var first = document.GetLineByOffset(range.Start);
        var top = textView.GetVisualTopByDocumentLine(first.LineNumber) - textView.ScrollOffset.Y;
        if (range.Length == 0)
        {
            return (top - 2, top + 2);
        }

        var last = document.GetLineByOffset(Math.Max(range.Start, range.End - 1));
        return (top, textView.GetVisualTopByDocumentLine(last.LineNumber) - textView.ScrollOffset.Y + lineHeight);
    }

    internal static IBrush? Brush(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;

    private string WithShortcut(string text, string command) =>
        _viewModel.ShortcutFor?.Invoke(command) is { Length: > 0 } keys ? $"{text} ({keys})" : text;

    /// <summary>A header: the side's name as the check box's label; checking it takes every conflict from that side.</summary>
    private CheckBox Header(MergeSide side)
    {
        var title = _viewModel.TitleFor(side);
        var box = new CheckBox
        {
            Content = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(box, title);
        ToolTip.SetTip(box, Localizer.Format("mergeEditor.takeAllFrom", "{0}: check to take every conflict from this side", title));
        box.Click += (_, _) =>
        {
            _viewModel.TakeAll(side, _viewModel.AllTake(side) != true);
            box.IsChecked = _viewModel.AllTake(side);
        };
        return box;
    }

    private static DockPanel Pane(Control header, TextEditor editor)
    {
        var border = new Border { Child = header };
        border.Classes.Add("gridHeader");
        var pane = new DockPanel();
        DockPanel.SetDock(border, Avalonia.Controls.Dock.Top);
        pane.Children.Add(border);
        pane.Children.Add(editor);
        return pane;
    }

    private Button IconButton(string icon, string name, string command, Action action)
    {
        var button = new Button();
        button.Classes.AddRange(["toolbar", "icon"]);
        ButtonIcon.SetKind(button, icon);
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, WithShortcut(name, command));
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>The gear menu: the panes' layout, which side is on the left, and the bulk actions.</summary>
    private DropDownButton GearMenu()
    {
        var name = Localizer.Get("mergeEditor.options", "Merge Editor Options");
        var sourcesAbove = Item(Localizer.Get("mergeEditor.layoutSourcesAbove", "Versions Above the Result"), () => _viewModel.Layout = MergeEditorViewModel.LayoutSourcesAbove, MenuItemToggleType.Radio);
        var threeColumns = Item(Localizer.Get("mergeEditor.layoutThreeColumns", "Three Columns"), () => _viewModel.Layout = MergeEditorViewModel.LayoutThreeColumns, MenuItemToggleType.Radio);
        var incomingOnLeft = Item(Localizer.Get("mergeEditor.incomingOnLeft", "Incoming on the Left"), () => _viewModel.IncomingOnLeft = !_viewModel.IncomingOnLeft, MenuItemToggleType.CheckBox);
        var menu = new MenuFlyout
        {
            Items =
            {
                sourcesAbove,
                threeColumns,
                new Separator(),
                incomingOnLeft,
                new Separator(),
                Item(Localizer.Get("mergeEditor.takeAllIncoming", "Take All Incoming"), () => _viewModel.TakeAll(MergeSide.Incoming, true)),
                Item(Localizer.Get("mergeEditor.takeAllCurrent", "Take All Current"), () => _viewModel.TakeAll(MergeSide.Current, true)),
                Item(Localizer.Get("mergeEditor.resetAll", "Reset All Conflicts"), _viewModel.ResetAll),
            },
        };
        menu.Opening += (_, _) =>
        {
            sourcesAbove.IsChecked = _viewModel.Layout == MergeEditorViewModel.LayoutSourcesAbove;
            threeColumns.IsChecked = _viewModel.Layout == MergeEditorViewModel.LayoutThreeColumns;
            incomingOnLeft.IsChecked = _viewModel.IncomingOnLeft;
        };
        var button = new DropDownButton { Content = new Icon { Kind = "gear" }, Flyout = menu };
        button.Classes.Add("toolbar");
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        return button;
    }

    private static MenuItem Item(string header, Action action, MenuItemToggleType toggle = MenuItemToggleType.None)
    {
        var item = new MenuItem { Header = header, ToggleType = toggle };
        item.Click += (_, _) => action();
        return item;
    }

    private static TextBlock Status(string text, string tooltip)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        block.Classes.Add("small");
        ToolTip.SetTip(block, tooltip);
        return block;
    }

    private static TextEditor Editor(EditorFont font, bool readOnly, string name)
    {
        var editor = new TextEditor
        {
            IsReadOnly = readOnly,
            ShowLineNumbers = true,
            FontFamily = font.Family,
            FontSize = font.Size,
            Padding = new Thickness(4, 2),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        };
        editor.Bind(BackgroundProperty, editor.GetResourceObservable("Shell.Editor.Background"));
        editor.Bind(ForegroundProperty, editor.GetResourceObservable("Shell.Editor.Foreground"));
        AutomationProperties.SetName(editor, name);
        return editor;
    }
}
