# 0039. Debugger sessions: startup projects, the terminal, remote targets, Roslyn components and Just-In-Time debugging

- **Status:** accepted (M14c, 2026-10-11; the maintainer's standing go-ahead for the milestones)
- **Date:** 2026-10-11
- **Deciders:** maintainer
- **Amended by:** [ADR 0086](0086-a-managed-coreclr-debugger.md) (M54, 2026-10-29: under the Codalus debugger, `debug.adapter` `codalus`, a program in the integrated terminal or a console window starts there through the adapter's launcher, DAP's `runInTerminal`, instead of the startup hook's hold and an attach, and on macOS too; netcoredbg keeps this ADR's attach)

## Context

ADR 0021 split debugger depth into M14a, M14b (ADR 0032) and M14c: sessions. The roadmap's M14c scope:
- multiple startup projects and compound launches;
- attach over SSH, into a Docker container and into WSL; a WSL launch profile;
- Roslyn components (the `DebugRoslynComponent` launch profile);
- Just-In-Time debugging, off by default, with consent per OS;
- the Hot Reload-while-debugging research (ADR 0040).

M14b left one gap for M14c: Detach All works only for attached programs, because a launched program's console is netcoredbg's.

What netcoredbg 3.2.0-1092 offers here, probed for M14c:
- **No `runInTerminal`.** Its DAP interpreter has no such reverse request (nor `console` in `launch`), so it can't start a program in a terminal.
- **Attach** works from a separate process, and `disconnect` with `terminateDebuggee: false` detaches.
- **On Windows, a detach from an apphost** (`App.exe`, not `dotnet App.dll`) stopped at a breakpoint ends the program with 0xC0000409, unless a function evaluation ran since the stop. Resuming first and detaching while it runs races with the program's own debug events (0xC0000354). Detaching from `dotnet App.dll`, or from a running apphost, works. This also affected M14b's Detach All for attached apphosts.
- **Paths:** netcoredbg on Linux binds breakpoints given with the Windows paths that a Windows build's PDB records, and reports frames with them. So a Windows build running in WSL needs no path mapping. A program built elsewhere (in a container image) records other paths.
- **Its Linux build** runs in a WSL distribution and in a bare Ubuntu container: it needs `libstdc++`, and it hosts its managed part on the debuggee's runtime.

## Decision

**Multiple startup projects are one compound session.**
- `CompoundDebugAdapter` (ADR 0027) gets an *independent* mode. Each program has its own netcoredbg, and each adapter is one part of the composite. The session lasts until the last program ends. Every thread is named after its program (`Partner: Main Thread`), and each program's exit is reported in the Debug Console. A part's process and exit raise `PartChanged`. A composite holds 8 adapters.
- `DebugService.LaunchAsync(IReadOnlyList<DebugLaunch>)` starts the first program as the primary, then the others join one by one. A program that can't start is reported, and the others go on. `DebugService.Processes` lists the session's programs for the Processes window.
- **The Startup Projects page** (`run.configureStartupProjects`, a solution-management document) chooses between a single startup project and multiple ones. With multiple, each project gets an action (None, Start, Start without debugging), a launch profile, and an order.
- The choice is saved with the solution's session (`MultipleStartupProjects`, `StartupProjects`), the way other IDEs keep it in the user's solution options, not in the solution file.
- F5 builds the solution once and starts the projects in order. Those set to Start debug in one session, and the others run as with Ctrl+F5.
- Set as Startup Project goes back to a single startup project.

**A program in the integrated terminal: launched by the IDE, attached by the debugger.** netcoredbg can't `runInTerminal`, so the IDE does it:
1. With `debug.console` set to `integratedTerminal` (console apps; the default stays the Debug Console), the IDE starts the program in a new integrated terminal.
2. The environment adds a startup hook (`DOTNET_STARTUP_HOOKS`): `DaedalusStudio.Debugging.StartupHook.dll`, a `netstandard2.0` assembly shipped in `debughost/`, so it loads on any .NET since 3.0.
3. Before `Main`, the hook writes its process id to `$DAEDALUS_DEBUG_WAIT/pid` and waits for `$DAEDALUS_DEBUG_WAIT/go`.
4. netcoredbg attaches to that process. The session's breakpoints are set, and once the program's own module has loaded, the IDE writes `go`.
5. After 120 seconds without `go`, the program runs without the debugger. The variables are cleared, so the program's children don't wait.

The program's console is then the terminal's, with input. Stop ends it. **Detach All leaves it running**, which closes the M14b gap. `DebugProcess.Kind` is `Terminal`, and `CanDetach` (the `debugCanDetach` context key) is true when every program was attached to or launched in the terminal.

DAP's `runInTerminal` is implemented as well, for adapters that send it:
- `DapConnection.Handle` answers a reverse request with a handler's result.
- `DebugAdapter.StartAsync(…, ITerminalLauncher)` advertises `supportsRunInTerminalRequest` and starts the program through the launcher.

**Preparing a detach.** Before a detach from a stopped program (Detach All, or Stop on an attached one), `DebugService` removes the breakpoints and exception filters, then runs a trivial function evaluation (`System.Environment.ProcessId`) on the stopped thread. This is the netcoredbg issue above. `TODO(issue)`: report it upstream.

**Remote targets through pipe transports.**
- A `RemoteTarget` runs `sh -c` scripts on a machine, with stdin and stdout piped:
  - `SshTarget`: the user's `ssh -T`, so their keys, agent and `~/.ssh/config` apply. Passwords and passphrases go through the shell's askpass (ADR 0031).
  - `ContainerTarget`: `docker exec -i`. The container CLI is a setting (`debug.containerTool`: `docker`, `podman`, or e.g. `wsl -d Ubuntu --exec podman`).
  - `WslTarget`: `wsl.exe -d <distribution> --exec`.
- `DebugAdapterStartInfo` describes how to start an adapter: netcoredbg here, or the transport's command line running `netcoredbg --interpreter=vscode` there. DAP then flows over the transport.
- **Processes** are listed from `/proc`, so slim images without `ps` work. .NET processes are recognized by `libcoreclr` in their memory maps. `ps` is the fallback on macOS hosts.
- **netcoredbg on the target.** `RemoteDebuggers` looks for it in `~/.daedalus/netcoredbg/<version>` (`/tmp/.daedalus/…` in containers). When it's missing, it asks the user, then streams a `.tar.gz` into `tar -xzf -` on the target. The target needs `tar` and `gzip`, but no network access.
  - The archive is the pinned release for the target's platform (`uname -s -m`: linux-x64, linux-arm64, osx-arm64), with the version and checksums of `eng/get-netcoredbg.sh`.
  - It comes from, in order: `DAEDALUS_NETCOREDBG_<PLATFORM>`; this machine's netcoredbg when the platforms match; `.work/netcoredbg-<platform>` in a source checkout (`eng/get-netcoredbg.sh --platform`); or a download into the cache, verified by SHA-256.
- **Source paths**: `PathMappingDebugAdapter` puts a `SourceMap` between the session and the adapter.
  - Breakpoints go out with the target's paths, and frames, breakpoint events and output come back with this machine's.
  - Mappings come from `debug.sourceFileMap` (`/src=C:\work\App`) or are inferred. A frame whose file isn't here is matched to a solution file by its folder and name, and that folder pair then maps the other files.
- **Commands.** Debug → Attach to Process on a Remote Machine (`debug.attachRemote`) picks SSH, a container or a WSL distribution, then a process. Reattach repeats the last one. The Processes window shows the target.

**The WSL launch profile.** A `launchSettings.json` profile with `"commandName": "WSL2"` (and `"distributionName"`, empty for the default) runs the project's Windows build output in the distribution:
- the distribution's `dotnet` (found by its login shell) runs `exec /mnt/…/App.dll`, from the Linux path of the working directory, with the profile's arguments and environment;
- Ctrl+F5 runs `wsl.exe -d … --cd … --exec sh -lc 'exec env … dotnet exec …'`;
- F5 launches it under netcoredbg in the distribution, as a remote launch (`DebugLaunch.Remote`).

The Windows paths of the PDB need no mapping (above). `WslLaunch.ToLinuxPath` assumes the default automount root (`/mnt/`). Other roots and `\\wsl$` paths are handled, but a custom `automount.root` isn't read from `wsl.conf`.

**Roslyn components.** A profile with `"commandName": "DebugRoslynComponent"` and `"targetProject"` debugs an analyzer or source generator by running the compiler on the target project under the debugger:
- the compiler is the SDK's `Roslyn/bincore/csc.dll` (or `vbc.dll`), on `dotnet exec`;
- its arguments are the target's command line from its design-time build (ADR 0005), in a response file;
- `/out`, `/pdb` and `/doc` go to a temporary folder, and `/refout` is dropped, so the project's own outputs are left alone;
- the generator's breakpoints bind as the compiler loads it. netcoredbg finds its PDB from the CodeView path, although the compiler loads analyzers through its own loader.

**Just-In-Time debugging is post-mortem, per user, and off by default.**
- No OS lets an unprivileged program become the debugger of other programs' crashes:
  - Windows' `AeDebug` key is machine-wide (HKLM), and .NET Core has no per-user JIT debugger setting;
  - Linux and macOS have no JIT debugger at all.
- So Debug → Just-In-Time Debugging, after a dialog that says exactly what changes, sets the .NET runtime's crash-dump variables in the user's environment: `DOTNET_DbgEnableMiniDump=1`, `DOTNET_DbgMiniDumpType=4` (full, so frames map to lines, ADR 0032) and `DOTNET_DbgMiniDumpName=<logs>/crash-dumps/%e.%p.dmp`. Where they go:
  - Windows: the user's environment variables (`HKCU\Environment`, broadcast to Explorer);
  - Linux: `~/.config/environment.d/90-daedalus-jit.conf` and a marked block in `~/.profile`;
  - macOS: `launchctl setenv` and a LaunchAgent that sets them again at login.
- A .NET program started afterwards that crashes writes a dump there. `CrashDumpWatcher` notices it, and the shell offers to open it in the dump view (M14b).
- Turning it off removes the variables. Tests use stand-in environments, so they never leave a registration.
- Attaching live to a crashing program (`AeDebug`, with administrator rights) isn't offered. `TODO(issue)`.

## Consequences

- A program in the terminal pays a startup hook and an attach: about a second more than a launch. Programs that disable startup hooks (trimmed or Native AOT apps, which netcoredbg can't debug anyway) can't be held; they fail with a message after 60 seconds. The default console stays the Debug Console.
- **Not covered** (each a `TODO(issue)` in the checkpoint):
  - Multiple startup projects stop programs independently: a stop in one doesn't pause the others (other IDEs' "break all processes when one process breaks" setting).
  - A web project among multiple startup projects debugs its server only; its browser opens but the page isn't debugged (ADR 0027 pairs one server with its page).
  - SSH: no password prompts without the shell's askpass, no jump hosts beyond `~/.ssh/config`, no Windows SSH targets (netcoredbg for Windows over SSH is possible, but the scripts are `sh`).
  - Containers: Windows containers, and images without `tar`, `gzip` or `libstdc++`.
  - The WSL profile: a custom `automount.root`, and launching a browser for web projects in WSL.
  - Roslyn components: F# projects; generators that only run in the IDE's design-time compilation.
  - Just-In-Time: live attach.
- **Validation without other machines.** The SSH, container and WSL tests use local stand-ins:
  - WSL itself (Windows);
  - a stand-in `ssh` script that runs the command locally (Linux, macOS);
  - Podman with a local Ubuntu image, the .NET of the machine running it mounted in (Linux, and WSL's Podman from Windows).
