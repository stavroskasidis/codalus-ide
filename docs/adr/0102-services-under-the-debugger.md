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
| How profiling reaches a container | A collector copied in, as the debugger's bundle is; the diagnostics port's socket shared through a mount | **The diagnostics port's socket, shared through a mount** (`DOTNET_DiagnosticPorts` naming a socket in a mounted folder). Not built in M63; built in M63b (below), as the maintainer decided on 2026-11-14. |
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
  - **Profiling in containers** (D55's diagnostics socket): built in M63b, below.
  - **Hot Reload without the debugger** in a container (the Hot Reload agent or `dotnet watch` in it): a service run without the debugger takes a code change by a build and a restart.
  - **Regular mode** (fast mode off) debugs one service, attached after its start, as before.
  - **Blazor WebAssembly** in a compose service or under Aspire: the browser's debugging (M16) needs the app's debug proxy reachable from the browser on this machine, which neither path sets up.
  - **The debug companion** reads the startup service's container only: display attributes of a type only another service has fall back to the debugger's values.
  - Podman parity is by construction (the same CLI commands, podman-compose's labels and names, the files read when `config --format json` doesn't answer); no Podman run was made for M63, and Windows and macOS with Docker Desktop are checked by hand (their runners have no engine).

## What M63b built (2026-11-14)

The maintainer decided on 2026-11-14 to build profiling in containers before 0.13, through D55's shared diagnostics socket ([M63b checkpoint](../checkpoints/m63b.md)).

### The runtime connects; the IDE listens

Two ways put a container's diagnostics port in a folder mounted from this machine: the runtime **listening** (its default `dotnet-diagnostic-<pid>-<key>-socket` under a mounted `TMPDIR`, or a `DOTNET_DiagnosticPorts` entry in `listen` mode), or the runtime **connecting** to a socket the IDE listens on (`connect` mode, the reverse server of `dotnet-trace --diagnostic-port` and `dotnet-monitor`). M63b connects, `DOTNET_DiagnosticPorts=/codalus-diag/<service>.sock,connect,nosuspend`, because:

- The runtime makes a socket it listens on readable and writable by its own user only (0600), and a service's user is the image's (`USER $APP_UID`, uid 1654, in the .NET 8 and later images and the classic IDE's Dockerfiles; or root, which rootful Docker maps to root here): the IDE's user couldn't connect to it. The IDE's own socket is opened to every user (0666) in a folder only the IDE writes (`rwx--x--x`: the container's user passes through it, nothing more), and a runtime that connects only advertises itself.
- A listening runtime's socket is named after its process id in the container's namespace and a key, found by listing the folder; a connecting one needs no discovery: the service's socket has the service's name.
- Microsoft.Diagnostics.NETCore.Client's public API connects to a process by its id only (`new DiagnosticsClient(int)`); a socket by its path is reached through `DiagnosticsClientConnector.FromDiagnosticPort("<path>,listen")`, which starts the reverse server and waits for the runtime (its `ReversedDiagnosticsServer` isn't public). The connector's client then sends each command on the runtime's next connection, so counters, a trace and a heap snapshot run at once on one listener. A wait the token ends returns a connector without a client, which the host takes as the time running out.
- `nosuspend`: while nothing listens, the runtime tries to connect again (every half second at most) without holding the program, which costs next to nothing, so the variable is always set and profiling attaches whenever it's asked, without recreating the container. Suspending (`suspend` and a resume, profiling from the start) would hold a service that restarts (a build, Restart Compose Service) until a resume nobody sends when the profiler isn't waiting: **a service is profiled attached, not from its start** (the report and the guide say so).

### What it touches

- **The override** (`ComposeFastMode.Plan`, `Override`): on Linux, with the engine on this machine's kernel (not an engine in WSL), each .NET service mounts `.codalus/state/<solution>/diag` at `/codalus-diag` and gets `DOTNET_DiagnosticPorts` (after a port the compose file already names), in both modes; a debugged program gets it in its launch's environment, and the debugger and its companion in the container start without it (`RemoteShell.Adapter`, `Companion`: `unset` before `exec`), since the Codalus debugger is a .NET program whose runtime would otherwise connect first. macOS and Windows get neither.
- **The protocol** (version 2, [docs/diagnostics-protocol.md](../diagnostics-protocol.md)): `diag/counters/start`, `diag/trace/start` and `diag/heap/snapshot` take `diagnosticPort`, a socket's path, instead of `processId`. The host (`PortConnections`) holds one listener per socket while a session uses it and closes it with the last one, so the next start accepts the runtime that runs then (a restarted service); a try the runtime doesn't answer in 10 s lets the listener go and accepts again. A path longer than a socket's (100 bytes; a solution's state folder deep in a tree) is bound through a link to its folder in the temp folder. Nothing connecting within 30 s fails with `NotAvailable`, naming the two reasons (no fast mode; a file share that doesn't carry sockets). File I/O has no counters for such a target (its process isn't this machine's).
- **The profiler**: the Performance Profiler's running-process list starts with the fast-mode services (`back (compose service)`), for every tool but File I/O and the full dump; **Profile Compose Service…** (Debug → Other Debug Targets) starts CPU Usage and .NET Counters on one; a session's target is `service`. M60's `profile_start` and `memory_snapshot` take `service`, a compose service's name.

### Consequences

- M21a's deferred remote profiling is closed for containers where it applies: a compose service fast mode runs, from a local window, with a Linux engine on this machine. It stays open for a project run with the **Container (Dockerfile)** launch profile (M17a's single container, which could take the same mount), for remote windows (SSH, WSL, Dev Containers), and for Docker Desktop (macOS, Windows, and Docker Desktop on Linux, whose file sharing carries no sockets: the profiler says so, and the IDE doesn't set the variable off Linux).
- Tests: `ComposeFastModeTests` (the mount and the variable in both modes, a port the compose file names kept, none without a folder), `DiagnosticsToolsUnitTests` (a long path bound through a link), `DiagnosticsHostTests` (a runtime started with the variable, its folder reached through a short link as a container's mount is: counters, a CPU trace and a heap snapshot at once on one socket, then again after the program restarts; a missing folder), `ComposeShellTests` (the stand-in engine: the override, the folder's mode, the Performance Profiler's rows, Profile Compose Service, `profile_start` and `memory_snapshot` by service on the back's program run here; and a real engine on CI's Linux runner: the back profiled in its container while requests reach it).
- Deferred (`TODO(issue)`): profiling a service from its start; the single-container launch profile; Podman (rootless: the container's root is the user, so it works by construction, untried) and SELinux hosts (a bind mount needs its label, as fast mode's other mounts do).
