using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
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

    /// <summary>
    /// The numbers shown, laid out once: a frame of scrolling formatted (shaped) every number on screen again, twice (its
    /// width, then its drawing), about a seventh of the UI thread's time while the 100k-line file scrolled. Only the
    /// last frame's are kept, and all are dropped when the font or the colors change.
    /// </summary>
    private Dictionary<(int Number, IBrush? Brush), TextLayout> _numbers = [];
    private Dictionary<(int Number, IBrush? Brush), TextLayout> _spare = [];
    private (Typeface Typeface, double EmSize, CultureInfo Culture) _numbersFont;

    /// <summary>How many numbers were laid out (tests: scrolling lays out only the new lines').</summary>
    internal int NumbersLaidOut { get; private set; }

    public override void Render(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid)
        {
            return;
        }

        var font = (Typeface, EmSize, CultureInfo.CurrentCulture);
        if (font != _numbersFont)
        {
            _numbersFont = font;
            _numbers.Clear();
        }

        var width = Bounds.Width;
        var shown = _spare;
        foreach (var visualLine in textView.VisualLines)
        {
            var number = visualLine.FirstDocumentLine.LineNumber;
            var key = (number, BrushFor(number));
            if (!_numbers.TryGetValue(key, out var text))
            {
                text = new TextLayout(number.ToString(CultureInfo.CurrentCulture), Typeface, EmSize, key.Item2);
                NumbersLaidOut++;
            }

            shown[key] = text;
            var y = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop);
            text.Draw(drawingContext, new Point(width - text.Width, y - textView.VerticalOffset));
        }

        _numbers.Clear();
        (_numbers, _spare) = (shown, _numbers);
    }
}
