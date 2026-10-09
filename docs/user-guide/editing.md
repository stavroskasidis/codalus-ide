# Editing and navigating code

## The editor

- **Tabs and splits**: Window → Split Right (`Ctrl+\`) or Split Down opens another editor group. Window → Split shows two views of the same file. `Ctrl+Tab` switches documents.
- **Multiple carets**: `Ctrl+Alt+Click`, `Ctrl+Alt+Up/Down`, or `Shift+Alt+.` (next occurrence of the selection). `Shift+Alt`+drag selects a box.
- **Folding**: regions, members, usings and comments fold from the margin, or with `Ctrl+M Ctrl+M`.
- **The minimap** on the right shows the whole file; click or drag to scroll (setting **Text Editor → Minimap**).
- **Find and replace**: `Ctrl+F` and `Ctrl+H`, with case, whole word and regular expressions; `F3` and `Shift+F3` move between matches.
- **Files**: encodings, BOMs and line endings are kept as they were. Indentation follows `.editorconfig`. A file changed outside the editor is reloaded (or, with unsaved edits, you are asked). Open documents are restored when the solution opens again.

## C# language services

Daedalus Studio uses Roslyn, with your projects' analyzers and source generators:

- **Highlighting**, by meaning: types, members, locals, parameters, and more.
- **Completion** (`Ctrl+Space`, and while you type). It can add the `using` an item needs.
- **Parameter info** (`Ctrl+Shift+Space`) and **Quick Info** on hover (`Ctrl+K Ctrl+I`).
- **Live diagnostics**: squiggles as you type, and the **Error List** (View → Error List). The Error List filters by severity, by source (Build, IntelliSense or both) and by document; double-click an entry to go to it.
- **Quick actions** (`Ctrl+.` or the lightbulb): code fixes and refactorings, each previewed before it applies.
- **Rename** (`F2`): previewed across the solution.
- **Format Document** (`Ctrl+K Ctrl+D`) and **Format Selection**, following `.editorconfig`, and formatting as you type (`;`, `}`).

## Navigation

| Command | Shortcut | Notes |
|---|---|---|
| Go to Definition | `F12`, `Ctrl+Click` | Into source-generated files and, for library types, into decompiled source (read-only). |
| Go to Implementation | `Ctrl+F12` | |
| Find All References | `Shift+F12` | Results in the Find Results window. |
| Go To All | `Ctrl+T` | Types and members; type `f ` first to search file names. |
| Find in Files | `Ctrl+Shift+F` | Case, whole word, regex and a file filter. |
| Navigate Back and Forward | `Ctrl+-`, `Ctrl+Shift+-` | |
