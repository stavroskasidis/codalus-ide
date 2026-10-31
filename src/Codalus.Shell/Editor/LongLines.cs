using System.Runtime.CompilerServices;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor;

/// <summary>
/// Draws a line up to <see cref="MaxRendered"/> characters, then a "… N more characters" marker in place of the rest, as
/// VS Code does (M56). AvaloniaEdit shapes a whole line into one text line at every redraw, so a minified script, a source
/// map or a one-line log of a megabyte froze the window for seconds each time its line was drawn. The text is all still
/// there: selecting, copying, find and save keep every character. Colors stop at the same point: the marker takes the
/// place of whatever the colorizers did past it, and it runs after all of them (its owner keeps it last).
/// <para>
/// The caret is never inside the hidden part. The arrow keys step over the marker in one go, to the line's end or back to
/// the marker's start. A caret that gets into the hidden part another way (typing at the marker, Find, Go To, a
/// diagnostic) has its line drawn up to it, so it shows with what was typed or found there, up to
/// <see cref="MaxDrawnToCaret"/> characters. Further in, it moves on the way it was going: forward to the line's end, or
/// back to that limit, so Find Next and Find Previous pass the line rather than land on its hidden matches again.
/// </para>
/// </summary>
internal sealed class LongLineCutter : IVisualLineTransformer
{
    /// <summary>The characters drawn of a line (VS Code's <c>editor.stopRenderingLineAfter</c>).</summary>
    public const int MaxRendered = 10_000;

    /// <summary>The furthest a line is drawn to show the caret on it: wherever the caret goes, a line costs at most this.</summary>
    public const int MaxDrawnToCaret = 2 * MaxRendered;

    private static bool s_noTextRunProperties;

    private readonly TextArea _textArea;
    private readonly Func<IBrush> _brush;

    /// <summary>The line drawn up to the caret and where it's cut: further while the caret stays on it, back to the usual cut when it leaves.</summary>
    private (TextDocument Document, DocumentLine Line, int Cut)? _drawnToCaret;

    /// <summary>Where the caret was at its last move, and the document's version then: which way a placement went.</summary>
    private (int Offset, ITextSourceVersion? Version) _caret;

    public LongLineCutter(TextArea textArea, Func<IBrush> brush)
    {
        _textArea = textArea;
        _brush = brush;
        textArea.Caret.PositionChanged += OnCaretMoved;
    }

    /// <summary>The cuts made so far, one each time a long line is built (tests).</summary>
    public int Cuts { get; private set; }

    /// <summary>Stops following the caret (its view closed).</summary>
    public void Detach() => _textArea.Caret.PositionChanged -= OnCaretMoved;

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
    {
        var line = context.VisualLine;
        var cut = _drawnToCaret is { } drawn && drawn.Line == line.FirstDocumentLine ? drawn.Cut : MaxRendered;
        if (line.VisualLength <= cut + 1)
        {
            return; // the marker would be as long as what it hides
        }

        // Never between the halves of a surrogate pair, wherever the colorizers before split the line.
        if (char.IsHighSurrogate(context.Document.GetCharAt(line.FirstDocumentLine.Offset + line.GetRelativeOffset(cut) - 1)))
        {
            cut--;
        }

        // The element the cut falls in is split there; one that can't be goes behind the marker.
        var index = 0;
        while (index < elements.Count && elements[index].VisualColumn + elements[index].VisualLength <= cut)
        {
            index++;
        }

        if (index < elements.Count && elements[index] is VisualLineText text && text.VisualColumn < cut)
        {
            text.Split(cut, elements, index);
            index++;
        }

        if (index >= elements.Count)
        {
            return;
        }

        var hidden = 0;
        for (var i = index; i < elements.Count; i++)
        {
            hidden += elements[i].DocumentLength;
        }

        var properties = new VisualLineElementTextRunProperties(context.GlobalTextRunProperties);
        properties.SetForegroundBrush(_brush());
        var marker = new Marker(Localizer.Format("msg.editor.moreCharacters", "… {0:N0} more characters", hidden), hidden);
        if (GiveTextRunProperties(marker, properties))
        {
            line.ReplaceElement(index, elements.Count - index, marker);
            Cuts++;
        }
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (_textArea.Document is not { } document)
        {
            return;
        }

        var offset = _textArea.Caret.Offset;
        var back = ReferenceEquals(_caret.Version, document.Version) && offset < _caret.Offset; // put there, not moved by an edit
        _caret = (offset, document.Version);
        var line = document.GetLineByOffset(offset);
        var column = offset - line.Offset;
        if (column > MaxDrawnToCaret && column < line.Length)
        {
            var limit = line.Offset + MaxDrawnToCaret;
            _textArea.Caret.Offset = !back ? line.EndOffset : char.IsHighSurrogate(document.GetCharAt(limit - 1)) ? limit - 1 : limit;
            return; // the move this makes draws the line
        }

        // In the hidden part, near: the line is drawn up to the caret, and stays so while the caret is on it.
        var drawn = _drawnToCaret;
        var cut = drawn is { } same && same.Document == document && same.Line == line ? same.Cut : MaxRendered;
        if (column > cut && column < line.Length)
        {
            cut = column;
        }

        if (drawn is { } was && (was.Document != document || was.Line != line))
        {
            _drawnToCaret = null;
            if (was.Document == document && !was.Line.IsDeleted)
            {
                _textArea.TextView.Redraw(was.Line); // the caret left: the usual cut again
            }
        }

        if (cut > MaxRendered && _drawnToCaret?.Cut != cut)
        {
            _drawnToCaret = (document, line, cut);
            _textArea.TextView.Redraw(line);
        }
    }

    private static bool GiveTextRunProperties(VisualLineElement element, VisualLineElementTextRunProperties properties)
    {
        if (!s_noTextRunProperties)
        {
            try
            {
                SetTextRunProperties(element, properties);
                return true;
            }
            catch (MissingMethodException)
            {
                s_noTextRunProperties = true; // a package without it: lines drawn whole rather than an exception at each one
            }
        }

        return false;
    }

    // AvaloniaEdit gives an element its text properties only while generators build a line, and a line over 3,000
    // characters is built without them; ReplaceElement leaves a transformer's element without any, and the colorizers
    // (and the selection's) set colors on every element's properties. Native AOT resolves this accessor at compile time.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetTextRunProperties")]
    private static extern void SetTextRunProperties(VisualLineElement element, VisualLineElementTextRunProperties properties);

    /// <summary>The rest of a long line, drawn as its count of characters: one caret stop wide, so the caret steps over it.</summary>
    internal sealed class Marker(string text, int documentLength) : FormattedTextElement(text, documentLength);
}
