using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Codalus.Shell.Editor;

/// <summary>
/// The line numbers with the caret's line number in its own, brighter color (<c>Shell.Editor.LineNumbers.Current</c>), as
/// the classic IDE draws them (#83, D47). AvaloniaEdit's margin draws every number in one brush; this one replaces it in
/// the editor's left margins, so the editor's own code (turning numbers off, the foreground) still finds a
/// <see cref="LineNumberMargin"/>.
/// </summary>
public sealed class CurrentLineNumberMargin : LineNumberMargin
{
    public static readonly StyledProperty<IBrush?> CurrentForegroundProperty =
        AvaloniaProperty.Register<CurrentLineNumberMargin, IBrush?>(nameof(CurrentForeground));

    private int _caretLine;

    static CurrentLineNumberMargin() => AffectsRender<CurrentLineNumberMargin>(CurrentForegroundProperty);

    /// <summary>The caret line's number color; null draws it as the others.</summary>
    public IBrush? CurrentForeground
    {
        get => GetValue(CurrentForegroundProperty);
        set => SetValue(CurrentForegroundProperty, value);
    }

    /// <summary>The one-based line the caret is on, whose number takes <see cref="CurrentForeground"/>.</summary>
    public int CaretLine => TextArea?.Caret.Line ?? 0;

    /// <summary>The brush a line's number is drawn in.</summary>
    public IBrush? BrushFor(int lineNumber) =>
        lineNumber == CaretLine && CurrentForeground is { } current ? current : GetValue(TemplatedControl.ForegroundProperty);

    /// <summary>Replaces <paramref name="editor"/>'s line number margin with one of these (no-op when it already has one or none).</summary>
    public static CurrentLineNumberMargin? Install(TextEditor editor, IObservable<object?> currentForeground)
    {
        var margins = editor.TextArea.LeftMargins;
        var index = -1;
        for (var i = 0; i < margins.Count; i++)
        {
            if (margins[i] is LineNumberMargin)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return null;
        }

        if (margins[index] is CurrentLineNumberMargin installed)
        {
            return installed;
        }

        var margin = new CurrentLineNumberMargin();
        margin.Bind(TemplatedControl.ForegroundProperty, editor.GetObservable(TextEditor.LineNumbersForegroundProperty));
        margin.Bind(CurrentForegroundProperty, currentForeground);
        margins[index] = margin;
        return margin;
    }

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (TextArea is { } before)
        {
            before.Caret.PositionChanged -= OnCaretPositionChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
        if (TextArea is { } after)
        {
            after.Caret.PositionChanged += OnCaretPositionChanged;
            _caretLine = after.Caret.Line;
        }
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        var line = CaretLine;
        if (line != _caretLine)
        {
            _caretLine = line;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid)
        {
            return;
        }

        var width = Bounds.Width;
        foreach (var visualLine in textView.VisualLines)
        {
            var number = visualLine.FirstDocumentLine.LineNumber;
            var text = new FormattedText(number.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, EmSize, BrushFor(number));
            var y = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop);
            drawingContext.DrawText(text, new Point(width - text.Width, y - textView.VerticalOffset));
        }
    }
}
