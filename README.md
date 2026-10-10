<h1>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo/daedalus-wordmark-dark.svg">
    <img src="assets/logo/daedalus-wordmark-light.svg" alt="Daedalus Studio" width="420">
  </picture>
</h1>

Daedalus Studio is an open-source (MIT), cross-platform IDE for .NET that runs natively on Windows, macOS and Linux: a rich semantic editor, a real project system, and integrated build, debugging and Git.

> **Status: pre-release (0.x).** The MVP (milestones M0–M7) is done, and the post-MVP milestones are under way ([roadmap](docs/roadmap.md#status)). The first preview packages are prepared but not published yet. The UI stack is Avalonia ([ADR 0001](docs/adr/0001-ui-stack.md)).

## Features

- **Solutions and projects**:
  - `.sln` and `.slnx` solutions and SDK-style projects.
  - A Solution Explorer with file operations, references, dependencies and analyzers.
  - Configurations, multi-targeting, restore, and templates for new projects.
  - Project property pages that show where each value comes from, launch profiles, the Reference Manager, Configuration Manager, build dependencies, unloading projects, solution filters and SDK selection.
  - Folder trust: untrusted code doesn't run.
- **NuGet**: a package manager for projects and the solution (browse, installed with transitive packages, updates, consolidate), Central Package Management, package sources with source mapping, Pack, and .NET tools.
- **Editor**:
  - Tabs with preview and split views, multiple carets, folding, a minimap and find/replace.
  - C# on Roslyn with your analyzers and source generators: semantic highlighting, completion, signature help, Quick Info, live diagnostics with an Error List.
  - Navigation: Go to Definition and Implementation (into generated and decompiled code), Find All References, Go To All, Find in Files.
  - Refactoring: Rename, code fixes and refactorings (all with previews), and `.editorconfig` formatting.
- **Testing**: a Test Explorer for xUnit, NUnit, MSTest and TUnit on VSTest and Microsoft.Testing.Platform, test margins in the editor, playlists, and code coverage with Coverlet.
- **Build and run**: Build, Rebuild and Clean of the solution or a project (MSBuild, cancellable, errors in the Error List). Run the startup project with its launch profile (Ctrl+F5).
- **Debugging** on netcoredbg:
  - launch (F5) and attach
  - breakpoints with conditions, hit counts and logpoints
  - stepping and threads
  - Call Stack, Locals, Autos, Watch, data tips, a Debug Console, and exception settings
  - Run to Cursor and Run to Click; function, temporary and dependent breakpoints with labels, import and export; QuickWatch and editing values; Text, JSON, XML and HTML visualizers; PerfTips; the Threads and Modules windows
- **Git** through the git CLI:
  - a Changes view with per-file and per-hunk staging
  - commit and amend
  - branches: create, switch, merge
  - fetch, pull and push
  - side-by-side and inline diffs, gutter change markers, and blame
- **Terminal and C# Interactive**: an integrated terminal (your shells, splits, shell integration with clickable `file:line` links, `vim`-capable emulation) that can also run your console apps, and a C# REPL with `#r "nuget:"`, Send to Interactive and Initialize Interactive with Project.
- **Navigation and inline information**: Peek Definition, Go to Base, Call and Type Hierarchy, Find All References with read/write classification, the Document Outline, a Code Definition window, a navigation bar and sticky scroll; inlay hints, CodeLens, an inheritance margin, bracket pair colors and an overview ruler; library code shown from its embedded source or Source Link before decompiling.
- **Editing and analysis**: snippets (VS Code's format, Visual Studio's imported, contributed by plugins), Insert Snippet and Surround With, duplicate and move lines, Expand Selection, a clipboard ring, Paste JSON as Classes, Code Cleanup and format on save, refactorings with options (Generate Constructor, Equals and GetHashCode, Overrides, Extract Interface, Change Signature), configuring severities in `.editorconfig`, AutoRecover and local history, bookmarks, the Task List, Code Metrics, a Syntax Visualizer, Compare Files and an image viewer.
- **A classic IDE's look and feel**: Blue, Light and Dark themes, the menu and a search box (Ctrl+Q) in the title bar, a standard toolbar with the configuration, startup project and Start, a status bar colored by state, the Solution Explorer on the right with search, an Options tree, and the classic keyboard scheme by default (VS Code's as an option).
- **Git depth**: a history graph with filters, file and line history, a 3-way merge editor, continue/skip/abort for merges, rebases, cherry-picks and reverts, interactive rebase, stash, tags, remotes, per-line staging, clone and init, and `daedalus merge`/`--wait` as git's merge tool and editor.
- **Web**: an Endpoints Explorer, `.http` files with environments and an HTTP Response window, the ASP.NET Core environment selector, the HTTPS development certificate and user secrets; Hot Reload through `dotnet watch`; Razor and Blazor editing with C# features inside `.razor` and `.cshtml`; and HTML, CSS, JavaScript and TypeScript language servers on a Node.js runtime downloaded on first use.
- **Other languages**: highlighting for about forty languages from TextMate grammars; MSBuild editing with evaluated values on hover; JSON and YAML with schemas; `.editorconfig`; a Markdown preview; and any LSP language server through a plugin, without code.
- **Extensible**: a versioned, out-of-process plugin API ([docs/plugins.md](docs/plugins.md)).
- **Private**: no telemetry. Crash reports are local and opt-in.

## Documentation

- [User guide](docs/user-guide/README.md): installing, solutions, NuGet, editing, build and run, debugging, web development, testing, Git, terminal and C# Interactive, settings, troubleshooting.
- [Roadmap](docs/roadmap.md): the post-MVP milestones and the decisions behind them.
- [Writing plugins](docs/plugins.md).
- [Architecture decisions](docs/adr), [accessibility](docs/accessibility.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md).

## Architecture in one paragraph

The UI shell (`daedalus`, Native AOT) is kept light and responsive. Heavy services run **out of process** and talk to the shell over JSON-RPC: a language service host (Roslyn), a build host (MSBuild), a markup host (JSON, XML and YAML), a C# Interactive host, a debug adapter (netcoredbg over DAP), plugin hosts, and language servers for other languages. A crashing service never takes the UI down. It is restarted, and the user is notified. Contracts are LSP/DAP-shaped and extended where an IDE needs more ([docs/protocol.md](docs/protocol.md)). Plugins use a versioned, out-of-process API ([docs/plugins.md](docs/plugins.md)), and built-in features use it too where practical. Decisions are recorded as ADRs in [docs/adr](docs/adr).

## Repository layout

| Path | Contents |
|---|---|
| `src/DaedalusStudio.Shell` | The IDE shell (`daedalus`): Avalonia views on top of the workbench services. |
| `src/DaedalusStudio.Workbench`, `src/DaedalusStudio.Platform` | UI-independent services: commands, key bindings, menus, settings, notifications, output, the plugin manager; OS conventions. |
| `src/DaedalusStudio.Plugins.*`, `src/Plugins/` | The plugin SDK, protocol and host process, and the built-in plugins. |
| `src/DaedalusStudio.Projects.*` | The build host (`daedalus-buildhost`): solutions, MSBuild evaluation, restore and project-file edits. |
| `src/DaedalusStudio.Rpc`, `src/DaedalusStudio.LanguageServices.*` | JSON-RPC and process supervision; the C# language service host (`daedalus-lshost`, Roslyn over LSP) and its client. |
| `src/DaedalusStudio.Debugging` | The Debug Adapter Protocol client for netcoredbg. |
| `src/DaedalusStudio.Markup.Host` | The markup host (`daedalus-markuphost`): JSON with schemas, XML and MSBuild, YAML and `.editorconfig`. |
| `src/DaedalusStudio.Interactive.*` | The C# Interactive host (`daedalus-replhost`) and its protocol. |
| `src/DaedalusStudio.Terminal` | The terminal's VT engine (the pseudo-terminals are in `DaedalusStudio.Platform`). |
| `eng/` | Scripts: fetching netcoredbg, building release packages (`package.sh`) and their templates. |
| `tests/` | Unit, integration and headless UI tests. |
| `benchmarks/` | The `daedalus-bench` harness, the fixture generator and committed results. |
| `samples/` | Fixture solutions and the HelloWorld sample plugin. Large fixtures are generated, not committed. |
| `docs/` | ADRs, protocols, the roadmap, milestone checkpoints and the user guide. |

## Building

You need the .NET SDK pinned in `global.json` (10.0) and git. For the debugger, run `eng/get-netcoredbg.sh` once.

```bash
dotnet build DaedalusStudio.slnx
```

```bash
dotnet test --solution DaedalusStudio.slnx
```

```bash
dotnet run --project src/DaedalusStudio.Shell
```

More commands, including publishing the shell and running the benchmarks, are in [CLAUDE.md](CLAUDE.md) and [benchmarks/README.md](benchmarks/README.md). `eng/package.sh <version>` builds the release package for your OS ([ADR 0009](docs/adr/0009-packaging-and-release.md)).

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
