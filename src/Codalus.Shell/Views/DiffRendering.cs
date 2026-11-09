using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Views;

/// <summary>
/// Draws under a diff pane's code (#86, D45): removed and added lines on their fills (<c>Shell.Diff.Removed.Line</c>,
/// <c>.Added.Line</c>), the changed words of paired lines on a deeper fill with a 1 px border
/// (<c>.Removed.Word</c>, <c>.Added.Word</c>, <c>.WordBorder</c>; High Contrast's fills are the background, so only the
/// borders show), and hatching on filler lines, where the other side has lines (<c>Shell.Diff.Filler</c>).
/// </summary>
internal sealed class DiffRenderer(Control owner, Func<DiffPane> pane, Func<int, IReadOnlyList<(int Start, int Length)>> words, Func<IReadOnlySet<int>?>? outlined = null) : IBackgroundRenderer
{
    private const double HatchSpacing = 6;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.Document is not { } document)
        {
            return;
        }

        var lines = pane().Lines;
        var addedLine = Brush("Shell.Diff.Added.Line");
        var removedLine = Brush("Shell.Diff.Removed.Line");
        var addedWord = Brush("Shell.Diff.Added.Word");
        var removedWord = Brush("Shell.Diff.Removed.Word");
        var addedBorder = Brush("Shell.Diff.Added.WordBorder") is { } a ? new Pen(a, 1) : null;
        var removedBorder = Brush("Shell.Diff.Removed.WordBorder") is { } r ? new Pen(r, 1) : null;
        var hatch = Brush("Shell.Diff.Filler") is { } f ? new Pen(f, 1) : null;
        var width = textView.Bounds.Width;
        var outline = outlined?.Invoke();
        var outlinePen = outline is { Count: > 0 } && Brush("Shell.Accent") is { } accent ? new Pen(accent, 1) : null;
        foreach (var line in textView.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            if ((uint)index >= (uint)lines.Count)
            {
                continue;
            }

            // A CodeLens row (DiffLensGenerator) ends the line before its line: drawn as the line it goes with.
            var kind = lines[index].Kind;
            var top = line.VisualTop - textView.VerticalOffset;
            var height = OwnHeight(line);
            var rect = new Rect(0, top, width, height);
            if (line.Height > height && index + 1 < lines.Count)
            {
                Fill(lines[index + 1].Kind, new Rect(0, top + height, width, line.Height - height), line.VisualTop + height);
            }

            Fill(kind, rect, line.VisualTop);
            if (kind is '-' or '+')
            {
                foreach (var (start, length) in words(index))
                {
                    var documentLine = line.FirstDocumentLine;
                    if (start >= documentLine.Length)
                    {
                        continue;
                    }

                    var segment = new TextSegment { StartOffset = documentLine.Offset + start, Length = Math.Min(length, documentLine.Length - start) };
                    foreach (var box in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    {
                        var word = new Rect(Math.Floor(box.X) + 0.5, Math.Floor(box.Y) + 0.5, Math.Max(1, Math.Round(box.Width) - 1), Math.Max(1, Math.Round(box.Height) - 1));
                        drawingContext.DrawRectangle(kind == '-' ? removedWord : addedWord, kind == '-' ? removedBorder : addedBorder, word);
                    }
                }
            }

            if (kind == 'z' && Brush("Shell.Border") is { } border)
            {
                // A folded run (Summary): a thin line above and below its note.
                var pen = new Pen(border, 1, new DashStyle([3, 3], 0));
                drawingContext.DrawLine(pen, new Point(0, Math.Floor(rect.Top) + 0.5), new Point(width, Math.Floor(rect.Top) + 0.5));
                drawingContext.DrawLine(pen, new Point(0, Math.Floor(rect.Bottom) - 0.5), new Point(width, Math.Floor(rect.Bottom) - 0.5));
            }

            if (outlinePen is not null && outline!.Contains(lines[index].Row) && kind is '-' or '+')
            {
                // The lines a staging button acts on: an outline round them (left and right edges, top and bottom at the run's ends).
                var top0 = Math.Floor(line.VisualTop - textView.VerticalOffset) + 0.5;
                var bottom = top0 + Math.Round(height) - 1;
                var above = index > 0 && outline.Contains(lines[index - 1].Row) && lines[index - 1].Kind is '-' or '+';
                var below = index + 1 < lines.Count && outline.Contains(lines[index + 1].Row) && lines[index + 1].Kind is '-' or '+';
                drawingContext.DrawLine(outlinePen, new Point(0.5, top0), new Point(0.5, bottom + 1));
                drawingContext.DrawLine(outlinePen, new Point(width - 0.5, top0), new Point(width - 0.5, bottom + 1));
                if (!above)
                {
                    drawingContext.DrawLine(outlinePen, new Point(0.5, top0), new Point(width - 0.5, top0));
                }

                if (!below)
                {
                    drawingContext.DrawLine(outlinePen, new Point(0.5, bottom), new Point(width - 0.5, bottom));
                }
            }
        }

        // A removed or added line's fill, or a filler line's diagonal stripes, anchored to the document so a run of
        // filler lines draws one pattern.
        void Fill(char kind, Rect rect, double visualTop)
        {
            if (kind is '-' or '+')
            {
                drawingContext.DrawRectangle(kind == '-' ? removedLine : addedLine, null, rect);
            }
            else if (kind == 'f' && hatch is not null)
            {
                using (drawingContext.PushClip(rect))
                {
                    var phase = visualTop % HatchSpacing;
                    for (var x = -rect.Height - phase; x < width; x += HatchSpacing)
                    {
                        drawingContext.DrawLine(hatch, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
                    }
                }
            }
        }
    }

    private IBrush? Brush(string key) => owner.TryFindResource(key, owner.ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;

    /// <summary>
    /// The height of a visual line's own text: its first text line when a CodeLens row of the next line follows it
    /// (<see cref="DiffLensGenerator"/>), else all of it.
    /// </summary>
    public static double OwnHeight(VisualLine line) => line.TextLines.Count > 1 ? line.TextLines[0].Height : line.Height;
}

/// <summary>A "+" or "−" beside added and removed lines, so a diff doesn't rely on color alone.</summary>
internal sealed class DiffSignMargin(Func<DiffPane> pane) : AvaloniaEdit.Editing.AbstractMargin
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
        var lines = pane().Lines;
        if (TextView is not { VisualLinesValid: true } view)
        {
            return;
        }

        var fallback = GetValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty) ?? Brushes.Gray;
        var added = this.TryFindResource("Shell.Diff.Added.Foreground", ActualThemeVariant, out var a) && a is IBrush addedBrush ? addedBrush : fallback;
        var removed = this.TryFindResource("Shell.Diff.Removed.Foreground", ActualThemeVariant, out var r) && r is IBrush removedBrush ? removedBrush : fallback;
        var typeface = new Typeface(GetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty));
        var size = GetValue(Avalonia.Controls.Documents.TextElement.FontSizeProperty);
        foreach (var line in view.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            var kind = (uint)index < (uint)lines.Count ? lines[index].Kind : ' ';
            if (kind is '+' or '-')
            {
                var text = new FormattedText(kind == '+' ? "+" : "−", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, kind == '+' ? added : removed);
                context.DrawText(text, new Point(2, line.VisualTop - view.VerticalOffset));
            }
        }
    }
}

/// <summary>
/// A diff pane's line numbers (#86): each side counts its own file's lines (blank on filler rows, which the editor's
/// own margin would count), and the inline view has two columns, the old file's then the new file's. The caret's line
/// is bold, and an accent bar at the left marks the change at the caret, the one F8 and the staging commands act on.
/// </summary>
internal sealed class DiffLineNumberMargin(Func<DiffPane> pane, Func<DiffChange?> currentChange) : AvaloniaEdit.Editing.AbstractMargin
{
    private const double BarWidth = 3;
    private double _digitWidth = 8;

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

        InvalidateMeasure();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>The digits of the largest line number a column shows.</summary>
    private int Digits()
    {
        var max = 1;
        foreach (var line in pane().Lines)
        {
            max = Math.Max(max, Math.Max(line.OldLine, line.NewLine));
        }

        return Math.Max(2, max.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
    }

    private Typeface Typeface => new(GetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty));

    private double FontSize => GetValue(Avalonia.Controls.Documents.TextElement.FontSizeProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        _digitWidth = new FormattedText("9", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, FontSize, Brushes.Black).WidthIncludingTrailingWhitespace;
        var columns = pane().Side == DiffPaneSide.Inline ? 2 : 1;
        return new Size(BarWidth + 4 + (columns * ((Digits() * _digitWidth) + 8)), 0);
    }

    /// <summary>The panes' texts changed: the width follows the largest number.</summary>
    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var current = pane();
        if (TextView is not { VisualLinesValid: true } view)
        {
            return;
        }

        var foreground = this.TryFindResource("Shell.Editor.LineNumbers", ActualThemeVariant, out var f) && f is IBrush numbers ? numbers : Brushes.Gray;
        var accent = this.TryFindResource("Shell.Accent", ActualThemeVariant, out var a) && a is IBrush accentBrush ? accentBrush : Brushes.SteelBlue;
        var caretLine = CaretLine?.Invoke() ?? -1;
        var change = currentChange();
        var columnWidth = (Digits() * _digitWidth) + 8;
        var lines = current.Lines;
        foreach (var line in view.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            if ((uint)index >= (uint)lines.Count)
            {
                continue;
            }

            var paneLine = lines[index];
            var top = line.VisualTop - view.VerticalOffset;
            if (change is { } c && c.Contains(paneLine.Row) && paneLine.Kind is not 'z')
            {
                context.DrawRectangle(accent, null, new Rect(0, top, BarWidth, DiffRenderer.OwnHeight(line)));
            }

            if (line.Height > DiffRenderer.OwnHeight(line) && change is { } next && index + 1 < lines.Count && next.Contains(lines[index + 1].Row) && lines[index + 1].Kind is not 'z')
            {
                context.DrawRectangle(accent, null, new Rect(0, top + DiffRenderer.OwnHeight(line), BarWidth, line.Height - DiffRenderer.OwnHeight(line))); // the next line's CodeLens row
            }

            var bold = index + 1 == caretLine;
            var (oldNumber, newNumber) = current.NumbersOf(index);
            if (current.Side == DiffPaneSide.Inline)
            {
                Number(oldNumber, 0);
                Number(newNumber, 1);
            }
            else
            {
                Number(Math.Max(oldNumber, newNumber), 0);
            }

            void Number(int number, int column)
            {
                if (number <= 0)
                {
                    return;
                }

                var text = new FormattedText(
                    number.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    bold ? new Typeface(Typeface.FontFamily, weight: FontWeight.Bold) : Typeface, FontSize, foreground);
                var right = BarWidth + 4 + ((column + 1) * columnWidth) - 8;
                context.DrawText(text, new Point(right - text.WidthIncludingTrailingWhitespace, top));
            }
        }
    }

    /// <summary>The caret's 1-based line, bold.</summary>
    public Func<int>? CaretLine { get; set; }
}

/// <summary>
/// The staging column (D45 questions 4 and 5): between the line numbers and the signs, so it never covers code, each
/// change gets its buttons at its first row in view, Stage and Discard (working tree) or Unstage (staged). The buttons
/// aren't tab stops: the keyboard reaches the same commands through the code's context menu. Hovering one outlines the
/// lines it acts on, and its tooltip names them.
/// </summary>
internal sealed class DiffStagingMargin : Canvas, ITextViewConnect
{
    private const double ButtonSize = 18;
    private readonly Func<DiffPane> _pane;
    private readonly Func<IReadOnlyList<DiffChange>> _changes;
    private readonly IReadOnlyList<(DiffAction Action, string Icon)> _actions;
    private readonly Func<DiffChange, DiffAction, string> _tooltip;
    private readonly Action<DiffChange, DiffAction> _click;
    private readonly Action<DiffChange?, DiffAction> _hover;
    private readonly List<(DiffChange Change, DiffAction Action, Button Button)> _buttons = [];
    private TextView? _textView;
    private double _height;

    public DiffStagingMargin(Func<DiffPane> pane, Func<IReadOnlyList<DiffChange>> changes, IReadOnlyList<(DiffAction Action, string Icon)> actions,
        Func<DiffChange, DiffAction, string> tooltip, Action<DiffChange, DiffAction> click, Action<DiffChange?, DiffAction> hover)
    {
        _pane = pane;
        _changes = changes;
        _actions = actions;
        _tooltip = tooltip;
        _click = click;
        _hover = hover;
        Width = (actions.Count * (ButtonSize + 2)) + 4;
        ClipToBounds = true;
    }

    /// <summary>The buttons shown now (tests): each change's, at its first row in view.</summary>
    public IReadOnlyList<(DiffChange Change, DiffAction Action, Button Button)> Buttons => [.. _buttons.Where(b => b.Button.IsVisible)];

    /// <summary>The changes moved: a pair of buttons for each.</summary>
    public void Rebuild()
    {
        Children.Clear();
        _buttons.Clear();
        foreach (var change in _changes())
        {
            foreach (var (action, icon) in _actions)
            {
                var button = new Button { Width = ButtonSize, Height = ButtonSize, MinWidth = 0, MinHeight = 0, Padding = default, Focusable = false, IsTabStop = false };
                button.Classes.AddRange(["toolbar", "icon"]);
                ButtonIcon.SetKind(button, icon);
                var (c, a) = (change, action);
                button.Click += (_, _) => _click(c, a);
                button.PointerEntered += (_, _) =>
                {
                    var tip = _tooltip(c, a);
                    ToolTip.SetTip(button, tip);
                    Avalonia.Automation.AutomationProperties.SetName(button, tip);
                    _hover(c, a);
                };
                button.PointerExited += (_, _) => _hover(null, a);
                Avalonia.Automation.AutomationProperties.SetName(button, _tooltip(c, a));
                _buttons.Add((change, action, button));
                Children.Add(button);
            }
        }

        Reposition();
    }

    /// <summary>While git runs, the buttons wait.</summary>
    public void SetBusy(bool busy)
    {
        foreach (var (_, _, button) in _buttons)
        {
            button.IsEnabled = !busy;
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
        _height = finalSize.Height;
        Reposition();
        return base.ArrangeOverride(finalSize);
    }

    /// <summary>Each change's buttons at its first line in view (a change that starts above the view: its first visible line).</summary>
    private void Reposition()
    {
        if (_textView is not { Document: not null } view)
        {
            return;
        }

        var lines = _pane().Lines;
        var lineHeight = view.DefaultLineHeight;
        var firstVisible = view.VisualLinesValid && view.VisualLines.Count > 0 ? view.VisualLines[0].FirstDocumentLine.LineNumber - 1 : 0;
        var height = Math.Max(_height, Bounds.Height);
        foreach (var (change, action, button) in _buttons)
        {
            var line = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (change.Contains(lines[i].Row) && lines[i].Kind is not 'z' && i >= firstVisible)
                {
                    line = i;
                    break;
                }
            }

            if (line < 0 || line + 1 > view.Document.LineCount)
            {
                button.IsVisible = false;
                continue;
            }

            // The change's first line must be in it: a line after its end belongs to another.
            var top = view.GetVisualTopByDocumentLine(line + 1) - view.ScrollOffset.Y;
            button.IsVisible = top + lineHeight > 0 && top < height;
            var index = 0;
            for (var k = 0; k < _actions.Count; k++)
            {
                if (_actions[k].Action == action)
                {
                    index = k;
                }
            }

            SetLeft(button, 2 + (index * (ButtonSize + 2)));
            SetTop(button, top + Math.Max(0, (lineHeight - ButtonSize) / 2));
        }
    }
}

/// <summary>A folded run's line (Summary) in muted text, so it reads as a note, not code.</summary>
internal sealed class FoldLineColorizer(Control owner, Func<DiffPane> pane) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        var lines = pane().Lines;
        if ((uint)(line.LineNumber - 1) < (uint)lines.Count && lines[line.LineNumber - 1].Kind == 'z' && line.Length > 0
            && owner.TryFindResource("Shell.MutedForeground", owner.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
                element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyle.Italic));
            });
        }
    }
}

/// <summary>
/// A CodeLens row of a diff pane: the indentation that lines it up with its line's code, its titles (empty while its
/// lenses wait for their counts, or on the other side's blank row), and what a click on it runs.
/// </summary>
internal sealed record DiffLensRow(string Indent, string Text, Action? Click)
{
    /// <summary>The other side's row opposite a lens: as tall, empty, not a link.</summary>
    public static DiffLensRow Blank { get; } = new(string.Empty, string.Empty, null);
}

/// <summary>
/// A diff pane's CodeLens rows (the maintainer's report: CodeLens didn't show in a diff), as the editor shows them
/// (<see cref="Editor.EditorInsights"/>): a line's row goes after the end of the line before (a line break, its
/// indentation, the titles smaller and muted in the UI's font), so the line itself keeps its height; the first line's
/// titles go at its end. Side by side, the other side gets a blank row at the same place, as tall, so the sides stay
/// row by row aligned. <paramref name="rowAbove"/> gives the row above a 0-based pane line, or null.
/// </summary>
internal sealed class DiffLensGenerator(TextView view, Func<int, DiffLensRow?> rowAbove) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        var line = CurrentContext.VisualLine.LastDocumentLine;
        return startOffset <= line.EndOffset && (AtEnd(line) is not null || Below(line) is not null) ? line.EndOffset : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.Document.GetLineByOffset(offset);
        if (offset != line.EndOffset)
        {
            return null;
        }

        var atEnd = AtEnd(line);
        var below = Below(line);
        return atEnd is null && below is null ? null : new DiffLensRowElement(view, atEnd, below, Brush);
    }

    /// <summary>The first line's own titles (it has no line before it to show them after), or null.</summary>
    private DiffLensRow? AtEnd(DocumentLine line) => line.LineNumber == 1 && rowAbove(0) is { Text.Length: > 0 } row ? row : null;

    /// <summary>The next line's row, after this one's end, or null.</summary>
    private DiffLensRow? Below(DocumentLine line) => line.NextLine is not null ? rowAbove(line.LineNumber) : null;

    private IBrush Brush(string key) => view.TryFindResource(key, view.ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
}

/// <summary>
/// The end of a diff pane's line: the first line's own CodeLens titles, and the next line's row after a line break. Not
/// part of the text and no caret stops. The row is a zero-width space in the code's font, then the indentation and the
/// titles (a space when there are none) in the smaller UI font: a blank row and a row of titles are exactly as tall.
/// </summary>
internal sealed class DiffLensRowElement : VisualLineElement
{
    private const string ZeroWidthSpace = "​";
    private readonly TextView _view;
    private readonly DiffLensRow? _atEnd;
    private readonly DiffLensRow? _below;
    private readonly Func<string, IBrush> _brush;
    private readonly string _end;
    private readonly string _indent;
    private readonly string _titles;

    public DiffLensRowElement(TextView view, DiffLensRow? atEnd, DiffLensRow? below, Func<string, IBrush> brush)
        : base(Length(atEnd, below), 0)
    {
        _view = view;
        _atEnd = atEnd;
        _below = below;
        _brush = brush;
        _end = atEnd is null ? string.Empty : "    " + atEnd.Text;
        _indent = below is null ? string.Empty : ZeroWidthSpace + below.Indent;
        _titles = below is null ? string.Empty : below.Text.Length > 0 ? below.Text : " ";
    }

    private static int Length(DiffLensRow? atEnd, DiffLensRow? below) =>
        (atEnd is null ? 0 : 4 + atEnd.Text.Length) + (below is null ? 0 : 1 + 1 + below.Indent.Length + Math.Max(1, below.Text.Length));

    public override Avalonia.Media.TextFormatting.TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var at = startVisualColumn - VisualColumn;
        var lens = _brush("Shell.Editor.CodeLens.Foreground");
        if (at < _end.Length)
        {
            return Run(_end[at..], lens, small: true, ui: false);
        }

        at -= _end.Length;
        if (at == 0)
        {
            return new Avalonia.Media.TextFormatting.TextEndOfLine(1);
        }

        at--;
        return at < _indent.Length
            ? Run(_indent[at..], lens, small: false, ui: false)
            : Run(_titles[(at - _indent.Length)..], lens, small: true, ui: true);
    }

    private Avalonia.Media.TextFormatting.TextCharacters Run(string text, IBrush foreground, bool small, bool ui)
    {
        var properties = new VisualLineElementTextRunProperties(TextRunProperties);
        properties.SetForegroundBrush(foreground);
        if (ui)
        {
            properties.SetTypeface(new Typeface(FontFamily.Default));
        }

        if (small)
        {
            properties.SetFontRenderingEmSize(TextRunProperties.FontRenderingEmSize * 0.9);
        }

        return new Avalonia.Media.TextFormatting.TextCharacters(text, properties);
    }

    public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) => -1;

    public override bool IsWhitespace(int visualColumn) => false;

    /// <summary>The click of the pointer's row: the first line's own titles, or the row below.</summary>
    private Action? ClickAt(Avalonia.Input.PointerEventArgs e)
    {
        var y = e.GetPosition(_view).Y + _view.ScrollOffset.Y;
        if (_view.GetVisualLineFromVisualTop(y) is not { } visualLine)
        {
            return null;
        }

        var below = visualLine.TextLines.IndexOf(visualLine.GetTextLineByVisualYPosition(y)) > 0;
        return below ? _below?.Click : _atEnd?.Click;
    }

    protected override void OnQueryCursor(Avalonia.Input.PointerEventArgs e)
    {
        if (ClickAt(e) is not null && e.Source is Avalonia.Input.InputElement element)
        {
            element.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed && ClickAt(e) is { } click)
        {
            click();
            e.Handled = true;
        }
    }
}
