# CLAUDE.md

Guidance for working in this repository. The mission and milestone plan live in `ide-prompt.md`, and decisions in `docs/adr/`.

## Build and test

```bash
dotnet build Daedalus.slnx                          # everything .NET (warnings are errors)
dotnet test --solution Daedalus.slnx                # unit + host integration + Avalonia headless UI test
dotnet run --project benchmarks/Daedalus.Benchmarks.FixtureGen -- all samples   # generate Large100k/ and Solution50/
```

Prototype B (Rust, in `spikes/gpui`):

```bash
cargo test                      # in spikes/gpui
cargo build --release
cargo deny check licenses sources
```

On Windows with several Visual Studio installs, Rust may pick an incomplete MSVC toolset (`LNK1104: msvcrt.lib`). Build from a "Developer PowerShell for VS" of a complete install. NativeAOT publish also needs `vswhere.exe` on `PATH` (`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`).

## Running the prototypes

```bash
dotnet publish src/Daedalus.LanguageServices.Host -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o artifacts/lshost
dotnet publish spikes/avalonia/Daedalus.Spike.Avalonia -c Release -r win-x64 -o artifacts/spike-a   # Native AOT
```

Copy `artifacts/lshost` to `artifacts/spike-a/lshost` (and `spike-b/lshost`). Alternatively, set `DAEDALUS_LSHOST` to a host `.exe` or `.dll`. Both prototypes accept `--solution <sln|slnx>`, `--open <file>`, `--bench typing|scroll` and `--exit-on interactive|highlight|done`. Prototype A also accepts `--screenshot <png>`. With `DAEDALUS_BENCH_EVENTS=<file>`, the prototypes write timestamped bench events, and `DAEDALUS_TRACE=1` makes the host log JSON-RPC errors to stderr.

## Benchmarks

See `benchmarks/README.md`. In short: `dotnet run --project benchmarks/Daedalus.Benchmarks.Harness -- list | run ... | report`. Scenarios that inject keystrokes require `--allow-input` and a machine that nobody is using at the time.

## Conventions

- Root namespace `Daedalus.*`. The product name is "Daedalus Studio", and the CLI is `daedalus`. Never use "Visual Studio" branding in the product.
- Central package management (`Directory.Packages.props`). Check and record the license of every new dependency in `THIRD-PARTY-NOTICES.md`.
- Heavy work runs out of process (language service host, build host, debug adapter, plugin hosts). The shell supervises hosts and restarts them on crashes (`LanguageHostClient`).
- Wire contracts follow LSP where it fits. Extensions use the `daedalus/` prefix and are documented in `docs/protocol.md`.
- Never block the UI thread, and make everything cancellable.
- Prototype A must stay Native-AOT clean: use compiled bindings, keep reflection-free view location, and use source-generated JSON.
- Fixtures under `samples/` are standalone. They do not inherit the repo's build settings.
- Deferred work gets a `TODO(issue)` and goes into the next checkpoint summary.
