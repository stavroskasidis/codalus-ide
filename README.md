<h1>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo/daedalus-wordmark-dark.svg">
    <img src="assets/logo/daedalus-wordmark-light.svg" alt="Daedalus Studio" width="420">
  </picture>
</h1>

Daedalus Studio is an open-source (MIT), cross-platform IDE for .NET that runs natively on Windows, macOS and Linux: a rich semantic editor, a real project system, and integrated build, debugging and Git.

> **Status: MVP (Milestone 7), pre-release.** The first preview packages are prepared but not published yet. The UI stack is Avalonia ([ADR 0001](docs/adr/0001-ui-stack.md)).

## Features

- **Solutions and projects**:
  - `.sln` and `.slnx` solutions and SDK-style projects.
  - A Solution Explorer with file operations, references, dependencies and analyzers.
  - Configurations, multi-targeting, restore, and templates for new projects.
  - Folder trust: untrusted code doesn't run.
- **Editor**:
  - Tabs with preview and split views, multiple carets, folding, a minimap and find/replace.
  - C# on Roslyn with your analyzers and source generators: semantic highlighting, completion, signature help, Quick Info, live diagnostics with an Error List.
  - Navigation: Go to Definition and Implementation (into generated and decompiled code), Find All References, Go To All, Find in Files.
  - Refactoring: Rename, code fixes and refactorings (all with previews), and `.editorconfig` formatting.
- **Build and run**: Build, Rebuild and Clean of the solution or a project (MSBuild, cancellable, errors in the Error List). Run the startup project with its launch profile (Ctrl+F5).
- **Debugging** on netcoredbg:
  - launch (F5) and attach
  - breakpoints with conditions, hit counts and logpoints
  - stepping and threads
  - Call Stack, Locals, Autos, Watch, data tips, a Debug Console, and exception settings
- **Git** through the git CLI:
  - a Changes view with per-file and per-hunk staging
  - commit and amend
  - branches: create, switch, merge
  - fetch, pull and push
  - side-by-side and inline diffs, gutter change markers, and blame
- **Extensible**: a versioned, out-of-process plugin API ([docs/plugins.md](docs/plugins.md)).
- **Private**: no telemetry. Crash reports are local and opt-in.

## Documentation

- [User guide](docs/user-guide/README.md): installing, solutions, editing, build and run, debugging, Git, settings, troubleshooting.
- [Writing plugins](docs/plugins.md).
- [Architecture decisions](docs/adr), [accessibility](docs/accessibility.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md).

## Architecture in one paragraph

The UI shell (`daedalus`, Native AOT) is kept light and responsive. Heavy services run **out of process** and talk to the shell over JSON-RPC: a language service host (Roslyn), a build host (MSBuild), a debug adapter (netcoredbg over DAP) and plugin hosts. A crashing service never takes the UI down. It is restarted, and the user is notified. Contracts are LSP/DAP-shaped and extended where an IDE needs more ([docs/protocol.md](docs/protocol.md)). Plugins use a versioned, out-of-process API ([docs/plugins.md](docs/plugins.md)), and built-in features use it too where practical. Decisions are recorded as ADRs in [docs/adr](docs/adr).

## Repository layout

| Path | Contents |
|---|---|
| `src/Daedalus.Shell` | The IDE shell (`daedalus`): Avalonia views on top of the workbench services. |
| `src/Daedalus.Workbench`, `src/Daedalus.Platform` | UI-independent services: commands, key bindings, menus, settings, notifications, output, the plugin manager; OS conventions. |
| `src/Daedalus.Plugins.*`, `src/Plugins/` | The plugin SDK, protocol and host process, and the built-in plugins. |
| `src/Daedalus.Projects.*` | The build host (`daedalus-buildhost`): solutions, MSBuild evaluation, restore and project-file edits. |
| `src/Daedalus.Rpc`, `src/Daedalus.LanguageServices.*` | JSON-RPC and process supervision; the C# language service host (`daedalus-lshost`, Roslyn over LSP) and its client. |
| `src/Daedalus.Debugging` | The Debug Adapter Protocol client for netcoredbg. |
| `eng/` | Scripts: fetching netcoredbg, building release packages (`package.sh`) and their templates. |
| `tests/` | Unit, integration and headless UI tests. |
| `benchmarks/` | The `daedalus-bench` harness, the fixture generator and committed results. |
| `samples/` | Fixture solutions and the HelloWorld sample plugin. Large fixtures are generated, not committed. |
| `docs/` | ADRs and design documents. |

## Building

You need the .NET SDK pinned in `global.json` (10.0) and git. For the debugger, run `eng/get-netcoredbg.sh` once.

```bash
dotnet build Daedalus.slnx
```

```bash
dotnet test --solution Daedalus.slnx
```

```bash
dotnet run --project src/Daedalus.Shell
```

More commands, including publishing the shell and running the benchmarks, are in [CLAUDE.md](CLAUDE.md) and [benchmarks/README.md](benchmarks/README.md). `eng/package.sh <version>` builds the release package for your OS ([ADR 0009](docs/adr/0009-packaging-and-release.md)).

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
