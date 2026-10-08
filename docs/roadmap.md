# Post-MVP roadmap

This is the plan for everything after the MVP (M0–M7 in `ide-prompt.md`). It turns the mission's one-line post-MVP list into milestones that future sessions can plan and build one at a time. It also covers the rest of the Visual Studio feature set that the mission's "parity is reachable" goal implies.

- **Status:** draft, written 2026-10-08 while M1 is in progress. Nothing here is approved for building yet.
- **Authority:** `ide-prompt.md` wins over this document. The fixed decisions and non-goals there still apply. ADRs win over this document once they are accepted.
- **Licences:** the licences given below are what we believe them to be when this was written. Check each one again before adopting it, and record it in `THIRD-PARTY-NOTICES.md`. If a licence is ambiguous, stop and ask the maintainer.

## How future sessions use this document

1. Read `ide-prompt.md`, then the latest checkpoint summary, then this document's [status table](#status).
2. Pick the next milestone allowed by the [dependency graph](#dependency-graph) and the maintainer's instructions. The numbers are identifiers, not a strict order. The maintainer may reorder phases.
3. Check the milestone's **Decisions needed** entries against the [decision register](#decision-register). Don't start work that depends on an open decision. Raise it at the checkpoint instead.
4. Follow the normal milestone workflow from `ide-prompt.md`:
   - plan mode
   - ADRs before code
   - task breakdown
   - the definition of done (unit, integration and headless UI tests, CI green on three OSes, budgets met, docs updated)
   - **stop at the checkpoint**
5. The scope lists here are the **target**. At plan time, split a milestone (for example M14a/M14b) if it is too big to ship in one checkpoint. Move items out explicitly with a `TODO(issue)`. Never drop them silently.
6. After each checkpoint, update the status table, the decision register and the [change log](#change-log) at the end of this file.

## Status

| Milestone | Title | Status |
|---|---|---|
| M0 | Scaffold and UI stack spike | done (ADR 0001, 0002) |
| M1 | Shell and plugin foundation | in progress |
| M2–M7 | MVP (`ide-prompt.md`) | planned |
| M8 | Testing: Test Explorer and code coverage | planned (this document) |
| M9 | Packages and project management | planned |
| M10 | Integrated terminal and interactive windows | planned |
| M11 | Language platform: LSP bridge, grammars, file-type editing | planned |
| M12 | Visual Basic | planned |
| M13 | F# | planned |
| M14 | Editor and code-navigation depth | planned |
| M15 | Git depth and hosting integration | planned |
| M16 | Debugger depth | planned |
| M17 | ASP.NET Core and Hot Reload | planned |
| M18 | Razor, Blazor and web front-end | planned |
| M19 | Containers, Aspire and publishing | planned |
| M20 | Profiling and diagnostics tools | planned |
| M21 | Avalonia XAML editing and live previewer | planned |
| M22 | .NET MAUI | exploratory, needs a go/no-go (D6) |
| M23 | Remote development | planned |
| M24 | Plugin platform 1.0 and marketplace | planned |
| M25 | Personalization, settings sync and localization | planned |

## Baseline: what the MVP is assumed to deliver

This plan assumes M7 ends with what `ide-prompt.md` describes. **At the M7 checkpoint, compare this list with what was actually built, and carry any deferred `TODO(issue)` items into the right post-MVP milestone.**

- **Shell** (`Daedalus.Shell`, Avalonia 12, Native AOT): docking with persisted layouts, commands, key bindings, the palette and menus, JSON settings with a UI, themes (light, dark, high contrast), status bar, Output window, notifications.
- **Workbench services** (`Daedalus.Workbench`) and the platform layer (`Daedalus.Platform`: paths, file watching, process launching, shell integration, keys).
- **Language service host** (`daedalus-lshost`): Roslyn C# with the M3 feature set (highlighting, completion, navigation, rename, code fixes with preview, formatting, Go To All, Find in Files, analyzers, source generators).
- **Build host**: MSBuild evaluation and builds, solution and project model, file nesting, project edits, `dotnet new` templates, configurations, multi-targeting.
- **Debugging**: a DAP client plus netcoredbg, with the M5 feature list and a documented list of known gaps.
- **Git**: the M6 feature set, using LibGit2Sharp or the git CLI as decided in the M6 ADR.
- **Plugins**: plugin API 0.x (ADR 0003): out-of-process hosts, declarative contributions, tree and Markdown views. The rich-UI ADR (webview or remote UI) is due by M6.
- **Release**: installers for three OSes, local opt-in crash reporting, performance gates in CI, a public repository.

## Principles for post-MVP work

These add to the architecture principles in `ide-prompt.md`. They don't replace them.

- **Every feature names its process.** Each milestone says where its code runs: shell, an existing host, a new supervised host, or the user's process. A new host starts lazily, never at startup, so the idle-memory budget (ADR 0002, ≤ 250 MB) holds. It is supervised by `SupervisedProcess` and follows the crash-loop policy.
- **Plugin first, where latency allows.** As ADR 0003 says, a new feature is a built-in plugin unless it sits on a latency-critical path. When a feature needs an API that doesn't exist yet, it is added under `Daedalus.Plugins.Abstractions.Proposed` first. Built-ins using proposed APIs is how the API gets proved before 1.0 (M24).
- **The shell owns generic UI, providers own data.** Examples: the shell renders Test Explorer, and test controllers supply tests. The shell renders language features, and language services (Roslyn, LSP servers) supply data. This keeps large views virtualized and AOT-clean, and lets third parties plug in.
- **Reuse before writing.** Prefer maintained MIT, Apache-2.0 or BSD components. When a VS feature is backed by something proprietary, say so, and either build an open replacement or record a known gap.
- **Every interactive surface gets a budget.** Each milestone adds harness scenarios, measures Rider (and VS Code where relevant), and proposes budgets at its checkpoint. The rule is the same as in ADR 0002: at least as good as VS Code for editor surfaces, and at least as good as Rider for solution and language features.
- **Every protocol extension is documented** in `docs/protocol.md` or `docs/plugin-protocol.md` with the `daedalus/` prefix. Plugin API changes are listed in the plugin changelog.

### Known proprietary traps

These come up repeatedly in post-MVP areas. They are **off limits** unless a later licence check shows otherwise:

- **vsdbg** and the **C# Dev Kit** (named in the mission).
- **Debugging and diagnostics:** Visual Studio's debugger engines, the Visual Studio profiler and diagnostics hub, IntelliTrace.
- **Code coverage:** `Microsoft.CodeCoverage` and `Microsoft.Testing.Extensions.CodeCoverage`. Verify their licences. Treat them as excluded unless they are OSI-licensed.
- **AI and collaboration:** IntelliCode, the GitHub Copilot language server, Live Share.
- **UI designers:** the .NET MAUI extension for VS Code and Visual Studio's XAML designer and XAML Hot Reload.
- **Visual Studio packages:** most `Microsoft.VisualStudio.*` packages, which ship under Visual Studio licence terms. Check each one. Some, such as `Microsoft.VisualStudio.Threading`, are MIT.
- **Hosted services:** Dev Tunnels and other Microsoft-hosted services. Don't depend on them by default. If one is ever offered, it is an opt-in plugin.

## Foundational decisions to settle early

These decisions get more expensive with every milestone. **Raise them at the M7 checkpoint at the latest.** Some of them (F1 and F4) affect public contracts that become harder to change once the repository is public and plugins exist.

| # | Decision | Why it matters | Recommendation |
|---|---|---|---|
| F1 | **Document identity: file paths or URIs.** The M0–M3 protocols identify documents by file path. | Remote development (M23), decompiled and Source Link documents (M14), Git revisions in diff views (M15), virtual and generated documents (Razor, M18), and untitled buffers all need non-file documents. LSP uses URIs. | Switch every shell↔host and plugin contract to URIs (`file:`, `untitled:`, `daedalus-decompiled:`, `daedalus-git:`, `daedalus-remote:`) before the plugin API reaches 1.0, ideally before going public. Keep path helpers in the SDK. |
| F2 | **Virtual file system.** `Daedalus.Platform` has a file watcher, but there is no file-system abstraction. | The same features as F1. Without this, remote development becomes a rewrite. | Add `IFileSystemProvider`, chosen by URI scheme, in `Daedalus.Platform`. Route every editor, Solution Explorer and search read through it. |
| F3 | **Rich plugin UI.** ADR 0003 requires a webview or remote-UI ADR by M6. | NuGet UI (if built as a plugin), test result details, profiler views, the previewer, and third-party tools. | Follow the M6 ADR. This plan assumes some rich surface exists by M9. If it doesn't, those UIs are built into the shell and move later. |
| F4 | **Node.js policy.** | The best open web language servers (HTML, CSS, JSON, TypeScript) and debuggers (`vscode-js-debug`, which also bridges Blazor WebAssembly debugging) run on Node. So do the Dockerfile language server and the Dev Containers CLI. | Download a pinned, checksum-verified Node runtime on demand, the first time a feature needs it. Don't bundle it, and don't require a user install. This needs an ADR (D3). |
| F5 | **Language host topology.** | VB, F#, Razor and LSP servers need a home. | Roslyn C# and VB share `daedalus-lshost`. Razor is cohosted in the same Roslyn host. F# runs in its own process (FsAutoComplete or our own FCS host). Everything else goes through the generic LSP bridge (M11). |
| F6 | **Workspace trust.** | Opening a solution runs MSBuild evaluation, analyzers and source generators, all of which can run arbitrary code. Plugins and tasks add more. That matters once the repository is public. | Add a "restricted mode" for untrusted folders before or with the public release. It parses but doesn't evaluate, build or load analyzers, generators or workspace plugins until the user trusts the folder. Raise this at the M7 checkpoint (D4). |
| F7 | **Per-solution state.** | Breakpoints, open documents, layouts, test playlists, profiles and caches need a home. | `.daedalus/` next to the solution (git-ignored, like `.vs/`) for state the user may want to inspect or share. User-local caches go in the platform data directory, keyed by a solution hash. |

## Phase overview

| Phase | Milestones | Theme |
|---|---|---|
| 1. Daily driver | M8, M9, M10 | What a C# developer misses most on day one after the MVP: tests, packages, a terminal. |
| 2. Languages | M11, M12, M13 | The mission's first post-MVP items (F# and VB) on top of a general language platform. |
| 3. Depth | M14, M15, M16 | Bringing the editor, Git and the debugger from "MVP" to "VS-class". |
| 4. Web and cloud-native | M17, M18, M19 | ASP.NET Core, Blazor, Hot Reload, containers, Aspire, publishing. |
| 5. Diagnostics | M20 | Profiling, counters, memory analysis, dumps, benchmarks. |
| 6. UI frameworks | M21, M22 | Avalonia XAML with a live previewer. MAUI is exploratory. |
| 7. Remote | M23 | SSH, WSL, Dev Containers. |
| 8. Ecosystem | M24, M25 | Plugin API 1.0, marketplace, profiles, sync, localization. |

**Why this order.**
- Tests, NuGet and a terminal are what stop people from using the IDE every day.
- F# and VB are explicitly next in the mission. They go after the language platform (M11), because M11 makes them cheaper and proves the "pluggable language services" requirement.
- Web development is the biggest .NET workload. It comes after depth work because Hot Reload and Blazor debugging build on M14 and M16.
- Remote development comes late because it touches every host. F1 and F2 make sure it doesn't need a rewrite.

### Dependency graph

```mermaid
graph LR
  MVP[M7 MVP release] --> M8 & M9 & M10 & M11 & M14 & M15 & M16
  F1[F1/F2 URIs + VFS] --> M14 & M15 & M18 & M23
  F3[F3 rich plugin UI] -.-> M9 & M20 & M24
  F4[F4 Node policy] --> M18
  M8 --> M14
  M9 --> M11
  M9 --> M19
  M10 --> M13
  M10 --> M23
  M11 --> M12 & M13 & M18 & M21 & M24
  M14 --> M16
  M16 --> M17
  M17 --> M18 & M19
  M16 --> M19
  M16 --> M22
  M21 --> M22
  M19 --> M23
  M8 & M11 & M16 --> M24
```

A dashed edge means "better with", not "blocked by".

---

## Phase 1: Daily driver

### M8 — Testing: Test Explorer and code coverage

**Goal.** Discover, run, debug and analyse tests across the solution, with coverage shown in the editor. This should be at least as capable as Rider's unit-test runner for the mainstream frameworks.

**Depends on.** M4 (builds), M5 (debugging). A split into **M8a** (Test Explorer) and **M8b** (coverage) is allowed.

**Scope.**
- **Frameworks:**
  - xUnit v2 and v3, NUnit 3 and 4, MSTest 3 and later.
  - Both test platforms: VSTest, and Microsoft.Testing.Platform (MTP) for projects that opt into it.
  - TUnit (MTP only), if its licence checks out.
  - F# frameworks (Expecto) are added in M13.
- **Discovery:**
  - **Source-based** discovery from Roslyn gives a tree immediately after the solution opens, before any build. It finds methods with known test attributes.
  - **Runner-based** discovery runs after a build and is authoritative: data rows, theories, dynamic tests.
  - The two are merged by fully qualified name and source location.
- **Test Explorer tool window:**
  - A virtualized hierarchy: project, namespace, class, method, data row.
  - Grouping by project, namespace, class, trait, outcome or duration.
  - A filter syntax: free text, `trait:`, `outcome:`, `project:`.
  - Run all, failed, selected or not-run tests. Repeat the last run. Cancel.
  - Playlists, saved as JSON under `.daedalus/`. VS `.playlist` files can be imported.
- **Results:**
  - outcome, duration, message, stack trace with clickable frames, stdout and stderr, attachments
  - the history of the last N runs per test
- **Editor integration:** run, debug and status glyphs in the gutter. CodeLens entries ("Run | Debug | ✓ 12 ms") come once CodeLens lands in M14. Failed tests can be navigated to with F8-style "next failure" commands.
- **Debugging tests:**
  - **VSTest:** the IDE launches the test host itself through the translation layer's custom-launcher API, under netcoredbg.
  - **MTP:** the test application runs under the debugger with a filter by test UID.
- **Settings:**
  - the `.runsettings` selection and MTP `testconfig.json`
  - the target framework for multi-targeted test projects, the architecture, environment variables, parallelism
  - an option to run affected tests after a build
- **Coverage (M8b):**
  - **Coverlet:** the VSTest collector, and Coverlet's MTP integration if it is available. Otherwise `coverlet.console`.
  - **Coverage Results window:** a tree by assembly, namespace, class and method, with line and branch percentages.
  - **Editor:** covered, partially covered and uncovered gutter colouring.
  - **Results:** merged across test projects, exported as Cobertura, and optionally turned into an HTML report.

**Out of scope.** Live Unit Testing (see [candidates](#unscheduled-candidates)) and test impact analysis.

**Architecture.**
- **Shell:** owns the Test Explorer view, the gutter glyphs, the result details view and the coverage overlay (generic, AOT-clean, virtualized).
- **New plugin API (proposed):** a **test controller** API, modelled on VS Code's `TestController`, to report items, run requests, results and output. Test providers are plugins.
- **Built-in plugin `Daedalus.Plugins.DotNetTest`:**
  - implements the controller with the VSTest translation layer and an MTP client
  - runs in a **dedicated** plugin host, because it can be heavy
  - gets two other new plugin APIs (proposed): `IBuild` to request builds, and `IDebug` to start a debug session with a launch configuration
- **`daedalus-lshost`:** adds `daedalus/testCandidates` for source-based discovery: test methods per document, with ranges. The shell forwards the results to the controller.
- **Coverage:** runs inside the test plugin. Line data is pushed to the shell as a decoration layer, keyed by document URI.

**Dependencies.**
- `Microsoft.TestPlatform.TranslationLayer` (MIT)
- An MTP server-mode client. MTP (`microsoft/testfx`) is MIT. Implement against its documented JSON-RPC protocol, and reference packages only where needed.
- `coverlet.collector` and `coverlet.console` (MIT)
- ReportGenerator (Apache-2.0, optional)

**Decisions needed.** None beyond the standing ones. The ADR covers the VSTest/MTP split.

**ADRs.** "Test platform integration" (process model, controller API, VSTest vs MTP, debugging tests). "Code coverage engine".

**Fixtures and tests.**
- `samples/Testing/`:
  - xUnit v2, xUnit v3 (MTP), NUnit, MSTest in both VSTest and MTP modes
  - theories and data rows, a multi-targeted test project
  - failing, skipped, hanging (for cancellation) and output-writing tests
- A `FixtureGen` target that generates 5,000 tests across 20 projects.
- Integration tests drive the plugin against the fixtures. A headless UI test checks tree population and filtering.

**Budgets to propose** (measure Rider first):
- Source-based tree for Solution50 plus the 5k-test fixture: ≤ 1 s after the solution tree appears.
- Discovery after a build: compare with Rider.
- Filtering 10k nodes: no frame > 50 ms.
- Run → first result shown: compare with Rider.

**Risks.**
- Different VSTest and MTP behaviour across framework versions.
- Timing of the test-host debug launch.
- Stability of the MTP server protocol across .NET SDK versions.
- Coverlet's support for MTP.

**Exit criteria.** All four frameworks discover, run, debug and report on three OSes. Coverage is shown in the editor for the fixtures. The controller API is documented as proposed.

### M9 — Packages and project management

**Goal.** The NuGet Package Manager, project properties, and the solution-management dialogs VS users rely on. No more hand-editing XML for routine work.

**Depends on.** M2 (project system), M4 (build/restore). F3 is helpful but not required.

**Scope.**
- **NuGet Package Manager**, at project and solution level:
  - Browse, Installed, Updates and Consolidate tabs.
  - Search across all sources from the effective `NuGet.Config`. Prerelease toggle and version picker.
  - Package details: dependencies per TFM, licence, README rendered as Markdown, links, download counts.
  - Badges for vulnerabilities, deprecation and the alternative package.
  - Licence acceptance for packages that require it.
  - Transitive packages view with "why is this here".
  - Restore progress and errors.
- **Central Package Management:**
  - edits `Directory.Packages.props`
  - understands `VersionOverride`, `GlobalPackageReference` and package source mapping
- **Package sources UI:** add, remove, enable and order sources, and map package patterns to sources.
- **Credential providers:** use the NuGet cross-platform credential-provider protocol, and invoke installed providers such as the Azure Artifacts provider. Don't bundle them.
- **Project properties UI.** Each page shows evaluated values, where a value comes from (the project file, `Directory.Build.props` or the SDK default), and its conditions. It writes the minimal change.
  - **Application:** TFMs, output type, assembly name, root namespace, nullable, implicit usings, language version.
  - **Build:** defines, warnings as errors, `NoWarn`, platform target, optimisation, debug symbols, deterministic builds.
  - **Package:** pack metadata, icon, README, licence expression, symbols.
  - **Code analysis:** `AnalysisLevel`, `EnforceCodeStyleInBuild`, analyzer packages.
  - **Signing:** strong naming.
  - **Debug:** a launch profile editor for `launchSettings.json` (command name, arguments, environment, working directory, application URL, launch browser).
- **References:**
  - add project and assembly references, with a filter
  - analyzer references
  - shared projects (`.projitems` imports), as display and edit only, at low priority
- **Solution management:**
  - Configuration Manager: solution-to-project configuration and platform mapping, written to `.sln` and `.slnx`.
  - Project build dependencies and build order.
  - Load and unload projects. Solution filters (`.slnf`): open, create and save.
  - `global.json` SDK selection, and status of installed SDKs and workloads.
- **Pack:** a `dotnet pack` command with output navigation.

**Architecture.**
- **Build host:**
  - `daedalus/packages/*`: search, metadata, install, update, uninstall and consolidate. Built on NuGet.Protocol. Edits go through MSBuild's `ProjectRootElement`, followed by a restore.
  - `daedalus/projectProperties/*`: read evaluated and unevaluated values with provenance, and write them with a condition scope.
  - All network access happens here. It is cancellable, uses the system proxy, supports an offline mode, and caches results.
- **UI:**
  - **NuGet manager:** if F3 delivered a rich plugin surface, the UI is a built-in plugin and the first real consumer of that surface. Otherwise it is a built-in shell view.
  - **Property pages:** built into the shell and generated from a page schema, so F# and VB pages reuse the same machinery.
- **Edits must match the CLI.** The resulting file content must match what `dotnet add package` and `dotnet remove package` would produce. Tests compare the two.

**Dependencies.** NuGet.Protocol, NuGet.Configuration, NuGet.Versioning and NuGet.Packaging (Apache-2.0). Reuse the Markdown renderer chosen in M1.

**ADRs.** "Package management architecture", covering the NuGet API surface used, CPM, credentials and offline mode. "Project property pages schema".

**Fixtures and tests.**
- `samples/Packages/`: plain PackageReference, CPM, multi-targeted, and package source mapping.
- A local file feed generated by `FixtureGen`, with packages that need licence acceptance, prerelease packages and deprecated packages.
- An in-process mock NuGet V3 server (Kestrel) in tests, for search and vulnerability data.
- Golden-file tests that check project file edits preserve formatting.

**Budgets to propose.**
- Browse, first page rendered after the response arrives: ≤ 200 ms.
- Install → restored and IntelliSense updated: ≤ restore time + 2 s.
- Property pages open: ≤ 300 ms.

**Risks.**
- Preserving formatting and comments in project files.
- CPM edge cases.
- Credential providers on macOS and Linux.
- Large `Directory.Build.props` hierarchies make provenance hard to show clearly.

### M10 — Integrated terminal and interactive windows

**Goal.** A fast, accessible integrated terminal, and a C# Interactive window.

**Depends on.** M1 (shell), M3 (the editor control, its automation peer and themes). M9 is optional (`#r "nuget:"`).

**Scope.**
- **Terminal tool window:**
  - Several instances and splits.
  - Profiles: pwsh, bash, zsh, fish and cmd, plus a "dotnet developer" profile with the SDK environment.
  - The working directory defaults to the solution directory.
- **Shell integration:**
  - cwd tracking (OSC 7) and command marks (OSC 133)
  - clickable paths with `file:line:col` that open in the editor
  - clickable URLs
- **Emulation:** xterm-compatible.
  - true colour, wide and emoji characters, combining marks
  - alternate screen (vim and htop work), mouse reporting, bracketed paste
  - IME, resizing, configurable scrollback, find
- **Theme and fonts:**
  - IDE theme colours, including high contrast
  - font settings
  - configurable key passthrough: which shortcuts go to the terminal and which go to the IDE
- **Accessibility:** a screen-reader mode that exposes the buffer through the same UIA/AX text-pattern peer the editor got in M3.
- **Run in terminal:** console apps run with "Run without debugging" can use the integrated terminal for stdin and stdout, controlled by a setting (the external console is the alternative). The same option exists for debugging, through a netcoredbg console redirect.
- **C# Interactive:**
  - A REPL on Roslyn scripting, with its own editor input (highlighting, completion and signature help served by `daedalus-lshost` as script documents) and a history.
  - Supports `#r` and `#load`. `#r "nuget:"` uses the M9 package service.
  - "Initialize Interactive with Project" and "Send selection to Interactive".

**Architecture.**
- **PTY:** lives in `Daedalus.Platform`, using ConPTY on Windows and `openpty`/`forkpty` on Unix through P/Invoke. This is AOT-safe and needs no extra host.
  - **Justification to record:** terminal I/O is latency-critical but light, and child processes are already isolated from the shell.
  - If the ADR concludes that PTY handling must leave the shell (for example for crash isolation), it becomes `daedalus-ptyhost`.
- **VT engine and renderer:** a VT parser and screen model, plus a custom Avalonia control that renders glyph runs with dirty-row invalidation and a glyph cache.
- **REPL host:** a new `daedalus-replhost`, out of process. It runs user code, which must never run in the shell or the language host. It is supervised, so a crash or `Environment.Exit` in a script only restarts the REPL.

**Dependencies (candidates for the ADR).**
- XtermSharp (MIT): VT engine. Check its maintenance and AOT compatibility.
- `vs-pty.net` (MIT): PTY. Check its AOT compatibility, or write our own P/Invoke.
- `Microsoft.CodeAnalysis.CSharp.Scripting` (MIT).

**ADRs.** "Terminal emulator: engine, renderer, PTY placement". "Interactive (REPL) host".

**Fixtures and tests.** Recorded VT streams (`ls --color`, vim, htop, a progress bar, `dotnet build`) are replayed into the engine and compared with screen snapshots. PTY integration tests run on all three OSes. REPL tests cover crash and restart.

**Budgets to propose** (compare with VS Code's terminal and Windows Terminal):
- Keystroke echo → displayed frame: p99 within the editor typing budget.
- `cat` of a 1M-line file: no frame > 50 ms, and throughput comparable to VS Code.
- Memory per terminal with 10k scrollback lines: ≤ 30 MB.

**Risks.**
- ConPTY differences between Windows versions. The supported Windows floor needs a decision.
- The macOS login-shell environment.
- IME in a custom-drawn control.
- Screen-reader support for a live buffer.

---

## Phase 2: Languages

### M11 — Language platform: LSP bridge, grammars and file-type editing

**Goal.** Make "a language" a pluggable unit, as the mission requires for F# and VB. Give first-class editing to the non-C# files every .NET repository has.

**Depends on.** M3 (the language-service abstraction), M9 (package and property data for MSBuild editing). F1 strongly recommended.

**Scope.**
- **Language registry.** Plugins contribute these through `contributes.languages`, `contributes.grammars` and `contributes.languageConfigurations`:
  - language ids, extensions, file names and first-line patterns
  - TextMate grammars
  - language configuration: comments, brackets, auto-closing pairs, indentation rules, word pattern, folding markers
- **Generic LSP bridge.** A plugin declares a language server (`contributes.languageServers`: command, arguments, transport, activation, initialization options, settings section). The shell supervises it and maps LSP 3.17 onto the same UI C# uses:
  - completion, hover, signature help
  - definition, declaration, implementation, references, rename
  - code actions, formatting (document, range and on-type)
  - semantic tokens, inlay hints, call and type hierarchy
  - push and pull diagnostics
  - document and workspace symbols (which feed Go To All)
  - folding, selection ranges, linked editing, CodeLens
- **Built-in file types:**
  - **MSBuild** (`.csproj`, `.fsproj`, `.vbproj`, `.props`, `.targets`, `Directory.Packages.props`):
    - Completion for elements, properties, items, metadata and well-known targets. Value completion for TFMs, RIDs, package ids and versions (from M9), and enums.
    - Hover shows the **evaluated value and its origin** from the build host.
    - Go to definition across imports.
    - Diagnostics for unknown items and invalid values.
    - This is a strength no other cross-platform editor has, because we own the evaluation.
  - **JSON with schemas:**
    - `global.json`, `launchSettings.json`, `appsettings*.json`, `.config/dotnet-tools.json`, `daedalus-plugin.json`
    - `testconfig.json` and `runtimeconfig.template.json`
    - any file mapped through the SchemaStore catalogue (cached, with an offline fallback)
  - **XML with XSD validation:** `.runsettings`, `nuget.config`, `.slnx`, `.resx`, `.xsd` and `.xml`.
  - **`.editorconfig`:** key and value completion from Roslyn's option metadata, plus hover documentation.
  - **`.resx` editor:** a grid editor for string resources (name, value, comment), with an "open as XML" toggle. Image and file resources can be viewed and replaced. The WinForms resource designer is a non-goal.
  - **YAML with schemas:** GitHub Actions and Azure Pipelines.
  - **Markdown** with a live preview.
- **Language-agnostic features:**
  - word-based completion as a fallback
  - indentation folding, bracket matching
  - encoding and BOM detection, binary-file detection
- **Samples:** a sample plugin that contributes a grammar plus an LSP server (for example PowerShell Editor Services, MIT) as an end-to-end proof, shipped under `samples/plugins/`.

**Architecture.**
- **New library `Daedalus.LanguageServices.Lsp`:** LSP types are **source-generated from the LSP `metaModel.json`** (MIT, from `vscode-languageserver-node`), which keeps them AOT-safe. It also holds the client and the adapter to the shell's language-service abstraction.
- **LSP servers:** supervised child processes of the shell. They are declared by manifests, so no plugin code is loaded to start a server.
- **TextMate tokenization:** runs off the UI thread, viewport first, like semantic tokens.
- **MSBuild language service:** lives in the **build host**, next to the evaluator. JSON, XML and YAML services run in a small `daedalus-markuphost`, or in the plugin host. The ADR decides.

**Dependencies.**
- TextMateSharp (MIT; AvaloniaEdit already integrates it).
- TextMate grammars: check each one. Most of VS Code's are MIT.
- JsonSchema.Net or NJsonSchema (MIT).
- YamlDotNet (MIT).
- Markdig (BSD-2-Clause), if it isn't adopted earlier.

**ADRs.** "Language service abstraction v1 and the LSP bridge" (feature mapping, multi-server per language, ordering and merging). "MSBuild language service". "Schema-based editing".

**Fixtures and tests.**
- A **conformance test LSP server** in `tests/`, with a scripted subset per feature, for the bridge tests.
- MSBuild editing tests against `samples/Solution50` and CPM fixtures.
- Grammar tokenization snapshot tests.

**Budgets.** Typing budget 2 must hold in every file type, since tokenization and LSP calls are async. Opening a 100k-line XML or JSON file must meet budget 3a.

**Risks.**
- LSP servers that only work well in VS Code (custom client capabilities).
- Grammar licences.
- Merging results from several providers (for example C# plus a plugin's CodeLens).

### M12 — Visual Basic

**Goal.** Rich editing for VB projects at the same level as C#. Since the MVP, VB projects only load, build and run.

**Depends on.** M3, M11 (language registry). Not much new infrastructure: Roslyn already contains VB.

**Scope.**
- Enable the VB language in `daedalus-lshost`, in the same workspace as C#:
  - classification (append VB-specific token types to the legend; the legend is append-only)
  - completion, Quick Info, signature help, diagnostics, navigation, rename
  - code fixes and refactorings, formatting
- **VB editor behaviour** (the "pretty listing" experience):
  - keyword casing, `End` construct generation (`If` → `End If`), automatic line continuation handling, and XML literal editing
  - **Note:** Roslyn implements part of this in its VS-specific `EditorFeatures` layers. Reimplement it on our editor through on-type formatting hooks, and document which parts are ours.
- VB snippets, project templates (console, classlib, tests) and property pages (M9 pages with VB-specific options: `Option Strict`, `Explicit`, `Infer`, `Compare`, imports).
- Test Explorer works for VB test projects (M8).
- Debugging is known to work through portable PDBs.
  - **Gap:** netcoredbg evaluates expressions with C# syntax, so VB expressions in Watch and Immediate may fail.
  - Document the gap. Investigate translating simple VB expressions, or contributing upstream.
- The `My` namespace: only basic support. The WinForms application framework is a non-goal.

**Fixtures.** `samples/VisualBasic/`: a VB console app, a VB library with XML literals, a mixed C#/VB solution with cross-language references, and VB tests.

**Budgets.** The same as C# (budget 2, plus the M3 language budgets).

### M13 — F#

**Goal.** First-class F# editing, scripting and project handling, on par with Ionide or Rider for everyday work.

**Depends on.** M11 (LSP bridge), M10 (REPL infrastructure for F# Interactive), M8 (tests).

**Decisions needed.** D10: the host approach.

**Scope.**
- **Language service.**
  - **Recommendation:** run **FsAutoComplete** (Apache-2.0, built on FSharp.Compiler.Service, MIT) as a built-in plugin, through the M11 LSP bridge. Pin its version, and update it together with the SDK's F# version.
  - Add `daedalus/` extensions through a thin wrapper, or upstream them, where LSP isn't enough.
  - **Alternative:** our own host directly on FCS. It gives full control, but costs much more. The ADR decides.
- **Features:**
  - semantic highlighting, completion (including unopened namespaces), tooltips with signatures and XML docs
  - signature help, diagnostics, go to definition and references, rename
  - code fixes (FsAutoComplete has many)
  - inlay hints (types and parameter names), signature CodeLens
  - formatting with **Fantomas** (Apache-2.0), honouring `.editorconfig`
  - F# analyzers (FSharp.Analyzers.SDK, MIT)
- **Project system:**
  - **compile order** in Solution Explorer: move up and down, add above or below, and drag and drop. Edits change the order of `Compile` items through the build host.
  - `.fsi` signature-file pairing (nesting).
- **Scripts:** `.fsx` editing with `#r "nuget:"` and `#load` resolution.
- **F# Interactive:** a `dotnet fsi` session in the M10 REPL infrastructure. Send line, selection or file. Reset. Shows bound values.
- **Testing:** Test Explorer for xUnit, NUnit and Expecto (through the YoloDev.Expecto.TestSdk VSTest adapter, MIT).
- **Debugging:** works through portable PDBs.
  - **Gap:** F# expression syntax in Watch. Mitigation: document it, and investigate simple translation.
  - **Gap:** cross-language navigation. C# → F# goes to metadata or decompiled source, and so does F# → C#. Document both.
- **Templates:** F# console, classlib and test projects.
- **Type providers:** design-time type providers run inside the F# host. Treat this as user code: workspace trust (F6) applies.

**Fixtures.**
- `samples/FSharp/`: console, library, a mixed C#/F# solution, scripts, a type-provider project (FSharp.Data, Apache-2.0), a Fantomas configuration and Expecto tests.

**Budgets.**
- Typing budget 2 holds (F# highlighting is async).
- Completion in a 2k-line F# file: compare with Rider.
- FsAutoComplete memory is counted in the "solution open" memory scenario, never in idle memory.

**Risks.**
- FsAutoComplete and FCS version alignment with the installed SDK.
- FsAutoComplete memory on large solutions.
- Type providers that load native or incompatible assemblies.

---

## Phase 3: Depth

### M14 — Editor and code-navigation depth

**Goal.** Close the gap between the MVP editor and Visual Studio or Rider for daily C# work. This milestone is large. **Plan it as M14a** (navigation and inline information) **and M14b** (editing, refactoring UI and analysis).

**Depends on.** M3, M8 (test CodeLens), M6 (Git CodeLens), F1 (decompiled and Source Link documents).

**Scope, M14a (navigation and inline information).**
- **Navigation:**
  - Peek Definition, Go to Base and Go to Derived, Call Hierarchy, Type Hierarchy
  - a navigation bar (project, type and member drop-downs), breadcrumbs, Document Outline
  - navigation history, if it wasn't finished in M3
- **Metadata navigation:**
  - Go to definition on a library symbol opens **Source Link** sources, or embedded sources from the PDB, when they are available.
  - Otherwise it opens **decompiled C#** (ICSharpCode.Decompiler).
  - Symbol servers (the Microsoft and NuGet symbol servers) are an opt-in setting, because they make network calls.
- **Class View and Object Browser:** browse the solution's types and the referenced assemblies, with search.
- **Inline information:**
  - **CodeLens:** reference counts, implementations, tests (M8), and Git authors and changes (M6). Computed lazily, for the viewport only, off the UI thread.
  - inlay hints for parameters and types
  - sticky scroll, structure guides, bracket-pair colourization, reference highlighting

**Scope, M14b (editing, refactoring UI and analysis).**
- **Snippets:**
  - our JSON format, and import of VS `.snippet` XML
  - Insert Snippet and Surround With
  - snippets as completion items
  - snippet contributions from plugins
- **Editing:**
  - paste with automatic usings, smart paste
  - duplicate line, move line, box selection (if it wasn't done in M3)
  - format on save, paste and type
  - XML doc comment generation
- **Refactorings that need UI.** Roslyn's "options services" need dialogs:
  - Change Signature, Extract Interface, Extract Class, Pull Members Up, Move Type and Move Static Members
  - Generate Overrides, Generate Equals/GetHashCode and Generate Constructor (member pickers)
  - Encapsulate Field, Introduce Parameter
  - **Protocol:** a generic `daedalus/codeAction/resolveOptions`. The host returns an options schema, the shell renders a generic, accessible dialog, and the host applies the result with the usual diff preview.
- **Code Cleanup:** profiles (sets of fixers, as in `dotnet format`), run on demand or on save.
- **Analysis configuration:**
  - "Configure severity" writes to `.editorconfig` or `.globalconfig`.
  - An EditorConfig settings UI, generated from Roslyn's option metadata.
  - Suppressions: in source, in a suppression file, or in `.editorconfig`.
- **Error List depth:**
  - grouping and filtering by project, document, severity, code, and build vs live diagnostics
  - full-solution analysis scope: open documents, the current project, or the whole solution
- **Task List:** the `TODO`, `HACK` and `UNDONE` tokens, which can be configured.
- **Code Metrics:**
  - maintainability index, cyclomatic complexity, depth of inheritance, class coupling, lines of code
  - per project, type and member
- **Other:**
  - bookmarks, with names and folders
  - a compare-any-two-files command (reuses the M6 diff viewer)
  - encoding and line-ending UI
  - a hex viewer for binaries
- **Spell checking** in comments and strings (D9: dictionary licensing).

**Architecture.**
- Mostly `daedalus-lshost` (Roslyn) and the shell.
- Decompilation and Source Link resolution run in `daedalus-lshost` and produce `daedalus-decompiled:` and `daedalus-sourcelink:` documents (F1).
- **Go To All index:** a persisted cache for fast reopen. Investigate Roslyn's own persistent storage before writing our own.

**Dependencies.**
- ICSharpCode.Decompiler (MIT).
- Microsoft.SymbolStore (MIT, `dotnet/symstore`).
- The Code Metrics implementation from `roslyn-analyzers` (MIT; verify which package exposes it).
- Spell checking: WeCantSpell.Hunspell (verify the licence), with dictionaries per D9.

**ADRs.** "Rich refactoring UI over the language protocol". "Metadata navigation: decompilation, Source Link, symbol servers". "Persistent index storage".

**Budgets to propose.**
- CodeLens and inlay hints must not regress budget 2.
- Inlay hints for the viewport: ≤ 100 ms.
- Decompiling a type: ≤ 1 s.
- First level of the call hierarchy on Solution50: compare with Rider.

### M15 — Git depth and hosting integration

**Goal.** Cover the Git workflows people currently leave the IDE for, starting with merge conflicts.

**Depends on.** M6 (and its LibGit2Sharp-or-CLI ADR), F1 (`daedalus-git:` documents for revisions).

**Scope.**
- **Conflict resolution:**
  - a **3-way merge editor**: base, ours, theirs and the result
  - accept per hunk or per line, conflict navigation
  - abort and continue for merge, rebase and cherry-pick
- **History:**
  - a graph view with filters (branch, author, path, message, date)
  - file history and line history
  - navigation from blame to the commit, and "compare with branch or commit"
- **Operations:**
  - stash: create, partial stash, apply, pop and drop
  - cherry-pick, revert
  - reset (soft, mixed or hard, with a confirmation)
  - rebase, including an **interactive rebase** todo editor
  - tags: create, push and delete
  - remotes management
  - per-line staging (per-hunk staging is MVP)
- **Repository features:**
  - worktrees, submodules (status, update, init)
  - Git LFS awareness (status, locks shown)
  - commit signing (honours the user's git configuration for GPG or SSH signing)
  - commit message templates
  - "Add to .gitignore" from Solution Explorer
  - solutions spanning several repositories
- **Authentication:** honour the user's credential helper, such as Git Credential Manager (MIT). An IDE askpass prompt handles HTTPS and SSH passphrases. **Never store credentials ourselves.** Anything that must be stored goes through a `Daedalus.Platform` credential-store abstraction: Windows Credential Manager, macOS Keychain, or libsecret on Linux.
- **Hosting integration** (D8). This is a built-in plugin for GitHub, GitLab and Azure DevOps. It stays inactive until the user signs in (OAuth device flow, with the token in the OS credential store).
  - list and check out pull requests
  - review comments shown inline in the editor
  - create a pull request
  - CI status for the current branch

**Architecture.** The Git UI is a built-in plugin (ADR 0003). The merge editor and the history graph are shell components, because they are rendering-heavy and reusable. Graph layout is computed in the Git backend and streamed in pages.

**Fixtures and tests.**
- Scripted repositories created in temporary directories by test helpers: merge, rebase and cherry-pick conflicts, submodules, LFS pointer files.
- A generated repository with 100k commits for graph performance.

**Budgets to propose** (compare with Rider and VS Code):
- Graph of 100k commits, first page: ≤ 300 ms. Scrolling has no frame > 50 ms.
- Status refresh on a 50k-file working tree: ≤ 500 ms.

### M16 — Debugger depth

**Goal.** Close the gaps between the M5 debugger and Visual Studio as far as netcoredbg allows, and fill the rest with our own components. **Start by re-reading the M5 known-gaps list.** Anything netcoredbg can't do is either contributed upstream (MIT, Samsung), worked around in a companion host, or documented.

**Depends on.** M5, M14 (Source Link and decompilation).

**Scope.**
- **Display:**
  - `DebuggerDisplay`, `DebuggerTypeProxy` and `DebuggerBrowsable`. Check netcoredbg's support, and fill gaps in the companion.
  - Pinned data tips with comments.
  - The exception helper, with the inner-exception chain.
  - Return values after a step-out, if netcoredbg exposes them.
- **Visualizers:**
  - Built-in: text, JSON, XML and HTML source.
  - A grid visualizer for `IEnumerable` and `DataTable`.
  - An image visualizer for byte arrays with image headers.
  - A **visualizer plugin API**: the object is serialized by evaluation and rendered by the plugin through a declarative view, or through F3's rich surface.
- **Windows:**
  - Threads (flag, and freeze if supported)
  - **Parallel Stacks**: thread view, plus a task view built from async state where possible
  - Modules (symbol status, load symbols)
  - Immediate window
  - a Memory window, if netcoredbg implements DAP `readMemory`
- **Breakpoints:**
  - function, temporary and dependent breakpoints
  - labels, groups, import and export
  - exception conditions per type and module
  - data breakpoints, if supported (likely a gap)
- **Execution:**
  - Run to Cursor, Step Into Specific, Set Next Statement (if supported)
  - Just My Code settings
  - stepping into Source Link sources (from M14)
  - stepping into decompiled code is a stretch goal: it needs PDB generation for decompiled assemblies
- **Sessions:**
  - multiple startup projects and compound launches
  - attach by name or PID with a filter, and re-attach to the last process
  - attach over SSH (pipe transport), into a Docker container (`docker exec` with netcoredbg mounted or copied in), and into WSL
- **Dumps:**
  - Open Windows minidumps and Linux or macOS core dumps with **ClrMD**: threads, managed stacks, the faulting exception, heap statistics and object inspection. This is a read-only "dump session" with the same windows.
  - "Create dump" for a running process, through DiagnosticsClient.
- **Hot Reload while debugging:** research only in this milestone. The goal is to know whether runtime metadata updates (M17) can be applied while netcoredbg is attached. Record the result in an ADR. VS Edit and Continue parity is **not** promised.

**Architecture.**
- A new **debug companion host** (`daedalus-debughost`) sits between the shell and netcoredbg, or next to it. It:
  - proxies DAP
  - adds `daedalus/` debug extensions (visualizer payloads, parallel stacks aggregation, dump sessions through ClrMD)
  - downloads Source Link files
- netcoredbg stays an unmodified adapter, unless we contribute upstream.

**Dependencies.** Microsoft.Diagnostics.Runtime (ClrMD, MIT), Microsoft.Diagnostics.NETCore.Client (MIT).

**ADRs.** "Debug companion host and DAP extensions". "Visualizer plugin API". "Dump analysis".

**Fixtures.** `samples/Debugging/`:
- a multithreaded app and an async deadlock
- nested exceptions
- large collections
- `DebuggerDisplay` and type-proxy types
- an app that crashes on purpose, for dumps
- a two-project compound launch

**Budgets to propose** (compare with Rider):
- F10 → UI updated with locals: p95 ≤ 150 ms.
- Data tip: ≤ 100 ms.
- Expanding a collection with 10k elements: paged, with no frame > 50 ms.

**Risks.** Whatever netcoredbg can't support is a permanent gap until it is contributed upstream. The checkpoint must list each gap with an issue link.

---

## Phase 4: Web and cloud-native

### M17 — ASP.NET Core and Hot Reload

**Goal.** A productive ASP.NET Core loop: run, browse, call endpoints, change code, and see the change without a restart.

**Depends on.** M4, M5, M16 (only for the Hot-Reload-while-debugging result), M11 (JSON schemas).

**Scope.**
- **Templates:** web, webapi, mvc, razor, blazor, grpc and worker (through the M2 `dotnet new` support).
- **Run:**
  - Launch profiles with application URLs and launch-browser (the system default or a chosen browser).
  - **Kestrel only**: IIS and IIS Express are Windows-only and a non-goal.
  - An environment selector (`ASPNETCORE_ENVIRONMENT`).
- **HTTPS development certificates:**
  - check, create and trust them through `dotnet dev-certs`
  - platform-specific guidance for Linux trust stores
- **User secrets:** initialize `UserSecretsId` and edit `secrets.json`.
- **Configuration:** completion for `appsettings*.json` keys and values. This uses the ASP.NET schema, plus keys discovered from options classes where Roslyn can find them.
- **Endpoints Explorer:**
  - minimal API routes and controller actions, found by Roslyn analysis (`daedalus/aspnet/endpoints`)
  - navigation from a route to its handler
  - "generate request" into a `.http` file
- **`.http` files:**
  - an editor (grammar, variables, environments in `http-client.env.json`)
  - a request runner, with the response shown in a tool window (headers, body formatted by content type, timing)
  - request history
- **Service references:**
  - OpenAPI client generation through Kiota (MIT) or NSwag (MIT); the ADR decides
  - gRPC client and server generation through `Grpc.Tools`, with `.proto` editing (grammar)
- **Hot Reload**, for all project types (console, web, worker, Blazor Server):
  - Applies code edits to a running process without a restart, on save (configurable).
  - **Rude edits** offer a restart.
  - Web apps get **browser refresh** and CSS hot reload.
- **Library manager and npm:** listed as candidates. Not committed in this milestone.

**Architecture.**
- **Hot Reload, the same way `dotnet watch` (MIT) does it:**
  - **Delta computation:** `daedalus-lshost` computes deltas with Roslyn's hot-reload service (the `ExternalAccess` watch/hot-reload API), comparing the current solution with the baseline from the launch.
  - **Delivery:** an agent assembly is injected with `DOTNET_STARTUP_HOOKS`, and `DOTNET_MODIFIABLE_ASSEMBLIES=debug` is set. It is based on `Microsoft.Extensions.DotNetDeltaApplier` from `dotnet/sdk` (MIT; vendor or reference it). The agent applies deltas through `MetadataUpdater.ApplyUpdate`.
  - **Browser refresh:** for web apps, the agent cooperates with the browser-refresh middleware script (from `dotnet/sdk`, MIT).
  - **Orchestration:** the shell decides when to apply. The launcher (build host or shell) owns the process.
- **Protocol:** `daedalus/hotReload/*` (start a session, apply, report rude edits, restart).

**Dependencies.**
- The Roslyn `ExternalAccess` hot-reload packages (MIT). **Availability risk:** they may only be published on the .NET engineering feeds. Decide between the feed, building from source and vendoring, and record the supply-chain decision.
- Delta applier and browser refresh (MIT, `dotnet/sdk`).
- Kiota or NSwag (MIT).

**ADRs.** "Hot Reload architecture". "HTTP file format and runner". "Service reference generation".

**Fixtures.** `samples/Web/`: minimal API, MVC, Razor Pages, gRPC and worker apps. A hot-reload app with scripted edits: a method body change (supported), a signature change (rude) and a CSS change.

**Budgets to propose** (compare with `dotnet watch` and Rider):
- Save → change active in the running app, for a single-method edit: ≤ 1 s.
- Endpoints Explorer populated: ≤ 1 s after the solution loads.

**Risks.**
- The `ExternalAccess` APIs change with every SDK.
- Combining the startup-hook agent with netcoredbg.
- Trusting development certificates on Linux.

### M18 — Razor, Blazor and web front-end

**Goal.** Rich editing for `.razor` and `.cshtml`, Blazor run, debug and hot reload, and good HTML, CSS and JavaScript editing.

**Depends on.** M17, M11, F1 (generated and virtual documents), F4 (Node policy).

**Decisions needed.** D3 (Node).

**Scope.**
- **Razor language services** from `dotnet/razor` (MIT):
  - **Recommended approach:** "cohosting" inside the Roslyn language host, which is upstream's direction.
  - **C# inside Razor:** completion, diagnostics, navigation, rename and code actions, mapped through generated documents.
  - **Razor syntax:** tag helper and component completion, directives, component parameters, formatting, and go to a component's definition.
- **HTML, CSS and JavaScript/TypeScript:**
  - **Inside Razor:** HTML and CSS editing.
  - **Standalone files:** `.html`, `.css`, `.js`, `.ts` and `.json`.
  - **Recommended under D3:** the VS Code HTML, CSS and JSON language services (MIT) and the TypeScript language server (TypeScript is Apache-2.0; `typescript-language-server` is MIT), all running on the on-demand Node runtime through the M11 LSP bridge.
- **Blazor:**
  - Run Server, WebAssembly and Web App (interactive render modes).
  - Hot Reload for components (M17).
  - **WebAssembly debugging:** the BrowserDebugProxy (`dotnet/runtime`, MIT) speaks the Chrome DevTools Protocol to Chromium-based browsers, and the DAP side is bridged through `vscode-js-debug` (MIT, Node). Firefox is a gap.
- **JavaScript debugging** for web apps, through `vscode-js-debug`: breakpoints in `.js` and `.ts` with source maps, and compound sessions with the server-side C# debugger.
- **Static web assets and scoped CSS** (`.razor.css` nesting and completion).
- **npm:** `package.json` scripts shown as runnable tasks, and npm restore. Library Manager (`libman.json`) is a candidate.

**Dependencies.**
- `dotnet/razor` (MIT). **Availability risk:** the packages are on the .NET engineering feeds, and the APIs are `ExternalAccess`.
- The VS Code HTML, CSS and JSON language services (MIT).
- TypeScript (Apache-2.0) and `typescript-language-server` (MIT).
- `vscode-js-debug` (MIT).
- Node.js (MIT), downloaded on demand under D3.

**ADRs.** "Razor integration (cohosting)". "Web language services and the Node runtime". "Browser debugging".

**Fixtures.** `samples/Web/Blazor*`: Server, WebAssembly, Web App, a Razor class library, an MVC app with tag helpers, and a site with TypeScript.

**Budgets to propose.**
- Typing budget 2 holds in `.razor` files.
- Razor completion: compare with Rider.
- Diagnostics updated ≤ 500 ms after the user pauses typing.

**Risks.**
- Razor's coupling to specific Roslyn versions.
- The size of the Node download and keeping it up to date.
- CDP debugging differences between browsers and OSes.

### M19 — Containers, Aspire and publishing

**Goal.** Get an app from the IDE to a deployable artefact or container, and develop multi-service apps with .NET Aspire.

**Depends on.** M17, M16 (container attach), M9 (property pages).

**Decisions needed.** D12 (Aspire's orchestrator licensing).

**Scope.**
- **Publish profiles** (`.pubxml`), with a UI:
  - folder output
  - framework-dependent or self-contained, RID
  - single-file, trimmed, ReadyToRun, **Native AOT** (trim and AOT warnings are surfaced in the Error List)
  - **container images** through the SDK's built-in container support (`PublishContainer`, MIT), to the local daemon or a registry
  - Cloud-specific targets (Azure, AWS, GCP) are opt-in plugins, not core.
- **Docker:**
  - **Files:** Dockerfile and `compose.yaml` editing (grammar, schema, and optionally the Dockerfile language server under D3).
  - **Add Docker support:** generates a Dockerfile that matches the official .NET templates.
  - **Build and run:** build and run a container, and run compose.
  - **Debugging:** debug a project running in a container (netcoredbg made available in the container and attached through `docker exec`, using M16).
  - **Containers tool window:** containers, images, logs, environment, ports and files.
  - **Engines:** Docker and Podman, through their CLIs or the Engine API.
  - "Fast mode" (mount the build output into a base image, as VS does) is a candidate.
- **.NET Aspire:**
  - Detect AppHost projects, and run the AppHost.
  - Show the **resource tree** (projects, containers and executables, with state, endpoints and logs) inside the IDE, and open the Aspire dashboard (MIT).
  - Implement the IDE side of Aspire's **IDE execution protocol**, so the AppHost asks Daedalus to launch, and optionally debug, each project.
  - Debug all services as one compound session.

**Dependencies.** Docker.DotNet (MIT), if the Engine API is used rather than the CLIs. The Aspire packages are the user's dependencies, not ours.
- **D12:** check the licence of Aspire's orchestrator (DCP). It runs as part of the user's app, and we don't redistribute it. Our integration must not need to ship it.

**ADRs.** "Publishing and container tooling". "Aspire integration".

**Fixtures.** `samples/Containers/`: a web app with a Dockerfile, a compose app, and an Aspire AppHost with two projects plus a Redis container.

**CI note.** Container tests run on Linux runners only, because GitHub's macOS runners have no Docker and Windows runners don't run Linux containers. Other OSes are covered by manual checkpoint testing.

---

## Phase 5: Diagnostics

### M20 — Profiling and diagnostics tools

**Goal.** Replace the Visual Studio Diagnostic Tools, the performance profiler and the memory analysis tools with open EventPipe-based equivalents. They work on all three OSes.

**Depends on.** M5, M16 (the dump view is shared). F3 helps for rich views.

**Scope.**
- **Diagnostic Tools window** during debugging or a normal run:
  - CPU usage, GC heap and working set over time
  - GC events and an exceptions timeline
  - a live view of `System.Runtime` counters
- **Counters monitor:**
  - attach to any .NET process by PID or name
  - custom EventCounters and `System.Diagnostics.Metrics` meters
  - export as CSV or JSON
- **CPU profiling:**
  - EventPipe sample profiler, started and stopped on a running process, or from launch to profile startup
  - **Views:** a top-down call tree, a bottom-up hot-path view, a **flame graph**, and caller/callee
  - **Filtering:** by time range and thread
  - **Source:** hot-line attribution in the editor gutter
- **Allocations:** allocation sampling events, shown as types and allocation stacks.
- **Memory:**
  - GC heap snapshots (gcdump), with type statistics and paths to roots
  - **diffs between two snapshots**, for leak hunting
  - full-dump object inspection through ClrMD (shared with M16)
- **Events viewer:**
  - open and save `.nettrace`, and view arbitrary EventSource and EventPipe events with filters
  - export to the Speedscope and Chromium trace formats
- **Activity views:**
  - HTTP requests (from `System.Net` events)
  - database commands, for providers that emit DiagnosticSource events
  - `Activity` spans in a timeline
- **BenchmarkDotNet integration:**
  - find `[Benchmark]` methods and run them with gutter glyphs
  - results table, and history across runs
- **Out of scope:** kernel-level native profiling (ETW kernel stacks, `perf_events`). It is a candidate.

**Architecture.**
- A new `daedalus-diaghost` handles trace processing (TraceEvent is heavy and needs JIT). It is supervised and lazy.
- Large traces stay in the host. The shell asks for aggregated, paged views, so large trees and flame graphs never cross the wire in full.
- The flame graph is a custom Avalonia control.

**Dependencies.**
- Microsoft.Diagnostics.NETCore.Client (MIT).
- Microsoft.Diagnostics.Tracing.TraceEvent (MIT; also check the licences of the native binaries the package contains).
- ClrMD (MIT).
- BenchmarkDotNet (MIT).

**ADRs.** "Diagnostics host and trace processing". "Profiler views".

**Fixtures.** `samples/Diagnostics/`: a CPU-bound app, an allocation-heavy app, a leaking app, an async-heavy app, and a benchmark project.

**Budgets to propose.**
- Sampling overhead in the CPU fixture: ≤ 10%.
- Opening a 1 GB `.nettrace` file: first view within a measured target. The UI is never blocked.
- Flame graph zoom and pan: no frame > 50 ms.

---

## Phase 6: UI frameworks

### M21 — Avalonia XAML editing and live previewer

**Goal.** First-class Avalonia development: rich `.axaml` editing and a live previewer. This is where ADR 0001's Avalonia choice pays off, because the previewer is .NET and Avalonia.

**Depends on.** M11 (XML infrastructure), M4 (builds), M14 (navigation).

**Scope.**
- **`.axaml` editing:**
  - **Completion:** elements, attributes, property values and enums, markup extensions, resources, style selectors, `x:Class` and `x:DataType`.
  - **Compiled bindings:** binding-path completion, which needs Roslyn type information.
  - **Diagnostics:** live parse errors, plus XAML compiler (XamlX) errors mapped back from builds.
  - **Navigation and editing:** go to definition (types, resources, styles), `x:Name` rename, hover, formatting.
- **Live previewer:**
  - Renders the edited file side by side with the editor, updated on edit (debounced).
  - Design-time data (`d:DataContext`, `d:DesignWidth`, `d:DesignHeight`), zoom, theme variant switching and scale.
  - Clicking in the preview selects the element in the XAML, and the reverse.
  - The preview needs a build of the project. Code-behind changes trigger an incremental rebuild.
- **Templates:** the Avalonia templates (MIT) through `dotnet new`.
- **Runtime XAML hot reload** for running apps: a candidate, which needs an open-source approach.

**Architecture.**
- **Previewer process:** the user's app runs out of process under Avalonia's designer host, with the remote protocol (`Avalonia.Remote.Protocol`, MIT): XAML updates go in, frames come out over a local transport. Out of process is required, because it runs user code and possibly a different Avalonia version from the shell.
- **Rendering:** the shell renders the frames in a tool window and forwards input.
- **XAML language service:** in the build host or a markup host, reusing the open-source Avalonia XAML completion engine from AvaloniaVS or the Avalonia VS Code extension (MIT; verify the licence and that it can be reused) where possible.

**ADRs.** "Avalonia previewer hosting". "XAML language service".

**Fixtures.** `samples/Avalonia/`: an MVVM app, a control library, styles and resources, compiled bindings, and design-time data.

**Budgets to propose.**
- Edit → preview updated: p95 ≤ 300 ms after the first render.
- Build → first preview frame: ≤ 3 s.

### M22 — .NET MAUI (exploratory)

**Status: needs a go/no-go decision (D6) before any planning.**

**Constraints to put to the maintainer.**
- MAUI has no Linux target.
- Its Windows target is WinUI, which the mission lists as a non-goal technology.
- iOS and Mac Catalyst builds need macOS and Xcode. Android needs the Android SDK and a JDK.
- MAUI apps run on the **Mono runtime** on Android and iOS, and **netcoredbg doesn't debug Mono**. A Mono soft-debugger DAP adapter (MonoDevelop's debugger libraries and `vscode-mono-debug` are MIT) would be needed.
- Microsoft's MAUI XAML language service, XAML Hot Reload and Live Preview are proprietary. We would have to build our own or accept gaps.

**If go, proposed scope.**
- **Phase 1:** the Android target on all three OSes:
  - device and emulator list (adb)
  - deploy and debug through a Mono debugger adapter
  - MAUI XAML editing (the M21 engine with MAUI schemas taken from assemblies)
  - C# Hot Reload, if the Mono runtime path supports it
- **Phase 2:** iOS and Mac Catalyst on macOS hosts.
- **Gaps:** the WinUI target (non-goal) and a XAML previewer (none exists in open source; candidate).

---

## Phase 7: Remote

### M23 — Remote development

**Goal.** Work on code that lives on another machine (SSH), in WSL or in a container, with local-feeling editing.

**Depends on.** F1 and F2 (required), M10 (terminals), M16 (remote attach), M19 (containers).

**Scope.**
- **Split architecture:**
  - **Local:** the shell keeps the UI and the editor buffers, so typing never crosses the network.
  - **Remote:** a version-locked **`daedalus-server`** runs on the remote machine and hosts:
    - the language and build hosts
    - debug adapters and test runs
    - terminals (PTYs)
    - workspace plugin hosts and the file-system provider
  - **Transport:** multiplexed JSON-RPC channels over a single stdio stream.
- **Targets:**
  - **SSH** to Linux, macOS and Windows machines.
  - **WSL** on Windows (`wsl.exe` launch).
  - **Dev Containers:** the `devcontainer.json` subset (image, Dockerfile, compose, features, ports, `postCreateCommand`). Use the Dev Containers CLI (MIT, Node, under D3), or implement the subset in C#. The ADR decides.
  - Attaching to a running container.
- **Server installation:**
  - The client copies or downloads a self-contained server bundle for the remote RID, verifies its checksum, and caches it in `~/.daedalus-server/<version>/`.
  - Updates happen with the client.
- **Port forwarding:** for web apps (M17) and debug sessions, with automatic detection of listening ports.
- **Plugin placement:** the manifest gets a field (for example `"runsOn": "ui" | "workspace"`) that says where a plugin runs.
- **Resilience:** reconnect after a network drop, resynchronizing the open documents and sessions.
- **Security:**
  - Use the **system OpenSSH client**, which honours `~/.ssh/config`, agents, `known_hosts` and keys. The IDE never sees passwords.
  - The server listens on stdio only, never on a network port.
  - The threat model is written in the ADR.

**ADRs.** "Remote development architecture". "Transport and multiplexing". "Dev Containers support".

**Fixtures and tests.**
- CI: SSH into a container on the Linux runner, with a full open/edit/build/debug cycle.
- WSL: manual on Windows at the checkpoint.
- A Dev Containers sample.

**Budgets to propose.**
- Typing: unchanged from local.
- Completion: ≤ local + one RTT + 20 ms at 50 and 150 ms RTT (simulated).
- Opening Solution50 remotely: ≤ local + 2 s at 50 ms RTT.
- Reconnect: ≤ 5 s.

---

## Phase 8: Ecosystem

### M24 — Plugin platform 1.0 and marketplace

**Goal.** Freeze plugin API 1.0, make plugin development pleasant, and give users a safe way to find and install plugins.

**Depends on.** F1, F3, and the proposed APIs proved by built-ins: test controllers (M8), language servers (M11), the visualizer API (M16), the SCM provider used by Git (M15), and placement (M23).

**Decisions needed.** D5 (distribution).

**Scope.**
- **Freeze API 1.0.** Promote the proved proposed APIs, and apply ADR 0003's post-1.0 policy (additive minor releases, the `DAEDALUS0001` deprecation diagnostics).
- **Contribution points to complete:**
  - **From earlier milestones:** languages, grammars and language servers (M11), test controllers (M8), visualizers (M16), SCM providers (M15).
  - **Debuggers:** DAP adapter contributions.
  - **Appearance:** themes and icon themes, snippets.
  - **UI:** quick picks and input boxes, progress, webviews or rich UI (F3), custom editors for file types.
  - **Platform:** file-system providers, authentication providers.
- **Developer experience:**
  - a `dotnet new daedalus-plugin` template
  - a `Daedalus.Plugins.Testing` package for headless plugin tests
  - "Run plugin in a development instance", with a debugger attached to its plugin host
  - an API reference generated from XML docs, plus samples
- **Distribution (D5).**
  - **Recommendation:** use **NuGet as the transport**. Use a dedicated package type, discover plugins through nuget.org search filtered by that type, and reuse author and repository signing.
  - **Alternative:** a static index hosted by the project.
  - **UI either way:** a plugin browser in the IDE, auto-update, enable and disable per workspace, and a "bisect plugins" troubleshooting command.
- **Trust:**
  - signature verification and a publisher identity
  - a **declared permissions** manifest (network, processes, file system outside the workspace), shown before install
  - **Be honest that .NET plugins can't be sandboxed in-process.** The permissions inform the user. They aren't enforced. WASM-based plugins with enforced capabilities are a candidate.
- **Non-.NET plugins:** the wire protocol is language-agnostic, but whether to ship an official non-.NET SDK (for example TypeScript) is a candidate decision.

### M25 — Personalization, settings sync and localization

**Scope.**
- **Profiles:** named sets of settings, key bindings, layout and enabled plugins. Switch profiles, export and import them.
- **Keymap presets:** Visual Studio, VS Code, Rider/ReSharper, Emacs. **Importers:** VS Code `settings.json` and `keybindings.json`, and the relevant parts of VS `.vssettings`.
- **Settings sync** with a **user-chosen backend**: a Git repository, a gist, or a synced folder. There is no Daedalus-hosted service, and nothing syncs until the user sets it up.
- **Localization:**
  - Move every UI string to resources (AOT-safe), with a pseudo-localization build and a test that catches hard-coded strings.
  - A community translation workflow (tool choice is D11).
  - An RTL layout review.
  - Plugin manifest localization (`%key%` placeholders with per-locale files).
- **Onboarding:** a first-run experience (theme, keymap, import), and tips, using the Welcome plugin.

---

## Cross-cutting tracks

### Plugin API evolution

New APIs start in `Daedalus.Plugins.Abstractions.Proposed` and are promoted in M24.

| Milestone | API additions |
|---|---|
| M8 | Test controller, `IBuild` (request builds), `IDebug` (start debug sessions), editor gutter decorations |
| M9 | Package and project-property queries (read only), the first rich-UI consumer (if F3 exists) |
| M10 | Terminals (create, send text, read output events), task-style "run command in terminal" |
| M11 | `contributes.languages`, `grammars`, `languageConfigurations`, `languageServers`; JSON schema associations |
| M14 | Snippet contributions, CodeLens providers (through LSP), code-action options dialogs (schema) |
| M15 | SCM provider API (Git built-in on it), authentication providers |
| M16 | Debugger visualizers, DAP adapter contributions |
| M17–M19 | Launch configuration providers, a port-forwarding API |
| M23 | `runsOn` placement, a file-system provider API |
| M24 | Freeze 1.0, permissions manifest, custom editors, themes and icon themes |

### Performance

- **Rider measurement is a prerequisite** for every "at least as good as Rider" budget. ADR 0002 has it pending. Do it before M8's budgets are proposed.
- Each milestone adds harness scenarios for its surfaces (listed in the milestone) and proposes budgets at its checkpoint. They become gates once the maintainer approves them.
- **Scaling:**
  - Add a **500-project solution** fixture (`FixtureGen`) by M14, and propose a large-solution budget.
  - Lightweight solution loading (show the structure and defer evaluation) and project unloading (M9) are the main tools.
- **Hosts and memory:** every new host has a lazy-start test. The idle-memory scenario must not change when a milestone adds a host.

### Accessibility

Every new surface must:
- be fully keyboard-operable
- name every element for UIA, AX and AT-SPI
- announce state changes (test outcomes, build results, debugger stops)
- respect high contrast

The terminal (M10), merge editor (M15), profiler views (M20) and previewer (M21) each need an explicit screen-reader design in their ADR. Automation-peer unit tests cover the trees exposed by custom controls.

### Security

- **Workspace trust** (F6). Applies to MSBuild evaluation, analyzers, source generators, F# type providers, workspace plugins, Hot Reload agents and the previewer.
- **Credential store abstraction** in `Daedalus.Platform` (M15 onward). Never store a secret in settings files.
- **Downloaded tools** (Node, FsAutoComplete, netcoredbg, the server bundle, grammars, schemas):
  - pinned versions, verified checksums (and signatures where they exist)
  - an offline path
  - a record in `THIRD-PARTY-NOTICES.md`
- **Network calls** are listed in the documentation and each can be turned off: NuGet, symbol servers, SchemaStore, update checks and hosting integrations.
- **Plugins:** signature checks and declared permissions (M24).
- **Remote:** a threat model (M23).

### Platforms

- **Targets:** Windows x64 and ARM64, macOS ARM64 and x64, Linux x64 and ARM64 (X11 and Wayland). Decide the ARM64 and Wayland status at M7. Each milestone lists any OS-specific behaviour.
- **Windows:** ConPTY versions and dev-certificate trust.
- **macOS:** the Keychain, notarization of downloaded tools, and the login-shell environment.
- **Linux:** libsecret availability, distribution trust stores, and Flatpak sandbox restrictions on spawning processes. The Flatpak build may need `--talk-name` and host-spawn permissions for the SDK, Docker and the terminal. Decide this in M7, and revisit it for M10, M19 and M23.

### Documentation

Each milestone updates:
- the user documentation for its features
- `docs/protocol.md` and `docs/plugin-protocol.md`
- the plugin changelog
- the **known gaps** page, with one entry per known gap and an issue link
- this roadmap's status table

### Fixtures

The fixtures to add under `samples/` are named in each milestone. Rules:
- They stay standalone (see `CLAUDE.md`).
- Large fixtures are generated by `Daedalus.Benchmarks.FixtureGen`.
- Network-dependent tests use local mock servers (NuGet, Git hosting, HTTP).

---

## Unscheduled candidates

These are VS or Rider features, or common requests, that aren't scheduled. Each needs a maintainer decision before it gets a milestone.

| Candidate | Notes |
|---|---|
| **AI-assisted coding** (D7) | Inline completions, chat, agents, an MCP client. If adopted: provider-agnostic, opt-in, bring your own key or a local model, no telemetry, and no proprietary Copilot components. Best built as a plugin over a provider API. |
| Real-time collaboration | A Live Share alternative. Large. Would need its own protocol and relay design. |
| Live Unit Testing | Test impact from coverage maps (M8b) plus continuous runs. Expensive. Belongs after M8 and M20. |
| Code maps and dependency diagrams, architecture validation | Project, namespace and type graphs, and layer rules as analyzers. |
| SQL database projects and a database explorer | Microsoft.Build.Sql and DacFx (verify their licences), plus a query editor. |
| Native AOT tooling | A trim and AOT warnings view, and a binary size analyzer (sizoscope, MIT). |
| Kubernetes tooling | Manifests, cluster explorer, debugging in a pod. Builds on M19 and M23. |
| Notebooks | `.ipynb` and Polyglot Notebooks through .NET Interactive (MIT). |
| Unity, Godot and other game-engine C# | Mono soft-debugger reuse from M22. Community plugins. |
| Uno Platform tooling | Community plugin. Uno is Apache-2.0. |
| Runtime XAML hot reload (Avalonia) | After M21. |
| Kernel-level profiling | ETW and `perf_events` with native stacks. After M20. |
| WebAssembly-sandboxed plugins | Enforced capabilities. After M24. |
| Library Manager and npm deep integration | After M18. |

## Out of scope

Restated from `ide-prompt.md`, plus what follows from it for the areas above:
- **Windows-only technologies:** WinForms, WPF and WinUI, including their designers, and therefore MAUI's Windows target (see M22). IIS and IIS Express. C++/CLI and COM.
- **Legacy projects:** non-SDK-style projects and .NET Framework-only run and debug scenarios.
- **Visual Studio compatibility:** VSIX compatibility and Visual Studio's extension APIs.
- **Proprietary components:** everything in the [known traps](#known-proprietary-traps) list.
- **Telemetry:** none on by default, in any feature or plugin we ship.

## Decision register

These are the maintainer decisions this plan depends on. Update the status column at each checkpoint.

| ID | Question | Needed by | Recommendation | Status |
|---|---|---|---|---|
| D1 | Switch document identity to URIs and add a VFS (F1, F2)? | M7 checkpoint | Yes, before going public and before API 1.0 | open |
| D2 | Rich plugin UI: webview, remote UI, or both (F3)? | M6 (ADR 0003) | Per the M6 ADR | open |
| D3 | Node.js policy (F4) | M18 (M11 if a Node LSP is wanted earlier) | Pinned, verified, on-demand download | open |
| D4 | Workspace trust and restricted mode (F6) | M7 checkpoint | Yes, with the public release | open |
| D5 | Plugin distribution channel | M24 | NuGet package type, with a plugin browser in the IDE | open |
| D6 | MAUI go/no-go, and the scope without WinUI | Before M22 | Decide after M21 | open |
| D7 | AI-assisted coding: whether, and in what form | Any time | Only as opt-in, provider-agnostic plugins | open |
| D8 | Hosting integrations (GitHub, GitLab, Azure DevOps) built in? | M15 | A built-in plugin, inactive until sign-in | open |
| D9 | Spell-check dictionaries and their licences | M14b | Only MIT/BSD-compatible dictionaries, or user-supplied ones | open |
| D10 | F# host: FsAutoComplete or our own FCS host | M13 | FsAutoComplete through the LSP bridge | open |
| D11 | Translation platform for localization | M25 | Decide at M25 | open |
| D12 | Licence of Aspire's orchestrator (DCP), and the IDE's relationship to it | M19 | Integrate through the IDE execution protocol only, never redistribute | open |
| D13 | Phase order | Each phase start | As listed in this document | open |
| D14 | Product 1.0 criteria (as opposed to preview releases) | After Phase 2 | Phases 1–2 complete, and plugin API 1.0 frozen early (pulled ahead of the marketplace work) | open |

## Change log

- 2026-10-08: first draft, written during M1.
