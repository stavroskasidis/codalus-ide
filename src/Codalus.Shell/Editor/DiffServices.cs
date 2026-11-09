using Codalus.LanguageServices.Protocol;

namespace Codalus.Shell.Editor;

/// <summary>
/// What the diff document and the merge editor need from the shell (#86): the editor's colors and grammars, the
/// languages of files, and the open documents with their language services. The shell makes one; a view model without
/// it (a test) draws plain text.
/// </summary>
public sealed class DiffServices
{
    public DiffServices(IEditorServices editor)
    {
        Editor = editor;
        Tokenizer = new GrammarTokenizer(editor.TextMate);
    }

    public IEditorServices Editor { get; }

    public GrammarTokenizer Tokenizer { get; }

    /// <summary>A file's language id, or null.</summary>
    public Func<string, string?> LanguageOf { get; init; } = CodeColors.LanguageOf;

    /// <summary>The open document of a file (its editor's text, saved or not), or null when the file isn't open.</summary>
    public Func<string, CodeDocument?> OpenDocument { get; init; } = _ => null;

    /// <summary>The editor of a file's open document (its symbols fill a diff side's navigation bar), or null.</summary>
    public Func<string, CodeEditorController?> OpenEditor { get; init; } = _ => null;

    /// <summary>Whether <c>text</c> is the open document's text of a file (null: compares with <see cref="OpenDocument"/>).</summary>
    public Func<string, string, bool>? OpenText { get; init; }

    /// <summary>Whether a side's text is the open document's: that side gets the language service's colors and the navigation bar.</summary>
    public bool IsOpenText(string path, string text) => OpenText?.Invoke(path, text) ?? OpenDocument(path)?.Text.Text == text;

    /// <summary>Saves an open document as the editor saves it (format on save and all); false when it couldn't.</summary>
    public Func<CodeDocument, Task<bool>> SaveAsync { get; init; } = _ => Task.FromResult(false);

    /// <summary>Shows a dialog (a discard's confirmation) and returns the button chosen.</summary>
    public Func<ViewModels.DialogRequest, Task<ViewModels.DialogResult>> ShowDialogAsync { get; init; } = _ => Task.FromResult(new ViewModels.DialogResult(null, null));

    /// <summary>Tells the user something that stopped an action (a save that changed the text).</summary>
    public Action<string> ShowMessage { get; init; } = _ => { };

    /// <summary>
    /// Discard All… on a file: today's confirmation (with a sentence when the open document's unsaved edits go too),
    /// then the working tree back to the index; true when it was discarded.
    /// </summary>
    public Func<string, bool, Task<bool>> DiscardAllAsync { get; init; } = (_, _) => Task.FromResult(false);

    /// <summary>
    /// The language service's semantic tokens (in the shell's legend) for a file whose open document's text is
    /// <c>text</c>, or null: the file isn't open, its text is another, or its language service has none.
    /// </summary>
    public Func<string, string, CancellationToken, Task<int[]?>> SemanticTokensAsync { get; init; } = (_, _, _) => Task.FromResult<int[]?>(null);

    /// <summary>
    /// Holds a file's document while a working-tree diff shows it (its language service then knows the file, open in an
    /// editor or not), or null when it can't be read; <see cref="ReleaseDocument"/> lets go of it.
    /// </summary>
    public Func<string, Task<CodeDocument?>> AcquireDocumentAsync { get; init; } = _ => Task.FromResult<CodeDocument?>(null);

    /// <summary>Lets go of a document <see cref="AcquireDocumentAsync"/> held: forgotten when nothing else shows or holds it.</summary>
    public Action<CodeDocument> ReleaseDocument { get; init; } = _ => { };

    /// <summary>Runs a CodeLens command (Find All References…) at a position of a file: opened there in the editor, then the command.</summary>
    public Func<string, Position, string, Task> RunLensAsync { get; init; } = (_, _, _) => Task.CompletedTask;
}
