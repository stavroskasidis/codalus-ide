# 0017. Navigation depth and inline information

- **Status:** accepted (M12a plan, 2026-10-10; the maintainer's standing go-ahead for M9–M16)
- **Date:** 2026-10-10
- **Deciders:** maintainer

## Context

M12 closes the gap between the MVP editor and Visual Studio or Rider for daily C# work. It is split as the roadmap says:
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
- AvaloniaEdit's lines have one height, so CodeLens can't sit above the declaration as in Visual Studio. It shows at the end of the declaration's line, as Rider's "code vision at the end of the line" does: `2 references | 3 implementations`.
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
- CodeLens placement differs from Visual Studio. A line-above presentation needs variable line heights in AvaloniaEdit (`TODO(issue)`).
- `TODO(issue)`, not in M12a:
  - **Solution Explorer:** members under files; generated sources under analyzers.
  - **Browsers:** Class View and the Object Browser.
  - **Symbols:** symbol servers.
  - **Other:** Go To All's persistent index.
  - **CodeLens:** Git authors (M6 data) and test status (M8 data).
