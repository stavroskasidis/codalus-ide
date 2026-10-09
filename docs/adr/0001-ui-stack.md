# 0001. UI stack for the Daedalus Studio shell

- **Status:** accepted (2026-10-08). Prototype B was removed from the repository; its measurements stay in `benchmarks/results/windows/gpui/`.
- **Date:** 2026-10-08
- **Deciders:** maintainer

## Context

The shell has to be light and responsive, run natively on Windows, macOS and Linux, host a VS-class editor, and expose rich Roslyn-driven features (refactoring previews, designers, debugger visualizers). Heavy services run out of process over JSON-RPC (the project brief, architecture principles 1–6). The mission required the stack to be chosen **by measurement**, comparing two prototypes:

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
| Integration problems hit | Dock's `AppendTool` silently does nothing before `InitLayout` (populated directly instead). The editor did not take keyboard focus when a document opened (fixed). AOT linker discovery needs `vswhere` on PATH. | The toolset resolver picked an incomplete MSVC install. The editor drops semantic styles without a language. A console window appears unless the subsystem flag is set. API snapshots are pinned exactly (`=0.3.8`). |
| Tests | Host integration tests run through the same client. A headless UI test (Avalonia.Headless) loads the solution and asserts that tokens are rendered. | Rust unit tests (edit diffing). GPUI has a `TestAppContext`, but no UI test was written in the spike. |

## Measurements

The method and raw data are in `benchmarks/README.md` and `benchmarks/results/`. Reference hardware: see ADR 0002. **The macOS and Linux columns are pending:** the repository is local only, and they come from `.github/workflows/benchmarks.yml` once it is pushed (GPU-less runners, so indicative only).

Windows, reference hardware (i5-13600KF, RTX 4060, 165 Hz display), 2026-10-08. The values are p50 / p99, and black-box values use the same procedure for both prototypes and for the reference IDEs. Both prototypes include their out-of-process language host (.NET 10, ReadyToRun) in every memory figure.

| Metric | A: Avalonia (AOT) | B: GPUI | Notes |
|---|---|---|---|
| Cold start → ready, empty window (black-box) | **346** / 404 ms | 373 / 400 ms | VS Code 1517 ms, VS 921 ms on the same probe |
| Cold start → `interactive` (in-app) | **262** / 266 ms | 288 / 311 ms | |
| Idle memory, empty window (process tree, private) | 194 MB | **185** MB | Shell alone: 96 vs 87 MB. The rest is the language host. |
| 100k-line file → ready (black-box) | **1396** ms | 1570 ms | Semantic highlight at 1304 vs 1495 ms (host-bound) |
| Memory with the 100k file open | 390 MB | **382** MB | Shell alone: 227 vs 220 MB |
| **Typing, keystroke → displayed frame** (black-box, 600 keys) | 13.6 / 24.9 ms (max 31.7) | **11.3 / 18.8** ms (max 19.4) | VS Code 13.2 / 20.0, VS 17.0 / 201 |
| Typing, input → rendered (in-app) | 6.4 / 18.0 ms | **3.2 / 5.6** ms | The clearest difference between the stacks |
| Self-driven typing, mid-file (in-app, 900 keys) | 7.2 / 19.5 ms | **5.5 / 8.7** ms | |
| **Scrolling, PageDown → displayed frame** (black-box, 450 presses) | 11.8 / 22.7 ms | 12.2 / **18.7** ms | VS Code 10.9 / 25.4, VS 25.2 / 53.4 |
| Responses > 50 ms while scrolling | 0 | 0 | VS: 2–3 per run |
| Self-driven scroll at 230 lines/s, frame time | 6.0 / 7.0 ms (max 8.3) | 6.1 / 7.0 ms (max 8.1) | Both locked to 165 Hz; no frame > 50 ms |
| Go-to-line jump → rendered | 4.2 / 13.7 ms | 6.4 / 8.4 ms | |
| Shell size on disk (zipped) | 45.5 MB (18.7 MB) | **27.5 MB (8.8 MB)** | Plus the shared host: 60.9 MB (needs the .NET runtime) |
| 50-project solution → tree shown | 8.5 s | 8.1 s | **Host-bound** (MSBuildWorkspace), not UI-bound. M2 must fix this for both. |

**Reading the numbers.** Both stacks clear the provisional startup, memory and scrolling targets by a wide margin, and both are within a frame or two of VS Code on black-box editor latency. GPUI is measurably better at **typing latency tails**: an in-app p99 of 5.6 ms against 18.0 ms, and 18.8 ms against 24.9 ms black-box. That is the only performance dimension where the stacks differ meaningfully. The AvaloniaEdit/Skia render path for a keystroke (visual-line rebuild, then our colorizer, then composition) has a long tail that M3 has to bring down. GPUI is also smaller on disk. Avalonia starts slightly faster.

**Measurement notes.**
- Avalonia composites through DirectComposition, so per-process present tracing (PresentMon) cannot see its frames. Every latency here therefore comes from DXGI Desktop Duplication: frame timestamps and changed rectangles of the composed desktop.
- GPUI's frame pacing (DwmFlush-based vsync) dropped to about 27 fps in some early runs when its window was occluded or in the background. Every published run had the target in the foreground, and the harness enforces this.
- GPUI's first cold-start memory sample (1.2 GB, 41 processes) was a harness artifact: a recycled PID matched an unrelated process tree. It is excluded. Fixed in M1: the harness skips candidates that started before their parent.

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

| Criterion | Weight | A: Avalonia | B: GPUI | Basis |
|---|---|---|---|---|
| Performance | 35% | **4** | **5** | Both beat the provisional budgets and track VS Code. B has clearly better typing tails (in-app p99 5.6 vs 18.0 ms) and a smaller binary. A starts slightly faster. |
| Exposing rich Roslyn features (previews, designers, visualizers) | 20% | **5** | **2** | A: shared C# contracts, .NET-native designers, previewer and visualizers. B: contracts mirrored in Rust, and .NET-rendered surfaces would have to be composited across processes. |
| Maturity and documentation | 15% | **4** | **2** | Avalonia 12 is stable with full docs, and AvaloniaEdit is mature. GPUI is pre-1.0 with weekly breaking snapshots and thin docs. Its best editor (Zed's) is GPL, and GPUI Kit's editor needed a patch. |
| Contributor accessibility | 15% | **5** | **2** | Single-language C# codebase for .NET developers, versus a C#/Rust split with two toolchains and slower builds. |
| Long-term maintenance risk | 15% | **4** | **2** | A: Dock's single maintainer and third-party AOT warnings (both manageable). B: unstable upstream with Zed-driven priorities, a vendored patch, and unresolved licence items. |
| **Weighted score** | | **4.35** | **3.05** | |

Accessibility is not weighted separately (see above): neither stack is complete, and both have a credible path. If the maintainer weights performance at 60% instead of 35%, with the other criteria scaled down proportionally, A still scores 4.22 against B's 3.80.

## Recommendation

**Adopt A: Avalonia 12 + AvaloniaEdit + Dock, with the shell published as Native AOT.** Delete prototype B after the maintainer approves.

GPUI wins on raw typing latency, and that result is real. But both stacks already meet the user-visible budgets on this hardware. The latency gap is a tail-latency problem in one component (the editor render path) that can be engineered down. The other gaps are structural and would compound over the whole project: the language boundary for every rich Roslyn feature, a split contributor base, a pre-1.0 upstream that breaks weekly, and licensing items. For an IDE whose users and contributors are .NET developers, one C# codebase on a mature cross-platform toolkit is the better long-term bet.

**Conditions attached to the recommendation**, to be tracked as M1/M3 work:
1. **Typing latency.** Bring AvaloniaEdit's keystroke path to in-app p99 ≤ 16 ms and black-box p99 within the ADR 0002 budget. Profile visual-line reconstruction and colorizer cost, and render only invalidated lines. If AvaloniaEdit cannot get there, fork it (MIT). Replacing the editor is the fallback, not the toolkit.
2. **Editor accessibility.** Add a UIA/AX `TextPattern` automation peer to the editor, and name all Solution Explorer and Dock elements.
3. **AOT hygiene.** Remove the third-party trim warnings: provide our own Dock data templates with compiled bindings, or upstream fixes. Keep `PublishAot` warnings-as-errors for our code in CI.
4. **Revisit trigger.** If M3 cannot meet the typing budget with AvaloniaEdit or a fork, or if Avalonia regresses on any OS, reopen this ADR. The harness keeps both measurement paths.

## Consequences

- M1 builds the real shell (`src/Daedalus.Shell`) on Avalonia 12, reusing from the spike the language-host client and supervisor, the AOT-safe protocol, and the bench-event instrumentation. The spike UI code is a reference, not a base.
- `spikes/gpui`, its vendored `gpui-base` and `deny.toml` are deleted, together with the pending MPL-2.0 and `bzip2-1.0.6` licence questions, which only apply to B. The Rust CI job is removed.
- The out-of-process protocol stays toolkit-neutral (LSP-shaped JSON-RPC), so a different UI front end remains possible later without touching the services.
- Risks we accept: Dock's bus factor (we can fork it, or write our own docking on Avalonia primitives) and an editor component we may have to fork.
- The macOS and Linux numbers (CI runners) must confirm the Windows picture after the first push. A large regression on either OS triggers a re-review.
