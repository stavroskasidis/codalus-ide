using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// What Edit → Undo, Cut, Paste… act on (M34a): the focused text box or text editor, else the active document's
/// editor. A menu click leaves the keyboard focus where it was, so the focused control is the one the user means.
/// </summary>
internal abstract class EditTarget
{
    public abstract void Undo();

    public abstract void Redo();

    public abstract void Cut();

    public abstract void Copy();

    public abstract void Paste();

    public abstract void SelectAll();

    public static EditTarget? Current(ShellHost shell)
    {
        var focused = (shell.Window as TopLevel)?.FocusManager?.GetFocusedElement() as Avalonia.Visual;
        if (focused is TextBox box)
        {
            return new BoxTarget(box);
        }

        if (focused?.FindAncestorOfType<TextEditor>(includeSelf: true) is { } editor)
        {
            return new EditorTarget(editor);
        }

        return shell.ActiveEditor?.Controller?.Editor is { } active ? new EditorTarget(active) : null;
    }

    private sealed class BoxTarget(TextBox box) : EditTarget
    {
        public override void Undo() => box.Undo();

        public override void Redo() => box.Redo();

        public override void Cut() => box.Cut();

        public override void Copy() => box.Copy();

        public override void Paste() => box.Paste();

        public override void SelectAll() => box.SelectAll();
    }

    private sealed class EditorTarget(TextEditor editor) : EditTarget
    {
        public override void Undo() => editor.Undo();

        public override void Redo() => editor.Redo();

        public override void Cut() => editor.Cut();

        public override void Copy() => editor.Copy();

        public override void Paste() => editor.Paste();

        public override void SelectAll() => editor.SelectAll();
    }
}
