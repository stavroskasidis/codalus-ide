using System.Runtime.CompilerServices;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor;

/// <summary>
/// Draws a line up to <see cref="MaxRendered"/> characters, then a "… N more characters" marker in place of the rest, as
/// VS Code does (M56). AvaloniaEdit shapes a whole line into one text line at every redraw, so a minified script, a source
/// map or a one-line log of a megabyte froze the window for seconds each time its line was drawn. The text is all still
/// there: selecting, copying, find and save keep every character, and the caret moves over the marker in one step, to the
/// line's end. Colors stop at the same point: the marker takes the place of whatever the colorizers did past it, and it
/// runs after all of them (<see cref="CodeEditorController"/> keeps it last).
/// </summary>
internal sealed class LongLineCutter(Func<IBrush> brush) : IVisualLineTransformer
{
    /// <summary>The characters drawn of a line (VS Code's <c>editor.stopRenderingLineAfter</c>).</summary>
    public const int MaxRendered = 10_000;

    /// <summary>The lines cut since the editor opened (tests).</summary>
    public int LinesCut { get; private set; }

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
    {
        var line = context.VisualLine;
        if (line.VisualLength <= MaxRendered + 1)
        {
            return; // the marker would be as long as what it hides
        }

        // The element the limit falls in: split there when it can be, else cut after it.
        var index = 0;
        while (index < elements.Count && elements[index].VisualColumn + elements[index].VisualLength <= MaxRendered)
        {
            index++;
        }

        if (index < elements.Count && elements[index] is { VisualColumn: < MaxRendered } straddling)
        {
            if (straddling is VisualLineText && SplitColumn(context, straddling) is var column && column > straddling.VisualColumn)
            {
                straddling.Split(column, elements, index);
            }

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
        properties.SetForegroundBrush(brush());
        var marker = new Marker(Localizer.Format("msg.editor.moreCharacters", "… {0:N0} more characters", hidden), hidden);
        SetTextRunProperties(marker, properties);
        line.ReplaceElement(index, elements.Count - index, marker);
        LinesCut++;
    }

    /// <summary>The visual column the limit splits a text element at, one before it when that would halve a surrogate pair.</summary>
    private static int SplitColumn(ITextRunConstructionContext context, VisualLineElement element)
    {
        var offset = context.VisualLine.FirstDocumentLine.Offset + element.RelativeTextOffset + (MaxRendered - element.VisualColumn);
        return offset > 0 && char.IsHighSurrogate(context.Document.GetCharAt(offset - 1)) ? MaxRendered - 1 : MaxRendered;
    }

    // AvaloniaEdit gives an element its text properties only while generators build a line, and a line over 3,000
    // characters is built without them; ReplaceElement leaves a transformer's element without any, and the colorizers
    // (and the selection's) set colors on every element's properties. Native AOT resolves this accessor at compile time.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetTextRunProperties")]
    private static extern void SetTextRunProperties(VisualLineElement element, VisualLineElementTextRunProperties properties);

    /// <summary>The rest of a long line, drawn as its count of characters: one caret stop wide, so the caret steps over it.</summary>
    internal sealed class Marker(string text, int documentLength) : FormattedTextElement(text, documentLength);
}
