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

## Other languages

Every file type gets the basics, and many get more:

- **Highlighting** for about forty languages from VS Code's TextMate grammars: XML and MSBuild, JSON, YAML, Markdown, HTML, CSS, JavaScript, TypeScript, F#, VB, SQL, PowerShell, shell scripts, Dockerfiles and more. The status bar shows the language.
- **Comments**: Toggle Line Comment (`Ctrl+/`), Comment Selection (`Ctrl+K Ctrl+C`) and Uncomment Selection (`Ctrl+K Ctrl+U`) use each language's comment style.
- **Brackets and quotes** close automatically, and typing the closing character steps over it (setting **Text Editor → Auto Closing Brackets**).
- **Folding** follows the language server, or else XML elements, `#region`-style markers and indentation.
- **Word completion** (`Ctrl+Alt+Space`, and while you type) offers the words of the document when no language service completes.

**Project and configuration files**
- **MSBuild** (`.csproj`, `.props`, `.targets` and friends): completion of elements, properties, items, metadata and values (target frameworks, runtime identifiers, choices), and warnings for unknown values. Hovering a property shows its **evaluated value and where it is set**, when the folder is trusted.
- **JSON**: syntax errors, and completion, hover and validation from a schema. Schemas come from `$schema`, from built-in schemas (`global.json`, `launchSettings.json`, `appsettings*.json`, `dotnet-tools.json`, `runtimeconfig.template.json`, `daedalus-plugin.json`), or from the SchemaStore catalog (setting **JSON → Schema Download**, on by default; schemas are cached for a week).
- **YAML**: syntax errors, and validation and hover from a schema.
- **`.editorconfig`**: completion and hover for EditorConfig and .NET options, and warnings for invalid values.
- **Markdown**: Open Preview to the Side (`Ctrl+K V`) shows a live preview that follows your edits.

Plugins can add languages, grammars and language servers ([plugins.md](../plugins.md#languages-grammars-and-language-servers)).

## Navigation

| Command | Shortcut | Notes |
|---|---|---|
| Go to Definition | `F12`, `Ctrl+Click` | Into source-generated files and, for library types, into decompiled source (read-only). |
| Go to Implementation | `Ctrl+F12` | |
| Find All References | `Shift+F12` | Results in the Find Results window. |
| Go To All | `Ctrl+T` | Types and members; type `f ` first to search file names. |
| Find in Files | `Ctrl+Shift+F` | Case, whole word, regex and a file filter. |
| Navigate Back and Forward | `Ctrl+-`, `Ctrl+Shift+-` | |
