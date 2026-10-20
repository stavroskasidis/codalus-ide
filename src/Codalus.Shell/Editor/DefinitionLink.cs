using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.LanguageServices.Client;

namespace Codalus.Shell.Editor;

/// <summary>
/// Ctrl+hover (Cmd on macOS) over a symbol that has a definition: the symbol is underlined and the pointer is a hand, the
/// link that Ctrl+click follows (Go to Definition). The language service is asked once per symbol under the pointer; a
/// keyword, a literal or blank space isn't a link. UI thread only.
/// </summary>
internal sealed class DefinitionLink(TextEditor editor, Func<CodeDocument> model, Func<LanguageHostClient?> client) : DocumentColorizingTransformer, IDisposable
{
    private static readonly Cursor s_hand = new(StandardCursorType.Hand);
    private CancellationTokenSource? _cts;
    private (int Start, int End)? _checking;
    private Point? _pointer;

    /// <summary>The linked symbol's document range, or null.</summary>
    public (int Start, int End)? Link { get; private set; }

    /// <summary>Raised when the link appears or goes (tests).</summary>
    public event EventHandler? Changed;

    /// <summary>The pointer moved over the text (view coordinates), or the modifier keys changed.</summary>
    public void Update(Point? pointer, KeyModifiers modifiers)
    {
        if (pointer is not null)
        {
            _pointer = pointer;
        }

        var word = IsLinkModifier(modifiers) && _pointer is { } at ? WordAt(at) : null;
        if (word is null)
        {
            Clear();
            return;
        }

        if (word == Link)
        {
            ShowHand();
            return;
        }

        if (word == _checking)
        {
            return;
        }

        Clear();
        _checking = word;
        _ = CheckAsync(word.Value);
    }

    /// <summary>Ctrl (Cmd on macOS) alone: Ctrl+Alt adds carets instead.</summary>
    private static bool IsLinkModifier(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta)) && !modifiers.HasFlag(KeyModifiers.Alt) && !modifiers.HasFlag(KeyModifiers.Shift);

    private async Task CheckAsync((int Start, int End) word)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            if (client() is not { } languages)
            {
                return;
            }

            var document = model();
            var locations = await languages.DefinitionAsync(document.Uri, document.PositionAt(word.Start), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || _checking != word || locations.Length == 0)
            {
                return;
            }

            _checking = null;
            Link = word;
            ShowHand();
            Redraw(word);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
            // No link: Ctrl+click still asks and says why.
        }
    }

    /// <summary>The identifier under a point (view coordinates), when the point is on its text.</summary>
    private (int Start, int End)? WordAt(Point point)
    {
        var view = editor.TextArea.TextView;
        if (view.GetPosition(point + view.ScrollOffset) is not { } position)
        {
            return null;
        }

        var document = editor.Document;
        var offset = document.GetOffset(position.Location);
        var line = document.GetLineByOffset(offset);
        int start = offset, end = offset;
        while (start > line.Offset && IsWordChar(document.GetCharAt(start - 1)))
        {
            start--;
        }

        while (end < line.EndOffset && IsWordChar(document.GetCharAt(end)))
        {
            end++;
        }

        if (start == end || char.IsDigit(document.GetCharAt(start)))
        {
            return null;
        }

        // On the word itself, not past the end of a shorter line (GetPosition snaps to the nearest column).
        var endX = view.GetVisualPosition(new TextViewPosition(document.GetLocation(end)), VisualYPosition.LineTop).X - view.ScrollOffset.X;
        return point.X <= endX ? (start, end) : null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void ShowHand()
    {
        // After the text view's own pointer handling, which resets its cursor when the element under the pointer changes.
        editor.TextArea.TextView.Cursor = s_hand;
    }

    public void Clear()
    {
        _cts?.Cancel();
        _checking = null;
        if (Link is { } link)
        {
            Link = null;
            editor.TextArea.TextView.ClearValue(InputElement.CursorProperty);
            Redraw(link);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Redraw((int Start, int End) range)
    {
        if (range.End <= editor.Document.TextLength)
        {
            editor.TextArea.TextView.Redraw(range.Start, range.End - range.Start);
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (Link is { } link && link.Start >= line.Offset && link.End <= line.EndOffset)
        {
            ChangeLinePart(link.Start, link.End, element => element.TextRunProperties.SetTextDecorations(TextDecorations.Underline));
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
