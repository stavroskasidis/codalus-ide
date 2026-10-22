using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Views;

/// <summary>
/// Draws under a diff pane's code (#86, D45): removed and added lines on their fills (<c>Shell.Diff.Removed.Line</c>,
/// <c>.Added.Line</c>), the changed words of paired lines on a deeper fill with a 1 px border
/// (<c>.Removed.Word</c>, <c>.Added.Word</c>, <c>.WordBorder</c>; High Contrast's fills are the background, so only the
/// borders show), and hatching on filler lines, where the other side has lines (<c>Shell.Diff.Filler</c>).
/// </summary>
internal sealed class DiffRenderer(Control owner, Func<DiffPane> pane, Func<int, IReadOnlyList<(int Start, int Length)>> words) : IBackgroundRenderer
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
        foreach (var line in textView.VisualLines)
        {
            var index = line.FirstDocumentLine.LineNumber - 1;
            if ((uint)index >= (uint)lines.Count)
            {
                continue;
            }

            var kind = lines[index].Kind;
            var top = line.VisualTop - textView.VerticalOffset;
            var rect = new Rect(0, top, width, line.Height);
            switch (kind)
            {
                case '-' or '+':
                    drawingContext.DrawRectangle(kind == '-' ? removedLine : addedLine, null, rect);
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

                    break;
                case 'f' when hatch is not null:
                    // Diagonal stripes, anchored to the document so a run of filler lines draws one pattern.
                    using (drawingContext.PushClip(rect))
                    {
                        var phase = line.VisualTop % HatchSpacing;
                        for (var x = -line.Height - phase; x < width; x += HatchSpacing)
                        {
                            drawingContext.DrawLine(hatch, new Point(x, rect.Bottom), new Point(x + line.Height, rect.Top));
                        }
                    }

                    break;
            }
        }
    }

    private IBrush? Brush(string key) => owner.TryFindResource(key, owner.ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;
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
                context.DrawRectangle(accent, null, new Rect(0, top, BarWidth, line.Height));
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
