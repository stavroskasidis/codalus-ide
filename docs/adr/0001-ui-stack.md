# 0001. UI stack for the Daedalus Studio shell

- **Status:** proposed (awaiting maintainer decision at the M0 checkpoint)
- **Date:** 2026-10-08
- **Deciders:** maintainer

## Context

The shell has to be light and responsive, run natively on Windows, macOS and Linux, host a VS-class editor, and expose rich Roslyn-driven features (refactoring previews, designers, debugger visualizers). Heavy services run out of process over JSON-RPC (`ide-prompt.md`, architecture principles 1–6). The mission required the stack to be chosen **by measurement**, comparing two prototypes:

- **A: Avalonia 12** with AvaloniaEdit and Dock, shell published with **Native AOT** (`spikes/avalonia`).
- **B: a Rust shell on Zed's GPUI** (through GPUI Kit's dock, tree and editor components) (`spikes/gpui`).

Both prototypes have the same docked layout (Solution Explorer, editor tabs, Output, status bar). Both show a C# file with **Roslyn semantic highlighting**, and both talk to **the same out-of-process .NET language service host** (`daedalus-lshost`, Roslyn and MSBuildWorkspace over LSP-framed JSON-RPC). The comparison therefore isolates the UI stack. Both supervise the host and survive host crashes.

## What was built (and what it took)

| | A: Avalonia | B: GPUI |
|---|---|---|
| Shell code | ~1,000 lines of C#: 8 files plus 3 XAML | ~1,350 lines of Rust across 6 files |
| Host client | Shared AOT-safe C# client (`Daedalus.LanguageServices.Client`), also used by tests | Separate Rust JSON-RPC client. Protocol types are mirrored by hand. |
| Semantic highlighting | `DocumentColorizingTransformer` fed with *range* requests for the viewport | GPUI Kit's `DocumentRangeSemanticTokensProvider`. The editor only requests **whole-document** tokens after edits and never on scroll, so the spike vendors `gpui-base` with a one-method patch (see `spikes/gpui/vendor/PATCHES.md`). Semantic tokens only render when a tree-sitter grammar is loaded, so B also runs tree-sitter C#. |
| Incremental edits to the host | Exact edit offsets from AvaloniaEdit's change events | The editor's change event carries no delta. A worker thread diffs snapshots (prefix and suffix) into UTF-16 edits. |
| Native packaging | Native AOT (`PublishAot`). Builds clean in our code. **13 trim/AOT warnings in third-party code** (Dock's default data templates and reflection bindings in library XAML). They are non-fatal, and the published app works. | A single native executable. A release build takes about 4 minutes cold and 1 minute incremental. |
| Integration problems hit | Dock's `AppendTool` silently does nothing before `InitLayout` (populated directly instead). AOT linker discovery needs `vswhere` on PATH. | The toolset resolver picked an incomplete MSVC install. The editor drops semantic styles without a language. A console window appears unless the subsystem flag is set. API snapshots are pinned exactly (`=0.3.8`). |
| Tests | Host integration tests run through the same client. A headless UI test (Avalonia.Headless) loads the solution and asserts that tokens are rendered. | Rust unit tests (edit diffing). GPUI has a `TestAppContext`, but no UI test was written in the spike. |

## Measurements

The method and raw data are in `benchmarks/README.md` and `benchmarks/results/`. Reference hardware: see ADR 0002. **The macOS and Linux columns are pending:** the repository is local only, and they come from `.github/workflows/benchmarks.yml` once it is pushed (GPU-less runners, so indicative only).

<!-- MEASUREMENTS:BEGIN -->
_Filled in from the benchmark run._
<!-- MEASUREMENTS:END -->

## Qualitative assessment

### Maturity and documentation

- **Avalonia 12** has been stable since April 2026. Its lineage goes back to 2013, and the 11.x line is widely deployed in commercial cross-platform desktop applications. There is a full documentation site, and a company (AvaloniaUI OÜ) backs the MIT-licensed core. **AvaloniaEdit** is a port of AvalonEdit, with more than 15 years of lineage, and lives in the AvaloniaUI organization. **Dock** is mature and feature-rich (docking, floating, persistence) but essentially has a single maintainer.
- **GPUI** is pre-1.0 and published as weekly `gpui-pre` snapshots of Zed's crates. GPUI Kit pins an exact snapshot because unpinned upgrades broke builds. The documentation is mostly rustdoc and examples. The most mature editor built on GPUI is **Zed's own editor, which is GPL-3.0 and unusable** for an MIT product. GPUI Kit's editor is younger. It is capable (tree-sitter, LSP hooks, 200k-line claims), but its LSP integration needed patching for range-based semantic tokens.

### Exposing rich Roslyn features across the boundary

Both stacks reach Roslyn through the same out-of-process host, so plain LSP-style features cost the same. The difference is in the *rich* features:

- **Refactoring previews and structured diagnostics.** With A, the shell and host share C# contract types (one `Protocol` assembly, source-generated JSON). With B, every contract must be mirrored in Rust, which in production means a schema plus code generation for two languages.
- **Designers and live previewers** (post-MVP: Avalonia/MAUI XAML editing). Avalonia's previewer *is* .NET and Avalonia. A can host design surfaces in-process or remote them easily. B would have to composite a .NET-rendered surface into a GPUI window across processes (shared textures, input forwarding), which is a hard problem on three OSes.
- **Debugger visualizers.** These are naturally .NET code. A can load them in a sandboxed .NET plugin host and render them with the same UI toolkit. B needs a declarative UI description or a bridged renderer.
- **Out-of-process plugin UI** is needed for both: contributions have to be declarative or remote for isolation. A gets a single-language plugin SDK (C#), while B forces plugin authors to target a UI model defined in Rust.

### Contributor accessibility

The target contributors are .NET developers. With A, the entire codebase is C#: one toolchain, one debugger, and `dotnet build` / `dotnet test` everywhere. With B, contributors have to work in both C# and Rust. B brings two package ecosystems (NuGet plus 850 crates), MSVC/Xcode/Linux system dependencies for GPUI, slower builds, and two CI toolchains. That cuts the contributor pool for an IDE whose users are .NET developers.

### Long-term maintenance risk

- **A:** low to moderate. The main risks are Dock's bus factor (mitigated: MIT, small enough to fork or replace with our own docking built on Avalonia primitives) and third-party AOT warnings, which need upstream fixes or our own templates.
- **B:** high. GPUI is pre-1.0 with weekly breaking snapshots, and its upstream priorities are set by Zed, so there are no stability guarantees for third parties. Windows support only became stable in late 2025. The editor component needed a vendored patch. Licensing is also unresolved: three MPL-2.0 crates and one non-OSI crate (`bzip2-1.0.6`) arrive transitively through GPUI (THIRD-PARTY-NOTICES.md, "Pending decision").

### Accessibility (screen readers)

Checked objectively by dumping each prototype's Windows UI Automation tree (what Narrator or NVDA see):

- **A** exposes a rich structure (panes, tabs, tree, list, scrollbars). However, **AvaloniaEdit exposes no text** to UIA (no Text or Value pattern). Tree items were unnamed in the spike (fixable with `AutomationProperties.Name`), and some Dock buttons have type names instead of labels.
- **B** exposes a flatter tree with properly named tree items. The editor is an `Edit` control with the **Value pattern**, so its text is readable, but there are no pane or tab semantics, and buttons are unnamed. GPUI integrates AccessKit (UIA, NSAccessibility, AT-SPI).
- **Neither is screen-reader complete.** For A, M3 must add a UIA `TextPattern` automation peer to the editor, which is real but bounded work on a toolkit with a mature automation-peer model. For B, structure would have to be added component by component.

## Decision matrix

The weights are the ones proposed in the M0 plan, and the maintainer may adjust them. Scores run from 1 (poor) to 5 (excellent).

<!-- MATRIX:BEGIN -->
_Filled in from the benchmark run._
<!-- MATRIX:END -->

## Recommendation

<!-- RECOMMENDATION:BEGIN -->
_Filled in from the benchmark run._
<!-- RECOMMENDATION:END -->

## Consequences

<!-- CONSEQUENCES:BEGIN -->
_Filled in from the benchmark run._
<!-- CONSEQUENCES:END -->
