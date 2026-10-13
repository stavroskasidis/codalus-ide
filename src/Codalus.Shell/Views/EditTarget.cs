using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Codalus.Shell.Terminal;

namespace Codalus.Shell.Views;

/// <summary>
/// What Edit → Undo, Cut, Paste… act on (M34a): the focused text box, text editor or terminal; with nothing focused,
/// the active document's editor. A menu click leaves the keyboard focus where it was, so the focused control is the one
/// the user means. Another focused control (the Solution Explorer…) has no text target: the command then runs what
/// the same keys are bound to there (macOS's menu takes ⌘C before the control sees it).
/// </summary>
internal abstract class EditTarget
{
    public virtual void Undo()
    {
    }

    public virtual void Redo()
    {
    }

    public abstract void Cut();

    public abstract void Copy();

    public abstract void Paste();

    public virtual void SelectAll()
    {
    }

    public static EditTarget? Current(ShellHost shell)
    {
        var focused = (shell.Window as TopLevel)?.FocusManager?.GetFocusedElement() as Avalonia.Visual;
        if (focused is MenuItem || focused?.FindAncestorOfType<MenuBase>() is not null || focused is null)
        {
            focused = shell.LastFocus; // the menu holds the focus while it is open: what had it before
        }

        if (focused is TextBox box)
        {
            return new BoxTarget(box);
        }

        if (focused?.FindAncestorOfType<TextEditor>(includeSelf: true) is { } editor)
        {
            return new EditorTarget(editor);
        }

        if (focused?.FindAncestorOfType<TerminalControl>(includeSelf: true) is { } terminal)
        {
            return new TerminalTarget(terminal);
        }

        if (focused is not null)
        {
            return null;
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

    private sealed class TerminalTarget(TerminalControl terminal) : EditTarget
    {
        public override void Cut() => _ = terminal.CopyAsync();

        public override void Copy() => _ = terminal.CopyAsync();

        public override void Paste() => _ = terminal.PasteAsync();
    }
}
