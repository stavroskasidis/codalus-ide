using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>
/// One side of a diff document (#86, D45): a header with the file's icon, name and what the side is ("(Index)"), the
/// navigation bar's row (the open document's side lists its types and members; the other keeps the row, empty and
/// disabled, so the lines stay aligned), the code, and a status bar with the caret, the indentation, the line endings
/// and the encoding (the left side's also has the zoom box).
/// </summary>
internal sealed class DiffPaneView : DockPanel
{
    private readonly TextBlock _name = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _note = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Icon _icon = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _position;
    private readonly TextBlock _selection;
    private readonly TextBlock _indentation;
    private readonly TextBlock _lineEnding;
    private readonly TextBlock _encoding;
    private readonly NavigationBar _navigationBar = new();
    private readonly Border _navigationPlaceholder;

    public DiffPaneView(DiffPaneSide side, TextEditor editor, DiffLineNumberMargin numbers, ComboBox? zoom)
    {
        Side = side;
        Editor = editor;
        Numbers = numbers;

        // The header.
        _label.Classes.Add("muted");
        _note.Classes.Add("muted");
        var title = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_note, Avalonia.Controls.Dock.Right);
        title.Children.Add(_note);
        var start = new StackPanel { Orientation = Orientation.Horizontal, Children = { _icon, _name, _label } };
        title.Children.Add(start);
        Header = new Border { Child = title };
        Header.Classes.Add("gridHeader");

        // The navigation bar's row: the bar on the open document's side, an empty disabled row on the other.
        var types = new ComboBox { PlaceholderText = Localizer.Get("diff.types", "Types"), IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        var members = new ComboBox { PlaceholderText = Localizer.Get("diff.members", "Members"), IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        types.Classes.Add("navigationBar");
        members.Classes.Add("navigationBar");
        Grid.SetColumn(members, 1);
        _navigationPlaceholder = new Border { Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { types, members } } };
        _navigationPlaceholder.Classes.Add("navigationBar");
        AutomationProperties.SetName(_navigationPlaceholder, Localizer.Get("diff.noNavigation", "Navigation bar (the other side is the open document)"));
        NavigationRow = new Panel { Children = { _navigationPlaceholder, _navigationBar } };
        _navigationBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                _navigationPlaceholder.IsVisible = !_navigationBar.IsVisible;
            }
        };

        // The status bar.
        _position = Status(Localizer.Get("diff.caret", "Line and character of the caret"));
        _selection = Status(Localizer.Get("diff.selection", "Lines selected"));
        _indentation = Status(Localizer.Get("diff.indentation", "Indentation: spaces (SPC) or tabs (TABS)"));
        _lineEnding = Status(Localizer.Get("diff.lineEndings", "Line endings"));
        _encoding = Status(Localizer.Get("diff.encoding", "Encoding"));
        var status = new DockPanel { Margin = new Thickness(4, 1), MinHeight = 22 };
        if (zoom is not null)
        {
            DockPanel.SetDock(zoom, Avalonia.Controls.Dock.Left);
            status.Children.Add(zoom);
        }

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0) };
        right.Children.AddRange([_position, _selection, _indentation, _lineEnding, _encoding]);
        status.Children.Add(right);
        StatusBar = new Border { Child = status, BorderThickness = new Thickness(0, 1, 0, 0) };
        StatusBar.Bind(Border.BackgroundProperty, StatusBar.GetResourceObservable("Shell.Panel"));
        StatusBar.Bind(Border.BorderBrushProperty, StatusBar.GetResourceObservable("Shell.Border"));

        DockPanel.SetDock(Header, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(NavigationRow, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(StatusBar, Avalonia.Controls.Dock.Bottom);
        Children.Add(Header);
        Children.Add(NavigationRow);
        Children.Add(StatusBar);
        Children.Add(editor);
    }

    public DiffPaneSide Side { get; }

    public TextEditor Editor { get; }

    public DiffLineNumberMargin Numbers { get; }

    public Border Header { get; }

    public Panel NavigationRow { get; }

    public Border StatusBar { get; }

    public NavigationBar NavigationBar => _navigationBar;

    /// <summary>The header's text (tests): the name, what the side is, and its note.</summary>
    public string HeaderText => string.Join(' ', new[] { _name.Text, _label.Text, _note.Text }.Where(t => !string.IsNullOrEmpty(t)));

    /// <summary>The status bar's items (tests).</summary>
    public IReadOnlyList<string> StatusItems => [.. new[] { _position, _selection, _indentation, _lineEnding, _encoding }.Where(b => b.IsVisible && !string.IsNullOrEmpty(b.Text)).Select(b => b.Text!)];

    public void SetHeader(string name, string label, string? note, string? tooltip)
    {
        _icon.Kind = FileIcons.ForFile(name);
        _name.Text = name;
        _label.Text = label.Length > 0 ? $"({label})" : string.Empty;
        _note.Text = note ?? string.Empty;
        ToolTip.SetTip(Header, tooltip);
    }

    public void SetFormat(DiffSide side)
    {
        _indentation.Text = side.Indentation;
        _lineEnding.Text = side.LineEnding;
        _encoding.Text = side.Encoding;
    }

    public void SetPosition(string position, string? selection)
    {
        _position.Text = position;
        _selection.Text = selection ?? string.Empty;
        _selection.IsVisible = selection is not null;
    }

    /// <summary>A narrow side keeps Ln and Ch only (G1).</summary>
    public void SetNarrow(bool narrow)
    {
        foreach (var item in new[] { _indentation, _lineEnding, _encoding })
        {
            item.IsVisible = !narrow;
        }
    }

    /// <summary>The navigation bar's row: shown or not, and the bar attached to the open document's symbols, or empty.</summary>
    public void SetNavigation(bool shown, INavigationSource? source)
    {
        NavigationRow.IsVisible = shown;
        if (source is null)
        {
            _navigationBar.Detach();
            _navigationBar.IsVisible = false;
        }
        else
        {
            _navigationBar.Attach(source);
        }

        _navigationPlaceholder.IsVisible = !_navigationBar.IsVisible;
    }

    private static TextBlock Status(string tooltip)
    {
        var block = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        block.Classes.Add("small");
        ToolTip.SetTip(block, tooltip);
        return block;
    }
}

/// <summary>
/// The open document's symbols for a diff side whose text is the document's (D45 question 8): the caret is the side's,
/// mapped from its pane line to the file line, and choosing a symbol moves the side's caret to its line.
/// </summary>
internal sealed class DiffNavigationSource(CodeEditorController controller, TextEditor editor, Func<DiffPane> pane, Action<int> showLine) : INavigationSource
{
    private EventHandler? _caretChanged;

    public IReadOnlyList<DocumentSymbol> Symbols => controller.Insights.Symbols;

    public event EventHandler? SymbolsChanged
    {
        add => controller.Insights.SymbolsChanged += value;
        remove => controller.Insights.SymbolsChanged -= value;
    }

    public event EventHandler? CaretChanged
    {
        add
        {
            if (_caretChanged is null)
            {
                editor.TextArea.Caret.PositionChanged += OnCaret;
            }

            _caretChanged += value;
        }

        remove
        {
            _caretChanged -= value;
            if (_caretChanged is null)
            {
                editor.TextArea.Caret.PositionChanged -= OnCaret;
            }
        }
    }

    private void OnCaret(object? sender, EventArgs e) => _caretChanged?.Invoke(this, e);

    public IReadOnlyList<DocumentSymbol> SymbolPathAtCaret()
    {
        var caret = editor.TextArea.Caret;
        var (old, line) = pane().SourceOf(caret.Line - 1);
        return old || line <= 0 ? [] : controller.Insights.SymbolPath(new Position(line - 1, caret.Column - 1));
    }

    public void Navigate(Position position) => showLine(position.Line + 1);
}

/// <summary>
/// A diff document (ADR 0008, #86, redesigned after the classic IDE's, D45): a toolbar with the next and previous
/// change (<c>F8</c>, <c>Shift+F8</c>), the change count and the removed and added lines, and the gear menu (Side by Side
/// or Inline, kept in <c>git.diff.layout</c>; Refresh; Open the file); then the two sides with today's splitter between
/// them (drag to resize, double-click for half and half; its place kept while the document is open), or one inline
/// pane. Each side is colored like the editor, numbers its own file's lines and has its own status bar.
/// TODO(issue): CodeLens and diagnostics on the working-tree side, and editing inside the diff (they need the filler
/// rows as view zones of a live editor).
/// TODO(issue): Ignore Trim Whitespace (it would make staging inexact), and the classic IDE's project drop-down.
/// </summary>
public sealed class DiffView : UserControl, IPaneView
{
    private static readonly double[] s_zooms = [0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    /// <summary>Narrower sides drop the navigation bars (G1).</summary>
    private const double NavigationMinWidth = 360;

    /// <summary>Narrower sides keep only Ln and Ch in their status bars (G1).</summary>
    private const double StatusMinWidth = 300;

    private readonly DiffViewModel _viewModel;
    private readonly EditorFont _font;
    private readonly SourceLineColorizer _leftColors;
    private readonly SourceLineColorizer _rightColors;
    private readonly SourceLineColorizer _inlineColors;
    private readonly DiffPaneView _left;
    private readonly DiffPaneView _right;
    private readonly DiffPaneView _inline;
    private readonly Grid _sideBySide;
    private readonly GridSplitter _splitter;
    private readonly TextBlock _count;
    private readonly TextBlock _removed;
    private readonly TextBlock _added;
    private readonly Border _removedChip;
    private readonly Border _addedChip;
    private readonly Button _next;
    private readonly Button _previous;
    private readonly ComboBox _zoom;
    private readonly ComboBox _inlineZoom;
    private readonly ToolbarPanel _toolbar;
    private readonly ProgressBar _progress;
    private readonly List<(ContentControl Control, string Text)> _labeled = [];
    private readonly ToggleButton? _stagingToggle;
    private readonly ToggleButton _summaryToggle;
    private readonly Border _empty;
    private readonly Icon _emptyIcon = new() { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _emptyMessage = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontWeight = FontWeight.SemiBold, MaxWidth = 520 };
    private readonly TextBlock _emptyDetail = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520 };
    private readonly StackPanel _emptyActions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Grid _binary = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly DiffStagingMargin? _rightStaging;
    private readonly DiffStagingMargin? _inlineStaging;
    private IReadOnlySet<int>? _outlined;
    private DiffPane _leftPane = DiffPane.Empty;
    private DiffPane _rightPane = DiffPane.Empty;
    private DiffPane _inlinePane = DiffPane.Empty;
    private DiffPaneView _active;
    private bool _syncing;
    private bool _firstChangeShown;

    public DiffView(DiffViewModel viewModel, EditorFont font)
    {
        _viewModel = viewModel;
        _font = font;
        _leftColors = new SourceLineColorizer(line => ColorsOf(_leftPane, line));
        _rightColors = new SourceLineColorizer(line => ColorsOf(_rightPane, line));
        _inlineColors = new SourceLineColorizer(line => ColorsOf(_inlinePane, line));
        _zoom = ZoomBox();
        _inlineZoom = ZoomBox();
        _left = Pane(DiffPaneSide.Old, Localizer.Get("diff.oldText", "Old text"), _leftColors, () => _leftPane, _zoom);
        _right = Pane(DiffPaneSide.New, Localizer.Get("diff.newText", "New text"), _rightColors, () => _rightPane, null);
        _inline = Pane(DiffPaneSide.Inline, Localizer.Get("diff.inlineText", "Diff"), _inlineColors, () => _inlinePane, _inlineZoom);
        _active = _right;
        if (viewModel.CanStage || viewModel.CanUnstage)
        {
            _rightStaging = StagingMargin(_right, () => _rightPane);
            _inlineStaging = StagingMargin(_inline, () => _inlinePane);
        }

        _splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        _splitter.DoubleTapped += (_, _) => ResetSplit();
        _splitter.DragCompleted += (_, _) =>
        {
            var total = _left.Bounds.Width + _right.Bounds.Width;
            if (total > 0)
            {
                _viewModel.SplitRatio = _left.Bounds.Width / total;
            }
        };
        ToolTip.SetTip(_splitter, Localizer.Get("diff.splitter", "Drag to resize the sides; double-click for half and half"));
        _sideBySide = new Grid();
        Grid.SetColumn(_splitter, 1);
        Grid.SetColumn(_right, 2);
        _sideBySide.Children.AddRange([_left, _splitter, _right]);
        _sideBySide.SizeChanged += (_, _) => UpdateWidths();
        SizeChanged += (_, _) => SetCompact(Bounds.Width is > 0 and < 760);
        _inline.SizeChanged += (_, _) => UpdateWidths();
        ApplySplit();
        _left.Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        // The toolbar.
        _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        AutomationProperties.SetLiveSetting(_count, AutomationLiveSetting.Polite);
        (_removedChip, _removed) = Chip("Shell.Diff.Removed.Line", "Shell.Diff.Removed.Foreground");
        (_addedChip, _added) = Chip("Shell.Diff.Added.Line", "Shell.Diff.Added.Foreground");
        _next = IconButton("arrow-down", Localizer.Get("diff.nextChange", "Next Change"), "git.diff.nextChange", () => GoToChange(1));
        _previous = IconButton("arrow-up", Localizer.Get("diff.previousChange", "Previous Change"), "git.diff.previousChange", () => GoToChange(-1));
        var spacer = new Border();
        spacer.Classes.Add("toolbarSpacer");
        _toolbar = new ToolbarPanel();
        _toolbar.Children.AddRange([_next, _previous, _count, _removedChip, _addedChip]);
        if (viewModel.CanStage)
        {
            _toolbar.Children.Add(Separator());
            _toolbar.Children.Add(LabeledButton("add", Localizer.Get("diff.stageAll", "Stage All"), Localizer.Get("diff.stageAllTip", "Stage All: the whole file"), "git.diff.stageAll", () => _ = StageAllAsync()));
            _toolbar.Children.Add(LabeledButton("discard", Localizer.Get("diff.discardAll", "Discard All…"), Localizer.Get("diff.discardAllTip", "Discard All: every change in the file (asks first)"), "git.diff.discardAll", () => _ = DiscardAllAsync()));
        }
        else if (viewModel.CanUnstage)
        {
            _toolbar.Children.Add(Separator());
            _toolbar.Children.Add(LabeledButton("subtract", Localizer.Get("diff.unstageAll", "Unstage All"), Localizer.Get("diff.unstageAllTip", "Unstage All: the whole file"), "git.diff.unstageAll", () => _ = UnstageAllAsync()));
        }

        _toolbar.Children.Add(spacer);
        if (viewModel.CanStage || viewModel.CanUnstage)
        {
            var name = Localizer.Get("diff.showStagingControls", "Show Staging Controls");
            _stagingToggle = new ToggleButton { Content = name, IsChecked = viewModel.ShowStagingControls };
            _stagingToggle.Classes.AddRange(["toolbar", "iconText"]);
            ButtonIcon.SetKind(_stagingToggle, "staging-controls");
            AutomationProperties.SetName(_stagingToggle, name);
            ToolTip.SetTip(_stagingToggle, name);
            _stagingToggle.IsCheckedChanged += (_, _) => viewModel.ShowStagingControls = _stagingToggle.IsChecked == true;
            _labeled.Add((_stagingToggle, name));
            _toolbar.Children.Add(_stagingToggle);
        }

        var summary = Localizer.Get("diff.summary", "Summary");
        _summaryToggle = new ToggleButton { Content = summary, IsChecked = viewModel.IsSummary };
        _summaryToggle.Classes.AddRange(["toolbar", "iconText"]);
        ButtonIcon.SetKind(_summaryToggle, "fold");
        AutomationProperties.SetName(_summaryToggle, summary);
        ToolTip.SetTip(_summaryToggle, Localizer.Get("diff.summaryTip", "Summary: fold the unchanged lines"));
        _summaryToggle.IsCheckedChanged += (_, _) => viewModel.IsSummary = _summaryToggle.IsChecked == true;
        _labeled.Add((_summaryToggle, summary));
        _toolbar.Children.Add(_summaryToggle);
        _toolbar.Children.Add(Separator());
        _toolbar.Children.Add(GearMenu());
        _progress = new ProgressBar { IsIndeterminate = true, Height = 2, MinHeight = 2, IsVisible = false };
        AutomationProperties.SetName(_progress, Localizer.Get("diff.working", "Working"));

        _emptyDetail.Classes.Add("muted");
        _empty = new Border
        {
            Child = new StackPanel
            {
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(24),
                Children = { _emptyIcon, _binary, _emptyMessage, _emptyDetail, _emptyActions },
            },
            IsVisible = false,
        };
        _empty.Bind(Border.BackgroundProperty, _empty.GetResourceObservable("Shell.Editor.Background"));
        AutomationProperties.SetLiveSetting(_emptyMessage, AutomationLiveSetting.Polite);
        var body = new Panel { Children = { _sideBySide, _inline, _empty } };
        var root = new DockPanel();
        DockPanel.SetDock(_toolbar, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(_progress, Avalonia.Controls.Dock.Top);
        root.Children.Add(_toolbar);
        root.Children.Add(_progress);
        root.Children.Add(body);
        Content = root;

        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        Rebuild();
        ApplyZoom();
        ApplyTheme();
    }

    /// <summary>Shows git errors (set by the shell).</summary>
    public static Action<Exception>? ReportError { get; set; }

    public DiffViewModel ViewModel => _viewModel;

    // ------------------------------------------------------------------ the panes

    private DiffPaneView Pane(DiffPaneSide side, string name, SourceLineColorizer colors, Func<DiffPane> pane, ComboBox? zoom)
    {
        var editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = false,
            FontFamily = _font.Family,
            FontSize = _font.Size,
            Padding = new Thickness(4, 2),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        };
        editor.Options.HighlightCurrentLine = true;
        editor.Bind(BackgroundProperty, editor.GetResourceObservable("Shell.Editor.Background"));
        editor.Bind(ForegroundProperty, editor.GetResourceObservable("Shell.Editor.Foreground"));
        editor.TextArea.TextView.BackgroundRenderers.Add(new DiffRenderer(editor, pane, i => WordsOf(pane(), i), () => _outlined));
        editor.TextArea.TextView.LineTransformers.Add(colors);
        var numbers = new DiffLineNumberMargin(pane, CurrentChange);
        numbers.CaretLine = () => editor.TextArea.Caret.Line;
        editor.TextArea.LeftMargins.Add(numbers);
        editor.TextArea.LeftMargins.Add(new DiffSignMargin(pane));
        AutomationProperties.SetName(editor, name);

        var view = new DiffPaneView(side, editor, numbers, zoom);
        editor.TextArea.Caret.PositionChanged += (_, _) => OnCaretMoved(view);
        editor.TextArea.SelectionChanged += (_, _) => UpdatePosition(view);
        editor.TextArea.GotFocus += (_, _) =>
        {
            _active = view;
            OnCaretMoved(view);
        };
        editor.TextArea.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && editor.GetPositionFromPoint(e.GetPosition(editor)) is { } position)
            {
                OpenFoldAt(view, position.Line);
            }
        };
        editor.TextArea.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenFoldAt(view, editor.TextArea.Caret.Line);
                e.Handled = true;
            }
        };
        editor.TextArea.TextView.LineTransformers.Add(new FoldLineColorizer(editor, pane));
        editor.ContextMenu = new ContextMenu();
        editor.ContextMenu.Opening += (_, _) =>
        {
            _active = view;
            FillContextMenu(editor.ContextMenu);
        };
        return view;
    }

    private DiffPaneView PaneOf(DiffPaneSide side) => side switch
    {
        DiffPaneSide.Old => _left,
        DiffPaneSide.New => _right,
        _ => _inline,
    };

    private DiffPane PaneLinesOf(DiffPaneView view) => view == _left ? _leftPane : view == _right ? _rightPane : _inlinePane;

    private void ApplySplit()
    {
        var ratio = Math.Clamp(_viewModel.SplitRatio, 0.1, 0.9);
        _sideBySide.ColumnDefinitions =
        [
            new ColumnDefinition(ratio, GridUnitType.Star) { MinWidth = 120 },
            new ColumnDefinition(4, GridUnitType.Pixel),
            new ColumnDefinition(1 - ratio, GridUnitType.Star) { MinWidth = 120 },
        ];
    }

    // ------------------------------------------------------------------ attaching and the view model

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel.Changed += OnChanged;
        _viewModel.ColorsChanged += OnColorsChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _font.PropertyChanged += OnFontChanged;
        ActualThemeVariantChanged += OnThemeVariantChanged;
        if (_viewModel.Services is { } services)
        {
            services.Editor.ThemeChanged += OnThemeChanged;
        }

        Rebuild();
        ApplyTheme();
        Avalonia.Threading.Dispatcher.UIThread.Post(RestoreState, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SaveState();
        _viewModel.Changed -= OnChanged;
        _viewModel.ColorsChanged -= OnColorsChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _font.PropertyChanged -= OnFontChanged;
        ActualThemeVariantChanged -= OnThemeVariantChanged;
        if (_viewModel.Services is { } services)
        {
            services.Editor.ThemeChanged -= OnThemeChanged;
        }

        foreach (var pane in new[] { _left, _right, _inline })
        {
            pane.SetNavigation(pane.NavigationRow.IsVisible, null);
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(Rebuild);

    private void OnColorsChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        Redraw();
        UpdateNavigation();
    });

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DiffViewModel.Zoom):
                ApplyZoom();
                break;
            case nameof(DiffViewModel.ShowStagingControls):
                ApplyStagingControls();
                break;
            case nameof(DiffViewModel.IsBusy) or nameof(DiffViewModel.BusyText) or nameof(DiffViewModel.IsLoading):
                ApplyBusy();
                break;
        }
    }

    /// <summary>
    /// The lines, or the message instead of them (E1–E6): no changes (Open Staged Changes when some are staged),
    /// only the line endings, the encoding or the file mode, a binary file's sizes, a file too large (Compare Anyway).
    /// TODO(issue): image previews for binary image files.
    /// </summary>
    private void ApplyState()
    {
        var vm = _viewModel;
        var lines = vm.State == DiffState.Lines;
        _empty.IsVisible = !lines && vm.IsLoaded;
        _summaryToggle.IsEnabled = lines;
        if (_stagingToggle is not null)
        {
            _stagingToggle.IsEnabled = lines;
        }

        foreach (var (control, _) in _labeled)
        {
            if (control != _stagingToggle && control != _summaryToggle)
            {
                control.IsEnabled = vm.State != DiffState.NoChanges && !vm.IsBusy;
            }
        }

        if (_summaryToggle.IsChecked != vm.IsSummary)
        {
            _summaryToggle.IsChecked = vm.IsSummary;
        }

        if (lines)
        {
            return;
        }

        _emptyIcon.Kind = vm.State switch
        {
            DiffState.NoChanges => "check-circle",
            DiffState.TooLarge => "warning",
            DiffState.Binary => "file",
            _ => "info",
        };
        _emptyIcon.IsVisible = vm.State != DiffState.Binary;
        _emptyMessage.Text = vm.StateMessage;
        _emptyMessage.IsVisible = vm.State != DiffState.Binary;
        _emptyDetail.Text = vm.StateDetail;
        _emptyDetail.IsVisible = vm.StateDetail is not null;
        _binary.IsVisible = vm.State == DiffState.Binary;
        _binary.Children.Clear();
        if (vm.State == DiffState.Binary)
        {
            foreach (var (side, size, column) in new[] { (vm.OldSide, vm.Sizes.Old, 0), (vm.NewSide, vm.Sizes.New, 1) })
            {
                var name = new TextBlock { Text = $"{side.Name} ({side.Label})", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
                var kind = new TextBlock { Text = Localizer.Get("diff.binaryFile", "Binary file"), HorizontalAlignment = HorizontalAlignment.Center };
                var bytes = new TextBlock { Text = size, HorizontalAlignment = HorizontalAlignment.Center };
                bytes.Classes.Add("muted");
                var card = new StackPanel { Spacing = 4, Margin = new Thickness(24, 0), Children = { new Icon { Kind = "file", Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center }, name, kind, bytes } };
                AutomationProperties.SetName(card, $"{side.Name} ({side.Label}): {kind.Text}, {size}");
                Grid.SetColumn(card, column);
                _binary.Children.Add(card);
            }
        }

        _emptyActions.Children.Clear();
        if (vm.State == DiffState.NoChanges && vm.OffersStagedChanges)
        {
            _emptyActions.Children.Add(Action("compare", Localizer.Get("diff.openStaged", "Open Staged Changes"), () => _ = vm.OpenStaged?.Invoke(vm.Path)));
        }

        if (vm.State == DiffState.TooLarge)
        {
            _emptyActions.Children.Add(Action("compare", Localizer.Get("diff.compareAnyway", "Compare Anyway"), () => _ = CompareAnywayAsync()));
        }

        _emptyActions.Children.Add(Action("document-text", Localizer.Get("diff.openFileAction", "Open File"), OpenFile));
    }

    private static Button Action(string icon, string text, Action action)
    {
        var button = new Button { Content = text };
        button.Classes.Add("iconText");
        ButtonIcon.SetKind(button, icon);
        button.Click += (_, _) => action();
        return button;
    }

    private async Task CompareAnywayAsync()
    {
        try
        {
            await _viewModel.CompareAnywayAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            ReportError?.Invoke(ex);
        }
    }

    /// <summary>A click or Enter on a folded run's line opens it (Summary).</summary>
    private void OpenFoldAt(DiffPaneView pane, int line)
    {
        var lines = PaneLinesOf(pane).Lines;
        if ((uint)(line - 1) < (uint)lines.Count && lines[line - 1].Kind == 'z')
        {
            _viewModel.ExpandFold(lines[line - 1].Row);
        }
    }

    /// <summary>The staging column shown or hidden (the toolbar's toggle, the gear menu, the setting).</summary>
    private void ApplyStagingControls()
    {
        foreach (var margin in new[] { _rightStaging, _inlineStaging })
        {
            if (margin is not null)
            {
                margin.IsVisible = _viewModel.ShowStagingControls;
            }
        }

        if (_stagingToggle is not null && _stagingToggle.IsChecked != _viewModel.ShowStagingControls)
        {
            _stagingToggle.IsChecked = _viewModel.ShowStagingControls;
        }
    }

    /// <summary>While git runs (A7): a progress line, "Staging…" in the toolbar, and the actions waiting.</summary>
    private void ApplyBusy()
    {
        _progress.IsVisible = _viewModel.IsBusy || (_viewModel.IsLoading && !_viewModel.IsLoaded);
        _rightStaging?.SetBusy(_viewModel.IsBusy);
        _inlineStaging?.SetBusy(_viewModel.IsBusy);
        ApplyState();
        UpdateCount();
    }

    private void OnFontChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyZoom();

    private void OnThemeVariantChanged(object? sender, EventArgs e) => Redraw();

    /// <summary>The theme changed: the editor theme's colors, and the TextMate theme's (other languages) made again.</summary>
    private void OnThemeChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        ApplyTheme();
        _ = _viewModel.ColorAsync();
    });

    private void ApplyTheme()
    {
        var theme = _viewModel.Services?.Editor.Theme ?? CodeColors.ThemeFor(ActualThemeVariant);
        foreach (var colors in new[] { _leftColors, _rightColors, _inlineColors })
        {
            colors.Theme = theme;
        }

        foreach (var pane in new[] { _left, _right, _inline })
        {
            pane.Editor.TextArea.TextView.CurrentLineBackground = theme.CurrentLine;
            pane.Editor.TextArea.TextView.CurrentLineBorder = null;
            pane.Editor.TextArea.SelectionBrush = theme.Selection;
        }

        Redraw();
    }

    private void Redraw()
    {
        foreach (var pane in new[] { _left, _right, _inline })
        {
            pane.Editor.TextArea.TextView.Redraw();
            pane.Numbers.InvalidateVisual();
        }
    }

    /// <summary>The panes again from the rows: texts, line numbers, headers and status bars; the carets stay on their rows.</summary>
    private void Rebuild()
    {
        var vm = _viewModel;
        _sideBySide.IsVisible = vm.SideBySide;
        _inline.IsVisible = !vm.SideBySide;
        var carets = new[] { _left, _right, _inline }.ToDictionary(p => p, p => (Row: RowAt(p, p.Editor.TextArea.Caret.Line - 1), p.Editor.TextArea.Caret.Column));

        var folds = vm.Folds;
        (_leftPane, _rightPane) = DiffRows.SideBySide(vm.Rows, vm.OldLines, vm.NewLines, folds, vm.FoldLabel);
        _inlinePane = DiffRows.Inline(vm.Rows, vm.OldLines, vm.NewLines, folds, vm.FoldLabel);
        foreach (var (pane, lines) in new[] { (_left, _leftPane), (_right, _rightPane), (_inline, _inlinePane) })
        {
            if (pane.Editor.Text != lines.Text)
            {
                var offset = pane.Editor.TextArea.TextView.ScrollOffset;
                pane.Editor.Text = lines.Text;
                ((IScrollable)pane.Editor.TextArea.TextView).Offset = offset;
                if (carets[pane].Row >= 0)
                {
                    SetCaret(pane, carets[pane].Row, carets[pane].Column, scroll: false);
                }
            }

            pane.Numbers.Refresh();
        }

        _left.SetHeader(vm.OldSide.Name, vm.OldSide.Label, vm.OldSide.Note, vm.OldSide.Path);
        _right.SetHeader(vm.NewSide.Name, vm.NewSide.Label, vm.NewSide.Note, vm.NewSide.Path);
        _inline.SetHeader(vm.NewSide.Name, vm.OldSide.Label.Length > 0 ? $"{vm.OldSide.Label} → {vm.NewSide.Label}" : string.Empty, vm.NewSide.Note, vm.NewSide.Path);
        _left.SetFormat(vm.OldSide);
        _right.SetFormat(vm.NewSide);
        _inline.SetFormat(vm.NewSide);
        _removed.Text = "−" + vm.RemovedLines.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _added.Text = "+" + vm.AddedLines.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AutomationProperties.SetName(_removedChip, Localizer.Format("diff.linesRemoved", "{0} lines removed", vm.RemovedLines));
        AutomationProperties.SetName(_addedChip, Localizer.Format("diff.linesAdded", "{0} lines added", vm.AddedLines));
        ToolTip.SetTip(_removedChip, AutomationProperties.GetName(_removedChip));
        ToolTip.SetTip(_addedChip, AutomationProperties.GetName(_addedChip));
        _next.IsEnabled = _previous.IsEnabled = vm.Changes.Count > 0;
        ApplyState();

        foreach (var pane in new[] { _left, _right, _inline })
        {
            pane.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            UpdatePosition(pane);
        }

        _rightStaging?.Rebuild();
        _inlineStaging?.Rebuild();
        ApplyStagingControls();
        ApplyBusy();
        UpdateCount();
        UpdateNavigation();
        UpdateWidths();
        if (vm.IsLoaded && !_firstChangeShown && !vm.PaneState.ContainsKey(DiffPaneSide.New))
        {
            _firstChangeShown = true;
            if (vm.NextChange(-1, 1) is { } first)
            {
                ShowRow(first);
            }
        }
    }

    /// <summary>Saves the panes' carets and scroll positions in the view model (a tab switch rebuilds the view).</summary>
    private void SaveState()
    {
        foreach (var pane in new[] { _left, _right, _inline })
        {
            var caret = pane.Editor.TextArea.Caret;
            _viewModel.PaneState[pane.Side] = (RowAt(pane, caret.Line - 1), caret.Column, pane.Editor.TextArea.TextView.ScrollOffset);
        }
    }

    private void RestoreState()
    {
        if (_viewModel.PaneState.Count == 0)
        {
            return;
        }

        _firstChangeShown = true;
        foreach (var pane in new[] { _left, _right, _inline })
        {
            if (_viewModel.PaneState.TryGetValue(pane.Side, out var state))
            {
                if (state.Row >= 0)
                {
                    SetCaret(pane, state.Row, state.Column, scroll: false);
                }

                ((IScrollable)pane.Editor.TextArea.TextView).Offset = state.Offset;
            }
        }
    }

    // ------------------------------------------------------------------ colors

    /// <summary>The changed words of a 0-based pane line, where it is half of a pair of changed lines.</summary>
    private IReadOnlyList<(int Start, int Length)> WordsOf(DiffPane pane, int index)
    {
        if ((uint)index >= (uint)pane.Lines.Count || pane.Lines[index] is not { Kind: '-' or '+' } line)
        {
            return [];
        }

        var (old, now) = _viewModel.WordRanges(line.Row);
        return line.Kind == '-' ? old : now;
    }

    /// <summary>A pane line's file line and that file's colors.</summary>
    private (SourceTokens? Tokens, int Line) ColorsOf(DiffPane pane, int line)
    {
        var (old, source) = pane.SourceOf(line - 1);
        return (old ? _viewModel.OldColors : _viewModel.NewColors, source);
    }

    // ------------------------------------------------------------------ the toolbar, the status bars and the navigation bars

    private void OnCaretMoved(DiffPaneView pane)
    {
        UpdatePosition(pane);
        UpdateCount();
        foreach (var other in new[] { _left, _right, _inline })
        {
            other.Numbers.InvalidateVisual();
        }
    }

    private void UpdateCount() => _count.Text = _viewModel.BusyText ?? _viewModel.CountText(CurrentRow);

    private void UpdatePosition(DiffPaneView pane)
    {
        var area = pane.Editor.TextArea;
        var lines = PaneLinesOf(pane);
        var source = 0;
        for (var i = area.Caret.Line - 1; i < lines.Lines.Count && source == 0; i++)
        {
            source = lines.SourceOf(i).Line; // a filler line shows the next line of its file
        }

        string? selection = null;
        if (!area.Selection.IsEmpty && pane.Editor.Document is { } document)
        {
            var first = document.GetLineByOffset(pane.Editor.SelectionStart).LineNumber;
            var last = document.GetLineByOffset(Math.Max(pane.Editor.SelectionStart, pane.Editor.SelectionStart + pane.Editor.SelectionLength - 1)).LineNumber;
            var count = last - first + 1;
            selection = count == 1 ? Localizer.Get("diff.oneLineSelected", "(1 line selected)") : Localizer.Format("diff.linesSelected", "({0} lines selected)", count);
        }

        pane.SetPosition(Localizer.Format("diff.position", "Ln: {0}   Ch: {1}", Math.Max(1, source), area.Caret.Column), selection);
    }

    /// <summary>
    /// The navigation bars (D45 question 8): on the open document's side only, the other side keeping an empty disabled
    /// row; neither without an open side or with a side narrower than 360 px.
    /// </summary>
    private void UpdateNavigation()
    {
        var openOld = _viewModel.OpenSideIsOld;
        var controller = openOld is { } isOld ? _viewModel.Services?.OpenEditor(isOld ? _viewModel.OldPath : _viewModel.Path) : null;
        var wide = _left.Bounds.Width is 0 or >= NavigationMinWidth && _right.Bounds.Width is 0 or >= NavigationMinWidth;
        var shown = openOld is not null && wide;
        _left.SetNavigation(shown, shown && openOld == true && controller is not null ? new DiffNavigationSource(controller, _left.Editor, () => _leftPane, line => ShowSourceLine(_left, line)) : null);
        _right.SetNavigation(shown, shown && openOld == false && controller is not null ? new DiffNavigationSource(controller, _right.Editor, () => _rightPane, line => ShowSourceLine(_right, line)) : null);
        var inlineShown = openOld == false && _inline.Bounds.Width is 0 or >= NavigationMinWidth;
        _inline.SetNavigation(inlineShown, inlineShown && controller is not null ? new DiffNavigationSource(controller, _inline.Editor, () => _inlinePane, line => ShowSourceLine(_inline, line)) : null);
    }

    private (double Left, double Right, double Inline) _widths;

    private void UpdateWidths()
    {
        var widths = (_left.Bounds.Width, _right.Bounds.Width, _inline.Bounds.Width);
        if (widths == _widths)
        {
            return;
        }

        _widths = widths;
        SetCompact(Bounds.Width is > 0 and < 760);
        _left.SetNarrow(widths.Item1 is > 0 and < StatusMinWidth);
        _right.SetNarrow(widths.Item2 is > 0 and < StatusMinWidth);
        _inline.SetNarrow(widths.Item3 is > 0 and < StatusMinWidth);
        UpdateNavigation();
    }

    private static (Border Chip, TextBlock Text) Chip(string background, string foreground)
    {
        var text = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(foreground));
        var chip = new Border { Child = text, Padding = new Thickness(5, 0), Margin = new Thickness(2, 0), CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, MinHeight = 18 };
        chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable(background));
        return (chip, text);
    }

    private static Border Separator()
    {
        var separator = new Border();
        separator.Classes.Add("toolbarSeparator");
        return separator;
    }

    /// <summary>An icon and text button (Stage All…); at narrow widths the icon alone, its name in the tooltip (G1).</summary>
    private Button LabeledButton(string icon, string text, string tooltip, string command, Action action)
    {
        var button = new Button { Content = text };
        button.Classes.AddRange(["toolbar", "iconText"]);
        ButtonIcon.SetKind(button, icon);
        AutomationProperties.SetName(button, text);
        ToolTip.SetTip(button, WithShortcut(tooltip, command));
        button.Click += (_, _) => action();
        _labeled.Add((button, text));
        return button;
    }

    /// <summary>The toolbar's labels give way to icons when the document is narrow (G1).</summary>
    private void SetCompact(bool compact)
    {
        foreach (var (control, text) in _labeled)
        {
            control.Content = compact ? null : text;
            control.Classes.Set("iconText", !compact);
            control.Classes.Set("icon", compact);
        }
    }

    /// <summary>The staging column of a pane, between its numbers and its signs.</summary>
    private DiffStagingMargin StagingMargin(DiffPaneView pane, Func<DiffPane> lines)
    {
        IReadOnlyList<(DiffAction, string)> actions = _viewModel.CanStage ? [(DiffAction.Stage, "add"), (DiffAction.Discard, "discard")] : [(DiffAction.Unstage, "subtract")];
        var margin = new DiffStagingMargin(lines, () => _viewModel.Changes, actions, StagingTooltip, (change, action) => _ = ApplyAsync(action, RowsFor(change), selected: SelectionInChange(change)),
            (change, _) =>
            {
                _outlined = change is { } c ? RowsFor(c).ToHashSet() : null;
                pane.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            })
        {
            IsVisible = _viewModel.ShowStagingControls,
        };
        pane.Editor.TextArea.LeftMargins.Insert(1, margin);
        return margin;
    }

    /// <summary>The rows a change's button acts on: the selected rows in it, or all of its rows.</summary>
    private List<int> RowsFor(DiffChange change)
    {
        var selected = SelectedRows().Where(change.Contains).ToList();
        return selected.Count > 0 && !ActivePane.Editor.TextArea.Selection.IsEmpty ? selected : [.. Enumerable.Range(change.FirstRow, change.LastRow - change.FirstRow + 1)];
    }

    private bool SelectionInChange(DiffChange change) => !ActivePane.Editor.TextArea.Selection.IsEmpty && SelectedRows().Any(change.Contains);

    /// <summary>A staging button's tooltip: the command, the lines it acts on, and the save-first note (A4, A5).</summary>
    private string StagingTooltip(DiffChange change, DiffAction action)
    {
        var selected = SelectionInChange(change);
        var title = (action, selected) switch
        {
            (DiffAction.Stage, false) => Localizer.Get("diff.stageChange", "Stage Change"),
            (DiffAction.Stage, true) => Localizer.Get("diff.stageSelectedLines", "Stage Selected Lines"),
            (DiffAction.Discard, false) => Localizer.Get("diff.discardChange", "Discard Change…"),
            (DiffAction.Discard, true) => Localizer.Get("diff.discardSelectedLines", "Discard Selected Lines…"),
            (DiffAction.Unstage, false) => Localizer.Get("diff.unstageChange", "Unstage Change"),
            _ => Localizer.Get("diff.unstageSelectedLines", "Unstage Selected Lines"),
        };
        var tip = $"{title}: {LinesPhrase(RowsFor(change))}";
        return action != DiffAction.Unstage && _viewModel.HasUnsavedEdits
            ? Localizer.Format("diff.savesFirst", "{0} (saves {1} first)", tip, System.IO.Path.GetFileName(_viewModel.Path))
            : tip;
    }

    /// <summary>"line 48", "lines 47–48" (the new side's numbers; the old side's for removed lines alone).</summary>
    private string LinesPhrase(IReadOnlyCollection<int> rows)
    {
        var lines = _viewModel.LinesText(rows, old: false) ?? _viewModel.LinesText(rows, old: true) ?? string.Empty;
        return lines.Contains('–', StringComparison.Ordinal) ? Localizer.Format("diff.lines", "lines {0}", lines) : Localizer.Format("diff.line", "line {0}", lines);
    }

    private string WithShortcut(string text, string command) =>
        _viewModel.ShortcutFor?.Invoke(command) is { Length: > 0 } keys ? $"{text} ({keys})" : text;

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

    /// <summary>The gear menu (Diff Options): the layout, then Refresh and opening the file.</summary>
    private DropDownButton GearMenu()
    {
        var name = Localizer.Get("diff.options", "Diff Options");
        var sideBySide = Item(Localizer.Get("diff.sideBySide", "Side by Side"), () => _viewModel.SideBySide = true, MenuItemToggleType.Radio);
        var inline = Item(Localizer.Get("diff.inline", "Inline"), () => _viewModel.SideBySide = false, MenuItemToggleType.Radio);
        var staging = Item(Localizer.Get("diff.showStagingControls", "Show Staging Controls"), () => _viewModel.ShowStagingControls = !_viewModel.ShowStagingControls, MenuItemToggleType.CheckBox);
        staging.IsVisible = _viewModel.CanStage || _viewModel.CanUnstage;
        var summary = Item(Localizer.Get("diff.summary", "Summary"), () => _viewModel.IsSummary = !_viewModel.IsSummary, MenuItemToggleType.CheckBox);
        var menu = new MenuFlyout
        {
            Items =
            {
                sideBySide,
                inline,
                new Separator(),
                staging,
                summary,
                new Separator(),
                MenuEntry.Create(Localizer.Get("diff.refresh", "Refresh"), "refresh", click: Refresh),
                MenuEntry.Create(OpenFileTitle, "document-text", click: OpenFile),
            },
        };
        menu.Opening += (_, _) =>
        {
            sideBySide.IsChecked = _viewModel.SideBySide;
            inline.IsChecked = !_viewModel.SideBySide;
            staging.IsChecked = _viewModel.ShowStagingControls;
            staging.IsEnabled = summary.IsEnabled = _viewModel.State == DiffState.Lines;
            summary.IsChecked = _viewModel.IsSummary;
        };
        var button = new DropDownButton { Content = new Icon { Kind = "gear" }, Flyout = menu };
        button.Classes.Add("toolbar");
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        return button;
    }

    private string OpenFileTitle => Localizer.Format("diff.openFile", "Open {0}", System.IO.Path.GetFileName(_viewModel.Path));

    private static MenuItem Item(string header, Action action, MenuItemToggleType toggle)
    {
        var item = new MenuItem { Header = header, ToggleType = toggle };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>The code's context menu: the change commands, then Copy and opening the file.</summary>
    private void FillContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var changeRows = _viewModel.ChangeRows(CurrentRow);
        var selectedChanged = SelectedRows().Any(r => (uint)r < (uint)_viewModel.Rows.Count && _viewModel.Rows[r].IsChange);
        MenuItem Entry(string key, string title, string icon, string command, Func<Task<bool>> action, bool enabled)
        {
            var item = MenuEntry.Create(Localizer.Get(key, title), icon, keys: _viewModel.ShortcutFor?.Invoke(command), click: () => _ = action());
            item.IsEnabled = enabled && !_viewModel.IsBusy;
            return item;
        }

        if (_viewModel.CanStage)
        {
            menu.Items.Add(Entry("diff.stageSelectedLines", "Stage Selected Lines", "add", "git.diff.stageSelectedLines", () => ApplyToSelectedLinesAsync(DiffAction.Stage), selectedChanged));
            menu.Items.Add(Entry("diff.stageChange", "Stage Change", "add", "git.diff.stageChange", () => ApplyToChangeAsync(DiffAction.Stage), changeRows.Count > 0));
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry("diff.discardSelectedLines", "Discard Selected Lines…", "discard", "git.diff.discardSelectedLines", () => ApplyToSelectedLinesAsync(DiffAction.Discard), selectedChanged));
            menu.Items.Add(Entry("diff.discardChange", "Discard Change…", "discard", "git.diff.discardChange", () => ApplyToChangeAsync(DiffAction.Discard), changeRows.Count > 0));
            menu.Items.Add(new Separator());
        }
        else if (_viewModel.CanUnstage)
        {
            menu.Items.Add(Entry("diff.unstageSelectedLines", "Unstage Selected Lines", "subtract", "git.diff.unstageSelectedLines", () => ApplyToSelectedLinesAsync(DiffAction.Unstage), selectedChanged));
            menu.Items.Add(Entry("diff.unstageChange", "Unstage Change", "subtract", "git.diff.unstageChange", () => ApplyToChangeAsync(DiffAction.Unstage), changeRows.Count > 0));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(MenuEntry.Create(Localizer.Get("diff.nextChange", "Next Change"), "arrow-down", keys: _viewModel.ShortcutFor?.Invoke("git.diff.nextChange"), click: () => GoToChange(1)));
        menu.Items.Add(MenuEntry.Create(Localizer.Get("diff.previousChange", "Previous Change"), "arrow-up", keys: _viewModel.ShortcutFor?.Invoke("git.diff.previousChange"), click: () => GoToChange(-1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry.Create(Localizer.Get("diff.copy", "Copy"), "copy", keys: _viewModel.ShortcutFor?.Invoke("editor.action.clipboardCopyAction"), click: () => ActiveEditor.Copy()));
        menu.Items.Add(MenuEntry.Create(OpenFileTitle, "document-text", click: OpenFile));
    }

    private ComboBox ZoomBox()
    {
        var zoom = new ComboBox { ItemsSource = s_zooms.Select(z => $"{z * 100:0} %").ToList(), MinWidth = 76 };
        zoom.Classes.Add("toolbar");
        AutomationProperties.SetName(zoom, Localizer.Get("diff.zoom", "Zoom"));
        ToolTip.SetTip(zoom, Localizer.Get("diff.zoom", "Zoom"));
        zoom.SelectionChanged += (_, _) =>
        {
            if (zoom.SelectedIndex >= 0)
            {
                _viewModel.Zoom = s_zooms[zoom.SelectedIndex];
            }
        };
        return zoom;
    }

    /// <summary>The zoom sizes both sides (B2): the editor font times the zoom.</summary>
    private void ApplyZoom()
    {
        foreach (var pane in new[] { _left, _right, _inline })
        {
            pane.Editor.FontFamily = _font.Family;
            pane.Editor.FontSize = _font.Size * _viewModel.Zoom;
            pane.Numbers.Refresh();
        }

        var index = Array.IndexOf(s_zooms, _viewModel.Zoom);
        foreach (var box in new[] { _zoom, _inlineZoom })
        {
            if (index >= 0 && box.SelectedIndex != index)
            {
                box.SelectedIndex = index;
            }
        }
    }

    /// <summary>Ctrl+wheel zooms both sides, a step at a time.</summary>
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

    // ------------------------------------------------------------------ scrolling, carets and changes

    private void Sync(DiffPaneView from, DiffPaneView to)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            ((IScrollable)to.Editor.TextArea.TextView).Offset = from.Editor.TextArea.TextView.ScrollOffset;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The row of a 0-based pane line, or -1.</summary>
    private int RowAt(DiffPaneView pane, int line)
    {
        var lines = PaneLinesOf(pane).Lines;
        return (uint)line < (uint)lines.Count ? lines[line].Row : -1;
    }

    /// <summary>The 0-based pane line of a row (its first line), or -1.</summary>
    private int LineOf(DiffPaneView pane, int row)
    {
        var lines = PaneLinesOf(pane).Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Row == row)
            {
                return i;
            }
        }

        return -1;
    }

    private void SetCaret(DiffPaneView pane, int row, int column, bool scroll)
    {
        var line = LineOf(pane, row);
        if (line < 0)
        {
            return;
        }

        var area = pane.Editor.TextArea;
        area.Caret.Line = line + 1;
        area.Caret.Column = Math.Max(1, column);
        if (scroll)
        {
            pane.Editor.ScrollToLine(line + 1);
        }
    }

    /// <summary>Moves the side's caret to the pane line that shows a 1-based line of its file (the navigation bar).</summary>
    private void ShowSourceLine(DiffPaneView pane, int line)
    {
        var lines = PaneLinesOf(pane);
        for (var i = 0; i < lines.Lines.Count; i++)
        {
            if (lines.SourceOf(i) is { Line: var source } && source == line)
            {
                ShowRow(lines.Lines[i].Row);
                return;
            }
        }
    }

    /// <summary>The change at the caret (the accent bar's), or null.</summary>
    private DiffChange? CurrentChange()
    {
        var index = _viewModel.ChangeIndexAt(CurrentRow);
        return index >= 0 ? _viewModel.Changes[index] : null;
    }

    public TextEditor ActiveEditor => ActivePane.Editor;

    private DiffPaneView ActivePane => _viewModel.SideBySide ? (_active == _left ? _left : _right) : _inline;

    /// <summary>The row of the caret in the active editor.</summary>
    public int CurrentRow => RowAt(ActivePane, ActivePane.Editor.TextArea.Caret.Line - 1);

    /// <summary>Moves the caret (both sides) to a row and scrolls it into view.</summary>
    public void ShowRow(int row)
    {
        if (row < 0 || row >= _viewModel.Rows.Count)
        {
            return;
        }

        foreach (var pane in _viewModel.SideBySide ? new[] { _left, _right } : [_inline])
        {
            SetCaret(pane, row, 1, scroll: true);
        }

        UpdateCount();
    }

    /// <summary>F8 and Shift+F8: the next or previous change.</summary>
    public void GoToChange(int direction)
    {
        if (_viewModel.NextChange(CurrentRow, direction) is { } row)
        {
            ShowRow(row);
        }
    }

    /// <summary>The rows of the selected lines in the active editor (the caret's row without a selection).</summary>
    public IReadOnlyList<int> SelectedRows()
    {
        var pane = ActivePane;
        var editor = pane.Editor;
        var area = editor.TextArea;
        int first, last;
        if (area.Selection.IsEmpty)
        {
            first = last = area.Caret.Line - 1;
        }
        else
        {
            first = editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber - 1;
            last = editor.Document.GetLineByOffset(Math.Max(editor.SelectionStart, editor.SelectionStart + editor.SelectionLength - 1)).LineNumber - 1;
        }

        var rows = new List<int>();
        for (var line = first; line <= last; line++)
        {
            var row = RowAt(pane, line);
            if (row >= 0 && !rows.Contains(row))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>Reads the texts again (the gear menu's Refresh).</summary>
    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            await _viewModel.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            ReportError?.Invoke(ex);
        }
    }

    /// <summary>Opens the file at the caret's line (<c>git.diff.openFile</c>).</summary>
    public void OpenFile()
    {
        var pane = ActivePane;
        var lines = PaneLinesOf(pane).Lines;
        var line = 1;
        for (var i = Math.Max(0, pane.Editor.TextArea.Caret.Line - 1); i < lines.Count; i++)
        {
            if (lines[i].NewLine > 0)
            {
                line = lines[i].NewLine;
                break;
            }
        }

        _ = _viewModel.OpenFile?.Invoke(_viewModel.Path, line);
    }

    /// <summary>
    /// Stage Change, Discard Change… or Unstage Change: the change at the caret (the context menu's, the command's).
    /// Discard asks first. False when nothing was applied.
    /// </summary>
    public Task<bool> ApplyToChangeAsync(DiffAction action) =>
        _viewModel.ChangeRows(CurrentRow) is { Count: > 0 } rows ? ApplyAsync(action, rows, selected: false) : Task.FromResult(false);

    /// <summary>Stage, Discard… or Unstage Selected Lines: the changed lines among the selected ones (the caret's line without a selection).</summary>
    public Task<bool> ApplyToSelectedLinesAsync(DiffAction action) =>
        SelectedRows().Where(r => (uint)r < (uint)_viewModel.Rows.Count && _viewModel.Rows[r].IsChange).ToList() is { Count: > 0 } rows
            ? ApplyAsync(action, rows, selected: true)
            : Task.FromResult(false);

    private async Task<bool> ApplyAsync(DiffAction action, IReadOnlyList<int> rows, bool selected)
    {
        if (!_viewModel.Allows(action) || _viewModel.IsBusy)
        {
            return false;
        }

        if (action == DiffAction.Discard && !await ConfirmDiscardAsync(rows).ConfigureAwait(true))
        {
            return false;
        }

        try
        {
            return await _viewModel.ApplyAsync(action, rows).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            ReportError?.Invoke(ex);
            return false;
        }
        finally
        {
            _outlined = null;
        }
    }

    /// <summary>
    /// Discard Change… and Discard Selected Lines… ask first, every time (D45 question 15, A9): the lines, what happens to
    /// them, and whether the unsaved edits are saved first; Cancel is the default.
    /// </summary>
    private async Task<bool> ConfirmDiscardAsync(IReadOnlyList<int> rows)
    {
        if (_viewModel.Services is not { } services)
        {
            return false;
        }

        var name = System.IO.Path.GetFileName(_viewModel.Path);
        var changed = rows.Where(r => (uint)r < (uint)_viewModel.Rows.Count && _viewModel.Rows[r].IsChange).Select(r => _viewModel.Rows[r]).ToList();
        var added = changed.Count(r => r.Kind == '+');
        var removed = changed.Count(r => r.Kind == '-');
        var message = added == changed.Count
            ? added == 1 ? Localizer.Get("diff.discardAddedLine", "This added line will be removed from the working tree.") : Localizer.Format("diff.discardAddedLines", "These {0} added lines will be removed from the working tree.", added)
            : removed == changed.Count
                ? removed == 1 ? Localizer.Get("diff.discardRemovedLine", "This removed line will be put back in the working tree.") : Localizer.Format("diff.discardRemovedLines", "These {0} removed lines will be put back in the working tree.", removed)
                : Localizer.Get("diff.discardChangedLines", "These lines will go back to the index's version in the working tree.");
        var details = new List<DetailLine>();
        foreach (var row in changed)
        {
            if (row.OldLine is { } o)
            {
                details.Add(new DetailLine($"− {o,4}  {_viewModel.OldLines[o - 1]}", "removed"));
            }
        }

        foreach (var row in changed)
        {
            if (row.NewLine is { } n)
            {
                details.Add(new DetailLine($"+ {n,4}  {_viewModel.NewLines[n - 1]}", "added"));
            }
        }

        if (_viewModel.HasUnsavedEdits)
        {
            details.Insert(0, new DetailLine(Localizer.Format("diff.discardSavesFirst", "{0} has unsaved edits: it is saved first, then the lines are discarded.", name), "note"));
        }

        var discard = Localizer.Get("diff.discard", "Discard");
        var cancel = Localizer.Get("diff.cancel", "Cancel");
        var answer = await services.ShowDialogAsync(new DialogRequest(
            Localizer.Format("diff.discardTitle", "Discard {0} of {1}?", LinesPhrase(rows), name),
            message + " " + Localizer.Get("diff.cannotBeUndone", "This can't be undone."),
            [discard, cancel])
        {
            DefaultButton = cancel,
            IsDanger = true,
            Icon = "discard",
            Context = $"{name} ({_viewModel.NewSide.Label})",
            Details = details,
        }).ConfigureAwait(true);
        return answer.Button == discard;
    }

    /// <summary>Stage All: the whole file, saved first.</summary>
    public async Task<bool> StageAllAsync()
    {
        try
        {
            return await _viewModel.StageAllAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            ReportError?.Invoke(ex);
            return false;
        }
    }

    /// <summary>Unstage All: the whole file (a staged rename's both paths).</summary>
    public async Task<bool> UnstageAllAsync()
    {
        try
        {
            return await _viewModel.UnstageAllAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            ReportError?.Invoke(ex);
            return false;
        }
    }

    /// <summary>Discard All…: today's confirmation (saying when unsaved edits go too), then the file back to the index.</summary>
    public Task<bool> DiscardAllAsync() => _viewModel.DiscardAllAsync();

    public void FocusPrimary() => ActiveEditor.TextArea.Focus();

    // ------------------------------------------------------------------ tests and the smoke test

    public string LeftText => _left.Editor.Text;

    public string RightText => _right.Editor.Text;

    public string InlineText => _inline.Editor.Text;

    /// <summary>The toolbar's count ("Change 3 of 4").</summary>
    public string CountText => _count.Text ?? string.Empty;

    /// <summary>The toolbar's removed and added line counts ("−5", "+4").</summary>
    public (string Removed, string Added) LineCounts => (_removed.Text ?? string.Empty, _added.Text ?? string.Empty);

    /// <summary>The editor of a side: the old file's, the new file's, or the inline view's.</summary>
    public TextEditor EditorOf(DiffPaneSide side) => PaneOf(side).Editor;

    /// <summary>A side's header text ("Greeter.cs (Index)").</summary>
    public string HeaderOf(DiffPaneSide side) => PaneOf(side).HeaderText;

    /// <summary>A side's status bar items ("Ln: 3   Ch: 1", "SPC", "LF", "UTF-8").</summary>
    public IReadOnlyList<string> StatusOf(DiffPaneSide side) => PaneOf(side).StatusItems;

    /// <summary>Whether a side has the navigation bar's row, and whether the bar lists the open document's types.</summary>
    public (bool Row, bool Bar) NavigationOf(DiffPaneSide side) => (PaneOf(side).NavigationRow.IsVisible, PaneOf(side).NavigationBar.IsVisible);

    /// <summary>The line numbers drawn beside a 1-based pane line of a side (the inline view's two columns; 0 for none).</summary>
    public (int Old, int New) NumbersAt(DiffPaneSide side, int line) => PaneLinesOf(PaneOf(side)).NumbersOf(line - 1);

    /// <summary>The staging column's buttons shown now on the new side (or inline), or none when it is hidden (tests).</summary>
    internal IReadOnlyList<(DiffChange Change, DiffAction Action, Button Button)> StagingButtons =>
        (_viewModel.SideBySide ? _rightStaging : _inlineStaging) is { IsVisible: true } margin ? margin.Buttons : [];

    /// <summary>The staging column's tooltip for a change's button (tests).</summary>
    internal string StagingTooltipOf(DiffChange change, DiffAction action) => StagingTooltip(change, action);

    /// <summary>The code's context menu as it opens at the caret (tests): its entries' headers and whether each is enabled.</summary>
    internal IReadOnlyList<(string Header, bool Enabled)> ContextMenuEntries()
    {
        var menu = new ContextMenu();
        FillContextMenu(menu);
        return [.. menu.Items.OfType<MenuItem>().Select(i => (AutomationProperties.GetName(i) ?? string.Empty, i.IsEnabled))];
    }

    /// <summary>The toolbar's buttons and toggles, by name (tests).</summary>
    internal IReadOnlyList<string> ToolbarNames => [.. _toolbar.Children.OfType<Control>().Select(AutomationProperties.GetName).Where(n => !string.IsNullOrEmpty(n))!];

    /// <summary>The message shown instead of the lines, and its actions' names (tests); null when the lines show.</summary>
    internal (string? Message, string? Detail, IReadOnlyList<string> Actions)? EmptyState => _empty.IsVisible
        ? (_viewModel.State == DiffState.Binary ? string.Join(" | ", _binary.Children.Select(AutomationProperties.GetName)) : _emptyMessage.Text, _emptyDetail.IsVisible ? _emptyDetail.Text : null, [.. _emptyActions.Children.OfType<Button>().Select(b => b.Content as string ?? string.Empty)])
        : null;

    /// <summary>Clicks a folded run's line (tests): the 1-based pane line of a side.</summary>
    internal void ClickFold(DiffPaneSide side, int line) => OpenFoldAt(PaneOf(side), line);

    /// <summary>Clicks an action of the message instead of the lines (tests).</summary>
    internal void ClickEmptyAction(string text) => _emptyActions.Children.OfType<Button>().Single(b => b.Content as string == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The left side's share of the width (tests).</summary>
    public double SplitRatio => _left.Bounds.Width + _right.Bounds.Width is > 0 and var total ? _left.Bounds.Width / total : _viewModel.SplitRatio;

    /// <summary>Double-clicks the splitter: half and half (tests).</summary>
    internal void ResetSplit()
    {
        _viewModel.SplitRatio = 0.5;
        ApplySplit();
    }

    /// <summary>The splitter (tests).</summary>
    internal GridSplitter Splitter => _splitter;

    /// <summary>A side's navigation bar (tests).</summary>
    public NavigationBar NavigationBarOf(DiffPaneSide side) => PaneOf(side).NavigationBar;

    /// <summary>The token type colored at a 1-based pane line and 0-based character of a side, or -1.</summary>
    public int ColoredTypeAt(DiffPaneSide side, int line, int character) => side switch
    {
        DiffPaneSide.Old => _leftColors.TypeAt(line, character),
        DiffPaneSide.New => _rightColors.TypeAt(line, character),
        _ => _inlineColors.TypeAt(line, character),
    };

    /// <summary>The changed words drawn on a 1-based line of a side: the ranges, or none.</summary>
    public IReadOnlyList<(int Start, int Length)> WordsAt(DiffPaneSide side, int line) => WordsOf(PaneLinesOf(PaneOf(side)), line - 1);

    /// <summary>The distinct text colors drawn on a 1-based line of a side.</summary>
    public int LineColors(DiffPaneSide side, int line)
    {
        var view = EditorOf(side).TextArea.TextView;
        view.EnsureVisualLines();
        return view.GetVisualLine(line) is { } visual ? Colors(visual) : 0;
    }

    /// <summary>Whether a visible line of a side is drawn in two colors or more.</summary>
    public bool IsColored(DiffPaneSide side)
    {
        var view = EditorOf(side).TextArea.TextView;
        view.EnsureVisualLines();
        return view.VisualLinesValid && view.VisualLines.Any(l => Colors(l) >= 2);
    }

    private static int Colors(VisualLine line) =>
        line.Elements.Select(e => e.TextRunProperties.ForegroundBrush is ISolidColorBrush brush ? brush.Color : default).Distinct().Count();
}
