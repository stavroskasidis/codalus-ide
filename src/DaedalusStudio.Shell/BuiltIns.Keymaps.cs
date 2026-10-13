namespace DaedalusStudio.Shell;

/// <summary>
/// The keyboard schemes over the default one (M23a): ReSharper's and Emacs's shortcuts. Both keep the default scheme's
/// bindings (their rules test <c>keyboardScheme != vscode</c>) and come last, so theirs win where a key is taken.
/// </summary>
internal static partial class BuiltIns
{
    /// <summary>ReSharper's shortcuts (its Visual Studio scheme) and Emacs's editing and <c>Ctrl+X</c> keys.</summary>
    public const string SchemeKeybindings = """
        [
          { "key": "ctrl+shift+r", "command": "editor.action.quickFix", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "alt+f7", "command": "editor.action.referenceSearch", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+alt+enter", "command": "editor.action.formatDocument", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+e ctrl+c", "command": "editor.action.codeCleanup", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+e ctrl+f", "command": "editor.action.codeCleanup", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+w", "command": "editor.action.smartSelect.expand", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+shift+w", "command": "editor.action.smartSelect.shrink", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+alt+shift+up", "command": "editor.action.moveLinesUpAction", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+alt+shift+down", "command": "editor.action.moveLinesDownAction", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+alt+/", "command": "editor.action.commentLine", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+p", "command": "editor.action.triggerParameterHints", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "alt+end", "command": "editor.action.goToDerived", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "alt+pagedown", "command": "editor.action.marker.next", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "alt+pageup", "command": "editor.action.marker.prev", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+alt+j", "command": "editor.action.surroundWithSnippet", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "ctrl+shift+alt+h", "command": "editor.action.showCallHierarchy", "when": "editorTextFocus && keyboardScheme == resharper" },
          { "key": "shift+alt+l", "command": "explorer.reveal", "when": "keyboardScheme == resharper" },
          { "key": "ctrl+shift+t", "command": "workbench.action.gotoAll", "when": "keyboardScheme == resharper" },

          { "key": "ctrl+f", "command": "cursorRight", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+b", "command": "cursorLeft", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+n", "command": "cursorDown", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+p", "command": "cursorUp", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+a", "command": "cursorHome", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+e", "command": "cursorEnd", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "alt+f", "command": "cursorWordRight", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "alt+b", "command": "cursorWordLeft", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+v", "command": "cursorPageDown", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "alt+v", "command": "cursorPageUp", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "alt+shift+,", "command": "cursorTop", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "alt+shift+.", "command": "cursorBottom", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+d", "command": "deleteRight", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+k", "command": "editor.action.cutToLineEnd", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+space", "command": "editor.action.setSelectionAnchor", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+g", "command": "editor.action.cancelSelectionAnchor", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+w", "command": "editor.action.clipboardCutAction", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "alt+w", "command": "editor.action.clipboardCopyAction", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+y", "command": "edit.paste", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+/", "command": "edit.undo", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+s", "command": "editor.action.find", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+r", "command": "editor.action.findPrevious", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "alt+shift+5", "command": "editor.action.replace", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "alt+;", "command": "editor.action.commentLine", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "alt+g g", "command": "editor.action.gotoLine", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "alt+x", "command": "workbench.showCommandPalette", "when": "keyboardScheme == emacs" },
          { "key": "ctrl+x ctrl+s", "command": "file.save", "when": "activeDocumentKind == textFile && keyboardScheme == emacs" },
          { "key": "ctrl+x s", "command": "file.saveAll", "when": "keyboardScheme == emacs" },
          { "key": "ctrl+x ctrl+f", "command": "workbench.openFile", "when": "keyboardScheme == emacs" },
          { "key": "ctrl+x b", "command": "workbench.action.gotoAll", "when": "keyboardScheme == emacs" },
          { "key": "ctrl+x k", "command": "workbench.closeActiveDocument", "when": "activeDocumentKind && keyboardScheme == emacs" },
          { "key": "ctrl+x h", "command": "edit.selectAll", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+x u", "command": "edit.undo", "when": "editorTextFocus && keyboardScheme == emacs" },
          { "key": "ctrl+x 2", "command": "workbench.splitEditorDown", "when": "activeDocumentKind == textFile && keyboardScheme == emacs" },
          { "key": "ctrl+x 3", "command": "workbench.splitEditorRight", "when": "activeDocumentKind == textFile && keyboardScheme == emacs" },
          { "key": "ctrl+x o", "command": "workbench.focusNextPane", "when": "keyboardScheme == emacs" },
          { "key": "ctrl+x ctrl+c", "command": "workbench.quit", "when": "keyboardScheme == emacs" }
        ]
        """;
}
