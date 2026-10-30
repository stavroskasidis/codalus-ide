# 0017. Navigation depth and inline information

- **Status:** accepted (M12a plan, 2026-10-10; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

M12 closes the gap between the MVP editor and the classic IDE or Rider for daily C# work. It is split as the roadmap says:
- **M12a:** navigation and inline information.
- **M12b:** editing, refactoring UI and analysis.

This ADR covers M12a. The decisions to make:
- the wire contract for hierarchies, symbols and inline information;
- where CodeLens goes in an editor (AvaloniaEdit) that has no variable-height lines;
- how library code is shown;
- how all of this stays within the typing budget.

## Decisions

**Wire contract: LSP first, `daedalus/` only for what LSP lacks**
- **Standard LSP 3.17 requests, so plugin language servers can provide the same features through the bridge (ADR 0016):**
  - `textDocument/documentSymbol`;
  - call hierarchy (`prepareCallHierarchy`, `incomingCalls`, `outgoingCalls`) and type hierarchy (`prepareTypeHierarchy`, `supertypes`, `subtypes`);
  - `textDocument/inlayHint`;
  - `textDocument/codeLens` and `codeLens/resolve`.
- **Extensions, in our C# host only:**
  - `daedalus/references`: references classified as read, write, declaration or name-only, with the containing member and project. Find All References groups and filters by these.
  - `daedalus/base`: Go to Base.
  - `daedalus/inheritanceMargin`: per-line relations for a range.
  - `daedalus/symbolInfo`: F1 help.
- **The normalizer (ADR 0016) also converts:**
  - flat `SymbolInformation[]` into a tree;
  - inlay hint label parts into strings.
- **Hierarchy items carry the symbol's documentation id in `data`.** Items without source can be listed and expanded again: framework methods, base types from packages.

**Document symbols from syntax**
- The C# host builds document symbols from the syntax tree alone. They are fast and available before a compilation.
- One cached list per editor (`EditorInsights.Symbols`, refreshed 400 ms after edits) feeds the Document Outline, the navigation bar and sticky scroll.

**CodeLens at the end of the line**
- AvaloniaEdit's lines have one height, so CodeLens can't sit above the declaration as in the classic IDE. It shows at the end of the declaration's line, as Rider's "code vision at the end of the line" does: `2 references | 3 implementations`.
- Clicking a count runs Find All References or Go to Implementation.
- Lenses are listed (syntax) per document version. Only those in view are resolved, which means counting with `SymbolFinder`. The previous counts stay until new ones arrive.

**Inlay hints as zero-length elements**
- Hints are zero-length visual line elements: they take space but aren't text.
- **Caret:** a hint would add a second caret position at its offset, so Left and Right skip it.
- **Which hints are shown:**
  - parameter names before literal, `null`, `default` and `new` arguments, unless the method name already says the name (`SetName("x")`);
  - types after `var` and before untyped lambda parameters, unless the right-hand side shows the type.
- **Settings:** parameter names are on by default, types are off.

**Inheritance margin, guides, bracket pairs, sticky scroll, overview ruler**
- **Inheritance margin:** comes from `daedalus/inheritanceMargin`, requested for the viewport.
- **Indentation guides:** follow indentation.
- **Bracket pair colors:** a C-style lexer, with its state cached per line start and recomputed from the first changed line.
- **Sticky scroll:** a TextView layer above the text.
- **Overview ruler:**
  - beside the scroll bar;
  - marks errors, warnings, Git changes, breakpoints, find results and the caret;
  - a health square at the top runs Go to Next Problem (F8).

**Metadata navigation: original source before decompiling**
- **The order for a symbol from a referenced assembly:**
  1. Sources embedded in its portable PDB (embedded in the assembly, or a `.pdb` next to it).
  2. The PDB's Source Link URL, when the user allows it (setting `navigation.sourceLink`, **off by default**: it downloads from the internet). Downloads are cached.
  3. Decompiled C# (ICSharpCode.Decompiler), as before.
- The document title says which: embedded source, Source Link or decompiled.
- Symbol servers (downloading PDBs) aren't in M12a.

**Budget**
- Everything is debounced and cancelled by the next keystroke: hints 250 ms, symbols 400 ms, CodeLens 700 ms.
- Requests cover the viewport plus 40 lines.
- Per-keystroke UI work is limited to segment bookkeeping and the visible lines' bracket colors.

## Consequences

- Plugin language servers get the outline, navigation bar, sticky scroll, hierarchies, inlay hints and CodeLens when they implement the LSP requests.
- Find All References results are richer for C# only. Other servers' results stay plain locations.
- CodeLens placement differs from the classic IDE. A line-above presentation needs variable line heights in AvaloniaEdit (`TODO(issue)`).
- `TODO(issue)`, not in M12a:
  - **Solution Explorer:** members under files; generated sources under analyzers.
  - **Browsers:** Class View and the Object Browser.
  - **Symbols:** symbol servers.
  - **Other:** Go To All's persistent index.
  - **CodeLens:** Git authors (M6 data) and test status (M8 data).

## Amendment (2026-10-29): the References window, after the classic IDE's

Find All References moves out of Find Results into a window of its own, laid out as the classic IDE's (the maintainer's request, with its screenshot):
- **Its own tool window**, `references`, its tab `'Circle' references`. Find Results keeps Find in Files and Replace only; its reference mode (Group by File/Project/None, the kind filter, the Earlier results drop-down) is gone.
- **A tree grouped by the classic IDE's choices:** Project then Definition (the default), Definition only, Definition then Project, Definition then Path, and Project, Definition then Path; List View flattens it. Each header shows its icon and count. Group by is the `references.groupBy` setting.
- **The definition comes from the host.** `codalus/references` items gain `definition` (as Roslyn displays the `ReferencedSymbol`'s definition, so a search that cascades to constructors or overrides shows each as its own group) and `definitionKind`, `containingType` for the column, and `textStart` for the highlight in the trimmed line. Other languages' plain locations group under the symbol's name.
- **The classic IDE's eight columns:** Code, File, Line, Col, Project, Containing member, Containing type, Kind. Code takes what the others leave; the others resize from the header's splitters (`references.columnWidths`). A row's cells are a `ColumnsPanel` that inherits the widths, and the header lines up with the rows whatever inset the theme gives them.
- **Filters as the classic IDE has them:** a filter button on File, Project, Containing member, Containing type and Kind with a check box per value; the scope box (Entire solution, Current project, Current document, Open documents) filters too, without searching again; Clear All Filters resets the columns and the search box.
- **Keep Results opens another window,** as in the classic IDE: an unregistered tool window (`references:2`…) beside the first, which a restart doesn't restore (`LayoutManager.ShowExtraToolWindow`). The next search goes to the first window that doesn't keep its results.

## Amendment (2026-10-30): CodeLens rows from the first frame

The maintainer's screenshots: a C# file opened without CodeLens, then every row the service listed and resolved pushed the code below it down, one lens at a time; the classic IDE shows the rows' room at once, empty, from its in-process syntax tree, and fills them in.
- **Where the rows will be, from the text.** `textDocument/codeLens` is syntax (types, delegates, methods, constructors, properties, indexers and events with accessors, in a type, a namespace or the file), so the shell finds the same identifiers itself: `CSharpLensScanner` (the language service client), a light lexer (comments, every kind of string with its interpolations, `#if` branches taken as a Debug build on current .NET takes them) and a walk of declarations that skips member bodies. It agrees with the host on every lens of the repository's C# files (`CSharpLensScannerTests` compares the two on all of them), at about 150 MB/s.
- **The editor keeps those rows, empty, from its first frame** until the service lists the lenses, which replace them; a listed lens keeps its row while it waits for its count, and one that resolves to nothing keeps it empty. A row without titles holds a space in the titles' font, so it is exactly as tall as the row they fill, and isn't a link until they come. A file over 512 KB is scanned off the UI thread (its rows may come after its first frame).
- **No rows are kept where none will come:** other languages, generated documents, `.csx` scripts, and a C# service that couldn't start or has no CodeLens.
- **A file reloaded from disk** (changed outside the editor) gets its new text's rows at once: its lenses' segments collapsed with the old text.
