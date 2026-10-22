using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Views;

/// <summary>Backgrounds of a diff editor's lines: removed (red), added (green), filler rows (gray).</summary>
internal sealed class DiffLineRenderer(Func<IReadOnlyList<char>> kinds) : IBackgroundRenderer
{
    private static readonly IBrush s_removed = new ImmutableSolidColorBrush(Color.FromArgb(0x45, 0xE5, 0x3E, 0x3E));
    private static readonly IBrush s_added = new ImmutableSolidColorBrush(Color.FromArgb(0x45, 0x3E, 0xB8, 0x4E));
    private static readonly IBrush s_filler = new ImmutableSolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80));

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var lineKinds = kinds();
        if (!textView.VisualLinesValid)
        {
            return;
        }

        foreach (var line in textView.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            var brush = index < lineKinds.Count ? lineKinds[index] switch
            {
                '-' => s_removed,
                '+' => s_added,
                'f' => s_filler,
                _ => null,
            } : null;
            if (brush is not null)
            {
                drawingContext.DrawRectangle(brush, null, new Rect(0, line.VisualTop - textView.VerticalOffset, textView.Bounds.Width, line.Height));
            }
        }
    }
}

/// <summary>A "+" or "−" beside added and removed lines, so a diff doesn't rely on color alone.</summary>
internal sealed class DiffSignMargin(Func<IReadOnlyList<char>> kinds) : AvaloniaEdit.Editing.AbstractMargin
{
    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(14, 0);

    public override void Render(DrawingContext context)
    {
        var lineKinds = kinds();
        if (TextView is not { VisualLinesValid: true } view)
        {
            return;
        }

        var foreground = GetValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty) ?? Brushes.Gray;
        var typeface = new Typeface(GetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty));
        var size = GetValue(Avalonia.Controls.Documents.TextElement.FontSizeProperty);
        foreach (var line in view.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            var sign = index < lineKinds.Count ? lineKinds[index] switch { '+' => "+", '-' => "−", _ => null } : null;
            if (sign is not null)
            {
                var text = new FormattedText(sign, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, foreground);
                context.DrawText(text, new Point(2, line.VisualTop - view.VerticalOffset));
            }
        }
    }
}

/// <summary>
/// A diff document: side by side (aligned rows, synchronized scrolling) or inline, with next/previous change and
/// Stage/Unstage/Discard for the hunk at the caret (ADR 0008).
/// </summary>
public sealed class DiffView : UserControl, IPaneView
{
    private readonly DiffViewModel _viewModel;
    private readonly TextEditor _left;
    private readonly TextEditor _right;
    private readonly TextEditor _inline;
    private readonly Grid _sideBySide;
    private readonly TextBlock _summary;
    private readonly SourceLineColorizer _leftColors;
    private readonly SourceLineColorizer _rightColors;
    private readonly SourceLineColorizer _inlineColors;
    private IReadOnlyList<char> _leftKinds = [];
    private IReadOnlyList<char> _rightKinds = [];
    private IReadOnlyList<char> _inlineKinds = [];
    private DiffPane _leftPane = DiffPane.Empty;
    private DiffPane _rightPane = DiffPane.Empty;
    private DiffPane _inlinePane = DiffPane.Empty;

    /// <summary>For each inline line, its row in <see cref="DiffViewModel.Rows"/>.</summary>
    private List<int> _inlineRows = [];
    private bool _syncing;

    public DiffView(DiffViewModel viewModel, EditorFont font)
    {
        _viewModel = viewModel;
        _leftColors = new SourceLineColorizer(line => ColorsOf(_leftPane, line));
        _rightColors = new SourceLineColorizer(line => ColorsOf(_rightPane, line));
        _inlineColors = new SourceLineColorizer(line => ColorsOf(_inlinePane, line));
        _left = Editor(font, () => _leftKinds, "Old text", _leftColors);
        _right = Editor(font, () => _rightKinds, "New text", _rightColors);
        _inline = Editor(font, () => _inlineKinds, "Diff", _inlineColors);
        _sideBySide = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
        _sideBySide.Children.Add(_left);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        _sideBySide.Children.Add(splitter);
        Grid.SetColumn(_right, 2);
        _sideBySide.Children.Add(_right);
        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        _summary.Classes.Add("muted");
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 4) };
        var mode = new ToggleButton { Content = "Side by side", IsChecked = viewModel.SideBySide };
        mode.IsCheckedChanged += (_, _) => viewModel.SideBySide = mode.IsChecked == true;
        AutomationProperties(mode, "Side by side");
        toolbar.Children.Add(mode);
        toolbar.Children.Add(Button("Previous Change", () => GoToChange(-1)));
        toolbar.Children.Add(Button("Next Change", () => GoToChange(1)));
        if (viewModel.CanStage)
        {
            toolbar.Children.Add(Button("Stage Hunk", () => _ = HunkActionAsync("stage")));
            toolbar.Children.Add(Button("Stage Selected Lines", () => _ = LineActionAsync("stage")));
            toolbar.Children.Add(Button("Discard Hunk", () => _ = HunkActionAsync("discard")));
            toolbar.Children.Add(Button("Discard Selected Lines", () => _ = LineActionAsync("discard")));
        }

        if (viewModel.CanUnstage)
        {
            toolbar.Children.Add(Button("Unstage Hunk", () => _ = HunkActionAsync("unstage")));
            toolbar.Children.Add(Button("Unstage Selected Lines", () => _ = LineActionAsync("unstage")));
        }

        toolbar.Children.Add(Button("Refresh", () => _ = viewModel.LoadAsync()));
        toolbar.Children.Add(_summary);

        var titles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*"), Margin = new Thickness(8, 0, 8, 2) };
        var oldTitle = new TextBlock { Text = viewModel.OldTitle };
        oldTitle.Classes.Add("muted");
        var newTitle = new TextBlock { Text = viewModel.NewTitle };
        newTitle.Classes.Add("muted");
        Grid.SetColumn(newTitle, 2);
        titles.Children.Add(oldTitle);
        titles.Children.Add(newTitle);

        var body = new Panel();
        body.Children.Add(_sideBySide);
        body.Children.Add(_inline);
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(titles, Avalonia.Controls.Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(titles);
        root.Children.Add(body);
        Content = root;

        _titles = titles;
        Rebuild();
        titles.IsVisible = viewModel.SideBySide;
        ApplyTheme();
    }

    private readonly Grid _titles;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel.Changed += OnChanged;
        _viewModel.ColorsChanged += OnColorsChanged;
        if (_viewModel.Services is { } services)
        {
            services.Editor.ThemeChanged += OnThemeChanged;
        }

        ApplyTheme();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _viewModel.Changed -= OnChanged;
        _viewModel.ColorsChanged -= OnColorsChanged;
        if (_viewModel.Services is { } services)
        {
            services.Editor.ThemeChanged -= OnThemeChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        Rebuild();
        _titles.IsVisible = _viewModel.SideBySide;
    });

    private void OnColorsChanged(object? sender, EventArgs e) => Ui.Post(Redraw);

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

        Redraw();
    }

    private void Redraw()
    {
        foreach (var editor in new[] { _left, _right, _inline })
        {
            editor.TextArea.TextView.Redraw();
        }
    }

    /// <summary>A pane line's file line and that file's colors.</summary>
    private (SourceTokens? Tokens, int Line) ColorsOf(DiffPane pane, int line)
    {
        var (old, source) = pane.SourceOf(line - 1);
        return (old ? _viewModel.OldColors : _viewModel.NewColors, source);
    }

    /// <summary>The editor of a side (tests, the smoke test): the old file's, the new file's, or the inline view's.</summary>
    public TextEditor EditorOf(DiffPaneSide side) => side switch
    {
        DiffPaneSide.Old => _left,
        DiffPaneSide.New => _right,
        _ => _inline,
    };

    /// <summary>The token type colored at a 1-based pane line and 0-based character of a side, or -1 (tests).</summary>
    public int ColoredTypeAt(DiffPaneSide side, int line, int character) => side switch
    {
        DiffPaneSide.Old => _leftColors.TypeAt(line, character),
        DiffPaneSide.New => _rightColors.TypeAt(line, character),
        _ => _inlineColors.TypeAt(line, character),
    };

    /// <summary>The distinct text colors drawn on a 1-based line of a side (tests, the smoke test).</summary>
    public int LineColors(DiffPaneSide side, int line)
    {
        var view = EditorOf(side).TextArea.TextView;
        view.EnsureVisualLines();
        return view.GetVisualLine(line) is { } visual
            ? visual.Elements.Select(e => e.TextRunProperties.ForegroundBrush is ISolidColorBrush brush ? brush.Color : default).Distinct().Count()
            : 0;
    }

    /// <summary>Whether a visible line of a side is drawn in two colors or more (the smoke test).</summary>
    public bool IsColored(DiffPaneSide side)
    {
        var view = EditorOf(side).TextArea.TextView;
        view.EnsureVisualLines();
        return view.VisualLinesValid && view.VisualLines.Any(l => l.Elements.Select(e => e.TextRunProperties.ForegroundBrush is ISolidColorBrush brush ? brush.Color : default).Distinct().Count() >= 2);
    }

    /// <summary>Shows git errors (set by the shell).</summary>
    public static Action<Exception>? ReportError { get; set; }

    public TextEditor ActiveEditor => _viewModel.SideBySide ? (_left.TextArea.IsKeyboardFocusWithin ? _left : _right) : _inline;

    public string LeftText => _left.Text;

    public string RightText => _right.Text;

    public string InlineText => _inline.Text;

    private static void AutomationProperties(Control control, string name) => Avalonia.Automation.AutomationProperties.SetName(control, name);

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Classes.Add("toolbar");
        button.Click += (_, _) => action();
        return button;
    }

    private static TextEditor Editor(EditorFont font, Func<IReadOnlyList<char>> kinds, string name, SourceLineColorizer colors)
    {
        var editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            FontFamily = font.Family,
            FontSize = font.Size,
            Padding = new Thickness(4, 2),
        };
        editor.Bind(BackgroundProperty, editor.GetResourceObservable("Shell.Editor.Background"));
        editor.Bind(ForegroundProperty, editor.GetResourceObservable("Shell.Editor.Foreground"));
        editor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineRenderer(kinds));
        editor.TextArea.TextView.LineTransformers.Add(colors);
        editor.TextArea.LeftMargins.Add(new DiffSignMargin(kinds));
        AutomationProperties(editor, name);
        return editor;
    }

    private void Rebuild()
    {
        var vm = _viewModel;
        _sideBySide.IsVisible = vm.SideBySide;
        _inline.IsVisible = !vm.SideBySide;
        _summary.Text = vm.Summary;

        (_leftPane, _rightPane) = DiffRows.SideBySide(vm.Rows, vm.OldLines, vm.NewLines);
        _inlinePane = DiffRows.Inline(vm.Rows, vm.OldLines, vm.NewLines);
        _leftKinds = [.. _leftPane.Lines.Select(l => l.Kind)];
        _rightKinds = [.. _rightPane.Lines.Select(l => l.Kind)];
        _inlineKinds = [.. _inlinePane.Lines.Select(l => l.Kind)];
        _inlineRows = [.. _inlinePane.Lines.Select(l => l.Row)];
        _left.Text = _leftPane.Text;
        _right.Text = _rightPane.Text;
        _inline.Text = _inlinePane.Text;

        foreach (var editor in new[] { _left, _right, _inline })
        {
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }

        if (vm.NextChange(-1, 1) is { } first)
        {
            ShowRow(first);
        }
    }

    private void Sync(TextEditor from, TextEditor to)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            ((IScrollable)to.TextArea.TextView).Offset = from.TextArea.TextView.ScrollOffset;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The row of the caret in the active editor.</summary>
    public int CurrentRow
    {
        get
        {
            var line = ActiveEditor.TextArea.Caret.Line - 1;
            return _viewModel.SideBySide ? line : line < _inlineRows.Count ? _inlineRows[line] : -1;
        }
    }

    /// <summary>Moves the caret (both sides) to a row.</summary>
    public void ShowRow(int row)
    {
        if (row < 0 || row >= _viewModel.Rows.Count)
        {
            return;
        }

        if (_viewModel.SideBySide)
        {
            foreach (var editor in new[] { _left, _right })
            {
                editor.TextArea.Caret.Line = row + 1;
                editor.ScrollToLine(row + 1);
            }
        }
        else
        {
            var line = _inlineRows.IndexOf(row);
            if (line >= 0)
            {
                _inline.TextArea.Caret.Line = line + 1;
                _inline.ScrollToLine(line + 1);
            }
        }
    }

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
        var editor = ActiveEditor;
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
            var row = _viewModel.SideBySide ? line : line < _inlineRows.Count ? _inlineRows[line] : -1;
            if (row >= 0 && !rows.Contains(row))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>Stages, unstages or discards the changed lines of the selection (per-line staging).</summary>
    public async Task LineActionAsync(string action)
    {
        try
        {
            await _viewModel.ApplyToLinesAsync(SelectedRows(), action).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            ReportError?.Invoke(ex);
        }
    }

    /// <summary>Stages, unstages or discards the hunk at the caret.</summary>
    public async Task HunkActionAsync(string action)
    {
        var index = CurrentRow;
        if (index < 0 || index >= _viewModel.Rows.Count)
        {
            return;
        }

        // On a context row, use the next change.
        if (_viewModel.Rows[index].Kind == ' ' && _viewModel.NextChange(index, 1) is { } next)
        {
            index = next;
        }

        var row = _viewModel.Rows[index];
        if (_viewModel.HunkAt(row.NewLine, row.OldLine) is not { } hunk)
        {
            return;
        }

        try
        {
            await _viewModel.ApplyToHunkAsync(hunk, action).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            ReportError?.Invoke(ex);
        }
    }

    public void FocusPrimary() => ActiveEditor.TextArea.Focus();
}
