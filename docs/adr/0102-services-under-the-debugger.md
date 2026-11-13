# 0102. Services under the debugger: compose and Aspire

- **Status:** accepted (0.13, 2026-11-13)
- **Date:** 2026-11-13
- **Deciders:** maintainer (D55, settled at plan time as recommended, below)
- **Amends:** [ADR 0042](0042-container-tooling-and-publishing.md) (compose gets fast mode, and Debug Compose debugs every .NET service), [ADR 0043](0043-aspire-integration.md) (a debug session holds every project of an AppHost; the Aspire window debugs one resource alone), [ADR 0027](0027-web-front-end-depth.md) and [ADR 0039](0039-debugger-sessions.md) (a compound session holds 64 programs, and ends one of them alone)

## Context

Until M63 (roadmap [M63](../roadmap.md#m63--services-under-the-debugger-compose-and-aspire)):
- **Compose** ran from its images: Compose Up built every image, and Debug Compose Service attached the debugger to the one service the user picked, after it started (start-up breakpoints were missed). A code change was an image build.
- **Aspire**: an F5 session held the AppHost and seven projects (`CompoundDebugAdapter.MaxParts` was 8); more were refused.
- **Hot Reload** and **profiling** didn't reach into a container.

A solution of services needs every .NET service debugged at once, each one restarted, stopped, debugged or detached alone while the others run, and code changes reaching them without an image build. The single-project container path (M17a's fast mode, M58b's debugger bundle copied into the container) already did this for one project.

## D55: the maintainer's decisions (settled at plan time, as recommended)

| Question | Options | Verdict |
|---|---|---|
| Where the IDE's compose override lives, and how it's shown | Next to the compose file; in the solution's state folder; in a temporary folder | **`.codalus/state/<solution>/compose.override.yml`**: user state, never committed (the state folder's `.gitignore`), written on each run. The Containers window's **Show the IDE's Override** (and a command) opens it as a read-only document. |
| Is fast mode the default for compose | Yes, as the classic IDE's Debug configuration; no, opt in | **Yes.** The existing `containers.fastMode` setting (on by default) turns it off for compose too: the compose file then runs as written and Debug Compose attaches to one service, as before. |
| How profiling reaches a container | A collector copied in, as the debugger's bundle is; the diagnostics port's socket shared through a mount | **The diagnostics port's socket, shared through a mount** (`DOTNET_DiagnosticPorts` naming a socket in a mounted folder). Not built in M63 (below). |
| Does Aspire's seven-project limit go | Raise it; keep it | **It goes**: every project resource runs under the debugger. |

## Decision

### Compose in fast mode (`ComposeFastMode`, `ShellHost.Compose.cs`)

- **The app** is read as compose runs it: `compose -f … -p … config --format json` (the files merged, variables filled in, full paths). Where that doesn't answer (podman-compose has no JSON output), the files are read with `ComposeYaml`, a reader of the block YAML compose files are written in (no reflection, so the Native AOT shell takes it), and merged in their order. The engine is still the user's CLI, one process per operation.
- **A .NET service** has a `build` and a project: the one its label `dev.codalus.project` names (relative to the compose file), else the one project of the solution in its Dockerfile's folder, else the one project file there on disk. A `.dcproj`'s `DockerServiceName` is the startup service (debugged first). A Dockerfile without a `base` stage, or a service without a project, runs as the compose file says, as every service that isn't .NET does.
- **The override** gives each .NET service: `image: <project>-<service>:dev` (the user's own image is left alone), `build.target: base` (only the base stage is built: the base image, no SDK build), the project's Debug build output mounted at `/app` (and its static web assets outside it, read-only, with the container's manifest, as M17a's fast mode), `working_dir: /app`, `ASPNETCORE_ENVIRONMENT=Development` unless the compose file sets it, `command: []` when the compose file has a command, and the labels `dev.codalus.project` (the Containers window lists it as the IDE's) and `dev.codalus.fastMode` (`debug` or `run`). The entry point is `dotnet /app/App.dll` for a service run without the debugger, and `tail -f /dev/null` for a debugged one, whose program the debugger starts. Every compose command of the IDE's names the user's files (and their `.override`), then the IDE's. Paths are written quoted, in the long volume syntax (Windows paths stay whole), and through `WslLaunch.ToLinuxPath` for an engine in WSL.
- **Compose Up** builds each .NET service's project (as F5 does, `build.beforeRun`), writes the override and runs `up -d --build`. **Rerun:** each run-mode service's build stamp (its newest program file and their count) is kept; Compose Up again restarts (`compose restart`) only the services whose build changed, and a build of the solution or a project does too while the app runs. A configuration change is compose's to see: it recreates those services itself.
- **Compose Down** ends the debugged programs and runs `down`; the `:dev` images stay as a cache.

### Debug every service

- **Debug Compose** (formerly Debug Compose Service, the same command id) writes the override with every .NET service in debug mode, runs `up -d --build`, finds each service's container (its `container_name`, else compose's labels `com.docker.compose.project` and `.service`, which podman-compose writes too, else compose's names), copies the debugger into it (M58b's bundle, else netcoredbg; the container is the IDE's, so it doesn't ask) and starts `dotnet /app/App.dll` there under it. The services are programs of one independent compound session (M14c), so breakpoints in start-up code hit, each program has its row in the Processes window, and a stop in one pauses the others (M58). Paths map back to the solution's files (the build is this machine's, so its PDBs name them; `SourceMap` as for any remote target). The startup service's container gives the session its debug companion (display attributes).
- **One service alone** (`ComposeServiceAsync`; commands **Restart / Stop / Debug / Detach Compose Service…** on Debug → Other Debug Targets, with a picker, or the service's name as the argument):
  - *Restart*: a debugged service's project is built, its program ends and starts again in the session (`DebugService.RestartProgramAsync`: the session is held open with a reserved place even when it was the only program); any other service is restarted by compose (a fast-mode one on its new build; one whose container idles for a debugger that has gone goes back to running its build).
  - *Stop*: its program leaves the session (`DebugService.EndProgramAsync`, `CompoundDebugAdapter.EndPartAsync`: its adapter disconnected with its program and disposed; the session goes on while another program runs), then `compose stop`.
  - *Debug*: a fast-mode service run without the debugger is recreated idle (`up -d --no-deps`) and joins the session (`AddLaunch`), or starts one.
  - *Detach*: its program leaves the session, and the service is recreated running its build without the debugger. A program the debugger started can't outlive its debugger in the container (its output goes through it), so detaching is a restart without it.
- **A session holds 64 programs** (`CompoundDebugAdapter.MaxParts`, ids keep the program's index in their low bits): the adapters' ids are small counters, and thread ids at most 2^22 on Linux, so 64 times them stays an `int`. An ended program keeps its place, so restarts count.

### Hot Reload in containers

A fast-mode service runs this machine's build, so the language host's deltas are for its modules: its remote adapter is marked `LocalBuild` (`RemoteLaunch.LocalBuild`, `PathMappingDebugAdapter.LocalBuild`), and `DebugService.CodalusAdapters` includes it. Apply Code Changes (and a save, and Continue) reaches every debugged service under the Codalus debugger, through its debugger in the container (M55; the debugger's launch sets `DOTNET_MODIFIABLE_ASSEMBLIES=debug`). Other remote targets (SSH, WSL) stay out, as M58b left them.

### Aspire

- The limit goes with `MaxParts`: an AppHost and up to 63 projects (and their restarts) in one session.
- The Aspire window restarts one resource alone (its own command, as before: under F5 the restarted project joins the session again). Its new **Debug** button debugs one project resource alone while the AppHost runs without the debugger (Ctrl+F5): the debugger attaches to the program the IDE started for it (found by the resource's `project.path` property, else its name), and Detach leaves it running.

## Consequences

- A code change reaches a compose service without an image build: a build and a restart of that service (Compose Up again, or any build while the app runs), or Apply Code Changes while it is debugged.
- The override is the IDE's alone and is rewritten on each run; a user who wants other settings puts them in their own compose files, which come first.
- Switching a service between run and debug mode recreates its container (its entry point differs), as the classic IDE's fast mode does; after Stop Debugging, the debugged services' containers idle until Compose Up, Restart Compose Service or Compose Down.
- Tests: `ComposeFastModeTests` (the YAML reader, the engine's JSON, discovery, the override, the restart decision) and `ComposeShellTests`' stand-in engine (a script that records its commands: the override, rerun, a build's restart, one service stopped and restarted, the read-only document) run everywhere but Windows (the stand-in is a shell script); `ComposeShellTests`' real-engine test (both services debugged, a breakpoint in each, the back restarted alone while the front runs, an edit applied to the back) runs where an engine runs Linux containers (CI's Linux runner) and pulls only `mcr.microsoft.com/dotnet` images (`aspnet:10.0`, `runtime-deps:10.0`). `CompoundDebugAdapterTests` covers 20 programs and ending one alone; `AspireShellTests`' Ctrl+F5 test debugs the frontend alone.
- Deferred (`TODO(issue)`):
  - **Profiling in containers** (D55's diagnostics socket): the diagnostics host connects to a process by its id (`DiagnosticsClient(processId)`); a container's process needs a connection by a socket path (`DOTNET_DiagnosticPorts=/codalus/diag/<service>.sock` in a mounted folder, the runtime connecting to a listener of the host's, or listening itself), which the diagnostics protocol, the Performance Profiler's targets and the override don't have yet. Unix sockets in a bind mount work with Docker and Podman on Linux, not through Docker Desktop's file sharing on macOS and Windows. This also keeps M21a's remote profiling open for containers.
  - **Hot Reload without the debugger** in a container (the Hot Reload agent or `dotnet watch` in it): a service run without the debugger takes a code change by a build and a restart.
  - **Regular mode** (fast mode off) debugs one service, attached after its start, as before.
  - **Blazor WebAssembly** in a compose service or under Aspire: the browser's debugging (M16) needs the app's debug proxy reachable from the browser on this machine, which neither path sets up.
  - **The debug companion** reads the startup service's container only: display attributes of a type only another service has fall back to the debugger's values.
  - Podman parity is by construction (the same CLI commands, podman-compose's labels and names, the files read when `config --format json` doesn't answer); no Podman run was made for M63, and Windows and macOS with Docker Desktop are checked by hand (their runners have no engine).
