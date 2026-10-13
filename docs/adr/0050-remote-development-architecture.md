# 0050. Remote development architecture

- **Status:** accepted (M21a, 2026-10-12; the maintainer's standing go-ahead for the milestones)
- **Date:** 2026-10-12
- **Deciders:** maintainer

## Context

M21 is about working on code that lives on another machine: over SSH, in WSL, in a container. Editing has to feel local. The roadmap fixes the split. The shell keeps the UI and the editor buffers, so typing never crosses the network. A version-locked `daedalus-server` on the remote machine hosts what reads the workspace or runs its code: the language and build hosts, debug adapters, terminals and a file-system provider. One multiplexed stdio stream carries it all (ADR 0051). The roadmap also names Dev Containers, attaching to a running container, remote test environments, plugin placement (`runsOn`), and resilience (reconnect and resync). The milestone is split (roadmap rule 5): **M21a** (this ADR) is the architecture, SSH and WSL. **M21b** adds Dev Containers, attaching to a running container, `testenvironments.json`, `runsOn`, and reconnecting.

What the codebase already had:
- Every host is a `SupervisedProcess` that speaks JSON-RPC on its stdio and restarts after a crash (ADR 0004, 0005).
- M14c's remote debugging (ADR 0039) runs netcoredbg on a `RemoteTarget` (SSH, a container, WSL) through a pipe transport. It copies a pinned, checksummed netcoredbg there with consent (`RemoteDebuggers`) and maps source paths (`SourceMap`).
- The protocols name documents by URI (D1, F1). Paths are strings everywhere else. Close to 500 places in the shell and the workbench read or write files directly, and every `Path` API works in this machine's terms.

## Decisions

**A remote window is this window, connected.**
- `RemoteSession` (workbench `Remote/`) owns the connection, the path map, the mounted file system, the launchers and port forwarding.
- `ShellHost.ConnectRemoteAsync` moves the window's build and language hosts, terminals and debugger onto the session. `daedalus --remote ssh:host /path` or `--remote wsl:Ubuntu /path` starts a window that way.
- **Open Remote Window…** (File menu, `remote.open`) asks for SSH or a WSL distribution and a remote path. A window with nothing open connects itself; otherwise it starts another `daedalus` process, as VS Code opens a new window.
- **Close Remote Connection** makes the window local again.
- The remote window does not answer `daedalus diff`, `merge` or `--wait` (single-instance requests); the local window does.

**Placement.** Each host's launch passes through a placement function (`SolutionService.Placement`, `LanguageService.Placement`):
- The build host, the C# host and the markup host run on the server, from the bundle's folder there. They always start as `dotnet <host>.dll`: this machine's apphost is for this OS.
- Hosts that can't run remotely yet return null, and that language service stays off with a log line. This covers Node-based web servers, the data, diagnostics, T4, debug-companion and C# Interactive hosts, and plugin hosts (`runsOn` is M21b).
- `SupervisedProcess` takes an `IProcessLauncher` (`ProcessLaunchInfo.Launcher`). The remote one, `RemoteProcessLauncher`, spawns through the server. Crash supervision, backoff and replay after a restart work as they do locally: a host that dies on the server is started again there.
- `DebugAdapter` starts through the same abstraction (`DebugAdapterStartInfo.Launcher`). So netcoredbg runs on the server without a second SSH connection.

**Paths: the remote machine's files have paths of this machine's form under a root** (`RemotePathMap`). This keeps every `Path` API, comparer, tree and cache in the shell working unchanged.
- **Windows, WSL:** the root is `\\wsl.localhost\<distribution>`. This is a real path, so a feature that still reads files directly works there too, through WSL's file sharing.
- **Windows, SSH:** the root is `\\daedalus-remote.invalid\<id>`.
- **Linux and macOS:** the root is `/daedalus-remote/<id>`.

Neither of the last two exists, so a feature that isn't remote-aware fails at once. It never reads the wrong machine's file. The remote side is a Unix file system. A Windows target over SSH is an error for now (`TODO(issue)`, M21b), except a stand-in server on this machine, whose paths are this machine's (an identity map).

**Rewriting.** The JSON on host channels (JSON-RPC, LSP and DAP, all `Content-Length`-framed) is rewritten both ways by `MappedJsonStream`:
- A string that is wholly a local path under the root, or a `file:` URI of one, becomes the remote path or URI. Coming back, an absolute Unix path, or a `file:` URI of one, becomes local.
- A path, unlike a URI, must have no whitespace and must not start with `//`. That keeps sentences, command lines and comments as they are.
- Paths inside longer strings stay as they are: a build line's file, a hover's link (`TODO(issue)`).
- Arguments, working folders and environment values of a spawn are mapped too.
- The hosts see their own machine's paths, and the shell sees its own. Nothing in the protocols changed.

**The file-system provider (F2).**
- `IFileSystemProvider` and `FileSystems` (workbench `FileSystem/`) route a path to the provider whose root covers it. The session mounts the server's provider (`RemoteFileSystem`: stat, read, atomic write, list, create, delete, rename, watch) under its root. A stand-in mounts it under the folders it opens.
- The editor reads, saves (atomically, through a temporary file there) and watches (the server's `FileSystemWatcher`) through it: `TextFiles`, `CodeDocument`.
- Opening a solution, a folder or a file checks its existence through it.
- The Solution Explorer's trees come from the build host on the server (ADR 0033's `fs/listTree`), so they need nothing more.
- Its plugin API comes with M22 (D1).

**Terminals.** `TerminalService.PtyFactory` starts pseudo-terminals on the server (`RemotePseudoTerminal`). The server uses the same `PseudoTerminal` (ConPTY or `openpty` + `posix_spawn`, ADR 0014).
- The terminal profile is **Remote shell**: the user's login shell there.
- Only the variables the terminal service set itself go to the remote machine, never this machine's environment.
- Shell integration scripts aren't copied yet (`TODO(issue)`).

**Run and debug.** In a remote window, Run Without Debugging runs the startup project's launch plan in a terminal on the server. The plan comes from the build host's `project/runInfo` and the launch profile, both read there; `RuntimeSelection`, which inspects this machine, is skipped. F5 runs netcoredbg on the server through the session's launcher, with its DAP rewritten like the hosts' JSON. An apphost gets `DOTNET_ROOT` from the server's `dotnet`, for an SDK in `~/.dotnet`.
- netcoredbg is installed by M14c's `RemoteDebuggers`: the pinned build for the target's platform, with consent. The local stand-in uses this machine's.
- Multiple startup projects, Hot Reload, containers, Aspire and browser debugging don't run in a remote window yet (`TODO(issue)`).

**Port forwarding.** `PortForwarder` listens on this machine's loopback for each forwarded port. Every accepted connection becomes a tunnel channel, which the server connects to the port on its machine; the server never listens.
- The server reports its listening ports: `/proc/net/tcp*` on Linux, with the owning process when it is the user's; `lsof` on macOS; the OS's table elsewhere. It polls every 1.5 s while watched.
- Ports that start listening after the session began are forwarded automatically (`remote.autoForwardPorts`). A web app's or a debug session's port is forwarded on the same number when it is free here, so `localhost:5000` in a launch profile works. Its notification offers the browser.
- Ports that were already listening are left alone. So are ports the user stopped forwarding, and the forwarder's own listeners, which a stand-in on this machine would otherwise see.
- A web app's launch URL opens once its port is forwarded.
- The **Ports** window (View → Other Windows) lists forwarded and listening ports. It can forward one, stop one, or open one in the browser.

**The status bar** shows the connection and its round trip (a ping frame every 5 s) on the left (`$(link) WSL: Ubuntu · 3 ms`). It turns into a warning when the connection drops. Clicking it offers the remote actions: Ports, Forward a Port, the Remote log, Close Remote Connection, Open Remote Window. The **Remote** output channel has the connection's log, ssh's messages included.

**The server bundle is version-locked and follows the client.**
- `RemoteServerBundle` packs `server/` with the hosts the shell runs itself (`buildhost/`, `lshost/`, `markuphost/`) as a `.tar.gz`. It is cached here by its files' names, sizes and times.
- It is copied through the user's ssh or `wsl.exe` into `cat > …` and checked against its SHA-256 there (`sha256sum` or `shasum -a 256`). Then it is unpacked into a temporary folder and moved into `~/.daedalus-server/<version>/` with its checksum in `.bundle`. A damaged or half-copied bundle is never used.
- The probe compares that checksum, so a bundle is copied once per client build. A new client version is a new folder: updates follow the client.
- The server answers `server/hello` with its version, and the client refuses another version or protocol.
- The target needs `sh`, `tar`, `gzip` and a SHA-256 tool, not network access. Downloading bundles from the releases instead of copying them is `TODO(issue)`.

**Self-contained per platform, hosts on the target's .NET.**
- A published install carries `remote/server/`: the server built framework-dependent, so it runs on any OS with .NET 10.
- It also carries `remote/<rid>/` for `$(DaedalusServerRids)` (default `linux-x64;linux-arm64`): the server self-contained, single-file and trimmed (about 16 MB). It starts without .NET on the target, so the terminal and the files work even there.
- The copied bundle uses the platform's self-contained server when the install has one, and the framework-dependent one otherwise.
- **The hosts stay framework-dependent** and run on the target's `dotnet`. The build host must load the target's SDK and roll forward with it (ADR 0025), and a .NET workspace needs that SDK anyway. The server finds `dotnet` itself: its own host, `DOTNET_ROOT`, `~/.dotnet`, the PATH, the usual folders. It reports it in `hello`, and a target without one gets a warning that projects won't load.
- The server starts after `~/.profile` (its output discarded), with `~/.dotnet` on the PATH.
- In development and tests, `DAEDALUS_SERVER` points at the framework-dependent build.

**Targets.**
- **SSH** goes through the system OpenSSH client (`SshTarget`, M14c), which honours `~/.ssh/config`, agents, `known_hosts` and keys. A remote window adds `-o BatchMode=yes`: keys and agents only. ssh never prompts, and the IDE never sees a password. A host that needs one fails at once with ssh's message in the Remote log (unlike M14c's attach, which offers askpass).
- **WSL** goes through `wsl.exe -d <distribution> --exec sh -c`. Its distribution name (`WSL_DISTRO_NAME`) names the root.
- **`local`** is a server on this machine. It is the stand-in for tests and the smoke test, and is reachable with `--remote local`.

## Threat model

The assets are the user's code and credentials on both machines, the remote account, and this machine.

| Threat | Mitigation |
|---|---|
| A network attacker reaches the server | It never listens: stdio inside the user's ssh session (encrypted, host-authenticated by `known_hosts`) or `wsl.exe` is the only way in. Port forwards listen on this machine's loopback only. |
| The IDE handles SSH secrets | It never sees them. ssh reads keys, agents and `known_hosts` itself, and `BatchMode=yes` forbids prompts. Nothing is stored. |
| A tampered or truncated bundle runs remotely | The SHA-256 of what was sent is checked there before unpacking. The bundle moves into place only whole, and its checksum is compared on every connect. |
| Another local user on the remote machine reads or swaps the bundle | It lives under the user's home (`~/.daedalus-server`, created with the user's umask), never in `/tmp`. (Tests use `/tmp` folders of their own and remove them.) |
| Another local process on this machine uses a forwarded port | Forwards are on loopback, as `ssh -L` would be. They reach only what the user's account can reach on the remote machine. Stop Forwarding closes them, and auto-forwarding can be turned off. |
| A malicious workspace runs code | As locally (ADR 0004): projects load and build, tasks and programs run, only for folders the user trusts. The trust prompt names the remote folder. |
| A malicious server or remote machine attacks the client | The server is code the client copied and checked; a compromised remote account is out of scope, as for any SSH client. The client parses its frames defensively: lengths are capped (32 KB per frame), a malformed frame ends the connection, and unknown channels are dropped. Paths it reports map under the session's root, which on Windows SSH and on Linux and macOS doesn't exist here, so a reported path can't make the editor read or write this machine's files. |
| Banner or profile output corrupts the protocol | The client skips everything before the server's preamble line, and the server's own console output goes to stderr. |
| Path rewriting changes data | Only whole strings that are paths under the root (or absolute Unix paths) are rewritten. Text, completion items, comments and command lines are left alone, and document text is never one whole path. |

## Consequences

- The features that work in a remote window are listed in the M21a checkpoint. Everything that still reads local files directly (Git, NuGet's UI, Find in Files, the data, profiler, T4, previewer, container and test windows, and more) doesn't work there yet. On WSL it may, through the share. Each is `TODO(issue)` for M21b or later, moved as remote use shows what matters.
- A dropped connection ends the session. The editor keeps unsaved buffers, and the status bar and a notification say so. Reconnecting and resyncing is M21b.
- Bundles grow the published install by the self-contained servers (about 16 MB each) and the copy by the hosts (about 40 MB unpacked, 15 MB compressed).
