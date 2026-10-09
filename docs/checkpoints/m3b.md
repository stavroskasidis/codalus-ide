# M3b checkpoint: references, rename, code actions, formatting, search

Date: 2026-10-09. Continues [M3a](m3a.md); M3 is complete with this checkpoint.

## Built
- **Language host** (LSP, [protocol.md](../protocol.md)): `textDocument/references`; `prepareRename`/`rename` with Roslyn's `Renamer` (workspace edits across files); `codeAction`/`codeAction/resolve` with Roslyn's built-in code fixes and refactorings (found by their MEF export attributes and created through their parameterless constructors) plus the projects' analyzer packages' fixes; formatting (document, range, on type after `}` and `;`) with `.editorconfig`; `workspace/symbol`; Go to Definition into referenced assemblies, decompiled with ICSharpCode.Decompiler (MIT) as `daedalus-metadata:` documents. Roslyn's IDE analyzers (unnecessary usings, code style) run for open documents up to 10,000 lines.
- **Shell**: Find Results tool window (Find All References with Shift+F12; Find in Files with Ctrl+Shift+F: match case, whole word, regex, file filter, streaming, cancellable); Rename (F2 / Ctrl+R Ctrl+R) and the lightbulb (Ctrl+. / Alt+Enter, and a bulb button beside lines with diagnostics) preview changes in a dialog (old/new lines per file) before applying them, to open documents as one undo step and to closed files on disk with their encoding; Format Document (Ctrl+K Ctrl+D, Shift+Alt+F) and Format Selection; format on type (`editor.formatOnType`); Go To All (Ctrl+T, live results; `f ` prefix for files); decompiled and generated documents open read-only.
- Tests: host integration tests for each feature; one headless UI test drives rename, references, Find in Files, the lightbulb, formatting, Go To All and metadata navigation. 212 tests.

## Deviations and gaps
- Fix All (fix every occurrence in the document/project/solution) is not offered. `TODO(issue)`.
- Code actions that create, rename or delete documents: only added documents are supported (created on apply); renames and removals are dropped (`TODO(issue)` in `WorkspaceEdits`).
- Decompiled types from reference assemblies (the SDK's ref packs) have stub bodies; no SourceLink yet. `TODO(issue)`: SourceLink and implementation assemblies.
- Replace in Files is not implemented (M3 asked for Find in Files). `TODO(issue)`.
- Known flaky test: the editor's completion test failed twice under the full-suite load (no completion window, no error); request failures are now logged to the Language Services output to diagnose it.
- Benchmarks were not re-run for M3b: the 100k-line file is a Miscellaneous File over the IDE-analyzer line limit, so the measured paths are unchanged.
