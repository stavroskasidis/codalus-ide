# Editing and navigating code

## The editor

- **Tabs and splits**: Window → Split Right (`Ctrl+\` in the VS Code scheme; the default scheme's `Ctrl+\`, `Ctrl+E` is the Error List) or Split Down opens another editor group. Window → Split shows two views of the same file. `Ctrl+Tab` switches documents: hold `Ctrl`, and each `Tab` (`Shift+Tab` back, or the arrow keys) moves through the open documents in the order you last used them; release `Ctrl` to switch (`Esc` stays). A quick `Ctrl+Tab` goes back to the document you used before. Next Document and Previous Document (`Ctrl+Alt+PageDown`, `Ctrl+Alt+PageUp`) go through them in tab order.
- **Multiple carets**: `Ctrl+Alt+Click`, `Ctrl+Alt+Up/Down`, or `Shift+Alt+.` (next occurrence of the selection; it goes on past the end of the file to the top). **Insert Carets at All Matches** puts one on every match in the file, tens of thousands at once if need be. `Shift+Alt`+drag selects a box. Typing with many carets, Format Document, a rename and Replace All each make one change of the document, undone in one step, with bookmarks and breakpoints following each edit.
- **Folding**: regions, members, usings and comments fold from the margin, or with `Ctrl+M Ctrl+M`. A file shows at most 5,000 folding regions, the outermost first; the ones you collapsed are always kept.
- **The minimap** on the right shows the whole file; click or drag to scroll (setting **Text Editor → Minimap**).
- **Find and replace**: `Ctrl+F` and `Ctrl+H` open the find box at the editor's top right, with case (**Aa**), whole word (**ab**) and regular expressions (**.\***). It searches in the background as you type, so typing never waits: the count reads **3 of 120**, or **20,000+** in a very large file, where the first 20,000 matches are highlighted and `F3` still goes past them. `Enter` and `F3` go to the next match, `Shift+Enter` and `Shift+F3` to the previous, `Alt+R` replaces (the first press selects the next match, the second replaces it), `Alt+A` replaces all (every match, as one undo step), and `Esc` closes it. An invalid regular expression, or one that runs too long, says so.
- **Files**: encodings, BOMs and line endings are kept as they were. Indentation follows `.editorconfig`. A file changed outside the editor is reloaded (or, with unsaved edits, you are asked), as an edit of only what changed: a log being written gets its new lines, and the view stays where it was. Typing while a change on disk is being read asks too, instead of losing what you typed. Open documents are restored when the solution opens again.
- **Very long lines** (minified files, generated data): a line is drawn up to 10,000 characters, then a muted marker, **… 52,000 more characters**. The text is all there: selecting, copying, Find and saving keep every character, and colors stop at the marker. The arrow keys step over the marker to the line's end and back. A caret placed in the hidden part (by Find, Go To Line, an error) shows the line drawn up to it when it's within 20,000 characters; farther in, it moves to the line's end, or back to the 20,000th character, the way it was going. Peek Definition and the other read-only views cut long lines the same way.
- **Large files**: a file over 20 MB or 300,000 lines opens without colors, folding, CodeLens or its language service (so no squiggles, completion or Quick Info from it), with a bar at the top saying so, and the editor stays responsive. That's decided when the file opens.
- **Bracket colors** far from where you've been (right after a jump to the end of a large file, or an unclosed bracket typed near the top) can show the default color for a moment, until a pass in the background colors them.

## C# language services

Codalus IDE uses Roslyn, with your projects' analyzers and source generators:

- **Highlighting**, by meaning: types, members, locals, parameters, and more.
- **Completion** (`Ctrl+Space`, and while you type). It can add the `using` an item needs. See [IntelliSense](#intellisense).
- **Parameter info** (`Ctrl+Shift+Space`) and **Quick Info** on hover (`Ctrl+K Ctrl+I`).
- **Live diagnostics** as you type: squiggles under errors and warnings, three dots under suggestions (information, such as `.editorconfig` code-style rules at `suggestion`), and the **Error List** (View → Error List). The Error List filters by severity, by source (Build, IntelliSense or both) and by document; double-click an entry to go to it.
  - **Group By** groups the entries by project, file, severity, code or source. Click a group's header (or press Enter on it) to collapse or expand it. The choice is the `errorList.groupBy` setting.
  - **Analysis Scope** sets where analyzers (warnings such as `CA2200`) run in the background: **open documents** (the default), the **current project**, or the whole **solution**. The current project is the active document's project, or the startup project when no document is open. Compiler errors are always reported for the whole solution. The choice is the `languageServices.analysisScope` setting.
- **Quick actions** (`Ctrl+.` or the lightbulb): code fixes and refactorings, each previewed before it applies. Those that rename or delete files (Rename File to Match Type, Move Type to File) do it through the project, so its items follow; an open file is saved and reopened under its new name.
- **Rename** (`F2`): previewed across the solution.
- **Format Document** (`Ctrl+K Ctrl+D`) and **Format Selection**, following `.editorconfig`, and formatting as you type (`;`, `}`).

## IntelliSense

The completion list, parameter info and Quick Info show code in the editor's own colors: types, keywords, methods and parameters look as they do in your code, in every theme. Generic types show the types you use (`LayoutBuilderBase<PageModel, PageBuilder<PageModel>>.AddTab(…)`, not `LayoutBuilderBase<TModel, TBuilder>`).

- **The completion list** opens below the line you're typing on (above it at the bottom of the screen):
  - Each item has an icon for its kind: class, structure, interface, enum, delegate, method, extension method, property, field, event, constant, namespace, keyword, snippet, local or parameter.
  - The characters you typed are bold, and the best match is selected. `↑` `↓`, `Page Up` `Page Down`, `Home` and `End` move; `Enter` or `Tab` inserts; `Esc` closes. In Visual Basic, `Enter` also starts a new line after inserting.
  - **Typing a commit character inserts the selected item first**, then the character: `Con` then `.` gives `Console.`, `WriteLi` then `(` gives `WriteLine(`, and `;`, `,`, `)`, `=` and the other punctuation work the same way. Some items aren't committed by some characters. A commit never waits for the language service: if the item's full details haven't come yet, its listed text goes in, and the details (an override's body, a `using` it needs) follow a moment later in the same undo step, unless you've typed meanwhile.
    - **Space** commits too, as in a classic IDE. To make Space just type a space, turn off **Text Editor → Completion: Commit on Space** (`editor.completion.commitOnSpace`).
    - Nothing is committed while the selection is **soft**: an outline instead of a filled row. It's soft before you've typed anything (after `.`, say), unless the language service picks an item for you (the type after `new`). It's also soft where you're naming something new (a lambda's parameter, a variable), so typing a name never gets replaced by an item. Choosing an item with `↑` `↓` or the mouse makes the selection firm.
  - **Items of the type expected there are preselected**: in `Take(` where `Take` wants an `int`, an `int` local, parameter, property or method is selected before other items. This happens in an argument (for the overload parameter info shows), after `=` in an assignment or initializer, after `return` or `=>`, and beside `==` or `!=`. C# only.
  - To the right of the selected item, its description: its declaration with "(+ 1 overload)" where it has more, and its summary.
  - **Filters** at the bottom show only some kinds. Click one, or press its key: `Alt+C` classes, `Alt+S` structures, `Alt+I` interfaces, `Alt+E` enums, `Alt+D` delegates, `Alt+N` namespaces, `Alt+M` methods, `Alt+X` extension methods, `Alt+P` properties, `Alt+F` fields, `Alt+V` events, `Alt+O` constants, `Alt+L` locals and parameters, `Alt+K` keywords, `Alt+T` snippets, and `Alt+J` **target type matches** (the target icon at the end: only the items of the expected type, whatever their kind). Several can be on at once; with none on, everything shows. With the Emacs keyboard scheme, `Alt+F`, `Alt+V` and `Alt+X` keep their Emacs meaning. You can rebind them in Keyboard Shortcuts ("Completion List: Toggle Classes"…).
- **Parameter info** opens after `(` and `,` (or `Ctrl+Shift+Space`), above the line, so it never covers the completion list:
  - The signature, with the parameter you're typing in bold.
  - Then the method's summary and that parameter's description.
  - With overloads, `˄ 1 of 2 ˅` at its left: click the chevrons, or press `↑` `↓` while the completion list is closed.
- **Quick Info** (hover, or `Ctrl+K Ctrl+I`) is one tooltip:
  - The symbol's icon and declaration, wrapped to fit, never cut off.
  - Its summary, then "Returns:", exceptions and remarks.
  - Under a line, each error or warning there: its code links to its documentation.
  - **Fix** (`Alt+.`) applies the top fix. **Other Fixes** (`Alt+Enter`) opens the quick actions there. **Add to Chat** asks AI Chat about the error, with the code around it; it shows only while AI assistance is on.
  - It doesn't open while the completion list or parameter info is open.
  - It stays open while the pointer is on the symbol or on its way to it, so its buttons are in reach. Moving the pointer elsewhere, scrolling or zooming closes it, and a click in the code acts where you click (`Ctrl+Click` goes to the definition).
- Summaries and descriptions come from your code's documentation comments, and for a referenced assembly (a NuGet package, .NET itself) from the XML documentation file next to it. When a localized one is installed beside it, in a folder named for your OS's language (`de`, `pt-br`, `zh-hans`), that one shows instead, as in the classic IDE.
- The C# Interactive window has the same completion list and parameter info. Other languages' servers get them too, with plain-text signatures.

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
- **XML**: syntax errors, and validation against XSD schemas. A file names its schemas with `xsi:noNamespaceSchemaLocation` or `xsi:schemaLocation` (paths relative to the file). The setting **XML → XML Schemas** (`xml.schemas`) also associates schemas by path: `{ "**/*.config": "schemas/config.xsd" }`, with relative schema paths from the solution's folder. Only local schema files are used: nothing is downloaded.
- **`.editorconfig`**: completion and hover for EditorConfig and .NET options, and warnings for invalid values. **EditorConfig Settings** (on an `.editorconfig` file's context menu in the Solution Explorer, or in the command palette) shows the file as a page of options: pick a section (`[*]`, `[*.cs]`…), search, and choose each option's value or clear it. Saving changes only those lines, so comments, order and keys the page doesn't know stay as they were; with no `.editorconfig` yet, saving creates one next to the solution. **View Code** opens the file as text.
- **Markdown**: Open Preview to the Side (`Ctrl+K V`) shows a live preview that follows your edits.

Plugins can add languages, grammars and language servers ([plugins.md](../plugins.md#languages-grammars-and-language-servers)).

## Navigation

| Command | Shortcut | Notes |
|---|---|---|
| Go to Definition | `F12`, `Ctrl+Click` (holding `Ctrl` (`⌘` on macOS) over a symbol underlines it as a link) | Into source-generated files and library code (read-only): its original source when the library's PDB embeds it, or through Source Link (setting **Navigation → Source Link**, off by default because it downloads); otherwise decompiled. The tab says which. |
| Peek Definition | `Alt+F12` | The definition in a popup under the line. `Enter` or **Open** opens it; `Esc` closes. |
| Go to Implementation, Go to Derived Types | `Ctrl+F12` | |
| Go to Base | `Alt+Home` | Base types and interfaces, or the members a member overrides or implements. |
| Find All References | `Shift+F12` | In the References window: see [Find All References](#find-all-references) below. |
| Call Hierarchy | `Shift+Alt+H` | Calls to and calls from a method or property, expanding recursively, in the Hierarchy window. |
| Type Hierarchy | (Edit menu, palette) | Base and derived types, in the Hierarchy window. |
| Go To All (Code Search) | `Ctrl+T`, `Ctrl+,` | Files, types, members and text, with a live preview ([Code Search](#code-search), below). Go To File, Type, Member and Text (`Ctrl+1` then `F`, `T`, `M` or `X`) open it on that filter. |
| Find in Files, Replace in Files | `Ctrl+Shift+F`, `Ctrl+Shift+H` | The Find and Replace window, below. |
| Navigate Back and Forward | `Ctrl+-`, `Ctrl+Shift+-`, the mouse's back and forward buttons | The places you were, as in the classic IDE: where a jump (Go to Definition, a search result, a bookmark) started, the document you left for another, and where the caret was before a move of more than ten lines. The toolbar's arrows do the same. |
| Go to Last Edit Location | `Ctrl+Shift+Backspace` | |
| Next and Previous Problem | `F8`, `Shift+F8` | Errors and warnings in the document, with the message in the status bar. |
| Help on Symbol | `F1`, `Shift+F1` | The API reference page of a framework symbol, or the package's site. |

**Tool windows** (View menu, or View → Other Windows)
- **Document Outline**: the document's types and members. It follows the caret; click a symbol to go there, or type to filter. The chevron, `Right` and `Left` expand and collapse, and what you collapsed stays collapsed as you edit. It lists a file of thousands of members without holding up typing, and does nothing while hidden.
- **Code Definition**: the definition of the symbol at the caret, read-only, updated as the caret moves.
- **Hierarchy**: Call Hierarchy and Type Hierarchy results.
- **Class View** (View → Other Windows): each C# and Visual Basic project's namespaces, types and members, with their signatures. Select one to see its declaration and summary at the bottom; double-click it, or press Enter, to go to it. The search box lists the types and members whose names contain what you type, the first 1,000 of them: type more to narrow it down.
- **Object Browser** (View → Other Windows → Object Browser): the assemblies a project compiles against, its own first, down to their namespaces, types and members. Pick the project at the top. Library code opens read-only, from its PDB's source or decompiled, as Go to Definition does. Search looks through them all.

## Code Search

`Ctrl+T` or `Ctrl+,` (Edit → Go To → Go To All) opens **Code Search**, a window over the editor under the title row. It works like the classic IDE's:

- **What it finds**: the solution's files by name (or by path when you type a `/`), its types and members, the symbols of every other language service that offers them (F#, XAML names through the C# service, the LSP languages), and lines of text (Find in Files' engine, for three characters or more; the setting **Code Search → Text Results** sets how many, `0` turns them off). Typing `PSF` finds `ProcessShippingFee`: the capitals of camel-case names, word starts, then any characters in order. The matched characters are bold.
- **Filters**: the chips under the box, **all**, **files (f:)**, **types (t:)**, **members (m:)** and **text (x:)**, or the same prefixes typed first (`t:Order`). `Alt` and the chip's letter picks one. `:42` (or `:42:7`) goes to a line of the current document.
- **Results** are two lines: the icon and the name, then where it is ("in `Circle` (project `Lib`)", or a file's folder), with the file name on the right. The best matches come first: exact names, then types, files and members, then text, with the files you opened recently and the current project's ahead. **Code Search → Result Grouping** lists them grouped by kind instead.
- **The preview** below the results is the document itself, in a full editor with its colors and language features, scrolled to the result with the match selected. Edit it there: `Ctrl+S` saves, undo works, and an edit you didn't save opens the document when you move on, so nothing is lost. The status line shows the line, column and selection. The page button above hides the preview.
- `Enter` (or a double-click) opens the result and closes the window; `Ctrl+Enter` opens it and keeps the window. `Esc` closes it. The pin keeps it open after every result.
- **Scope**: the entire solution, the current project or the current document. **External items** adds the types of the referenced assemblies (NuGet packages and the framework), which open from their embedded or Source Link source, or decompiled.
- **Incomplete results**: while projects load, or before the C# language service has indexed the solution, a notice says so and the results refresh by themselves when it's done. The index is kept with the solution (`.codalus/state/<solution>/code-search-index.json`), so after a restart Code Search finds types and members at once, before the language service has loaded anything. The documents you edit are indexed again as you type; the whole solution, when its projects change and every few minutes, which brings in files changed outside the editor.
- **Feature Search**: the second tab, also `Ctrl+Q` or the search box in the title row: commands (with their shortcuts), settings and Options pages. `Enter` runs the command or opens the setting or page.
- The window remembers its size, position, pin and preview. Drag it by its tabs and resize it from its corner. The window button docks it as the **Code Search** tool window (and floats it again). Everything works from the keyboard: `Tab` goes from the box to the chips, the scope, the results and the preview.

## Find and Replace in files

`Ctrl+Shift+F` (Edit → Find and Replace → Find in Files) opens the **Find and Replace** window over the documents' top right, with the editor's selection to find. It isn't modal: keep editing while it's open, drag it by its title, and close it with `Esc`. `Ctrl+Shift+H` opens it on **Replace in Files**.

- **Find what** and **Replace with** remember earlier searches in their drop-downs. With **Use regular expressions**, the replacement can use the match's groups (`$1`, `${name}`).
- **Look in**: the entire solution, the current project, the current document, all open documents, or a folder (the folder button). Without a solution, the entire solution is the open documents.
- **File types**: patterns separated by `;`. One without a path separator matches the file name (`*.cs;*.razor`); one with it matches the path below the solution (`*\bin\*`), and `!` excludes. The default, `!*\bin\*;!*\obj\*;!*\.*\*`, leaves out build output and dot folders such as `.git`.
- **Find All** (`Enter`) lists the matches in the **Find Results** window and closes Find and Replace; **Append results** adds them under the earlier ones. **Find Next**, **Find Previous** and **Skip File** go through the matches one by one, opening each with the match selected, and leave the window open.
- **Replace** replaces the current match and goes to the next. **Replace All** asks first, then replaces every match: open documents change in the editor, where you can undo and save, and the other files change on disk. Find Results then lists the changed lines.

**Find Results** shows a line per match, grouped by file, with the file, line and column beside it. The line is drawn in the editor's colors for its language, in the current theme, with each match highlighted. A line above the results says what was searched (`Find all "Person", Match case, Whole word, Entire solution, "*.cs"`), and the status line below counts the matching lines and files and the files searched. Double-click or `Enter` opens a match; `F8` and `Shift+F8` (or the arrows on the toolbar) open the next and the previous; **Modify Find** opens Find and Replace again with the same search.

- **Search Find Results**, the box at the right of the toolbar, shows only the matches whose code or file name contains its text, under their files' headers; the headers' counts and the summary below count what's shown.
- **Tree** or **List**: Tree groups the matches by **Group by**: **File** (a header per file, the default), **Path and file** (a header per folder, below the folder searched or the solution's, with its files under it), or **None**. Each header shows its folder or file icon and how many matches it holds. Headers collapse and expand: click the chevron, double-click the header, or use `Left` and `Right`. Next and Previous skip what's collapsed. **List** shows the matches in one flat list with each file's full path in the File column.
- **Keep Results** (the pin) keeps the current results: the next Find All opens a new page, **Find Results 2**, and so on, shown as tabs at the top of the window, each with its count. A kept page stays until you close its tab; Modify Find on it brings back its own search. Closing the last page clears it.
- **Clear All** empties the page, and **Stop Searching** stops a search that is still running.

## Find All References

**Find All References** (`Shift+F12`, or a CodeLens count) lists the references to the symbol at the caret in the **References** window, whose tab names the symbol: `'Circle' references`. It is laid out as the classic IDE's:

- **A tree.** By default the references are grouped by project, then by definition: the symbol itself, and the ones the search reached from it, such as a type's constructors or a method's overrides, each with its icon. **Group by** also offers *Definition only*, *Definition then Project*, *Definition then Path* (the file) and *Project, Definition then Path*; your choice is remembered. Each header shows how many references it holds. Headers collapse and expand: click the chevron, double-click the header, or use `Left` and `Right`; **Expand All** and **Collapse All** do every level at once. **List View** shows the references in one flat list.
- **Columns.** Code (the line in the editor's colors, the symbol highlighted), File, Line, Col, Project, Containing member, Containing type and Kind (Read, Write, Declaration or Reference). Drag a header's right edge to resize its column; Code takes the rest. The widths are remembered.
- **Filters.** The box at the left of the toolbar shows the references in the **Entire solution**, the **Current project**, the **Current document** or the **Open documents**; the search itself always covers the solution, so changing it doesn't search again. The funnel in the File, Project, Containing member, Containing type and Kind headers lists that column's values, each with how many references have it: uncheck one to hide its references (the funnel turns the accent color). **Search Find All References** shows the references whose code, file, project, member, type or definition contains its text. **Clear All Filters** checks every value again and empties the search.
- **Opening and copying.** Double-click or `Enter` opens a reference with its name selected; `F8` and `Shift+F8` open the next and the previous. **Copy** (or `Ctrl+C`) copies the selected reference's columns, tab-separated, or a header's whole group.
- **Keep Results** keeps the window as it is: the next Find All References opens another References window beside it. Close a kept window when you're done with it; the extra windows aren't reopened after a restart.

Read, write and declaration, the containing member and type, and the definitions come from the C# language service. For other languages the window lists plain locations under the symbol's name.

## Inline information

Settings under **Text Editor** turn each of these on or off.

- **Inlay hints**: parameter names before literal and `new` arguments (`radius: 2`), and optionally the inferred types of `var` declarations and lambda parameters (**Inlay Hints: Types**, off by default). The caret moves over a hint in one step.
- **CodeLens**: reference and implementation counts on a small row above each declaration, lined up with its code. Click a count to find the references or go to the implementations.
  - In a C# file the rows are there from the moment the file opens, empty until their counts arrive, as in the classic IDE: the code doesn't move down when they do, also when the file opens at startup, while the language service is still loading the solution. A file reloaded after a change outside the editor keeps its rows the same way.
  - In a Git repository, each member's history from blame follows: who changed it last and when, and how many authors and changes it has (`Ada, 3 days ago | 2 authors, 5 changes`). Saved files are blamed again on save, and edited ones every few seconds with their unsaved text.
  - Tests show their last outcome (`✓ Passed (12 ms)`, `✗ Failed`, `Not run`), and test classes how many of their tests pass (`✓ 4/4 passing`).
  - Turn these off with Text Editor → CodeLens: Git History (`editor.gitCodeLens`) and CodeLens: Test Status (`editor.testCodeLens`).
- **Inheritance margin**: arrows in the margin of members and types that implement, override or are overridden. Click an arrow to see and open the related symbols.
- **Navigation bar**: above the editor, the document's types and the members of the type at the caret. In a C# file that declares a type it is there from the moment the file opens, empty until the language service lists the types, so the code doesn't move down when they come.
- **Sticky scroll**: the lines of the enclosing namespace, type and member stay at the top while you scroll, drawn as in the editor (colors, indentation, line numbers). Click one to go there.
- **Indentation guides** and **bracket pair colors**: matching brackets share a color by nesting depth. Bracket pair colors are off by default in the Classic themes, as in the classic IDE, and on in the other families; **Text Editor → Bracket Pair Colorization** (`editor.bracketPairColorization`) sets them for every theme.
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
| Paste with automatic usings | `Ctrl+V` | C# and Visual Basic code you paste gets the `using` (`Imports`) directives it needs. A name is imported when it has one possible namespace, or when it's one of the namespaces of the file you copied it from; other names are left for the lightbulb. The status bar shows what was added, and Undo removes it. Turn it off with Text Editor → Add Missing Usings on Paste (`editor.pasteAddsImports`). |
| Paste JSON as Classes | (Edit menu) | C# classes for the JSON on the clipboard. |
| Paste XML as Classes | (Edit menu) | C# classes for the XML on the clipboard, with `XmlSerializer`'s attributes: attributes and child elements become properties (a list when an element repeats), and values get the narrowest type that fits them all. |
| Zoom In, Out and Reset | `Ctrl+Shift+.`, `Ctrl+Shift+,`, `Ctrl+Wheel` | Per editor. |
| XML documentation comment | type `///` | Above a member: the comment with its `param` and `returns` tags. |
| Code Cleanup | `Ctrl+K Ctrl+E` | Removes unnecessary usings, applies the code style fixes, sorts usings and formats. Settings: **Code Cleanup → Fixes** (diagnostic ids) and **Code Cleanup → On Save**. |

**Format on save** (setting **Text Editor → Format On Save**) formats each document before saving it.

## Snippets

- Type a snippet's prefix (`prop`, `ctor`, `for`, `try`…) and pick it in completion, or use **Insert Snippet** (`Ctrl+K Ctrl+X`). `Tab` moves between the placeholders in the order of their numbers (`$1`, `$2`…, then `$0`, where the snippet ends), whatever their place in the text; `Shift+Tab` goes back, and linked placeholders change together. A placeholder with choices (`${1|public,private|}`) lists them when you reach it: pick one, or type.
- **Surround With** (`Ctrl+K Ctrl+S`, or `Ctrl+K Ctrl+Shift+S` in either keyboard scheme) wraps the selection (`if`, `try`, `#region`, `using`…).
- **Configure User Snippets** opens your snippets for the current language, in VS Code's JSON format. Yours replace built-in snippets with the same name.
- **Tools → Code Snippets Manager…** (`Ctrl+K Ctrl+B`) lists every language's snippets, built in, from plugins and yours, with their prefixes and bodies. **Copy to Your Snippets** makes a snippet yours to change, **Remove** deletes one of yours, and **Open Your Snippets** and **Import .snippet Files…** are there too.
- **Import Snippets (.snippet)…** converts the classic IDE's `.snippet` files into your snippets.
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

- **Bookmarks**: `Ctrl+K Ctrl+K` toggles one on the caret's line; `Ctrl+K Ctrl+N` and `Ctrl+K Ctrl+P` go to the next and previous; `Ctrl+K Ctrl+L` clears them. The Bookmarks window (View menu) lists them with their folder; `F2` names one and `Delete` removes it. Bookmarks belong to the open solution and come back with it (in `.codalus/state/`, see [What a solution reopens with](solutions.md#what-a-solution-reopens-with)); bookmarks in files opened without a solution are kept in your user data.
- **Task List** (View menu): the `TODO`, `HACK` and `UNDONE` comments in the solution's files, unsaved edits included. **Refresh** scans again; type to filter. The tokens are a setting (**Task List → Tokens**).

## Analysis

- **Code Metrics** (Analyze → Calculate Code Metrics for Solution or for Project): the maintainability index, cyclomatic complexity, depth of inheritance, class coupling and lines of code of each project, namespace, type and member. A maintainability index under 10 is shown in red. Double-click a row to go to it; **Copy** copies the results as tab-separated text.
- **Syntax Visualizer** (View or Analyze menu), for writing analyzers and source generators: the C# syntax tree of the active document, with its tokens (green) and trivia, following the caret. Selecting a node selects its text. Below the tree: the node's kind and span, the symbol, type, converted type, conversion and constant value at the caret, and its `IOperation` tree.

## Files and tabs

- **Compare Files…** and **Compare Active File With…** (File menu) open two files in the diff viewer.
- **Close All to the Right** and **Close All But This** (Window menu, and the tab's context menu).
- **Images** (PNG, JPEG, GIF, BMP, ICO, WebP) open in an image viewer: zoom with `Ctrl+Wheel`, `Ctrl+=` and `Ctrl+-`, `Ctrl+0` for the actual size, or **Fit**. SVG files open as XML.
- **Binary files** (a `.dll`, a database, any file with NUL bytes and no byte order mark) open in the **hex viewer**: each row shows the offset, 16 bytes in hex and their printable characters. Click a byte, or move with the arrows, `Page Up`/`Page Down`, `Home` and `End` (`Ctrl+Home`, `Ctrl+End` for the whole file); the bar below shows its offset and value. Large files open at once: only the rows on screen are read. **Open With Hex Viewer** (a file's context menu in Solution Explorer, or the command palette for the active file) shows any file this way, and **Reopen With Text Editor** goes back. The hex viewer doesn't edit.
