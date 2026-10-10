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
| Go to Definition | `F12`, `Ctrl+Click` | Into source-generated files and library code (read-only): its original source when the library's PDB embeds it, or through Source Link (setting **Navigation → Source Link**, off by default because it downloads); otherwise decompiled. The tab says which. |
| Peek Definition | `Alt+F12` | The definition in a popup under the line. `Enter` or **Open** opens it; `Esc` closes. |
| Go to Implementation, Go to Derived Types | `Ctrl+F12` | |
| Go to Base | `Alt+Home` | Base types and interfaces, or the members a member overrides or implements. |
| Find All References | `Shift+F12` | In the Find Results window. For C#: each reference is marked read, write or declaration with its containing member; group by file, project or none, show only reads, writes or declarations, and go back to earlier results from the drop-down. |
| Call Hierarchy | `Shift+Alt+H` | Calls to and calls from a method or property, expanding recursively, in the Hierarchy window. |
| Type Hierarchy | (Edit menu, palette) | Base and derived types, in the Hierarchy window. |
| Go To All | `Ctrl+T` | Types and members; type `f ` first to search file names. |
| Find in Files | `Ctrl+Shift+F` | Case, whole word, regex and a file filter. |
| Navigate Back and Forward | `Ctrl+-`, `Ctrl+Shift+-` | |
| Go to Last Edit Location | `Ctrl+Shift+Backspace` | |
| Next and Previous Problem | `F8`, `Shift+F8` | Errors and warnings in the document, with the message in the status bar. |
| Help on Symbol | `Shift+F1` | The API reference page of a framework symbol, or the package's site. |

**Tool windows** (View menu)
- **Document Outline**: the document's types and members. It follows the caret; click a symbol to go there, or type to filter.
- **Code Definition**: the definition of the symbol at the caret, read-only, updated as the caret moves.
- **Hierarchy**: Call Hierarchy and Type Hierarchy results.

## Inline information

Settings under **Text Editor** turn each of these on or off.

- **Inlay hints**: parameter names before literal and `new` arguments (`radius: 2`), and optionally the inferred types of `var` declarations and lambda parameters (**Inlay Hints: Types**, off by default). The caret moves over a hint in one step.
- **CodeLens**: reference and implementation counts at the end of each declaration's line. Click a count to find the references or go to the implementations.
- **Inheritance margin**: arrows in the margin of members and types that implement, override or are overridden. Click an arrow to see and open the related symbols.
- **Navigation bar**: above the editor, the document's types and the members of the type at the caret.
- **Sticky scroll**: the lines of the enclosing namespace, type and member stay at the top while you scroll. Click one to go there.
- **Indentation guides** and **bracket pair colors**: matching brackets share a color by nesting depth.
- **Overview ruler**: beside the scroll bar, marks for errors, warnings, Git changes, breakpoints, find results and the caret. Click to scroll there. The square at the top is the document's health (red: errors, yellow: warnings, green: none); click it for the next problem.
- **Inline diagnostics** (off by default): error and warning messages at the end of their lines.

Language services from plugins provide the outline, navigation bar, sticky scroll, hierarchies, inlay hints and CodeLens when they support them.
