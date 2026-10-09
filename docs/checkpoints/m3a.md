# M3a checkpoint: editor and core C# language services

Date: 2026-10-09. CI (Windows, macOS, Linux) green on `c265f1f`; 206 tests pass.

## Built
- **Design-time builds** in the build host (`project/compilerInputs`): the compiler command line per project for its active target framework, so the language service gets analyzers, source generators, `.editorconfig` and options; an Analyzers group under Dependencies. [ADR 0005](../adr/0005-editor-and-language-services.md).
- **C# language host rewritten** on LSP 3.17 + `daedalus/` extensions ([protocol.md](../protocol.md)), on Roslyn's public Workspaces and Features APIs and `Daedalus.Rpc`: semantic tokens, completion (with added `using`s), signature help, Quick Info, definition and implementation (into source-generated files), document highlights, folding, live diagnostics with analyzers and a background compiler pass. Miscellaneous Files for files outside projects and untrusted folders. Restarts with projects and documents replayed.
- **Workbench language client** (`LanguageService`): hosts start on demand; generic LSP client.
- **Editor in the shell**: encodings, BOM and line endings kept; atomic saves; external changes; deleted-file flag; preview tabs; split groups; in-tab split; Ctrl+Tab; multiple carets; find/replace; Go to Line; minimap; folding; navigation history; project-context picker; Error List; status bar items; per-solution session restore; Edit and Window menus.
- Fixture `samples/LanguageFeatures`; editor benchmarks run in the shell; the M0 spike is retired.

## Results (Windows reference machine)
| Measure | Result | Budget |
|---|---|---|
| Cold start ready / in-app (p50) | 526 / 295 ms | ≤ 800 ms |
| Idle memory | 122 MB | ≤ 250 MB |
| 100k-line file ready (p50) | 1.72 s | ≤ 1.8 s |
| Memory with the 100k-line file | 492 MB | ≤ 500 MB |
| Scroll frames over 50 ms (self-driven) | 0 | 0 |
| Solution50 tree / evaluated | 0.47 / 1.25 s | ≤ 1.5 / 5 s |
| Solution50 language service ready | ~13 s | informational |
| Typing in-app p99 (self-driven) | 22.3 ms | ≤ 16 ms — **fails**; the M0 spike measured 19.5 ms on the same machine |

Black-box typing and PageDown (injected input) were not re-measured.

## Deviations
- Nightly CI and benchmark schedules removed (free plan minutes); manual dispatch once per checkpoint.
- Protocol is standard LSP; M0 offset messages retired. StreamJsonRpc no longer used (all hosts on `Daedalus.Rpc`).
- M0 spike and SpikeSolution deleted (results kept under `benchmarks/results/`).
- Adding carets is Ctrl+Alt+Click (Alt+drag is AvaloniaEdit's box selection).
- Background analyzers run for open documents by default (`languageServices.analysisScope`).

## Deferred (`TODO(issue)`)
Metadata decompilation (M3b); `languageServers` plugin contribution (post-MVP); Show All Files and multi-selection in the Solution Explorer; transitive packages; floating windows in the saved layout; opening a bare folder; legacy projects.
