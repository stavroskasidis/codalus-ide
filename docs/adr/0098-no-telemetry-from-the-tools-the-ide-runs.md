# 0098. No telemetry from the tools the IDE runs

- **Status:** accepted (0.13, 2026-11-13)
- **Date:** 2026-11-13
- **Deciders:** maintainer (D50, D51)
- **Milestone:** [M59](../roadmap.md#m59--no-telemetry-from-the-tools-the-ide-runs)

## Context

Codalus sends no telemetry and turns none on in what it ships, but some tools it starts send their own unless an environment variable stops them:

- **Avalonia's build task.** Every Avalonia 12 package brings `Avalonia.BuildServices` (11.3.2). Its `AvaloniaStats` target runs before `CoreCompile` in every build that isn't a design-time build, and its collector posts a hashed machine identifier and name, the hashed project name, the output type, target framework, runtime identifier, Avalonia version, Accelerate tier, development environment, OS and CI to Avalonia. Its only switch is the environment variable `AVALONIA_TELEMETRY_OPTOUT=1`; no MSBuild property turns it off. Codalus set it nowhere: not for the build host, where users' Avalonia projects build, not for `dotnet watch`, `test` or `ef`, and not for its own CI.
- **The .NET SDK.** Eight call sites set `DOTNET_CLI_TELEMETRY_OPTOUT=1` themselves; the build host's start didn't.
- **Microsoft.Testing.Platform** (`TESTINGPLATFORM_TELEMETRY_OPTOUT`) was set only by `eng/validate.sh`, and **`dotnet-svcutil`** (`DOTNET_SVCUTIL_TELEMETRY_OPTOUT`) only where it runs.

## Decisions (D51, settled at M59's plan)

| Question | Verdict |
|---|---|
| Does a value already in the user's environment win? | **Yes.** A user who opted in (`AVALONIA_TELEMETRY_OPTOUT=0`) stays opted in; the IDE only adds what isn't there. |
| Do the integrated terminal's shells get the variables? | **No.** The terminal is the user's own, and so are the programs they run and debug, their tasks (`tasks.json`), external tools, agent mode's commands (approved by the user and run as in the terminal) and external agents. |
| A project on an Avalonia Accelerate tier that refuses the opt-out | The task's own message ("Avalonia Accelerate Community requires telemetry. To opt out, please upgrade to a paid tier.", and the same for Trial) reaches the build output; the IDE adds a line after it saying the IDE set the variable and how to build: `AVALONIA_TELEMETRY_OPTOUT=0` in the environment Codalus starts with, or a tier that allows the opt-out. |

## Decision

- **One helper**, `ToolTelemetry` in `Codalus.Platform` (compiled into `Codalus.Rpc`, the build and REPL hosts and the test plugin as an internal copy, as `ProcessTree` is). `Variables` lists the four variables. `OptOut` adds each one a process's environment doesn't have; `OptOutOnTop` does the same for variables on top of an inherited environment (a `dotnet watch` plan); `PosixShellExports` writes `export NAME="${NAME-1}"` for a script run on another machine (remote test environments).
- **Hosts.** `ProcessLaunchInfo.ToolTelemetryOptOut`, set by `ProcessLaunchInfo.Host`: `LocalProcessLauncher` applies it where the process starts. Every host uses it: build, language, markup, debug companion, diagnostics, data, plugin, T4, REPL, the WinForms designer, FsAutoComplete and plugins' language servers. The previewer and the icon compiler apply it to their own start.
- **Remote.** `process/spawn` carries `toolTelemetryOptOut`, so the server adds the opt-outs on its machine, keeping a value that machine's environment has. The server's own environment stays as it is, so its terminals get nothing.
- **Tools.** `dotnet watch` (its plan's environment; the program it starts inherits them from it), `dotnet test` in remote test environments and Microsoft.Testing.Platform's test programs, `dotnet new` and its template commands, `dotnet tool`, `ef`, `restore`, `svcutil` and the web tools (through the build host's `DotnetProcess`, which the build host's own environment feeds), `dotnet nuget verify`, Check Environment, benchmarks, the web development certificate and installs the IDE runs.
- **Not the debug adapters.** The program a debug adapter launches inherits its environment, and the user's program gets nothing new. The adapters themselves run no .NET CLI command.
- **A source scan** (`ToolTelemetryTests`): every file in `src/` that starts a process uses the helper or is listed with why it doesn't, and a listed file that no longer needs it fails too.
- **The repository's own builds.** `eng/telemetry-optout.sh` exports the variables, sourced by `validate.sh`, `smoke-test.sh`, `wsl-test.sh`, `ci-test.sh`, `compat-containers.sh` and `package.sh`. The three workflows set them in their top-level `env`, and `.github/actions/setup` fails a job that runs without one. `CONTRIBUTING.md` says to set them for a build outside the scripts.

## The sweep

Every tool `src/` and `eng/` start, checked for telemetry of its own:

- **Turned off:** the .NET SDK (and MSBuild, NuGet, `dotnet new`, `tool`, `publish`, `watch` and `test` through it), Avalonia's build task, Microsoft.Testing.Platform, `dotnet-svcutil`.
- **None to turn off:** VSTest (its telemetry is opt-in, `VSTEST_TELEMETRY_OPTEDIN`), `dotnet-ef`, git, docker and podman's CLIs, ssh, Node and npm, netcoredbg and the Codalus debugger, FsAutoComplete, vscode-js-debug, lsof, launchctl.
- **The user's own:** what the project's npm scripts run (Angular's CLI asks first; Next.js's `NEXT_TELEMETRY_DISABLED`), external agents and MCP servers, external tools: the user's choice, as their programs are.

## Consequences

- A user's Avalonia project no longer reports its builds from Codalus, unless they opt in. A project on Avalonia Accelerate's Community or Trial tier fails to build until its user sets `AVALONIA_TELEMETRY_OPTOUT=0` or changes tier, and the build output says so.
- The build host's environment carries the variables, so every process it starts (MSBuild's nodes, `dotnet ef`, restore) has them.
- A new process start in `src/` fails the scan until it takes the helper or says why not.
