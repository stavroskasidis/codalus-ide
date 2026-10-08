# CLAUDE.md

Guidance for working in this repository. The mission and milestone plan live in `ide-prompt.md`, the post-MVP plan (M8 onward) in `docs/roadmap.md`, and decisions in `docs/adr/`.

## Build and test

```bash
dotnet build Daedalus.slnx                          # everything .NET (warnings are errors)
dotnet test --solution Daedalus.slnx                # unit + host integration + Avalonia headless UI test
dotnet run --project benchmarks/Daedalus.Benchmarks.FixtureGen -- all samples   # generate Large100k/ and Solution50/
```

NativeAOT publish on Windows needs `vswhere.exe` on `PATH` (`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`).

## Running the prototypes

```bash
dotnet publish src/Daedalus.LanguageServices.Host -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o artifacts/lshost
dotnet publish spikes/avalonia/Daedalus.Spike.Avalonia -c Release -r win-x64 -o artifacts/spike-a   # Native AOT
```

Copy `artifacts/lshost` to `artifacts/spike-a/lshost`. Alternatively, set `DAEDALUS_LSHOST` to a host `.exe` or `.dll`. The prototype accepts `--solution <sln|slnx>`, `--open <file>`, `--bench typing|scroll` and `--exit-on interactive|highlight|done`. It also accepts `--screenshot <png>`. With `DAEDALUS_BENCH_EVENTS=<file>`, it writes timestamped bench events, and `DAEDALUS_TRACE=1` makes the host log JSON-RPC errors to stderr.

## Benchmarks

See `benchmarks/README.md`. In short: `dotnet run --project benchmarks/Daedalus.Benchmarks.Harness -- list | run ... | report`. Scenarios that inject keystrokes require `--allow-input` and a machine that nobody is using at the time.

## Git

- Commit directly to `master`; no pull requests and no feature branches for now (the repo is private and pre-public).
- Commit messages carry **no** AI co-author or "Generated with" trailers.
- CI: every push to `master` builds and tests on Linux; the full Windows/macOS/Linux matrix and the benchmarks run nightly and on manual dispatch.

## Conventions

- Root namespace `Daedalus.*`. The product name is "Daedalus Studio", and the CLI is `daedalus`. Never use "Visual Studio" branding in the product.
- Central package management (`Directory.Packages.props`). Check and record the license of every new dependency in `THIRD-PARTY-NOTICES.md`.
- Heavy work runs out of process (language service host, build host, debug adapter, plugin hosts). The shell supervises hosts and restarts them on crashes (`LanguageHostClient`).
- Wire contracts follow LSP where it fits. Extensions use the `daedalus/` prefix and are documented in `docs/protocol.md`.
- Never block the UI thread, and make everything cancellable.
- The shell must stay Native-AOT clean: use compiled bindings, keep reflection-free view location, and use source-generated JSON.
- Fixtures under `samples/` are standalone. They do not inherit the repo's build settings.
- Deferred work gets a `TODO(issue)` and goes into the next checkpoint summary.
