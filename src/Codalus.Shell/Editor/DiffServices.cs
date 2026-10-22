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

    /// <summary>
    /// The language service's semantic tokens (in the shell's legend) for a file whose open document's text is
    /// <c>text</c>, or null: the file isn't open, its text is another, or its language service has none.
    /// </summary>
    public Func<string, string, CancellationToken, Task<int[]?>> SemanticTokensAsync { get; init; } = (_, _, _) => Task.FromResult<int[]?>(null);
}
