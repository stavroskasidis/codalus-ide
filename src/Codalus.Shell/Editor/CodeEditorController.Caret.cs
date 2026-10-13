using AvaloniaEdit;

namespace Codalus.Shell.Editor;

/// <summary>Where a caret command moves.</summary>
public enum CaretMove
{
    Left,
    Right,
    Up,
    Down,
    LineStart,
    LineEnd,
    WordLeft,
    WordRight,
    PageUp,
    PageDown,
    DocumentStart,
    DocumentEnd,
}

/// <summary>
/// Caret commands for keyboard schemes that move without the arrow keys (M23a, the Emacs scheme): each moves the caret,
/// or extends the selection while a mark is set (<c>editor.action.setSelectionAnchor</c>, Ctrl+Space in Emacs).
/// </summary>
public sealed partial class CodeEditorController
{
    /// <summary>True between setting a mark and cancelling it (or cutting, copying, killing).</summary>
    public bool IsMarkSet { get; private set; }

    public void MoveCaret(CaretMove move)
    {
        var command = (move, IsMarkSet) switch
        {
            (CaretMove.Left, false) => EditingCommands.MoveLeftByCharacter,
            (CaretMove.Left, true) => EditingCommands.SelectLeftByCharacter,
            (CaretMove.Right, false) => EditingCommands.MoveRightByCharacter,
            (CaretMove.Right, true) => EditingCommands.SelectRightByCharacter,
            (CaretMove.Up, false) => EditingCommands.MoveUpByLine,
            (CaretMove.Up, true) => EditingCommands.SelectUpByLine,
            (CaretMove.Down, false) => EditingCommands.MoveDownByLine,
            (CaretMove.Down, true) => EditingCommands.SelectDownByLine,
            (CaretMove.LineStart, false) => EditingCommands.MoveToLineStart,
            (CaretMove.LineStart, true) => EditingCommands.SelectToLineStart,
            (CaretMove.LineEnd, false) => EditingCommands.MoveToLineEnd,
            (CaretMove.LineEnd, true) => EditingCommands.SelectToLineEnd,
            (CaretMove.WordLeft, false) => EditingCommands.MoveLeftByWord,
            (CaretMove.WordLeft, true) => EditingCommands.SelectLeftByWord,
            (CaretMove.WordRight, false) => EditingCommands.MoveRightByWord,
            (CaretMove.WordRight, true) => EditingCommands.SelectRightByWord,
            (CaretMove.PageUp, false) => EditingCommands.MoveUpByPage,
            (CaretMove.PageUp, true) => EditingCommands.SelectUpByPage,
            (CaretMove.PageDown, false) => EditingCommands.MoveDownByPage,
            (CaretMove.PageDown, true) => EditingCommands.SelectDownByPage,
            (CaretMove.DocumentStart, false) => EditingCommands.MoveToDocumentStart,
            (CaretMove.DocumentStart, true) => EditingCommands.SelectToDocumentStart,
            (CaretMove.DocumentEnd, false) => EditingCommands.MoveToDocumentEnd,
            _ => EditingCommands.SelectToDocumentEnd,
        };
        command.Execute(null, Editor.TextArea);
    }

    /// <summary>Sets the mark at the caret: the caret commands extend the selection from here.</summary>
    public void SetMark()
    {
        Editor.TextArea.ClearSelection();
        IsMarkSet = true;
    }

    /// <summary>Cancels the mark and the selection (Ctrl+G in Emacs).</summary>
    public void CancelMark()
    {
        IsMarkSet = false;
        Editor.TextArea.ClearSelection();
    }

    /// <summary>Deletes the character after the caret, or the selection.</summary>
    public void DeleteRight()
    {
        if (!Model.IsReadOnly)
        {
            EditingCommands.Delete.Execute(null, Editor.TextArea);
        }
    }

    /// <summary>
    /// Cuts from the caret to the end of its line, or the line break when the caret is already there (Emacs's kill-line,
    /// Ctrl+K); the text goes to the clipboard and its ring.
    /// </summary>
    public void CutToLineEnd()
    {
        IsMarkSet = false;
        if (Model.IsReadOnly)
        {
            return;
        }

        var caret = Editor.CaretOffset;
        var line = Model.Text.GetLineByOffset(caret);
        var end = caret < line.EndOffset ? line.EndOffset : line.EndOffset + line.DelimiterLength;
        if (end > caret)
        {
            Editor.Select(caret, end - caret);
            Editor.Cut();
        }
    }

    /// <summary>Cut and copy end the mark (Emacs's Ctrl+W and Alt+W).</summary>
    public void CutSelection()
    {
        IsMarkSet = false;
        Editor.Cut();
    }

    public void CopySelection()
    {
        IsMarkSet = false;
        Editor.Copy();
        Editor.TextArea.ClearSelection();
    }
}
