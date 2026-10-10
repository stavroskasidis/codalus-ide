# 0018. Editing, refactoring UI and analysis

- **Status:** accepted (M12b plan, 2026-10-10; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

M12b is the second half of M12 (ADR 0017 covers M12a): editing, the refactorings that need a dialog, and analysis. The decisions to make:
- the snippet format, and where snippets come from;
- how a refactoring asks the user for options when Roslyn's "options services" are Visual Studio UI;
- what Code Cleanup is and how it runs;
- how edits are kept safe: AutoRecover and local history;
- how the Syntax Visualizer and Code Metrics get their data.

## Decisions

**Snippets: VS Code's format, Visual Studio's imported**
- Snippets use VS Code's JSON (`{ "name": { "prefix", "body", "description" } }`) and TextMate snippet syntax (`$1`, `${1:default}`, `${1|a,b|}`, `$0`, `$TM_SELECTED_TEXT`). Plugins and users can reuse existing snippet files unchanged.
- **Sources, later ones winning on the same name:** built in (`csharp.json`, embedded in the shell), plugins (`contributes.snippets`: `{ language, path }`, plugin API 0.2.5), and the user's (`<user data>/config/snippets/<language>.json`, opened by Configure User Snippets).
- Visual Studio's `.snippet` XML is imported, not read in place: Import Visual Studio Snippets converts literals and `$end$`/`$selected$` and merges them into the user's files, by language.
- The expansion uses AvaloniaEdit's snippet engine. Placeholders with the same number are linked; Tab moves between them.
- Snippets show in completion (by prefix, even when the language server returns nothing) and in Insert Snippet (Ctrl+K Ctrl+X) and Surround With (Ctrl+K Ctrl+Shift+S, snippets that use the selection). The client announces `snippetSupport`, so server items with `insertTextFormat: 2` expand the same way.

**Refactoring options over the protocol: `daedalus/codeActionOptions`**
- Roslyn's dialog refactorings (Generate Constructor, Equals and GetHashCode, Overrides, Extract Interface, Change Signature) call into Visual Studio-only options services. Our host offers its own versions of these, built on Roslyn's syntax generator, with the same titles ending in `…`.
- **The flow:**
  1. `textDocument/codeAction` lists the action as usual.
  2. Before resolving, the client asks `daedalus/codeActionOptions`. A null result means "no options": resolve directly.
  3. Otherwise the result describes a generic dialog: a title, an optional name to enter (with its label and default), and a checklist of items (`{id, label, checked, detail?}`), optionally reorderable.
  4. The client shows the shell's one dialog (accessible: keyboard, screen reader names) and attaches the answers to the action's `data.choices` (`{name, selected}`, the checked ids in order).
  5. `codeAction/resolve` applies them and returns the `WorkspaceEdit`, previewed as usual.
- One generic schema instead of a dialog per refactoring: plugins' language servers could use the same extension later, and the shell has no refactoring-specific UI.

**Code Cleanup: one request, configured by settings**
- `daedalus/codeCleanup` applies, to one document: Fix All for each configured diagnostic id (`codeCleanup.fixes`; empty means the default profile: IDE0005, IDE0001, IDE0002, IDE0011, IDE0040, IDE0044, IDE0090 and IDE0161, which follow the project's `.editorconfig` preferences), then removing unnecessary usings, sorting usings and formatting.
- Ctrl+K Ctrl+E runs it; `codeCleanup.onSave` runs it before saving.
- Unnecessary usings are removed from the compiler's own diagnostics (CS8019, CS0105) rather than through Roslyn's IDE0005 fixer, which needs the IDE's options.

**Analysis configuration from the light bulb**
- Each diagnostic's actions include "Suppress {id} with #pragma" and "Configure {id} severity: none/silent/suggestion/warning/error", which writes `dotnet_diagnostic.{id}.severity` to the nearest `.editorconfig` (created at the project folder when there is none).

**Safety: AutoRecover and local history in the state folder**
- **AutoRecover:** every 30 s, modified documents are written to `<state>/backups`. A clean exit deletes them. After a crash, the next start offers to restore them (setting `files.autoRecover`).
- **Local history:** every save keeps the previous file in `<state>/history` (at most 50 versions per file, files up to 2 MB). Show Local History lists the versions; choosing one opens a diff and offers to restore it.
- Both are independent of Git and of the project system.

**Syntax Visualizer and Code Metrics in the host**
- `daedalus/syntaxTree` returns the document's syntax tree with tokens and trivia (structured trivia expanded), cut off after 5000 items away from the position, and the semantics of the innermost node at the position: symbol, type, converted type, conversion, constant value and the `IOperation` tree.
- `daedalus/codeMetrics` computes Visual Studio's five metrics for one project or the whole solution: maintainability index, cyclomatic complexity, depth of inheritance, class coupling, and source and executable lines, per member, type, namespace and project. They're computed in the host from Roslyn's operations, following the formulas `roslyn-analyzers` documents. Taking a dependency on its internal implementation was not worth it.

**Editing commands from syntax**
- Expand and Shrink Selection use `textDocument/selectionRange` (LSP), so other language servers can provide it.
- Typing `///` above a member asks `daedalus/docCommentTemplate` for the comment, with the caret placed in the summary.

## Consequences

- Plugins can contribute snippets declaratively (plugin API 0.2.5).
- Refactorings with options work over the protocol with one generic dialog. A refactoring that needs a richer editor than a checklist and a name (Extract Class's member grid, Pull Members Up's destination) needs a schema extension first.
- `TODO(issue)`, not in M12b:
  - **Snippets:** a Code Snippets Manager window; choice placeholders shown as a list; a per-snippet tab order.
  - **Editing:** paste with automatic usings, Paste XML as classes, per-language editor options beyond `.editorconfig`.
  - **Refactorings with options:** Extract Class, Pull Members Up, Move Type and Move Static Members, Encapsulate Field and Introduce Parameter with their dialogs.
  - **Analysis:** an EditorConfig settings UI from Roslyn's option metadata; a suppression file (`GlobalSuppressions.cs`); Error List grouping and the full-solution analysis scope picker; spell checking (D9).
  - **Tabs and windows:** pinned and vertical tabs, tab colors, the Ctrl+Tab switcher window, persisted floating documents.
  - **Bookmarks:** user-defined bookmark folders (the window groups by the file's folder).
  - **Viewers:** a hex viewer, SVG rendering and animated GIFs in the image viewer.
  - **Roslyn component authoring:** analyzer, code fix and generator project templates.
