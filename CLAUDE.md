# CLAUDE.md

Guidance for working in this repository. The MVP (milestones M0–M7) is done; the milestone notes are in `docs/checkpoints/`, the post-MVP plan (M8 onward) in `docs/roadmap.md`, and decisions in `docs/adr/`.

## Build and test

```bash
dotnet build DaedalusStudio.slnx                          # everything .NET (warnings are errors)
dotnet test --solution DaedalusStudio.slnx                # unit + host integration + Avalonia headless UI test
dotnet run --project benchmarks/DaedalusStudio.Benchmarks.FixtureGen -- all samples   # generate Large100k/ and Solution50/
```

NativeAOT publish on Windows needs `vswhere.exe` on `PATH` (`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`).

## Running the shell

```bash
dotnet run --project src/DaedalusStudio.Shell -- --user-data-dir .work/user-data          # Debug; plugin host and built-in plugins are copied next to the build
dotnet publish src/DaedalusStudio.Shell -c Release -r win-x64 -o artifacts/shell           # Native AOT, with pluginhost/, buildhost/, lshost/ and plugins/ next to daedalus.exe
```

Arguments: an optional solution, project or folder to open. Options: `--user-data-dir <dir>` (settings, state and plugins under one folder), `--plugin-dir <dir>` (repeatable; load a plugin under development), `--enable-proposed-api`, `--dev`, `--version`, `--open <file>`, `--screenshot <png>`; for benchmarks `--bench typing|scroll` (on the `--open` file) and `--exit-on interactive|highlight|done|language`; for CI `--smoke-test <samples dir>` (below). With `DAEDALUS_BENCH_EVENTS=<file>` the shell writes timestamped bench events. `DAEDALUS_PLUGINHOST`, `DAEDALUS_BUILDHOST` and `DAEDALUS_LSHOST` point at a host `.exe` or `.dll`; `DAEDALUS_TRASH_DIR` sends deletes to a folder instead of the OS trash (tests). Plugin authoring: `docs/plugins.md`; build host protocol: `docs/project-protocol.md`.

## End-to-end smoke test

`eng/smoke-test.sh <daedalus executable>` runs the real, windowed app through one feature of each MVP milestone (`--smoke-test samples`, driven by `SmokeTest` in `src/DaedalusStudio.Shell/Infrastructure`): settings, themes, palette, plugins, opening and evaluating a solution, highlighting, diagnostics, completion, navigation, rename and formatting, build, run, debugging and Git, on a copy of `samples/BuildAndRun`. It writes `smoke-test.md`/`.json` and screenshots under the results folder (default `artifacts/smoke-test`) and fails when any step does. `ci.yml` runs it on the published Native AOT app on every OS it builds; `release.yml` runs `eng/smoke-test.sh --packages artifacts/packages` on the installed packages (zip, `.app` from the dmg, `.deb`). Add a step there when a feature should be proven in the shipped app, not just in the in-process headless tests. On Linux without a display it uses `xvfb-run`.

## Packaging

`eng/package.sh <version>` builds the release package for the current OS into `artifacts/packages` (ADR 0009): a zip and winget manifests on Windows, a `.app` in a `.dmg` on macOS (signed with `DAEDALUS_CODESIGN_IDENTITY`), and a tar.gz, `.deb` and AppImage on Linux. `release.yml` runs it on all three (manual dispatch).

## Language services

The C# language service host (`daedalus-lshost`, ADR 0005) speaks LSP over stdio (`docs/protocol.md`). It gets its projects from the build host's design-time builds, relayed by the shell (`LanguageService` in `src/DaedalusStudio.Workbench/Languages`). `DAEDALUS_LSHOST` points at a host `.exe` or `.dll`; otherwise the shell uses `lshost/` next to it. Fixture: `samples/LanguageFeatures`.

## Debugging

The shell debugs through netcoredbg (Samsung, MIT) over DAP (ADR 0007; client in `src/DaedalusStudio.Debugging`, sessions in `DebugService` in `src/DaedalusStudio.Workbench/Debugging`). Get it with `eng/get-netcoredbg.sh` (pinned, checksummed, into `.work/netcoredbg`); `DAEDALUS_NETCOREDBG` points at another one, and installed builds look in `debugger/` next to the shell. Debugger tests skip without it locally and fail without it in CI. Fixture: `samples/Debugging`.

## Git

Source control uses the git CLI (ADR 0008), not a library: `GitService` in `src/DaedalusStudio.Workbench/Git` runs one `git` process per operation and parses porcelain output only. `DAEDALUS_GIT` points at another git. Tests build throwaway repositories in temp folders (git writes read-only object files; clear the attribute before deleting).

## Benchmarks

See `benchmarks/README.md`. In short: `dotnet run --project benchmarks/DaedalusStudio.Benchmarks.Harness -- list | run ... | report | check | compare --baseline DIR`. Budgets live in `benchmarks/budgets.json`. Scenarios that inject keystrokes require `--allow-input` and a machine that nobody is using at the time.

## Linux and macOS validation

The development machine is Windows. Validate on Linux and macOS locally, and leave the hosted 3-OS matrix for the milestone dispatch (macOS minutes bill 10×).

- **When**: before every push that touches platform-sensitive code: process launching and paths, the hosts (`pluginhost/`, `buildhost/`, `lshost/`), the debugger, git, packaging (`eng/`), file watching, fonts and rendering, keyboard shortcuts, or `#if`/`OperatingSystem.Is*` branches. Run Linux (WSL) for every such change. Run macOS when the change is UI-, packaging- or debugger-related, or before a milestone checkpoint. Report each platform's result separately, with failing output. Never call a change cross-platform when a platform was skipped.
- **Linux (WSL 2, Ubuntu 24.04)**: `wsl -d Ubuntu -- bash eng/wsl-test.sh [--smoke] [dotnet test args]`. It mirrors the working tree, uncommitted changes included, into `~/daedalus-studio` inside WSL so Linux and Windows never share `bin/`/`obj/`, then runs `eng/validate.sh` there. The SDK pinned by `global.json` installs into `~/.dotnet` (the distro's `/usr/lib/dotnet` is too old). `--smoke` adds the Native AOT publish and `eng/smoke-test.sh` on the WSLg display; it needs `clang` and `zlib1g-dev` (`sudo apt install clang zlib1g-dev`, which the user must run because sudo asks for a password). Run it in the background; a full run takes several minutes. Don't build the Linux side straight from `/mnt/d`.
- **macOS (the user's Mac, Apple silicon, a Remote Control session)**: the user's clone is `/Volumes/M2/Projects/daedalus-studio`; leave its checkout alone. A Claude session started with `--name daedalus-mac --remote-control` runs there. With Remote Control on in this session, it shows up in `ListAgents` as `daedalus-mac`.
  1. Push the working tree with `bash eng/push-snapshot.sh`. This force-pushes a snapshot commit to the scratch branch `validate`, the one exception to "no feature branches". It leaves HEAD and the index alone, and costs no CI minutes because workflows only trigger on `master`.
  2. `SendMessage` to `daedalus-mac` with the snapshot commit. Ask it to `git fetch origin validate`, create or reuse a git worktree at `FETCH_HEAD` outside the user's checkout, run `bash eng/validate.sh [--smoke]` there, and reply with: the environment, the counts, the failing tests with their messages and our stack frames, the smoke-test steps that failed, and the artifact paths. The first run installs the SDK (10–20 min with `--smoke`).
  3. If `daedalus-mac` is missing or `offline`, say so and ask the user to start it. Don't fall back to the hosted macOS runner without asking. Nothing reports back when the Mac holds a message for approval, so silence isn't a result.
- `eng/validate.sh` is the shared entry point on Linux and macOS: SDK check, netcoredbg, restore, build, test (Release, as CI), and with `--smoke`, publish plus smoke test.

## Git

- Commit directly to `master`; no pull requests and no feature branches for now (the repo is private and pre-public).
- Commit messages carry **no** AI co-author or "Generated with" trailers.
- CI minutes are limited (private repo, free plan). Every push to `master` builds and tests on Linux (documentation-only pushes are skipped); the full Windows/macOS/Linux matrix and the benchmarks run only on manual dispatch, once per milestone checkpoint. Test locally first, push in batches, and don't push while a matrix run is in progress (it gets cancelled).

## Conventions

- Root namespace and assembly/package prefix `DaedalusStudio.*` (`Daedalus.*` is taken on nuget.org). The product name is "Daedalus Studio", and the CLI is `daedalus`. Never use "Visual Studio" branding in the product.
- Central package management (`Directory.Packages.props`). Check and record the license of every new dependency in `THIRD-PARTY-NOTICES.md`.
- Heavy work runs out of process (language service host, build host, debug adapter, plugin hosts). The shell supervises hosts with `DaedalusStudio.Rpc.SupervisedProcess` and restarts them on crashes. MSBuild only ever runs in the build host (ADR 0004), and only for trusted folders.
- Project-system fixtures: `samples/ProjectSystem` (`.sln` and `.slnx`); build and run: `samples/BuildAndRun` (ADR 0006). Tests copy them to a temp folder before changing anything.
- Wire contracts follow LSP where it fits. Extensions use the `daedalus/` prefix and are documented in `docs/protocol.md`; the plugin host protocol is in `docs/plugin-protocol.md`.
- The plugin API (`DaedalusStudio.Plugins.Abstractions`) is a public contract: changes follow ADR 0003's versioning policy and go into the changelog in `docs/plugins.md`.
- Shell views talk to `DaedalusStudio.Workbench` services; workbench events arrive on any thread, so views marshal through `Ui.Post`. Built-in features register through the same registries as plugins (`BuiltIns.cs`).
- Never block the UI thread, and make everything cancellable.
- The shell must stay Native-AOT clean: use compiled bindings, keep reflection-free view location, and use source-generated JSON.
- Fixtures under `samples/` are standalone. They do not inherit the repo's build settings.
- Deferred work gets a `TODO(issue)` and goes into the next checkpoint summary.
