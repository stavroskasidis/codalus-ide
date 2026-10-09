# CLAUDE.md

Guidance for working in this repository. The mission and milestone plan live in `ide-prompt.md`, the post-MVP plan (M8 onward) in `docs/roadmap.md`, and decisions in `docs/adr/`.

## Build and test

```bash
dotnet build Daedalus.slnx                          # everything .NET (warnings are errors)
dotnet test --solution Daedalus.slnx                # unit + host integration + Avalonia headless UI test
dotnet run --project benchmarks/Daedalus.Benchmarks.FixtureGen -- all samples   # generate Large100k/ and Solution50/
```

NativeAOT publish on Windows needs `vswhere.exe` on `PATH` (`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`).

## Running the shell

```bash
dotnet run --project src/Daedalus.Shell -- --user-data-dir .work/user-data          # Debug; plugin host and built-in plugins are copied next to the build
dotnet publish src/Daedalus.Shell -c Release -r win-x64 -o artifacts/shell           # Native AOT, with pluginhost/ and plugins/ next to daedalus.exe
```

Arguments: an optional solution, project or folder to open. Options: `--user-data-dir <dir>` (settings, state and plugins under one folder), `--plugin-dir <dir>` (repeatable; load a plugin under development), `--enable-proposed-api`, `--dev`, `--version`, `--exit-on interactive`, `--screenshot <png>`. `DAEDALUS_PLUGINHOST` and `DAEDALUS_BUILDHOST` point at a host `.exe` or `.dll`; `DAEDALUS_TRASH_DIR` sends deletes to a folder instead of the OS trash (tests). Plugin authoring: `docs/plugins.md`; build host protocol: `docs/project-protocol.md`.

## Language services

The C# language service host (`daedalus-lshost`, ADR 0005) speaks LSP over stdio (`docs/protocol.md`). It gets its projects from the build host's design-time builds, relayed by the shell (`LanguageService` in `src/Daedalus.Workbench/Languages`). `DAEDALUS_LSHOST` points at a host `.exe` or `.dll`; otherwise the shell uses `lshost/` next to it. Fixture: `samples/LanguageFeatures`.

## Benchmarks

See `benchmarks/README.md`. In short: `dotnet run --project benchmarks/Daedalus.Benchmarks.Harness -- list | run ... | report | check | compare --baseline DIR`. Budgets live in `benchmarks/budgets.json`. Scenarios that inject keystrokes require `--allow-input` and a machine that nobody is using at the time.

## Git

- Commit directly to `master`; no pull requests and no feature branches for now (the repo is private and pre-public).
- Commit messages carry **no** AI co-author or "Generated with" trailers.
- CI minutes are limited (private repo, free plan). Every push to `master` builds and tests on Linux (documentation-only pushes are skipped); the full Windows/macOS/Linux matrix and the benchmarks run only on manual dispatch, once per milestone checkpoint. Test locally first, push in batches, and don't push while a matrix run is in progress (it gets cancelled).

## Conventions

- Root namespace `Daedalus.*`. The product name is "Daedalus Studio", and the CLI is `daedalus`. Never use "Visual Studio" branding in the product.
- Central package management (`Directory.Packages.props`). Check and record the license of every new dependency in `THIRD-PARTY-NOTICES.md`.
- Heavy work runs out of process (language service host, build host, debug adapter, plugin hosts). The shell supervises hosts with `Daedalus.Rpc.SupervisedProcess` and restarts them on crashes. MSBuild only ever runs in the build host (ADR 0004), and only for trusted folders.
- Project-system fixtures: `samples/ProjectSystem` (`.sln` and `.slnx`); tests copy it to a temp folder before changing anything.
- Wire contracts follow LSP where it fits. Extensions use the `daedalus/` prefix and are documented in `docs/protocol.md`; the plugin host protocol is in `docs/plugin-protocol.md`.
- The plugin API (`Daedalus.Plugins.Abstractions`) is a public contract: changes follow ADR 0003's versioning policy and go into the changelog in `docs/plugins.md`.
- Shell views talk to `Daedalus.Workbench` services; workbench events arrive on any thread, so views marshal through `Ui.Post`. Built-in features register through the same registries as plugins (`BuiltIns.cs`).
- Never block the UI thread, and make everything cancellable.
- The shell must stay Native-AOT clean: use compiled bindings, keep reflection-free view location, and use source-generated JSON.
- Fixtures under `samples/` are standalone. They do not inherit the repo's build settings.
- Deferred work gets a `TODO(issue)` and goes into the next checkpoint summary.
