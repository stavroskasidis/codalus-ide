using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Shell.Editor;

/// <summary>
/// Ctrl+hover (Cmd on macOS) over a symbol that has a definition: the symbol is underlined and the pointer is a hand, the
/// link that Ctrl+click follows (Go to Definition). The language service is asked once per symbol under the pointer; a
/// keyword, a literal or blank space isn't a link. An answer without a definition (or none: no service yet, or an error)
/// isn't final, because the service can briefly not know a symbol while it loads the projects again (after the restore,
/// or a project change): the symbol is asked again when the pointer moves on it <see cref="AskAgainAfter"/> or more later,
/// and at once when the service's view of the projects changed (<see cref="Recheck"/>). UI thread only.
/// </summary>
/// <param name="definitions">The definitions of the symbol at a document offset; null without a language service.</param>
/// <param name="time">The clock that says when a symbol may be asked again (tests).</param>
internal sealed class DefinitionLink(TextEditor editor, Func<int, CancellationToken, Task<Location[]?>> definitions, TimeProvider? time = null) : DocumentColorizingTransformer, IDisposable
{
    /// <summary>How long pointer moves on a symbol that had no link don't ask again.</summary>
    internal static readonly TimeSpan AskAgainAfter = TimeSpan.FromSeconds(1);

    private static readonly Cursor s_hand = new(StandardCursorType.Hand);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CancellationTokenSource? _cts;
    private (int Start, int End)? _checking;
    private ((int Start, int End) Word, long At)? _noLink;
    private Point? _pointer;
    private KeyModifiers _modifiers;

    /// <summary>The linked symbol's document range, or null.</summary>
    public (int Start, int End)? Link { get; private set; }

    /// <summary>Raised when the link appears or goes (tests).</summary>
    public event EventHandler? Changed;

    /// <summary>What the link is waiting for, for a test's failure message.</summary>
    internal string State => $"link {Link?.ToString() ?? "none"}, asking for {_checking?.ToString() ?? "nothing"}, no link for {_noLink?.Word.ToString() ?? "nothing"}, {Requests} request(s)";

    /// <summary>The symbol whose last answer wasn't a link (tests).</summary>
    internal (int Start, int End)? Missed => _noLink?.Word;

    /// <summary>The definition requests made (tests).</summary>
    internal int Requests { get; private set; }

    /// <summary>The pointer moved over the text (view coordinates), or the modifier keys changed.</summary>
    public void Update(Point? pointer, KeyModifiers modifiers)
    {
        if (pointer is not null)
        {
            _pointer = pointer;
        }

        _modifiers = modifiers;
        var word = IsLinkModifier(modifiers) && _pointer is { } at ? IdentifierAt(editor, at) : null;
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

        // Asked already: the answer is coming, or it was no link a moment ago.
        if (word == _checking || (_noLink is { } noLink && noLink.Word == word && _time.GetElapsedTime(noLink.At) < AskAgainAfter))
        {
            return;
        }

        Clear();
        _checking = word;
        _ = CheckAsync(word.Value);
    }

    /// <summary>
    /// The language service's view of the projects changed (it loaded them, or started again): the symbol under the
    /// pointer, when it isn't a link, is asked again now, without waiting for the pointer to move.
    /// </summary>
    public void Recheck()
    {
        if (Link is not null || (_checking is null && _noLink is null))
        {
            return;
        }

        // The modifiers are the last pointer or key event's. They're current only while the pointer is on this text and
        // the keyboard focus is in this editor, where Ctrl's release is seen: Ctrl let go in another window (or the editor
        // hidden behind another document) would otherwise underline the symbol with Ctrl up. Then the next move asks.
        var view = editor.TextArea.TextView;
        if (!view.IsPointerOver || !editor.TextArea.IsKeyboardFocusWithin)
        {
            Clear();
            return;
        }

        _checking = null;
        _noLink = null;
        Update(null, _modifiers);
    }

    /// <summary>Ctrl (Cmd on macOS) alone: Ctrl+Alt adds carets instead.</summary>
    private static bool IsLinkModifier(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta)) && !modifiers.HasFlag(KeyModifiers.Alt) && !modifiers.HasFlag(KeyModifiers.Shift);

    private async Task CheckAsync((int Start, int End) word)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        Requests++;
        Location[]? locations = null;
        try
        {
            locations = await definitions(word.Start, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or JsonException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
            // No answer: no link for now, as for no definition. Ctrl+click still asks and says why.
        }

        if (cts.IsCancellationRequested || _checking != word)
        {
            return; // the pointer moved on, Ctrl went, or the symbol is being asked again
        }

        _checking = null;
        if (locations is not { Length: > 0 })
        {
            _noLink = (word, _time.GetTimestamp());
            return;
        }

        Link = word;
        ShowHand();
        Redraw(word);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The identifier under a point (the text view's coordinates), when the point is on its text.</summary>
    internal static (int Start, int End)? IdentifierAt(TextEditor editor, Point point)
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

    /// <summary>No link: the pointer left the symbol, or Ctrl went. The next symbol (or this one again) is asked anew.</summary>
    public void Clear()
    {
        _cts?.Cancel();
        _checking = null;
        _noLink = null;
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
        _checking = null;
        _noLink = null; // nothing for Recheck to ask
    }
}
