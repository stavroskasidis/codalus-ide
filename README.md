# Daedalus Studio

Daedalus Studio is an open-source (MIT), cross-platform IDE for .NET that runs natively on Windows, macOS and Linux. It aims to be a Visual Studio alternative with a VS-class experience: a rich semantic editor, a real project system, and integrated build and debugging.

> **Status: Milestone 0.** The UI stack is being chosen by measurement. Nothing here is usable as an IDE yet.

## Architecture in one paragraph

The UI shell is kept light and responsive. Heavy services run **out of process** and talk to the shell over JSON-RPC: a language service host (Roslyn), a build host (MSBuild), a debug adapter (netcoredbg over DAP) and plugin hosts. A crashing service never takes the UI down. It is restarted, and the user is notified. Contracts are LSP/DAP-shaped and extended where an IDE needs more ([docs/protocol.md](docs/protocol.md)). Decisions are recorded as ADRs in [docs/adr](docs/adr).

## Repository layout

| Path | Contents |
|---|---|
| `src/` | Product code (`Daedalus.*`). In M0 this is the shared language service host, its protocol and its client. |
| `spikes/` | M0 UI-stack prototypes: `avalonia/` (A) and `gpui/` (B, Rust). The losing prototype is deleted after the M0 decision. |
| `tests/` | Unit, integration and headless UI tests. |
| `benchmarks/` | The `daedalus-bench` harness, the fixture generator and committed results. |
| `samples/` | Fixture solutions. Large fixtures are generated, not committed. |
| `docs/` | ADRs and design documents. |

## Building

You need the .NET SDK pinned in `global.json` (10.0). Prototype B also needs Rust (pinned in `spikes/gpui/rust-toolchain.toml`).

```bash
dotnet build Daedalus.slnx
```

```bash
dotnet test --solution Daedalus.slnx
```

More commands, including running the prototypes and benchmarks, are in [CLAUDE.md](CLAUDE.md) and [benchmarks/README.md](benchmarks/README.md).

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
