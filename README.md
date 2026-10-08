# Daedalus Studio

Daedalus Studio is an open-source (MIT), cross-platform IDE for .NET that runs natively on Windows, macOS and Linux. It aims to be a Visual Studio alternative with a VS-class experience: a rich semantic editor, a real project system, and integrated build and debugging.

> **Status: Milestone 2 (solution and project system).** The shell opens `.sln`/`.slnx` solutions and SDK-style projects, with a Solution Explorer, file operations, configurations, restore and new projects from templates. It can't edit code with language services yet (M3). The UI stack is Avalonia ([ADR 0001](docs/adr/0001-ui-stack.md)).

## Architecture in one paragraph

The UI shell (`daedalus`, Native AOT) is kept light and responsive. Heavy services run **out of process** and talk to the shell over JSON-RPC: a language service host (Roslyn), a build host (MSBuild), a debug adapter (netcoredbg over DAP) and plugin hosts. A crashing service never takes the UI down. It is restarted, and the user is notified. Contracts are LSP/DAP-shaped and extended where an IDE needs more ([docs/protocol.md](docs/protocol.md)). Plugins use a versioned, out-of-process API ([docs/plugins.md](docs/plugins.md)), and built-in features use it too where practical. Decisions are recorded as ADRs in [docs/adr](docs/adr).

## Repository layout

| Path | Contents |
|---|---|
| `src/Daedalus.Shell` | The IDE shell (`daedalus`): Avalonia views on top of the workbench services. |
| `src/Daedalus.Workbench`, `src/Daedalus.Platform` | UI-independent services: commands, key bindings, menus, settings, notifications, output, the plugin manager; OS conventions. |
| `src/Daedalus.Plugins.*`, `src/Plugins/` | The plugin SDK, protocol and host process, and the built-in plugins. |
| `src/Daedalus.Projects.*` | The build host (`daedalus-buildhost`): solutions, MSBuild evaluation, restore and project-file edits. |
| `src/Daedalus.Rpc`, `src/Daedalus.LanguageServices.*` | JSON-RPC and process supervision; the Roslyn language service host. |
| `spikes/` | The M0 Avalonia editor prototype, kept as the editor benchmark baseline until M3. |
| `tests/` | Unit, integration and headless UI tests. |
| `benchmarks/` | The `daedalus-bench` harness, the fixture generator and committed results. |
| `samples/` | Fixture solutions and the HelloWorld sample plugin. Large fixtures are generated, not committed. |
| `docs/` | ADRs and design documents. |

## Building

You need the .NET SDK pinned in `global.json` (10.0).

```bash
dotnet build Daedalus.slnx
```

```bash
dotnet test --solution Daedalus.slnx
```

```bash
dotnet run --project src/Daedalus.Shell
```

More commands, including publishing the shell and running the benchmarks, are in [CLAUDE.md](CLAUDE.md) and [benchmarks/README.md](benchmarks/README.md).

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
