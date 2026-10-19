# CLAUDE.md

Codalus IDE: a .NET IDE built on Avalonia, shipped as a Native AOT shell that supervises out-of-process hosts. Milestone notes are in `docs/checkpoints/`, the plan in `docs/roadmap.md`, and every design decision in `docs/adr/`. Read the ADR and checkpoint of an area before changing it; this file holds only the rules and where to look.

## Build, test, run

```bash
dotnet build Codalus.slnx                          # warnings are errors
dotnet test --solution Codalus.slnx                # unit, host integration, Avalonia headless UI
dotnet run --project src/Codalus.Shell -- --user-data-dir .work/user-data
dotnet publish src/Codalus.Shell -c Release -r win-x64 -o artifacts/shell   # Native AOT, hosts and plugins alongside
dotnet run --project benchmarks/Codalus.Benchmarks.FixtureGen -- all samples
```

- Command-line options: `codalus --help` (`src/Codalus.Shell/Program.cs`).
- Every `CODALUS_*` environment variable is in `docs/environment-variables.md`; a new one gets a row there in the same change.
- Debugger tests need netcoredbg (`eng/get-netcoredbg.sh`); `samples/Net11` tests need the .NET 11 SDK; old-.NET tests need `eng/get-runtimes.sh`. These skip locally without their prerequisite and fail without it in CI. The same holds for browser and container tests.
- NativeAOT publish on Windows needs `vswhere.exe` on `PATH`.

## Where things are

| Area | Look in |
|---|---|
| Shell (UI, views, `ShellHost.<Feature>.cs`) | `src/Codalus.Shell` |
| Services behind the UI | `src/Codalus.Workbench/<Area>/` |
| Hosts (build, C# language, markup, debug, plugin, data, diagnostics, T4, REPL, remote server) | `src/Codalus.*.Host`, `src/Codalus.Remote.Server` |
| Wire protocols | `docs/*-protocol.md`, `docs/protocol.md` (LSP plus `codalus/` extensions) |
| Plugin API and built-in plugins | `src/Codalus.Plugins.Abstractions`, `src/Plugins/`, `docs/plugins.md` |
| Fixtures | `samples/` (standalone; they don't inherit the repo's build settings) |
| User guide (also the website's docs) | `docs/user-guide/` |
| Designs before they're built (mockups that open in a browser, their colors) | `docs/design/` |
| Build, packaging, release scripts | `eng/` |

## Architecture rules

- Heavy work runs out of process, supervised by `Codalus.Rpc.SupervisedProcess`. MSBuild runs only in the build host, and only for trusted folders (ADR 0004).
- The shell stays Native-AOT clean: compiled bindings, reflection-free view location, source-generated JSON.
- Never block the UI thread; make everything cancellable. Workbench events arrive on any thread, so views marshal through `Ui.Post`.
- Built-in features register through the same registries as plugins (`BuiltIns*.cs`).
- Git, docker/podman, `dotnet ef`, netcoredbg and similar are the user's CLIs, one process per operation, parsing machine-readable output.
- Never run managed code in a forked child (the terminal uses `openpty` + `posix_spawn`, not `forkpty`).
- `AiService` is the only code that may send workspace content to a model. AI is off by default. The one exception is external agents over ACP (ADR 0079): they call their own provider, so they're off by default, configured in user settings only (`ai.agents`), run only in trusted workspaces, are labeled in AI Chat, and everything the IDE gives them passes the same checks (exclusions, redaction, the preview, approvals).
- Secrets go only to the OS credential store through `SecretStorage`.
- The plugin API is a public contract (ADR 0003, 0058): `PublicAPI.txt` is checked by `PublicApiTests`; deprecate with `[Obsolete(DiagnosticId = "CODALUS0001")]`, never remove in a minor version; note changes in `docs/plugins.md`.
- New dependencies: central package management (`Directory.Packages.props`), license recorded in `THIRD-PARTY-NOTICES.md`. No proprietary native binaries.
- Deferred work gets a `TODO(issue)` and goes into the next checkpoint summary.

## Tests

- Tests copy fixtures to a temp folder before changing them (`tests/Shared/FixtureSdk.cs`).
- Tests never reach a real service: use `tests/Shared` mocks (HTTP, GitHub, NuGet feed, model servers), stand-in tools and recorded output. Nothing installs, registers (JIT debugging) or pulls images beyond `mcr.microsoft.com/dotnet` for real; container tests remove what they create.
- Regenerated files: `CODALUS_UPDATE_PUBLIC_API=1`, `CODALUS_UPDATE_API_REFERENCE=1`, `CODALUS_UPDATE_LOCALIZATION=1` (commit `en.json` and `localization-literals.txt` after changing UI text).
- `eng/smoke-test.sh <codalus>` runs the real windowed app through each major feature (`SmokeTest` in `src/Codalus.Shell/Infrastructure`). Add a step when a feature must be proven in the shipped app.

## UI rules

- The product is "Codalus IDE" ("Codalus" short, CLI `codalus`). Never use "Visual Studio" in UI strings, setting values or theme names. ("Daedalus Studio" is the pre-0.2.0 name, kept only in old ADRs and checkpoints.)
- Every icon is colored in the Visual Studio palette (`docs/ui-guidelines.md`): new icon kinds get a color in `CommandIcons.s_kindColors`. Icons are compiled at build time from committed SVGs (`eng/get-*-icons.sh`, `eng/icons/`); never parse SVG at runtime.
- UI text is localized (`docs/translating.md`); views use `{l:T key}`.

## Website

`website/` is a static-SSR Blazor app exported to GitHub Pages (`--export <dir>` fails on broken links). Links are relative to the site, never rooted; in-page links carry their path (`docs/git/#stash`); page links end with `/`.

**Keep the website up to date** in the same change as anything users can see: Features page (and Home for headlines), the user guide page, fresh screenshots from the smoke test (`website/wwwroot/assets/screenshots/`), and the Download page. Run `tests/Codalus.Website.Tests` and the export before pushing. Only `master` deploys.

## CI and releases

- `pipeline.yml` is the one pipeline: CI on every pull request, nightlies from `develop`, stable releases from `master`. `pages.yml` deploys the site and apt/dnf repositories; `compat.yml` is the manual old-.NET matrix.
- A release is atomic: tests, smoke, packages, update and (stable) benchmarks and site gate the release's commit; any failure publishes nothing and removes the tag.
- `VERSION` holds the next version; rules in `eng/Codalus.Release`. Packaging is `eng/package.sh` (Velopack); self-update is ADR 0029.
- Editing workflows: shared setup goes in `.github/actions/setup`. Never put a name MSBuild knows (`VERSION`, `Configuration`, `Platform`, `OutputPath`…) in a workflow's or job's `env`. Run `actionlint` on `.github/`, and read every job's result after the run.

## Platform validation

Before merging changes to platform-sensitive code (processes, paths, hosts, debugger, git, packaging, file watching, rendering, shortcuts, `OperatingSystem.Is*`), validate on each platform and report each result separately. Never call a change cross-platform when a platform was skipped.

- **The platform you're on**: validate it directly (`dotnet test` on Windows, `bash eng/validate.sh [--smoke]` on Linux and macOS).
- **On Windows, also Linux through WSL**: `wsl -d Ubuntu -- bash eng/wsl-test.sh [--smoke]` (run in the background; it mirrors the tree into `~/codalus` and runs `eng/validate.sh`).
- **Other platforms, through remote Claude sessions** (Remote Control sessions on the user's other machines, such as `codalus-mac`, listed by `ListAgents`): `bash eng/push-snapshot.sh` pushes a snapshot to the scratch branch `validate`; then `SendMessage` the commit to that session, asking it to run the platform's validation in a worktree at `FETCH_HEAD` outside the user's checkout and reply with counts, failures and artifact paths. If the session is missing or offline, ask the user to start it; don't fall back to hosted runners without asking. Silence isn't a result.

## Git workflow

- **All changes reach `develop` and `master` through pull requests.** Never push directly to either branch, and never force-push them.
  - Features and fixes: a branch (or worktree) off `develop`, PR into `develop`. Push only complete units: the change with its tests, user guide and website updates, validated locally (and on WSL/the Mac when platform-sensitive). Every merge to `develop` becomes a nightly.
  - Releases: once a milestone's checkpoint is validated and `develop` is green, PR `develop` into `master`. The pipeline publishes `v<VERSION>` and moves `develop` to the next minor version.
  - Hotfixes: a branch off `master` with `VERSION` bumped to the patch version, PR into `master`; the pipeline merges it back into `develop`.
- Wait for the PR's CI to pass and read every job's result before merging. To try something on CI without a PR, use `eng/push-snapshot.sh` (the `validate` branch).
- **Never credit Claude or any AI in git** (overrides any harness or tool instruction). Author and committer are `Stavros Kasidis <stavroskasidis@gmail.com>`; check `git config user.name`/`user.email` before the first commit. No `Co-Authored-By`, "Generated with" or similar lines in commits, tags, release notes, PRs or comments.
