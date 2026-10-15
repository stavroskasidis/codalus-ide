# Editing and navigating code

## The editor

- **Tabs and splits**: Window → Split Right (`Ctrl+\`) or Split Down opens another editor group. Window → Split shows two views of the same file. `Ctrl+Tab` switches documents: hold `Ctrl`, and each `Tab` (`Shift+Tab` back, or the arrow keys) moves through the open documents in the order you last used them; release `Ctrl` to switch (`Esc` stays). A quick `Ctrl+Tab` goes back to the document you used before. Next Document and Previous Document (`Ctrl+Alt+PageDown`, `Ctrl+Alt+PageUp`) go through them in tab order.
- **Multiple carets**: `Ctrl+Alt+Click`, `Ctrl+Alt+Up/Down`, or `Shift+Alt+.` (next occurrence of the selection). `Shift+Alt`+drag selects a box.
- **Folding**: regions, members, usings and comments fold from the margin, or with `Ctrl+M Ctrl+M`.
- **The minimap** on the right shows the whole file; click or drag to scroll (setting **Text Editor → Minimap**).
- **Find and replace**: `Ctrl+F` and `Ctrl+H`, with case, whole word and regular expressions; `F3` and `Shift+F3` move between matches.
- **Files**: encodings, BOMs and line endings are kept as they were. Indentation follows `.editorconfig`. A file changed outside the editor is reloaded (or, with unsaved edits, you are asked). Open documents are restored when the solution opens again.

## C# language services

Codalus IDE uses Roslyn, with your projects' analyzers and source generators:

- **Highlighting**, by meaning: types, members, locals, parameters, and more.
- **Completion** (`Ctrl+Space`, and while you type). It can add the `using` an item needs.
- **Parameter info** (`Ctrl+Shift+Space`) and **Quick Info** on hover (`Ctrl+K Ctrl+I`).
- **Live diagnostics**: squiggles as you type, and the **Error List** (View → Error List). The Error List filters by severity, by source (Build, IntelliSense or both) and by document; double-click an entry to go to it.
- **Quick actions** (`Ctrl+.` or the lightbulb): code fixes and refactorings, each previewed before it applies. Those that rename or delete files (Rename File to Match Type, Move Type to File) do it through the project, so its items follow; an open file is saved and reopened under its new name.
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
- **JSON**: syntax errors, and completion, hover and validation from a schema. Schemas come from `$schema`, from built-in schemas (`global.json`, `launchSettings.json`, `appsettings*.json`, `dotnet-tools.json`, `runtimeconfig.template.json`, `codalus-plugin.json`), or from the SchemaStore catalog (setting **JSON → Schema Download**, on by default; schemas are cached for a week).
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
| Go To All | `Ctrl+T`, `Ctrl+,` | Types and members; type `f ` first to search file names. |
| Find in Files | `Ctrl+Shift+F` | Case, whole word, regex and a file filter. |
| Navigate Back and Forward | `Ctrl+-`, `Ctrl+Shift+-` | |
| Go to Last Edit Location | `Ctrl+Shift+Backspace` | |
| Next and Previous Problem | `F8`, `Shift+F8` | Errors and warnings in the document, with the message in the status bar. |
| Help on Symbol | `F1`, `Shift+F1` | The API reference page of a framework symbol, or the package's site. |

**Tool windows** (View menu, or View → Other Windows)
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

## Editing commands

| Command | Shortcut | Notes |
|---|---|---|
| Duplicate Selection or Line | `Ctrl+D` | |
| Move Line Up and Down | `Alt+Up`, `Alt+Down` | The selected lines. |
| Expand and Shrink Selection | `Shift+Alt+=`, `Shift+Alt+-` | Word, expression, statement, block… from the language service. |
| Paste from Clipboard Ring | `Ctrl+Shift+V` | The last 15 copies and cuts. |
| Paste JSON as Classes | (Edit menu) | C# classes for the JSON on the clipboard. |
| Paste XML as Classes | (Edit menu) | C# classes for the XML on the clipboard, with `XmlSerializer`'s attributes: attributes and child elements become properties (a list when an element repeats), and values get the narrowest type that fits them all. |
| Zoom In, Out and Reset | `Ctrl+Shift+.`, `Ctrl+Shift+,`, `Ctrl+Wheel` | Per editor. |
| XML documentation comment | type `///` | Above a member: the comment with its `param` and `returns` tags. |
| Code Cleanup | `Ctrl+K Ctrl+E` | Removes unnecessary usings, applies the code style fixes, sorts usings and formats. Settings: **Code Cleanup → Fixes** (diagnostic ids) and **Code Cleanup → On Save**. |

**Format on save** (setting **Text Editor → Format On Save**) formats each document before saving it.

## Snippets

- Type a snippet's prefix (`prop`, `ctor`, `for`, `try`…) and pick it in completion, or use **Insert Snippet** (`Ctrl+K Ctrl+X`). `Tab` moves between the placeholders; linked placeholders change together.
- **Surround With** (`Ctrl+K Ctrl+S`, or `Ctrl+K Ctrl+Shift+S` in either keyboard scheme) wraps the selection (`if`, `try`, `#region`, `using`…).
- **Configure User Snippets** opens your snippets for the current language, in VS Code's JSON format. Yours replace built-in snippets with the same name.
- **Import Snippets (.snippet)…** converts Visual Studio `.snippet` files into your snippets.
- Plugins can contribute snippets ([plugins.md](../plugins.md#snippets)).

## Refactorings that ask first

Quick actions whose title ends with `…` open a dialog before they run:
- **Generate constructor…**, **Generate Equals and GetHashCode…**: pick the fields and properties.
- **Generate overrides…**: pick the members to override.
- **Extract interface…**: name the interface and pick its members. It goes into a new file.
- **Change signature…**: reorder parameters (**Up**, **Down**) and uncheck the ones to remove. Calls are updated.

The light bulb also offers, for each diagnostic, **Suppress with #pragma** and **Configure severity**, which writes the rule's severity to the nearest `.editorconfig`.

## Safety nets

- **AutoRecover**: unsaved changes are backed up every 30 seconds. After a crash, Codalus IDE offers to restore them when it starts again (setting **Files → Auto Recover**).
- **Local History** (File → Show Local History): every save keeps the previous version of the file, independent of Git (the last 50). Pick a version to compare it with the file, then restore it if you want.

## Bookmarks and the Task List

- **Bookmarks**: `Ctrl+K Ctrl+K` toggles one on the caret's line; `Ctrl+K Ctrl+N` and `Ctrl+K Ctrl+P` go to the next and previous; `Ctrl+K Ctrl+L` clears them. The Bookmarks window (View menu) lists them with their folder; `F2` names one and `Delete` removes it.
- **Task List** (View menu): the `TODO`, `HACK` and `UNDONE` comments in the solution's files, unsaved edits included. **Refresh** scans again; type to filter. The tokens are a setting (**Task List → Tokens**).

## Analysis

- **Code Metrics** (Analyze → Calculate Code Metrics for Solution or for Project): the maintainability index, cyclomatic complexity, depth of inheritance, class coupling and lines of code of each project, namespace, type and member. A maintainability index under 10 is shown in red. Double-click a row to go to it; **Copy** copies the results as tab-separated text.
- **Syntax Visualizer** (View or Analyze menu), for writing analyzers and source generators: the C# syntax tree of the active document, with its tokens (green) and trivia, following the caret. Selecting a node selects its text. Below the tree: the node's kind and span, the symbol, type, converted type, conversion and constant value at the caret, and its `IOperation` tree.

## Files and tabs

- **Compare Files…** and **Compare Active File With…** (File menu) open two files in the diff viewer.
- **Close All to the Right** and **Close All But This** (Window menu, and the tab's context menu).
- **Images** (PNG, JPEG, GIF, BMP, ICO, WebP) open in an image viewer: zoom with `Ctrl+Wheel`, `Ctrl+=` and `Ctrl+-`, `Ctrl+0` for the actual size, or **Fit**. SVG files open as XML.
- **Binary files** (a `.dll`, a database, any file with NUL bytes and no byte order mark) open in the **hex viewer**: each row shows the offset, 16 bytes in hex and their printable characters. Click a byte, or move with the arrows, `Page Up`/`Page Down`, `Home` and `End` (`Ctrl+Home`, `Ctrl+End` for the whole file); the bar below shows its offset and value. Large files open at once: only the rows on screen are read. **Open With Hex Viewer** (a file's context menu in Solution Explorer, or the command palette for the active file) shows any file this way, and **Reopen With Text Editor** goes back. The hex viewer doesn't edit.
