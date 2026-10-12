# M21a work in progress (paused 2026-10-12)

M21a (remote development over SSH and WSL) was paused for UX and performance work. This branch is based on `develop` at `067d462`; it was **not** landed. The planned checkpoint, with its results left empty, is [m21a.md](m21a.md).

## Done

- **Decisions:** [ADR 0049](../adr/0049-remote-development-architecture.md) (architecture and threat model) and [ADR 0050](../adr/0050-transport-and-multiplexing.md) (transport and multiplexing).
- **Docs:** [docs/remote-protocol.md](../remote-protocol.md), the user guide's [remote.md](../user-guide/remote.md), CLAUDE.md, and the roadmap's M21 row. The row already says "M21a at checkpoint"; correct it if this stays unlanded.
- **`daedalus-server`** (`src/DaedalusStudio.Remote.Server`, with the contracts in `src/DaedalusStudio.Remote.Protocol`): processes, PTYs, the file system with watches, listening ports, tunnels, and `server/hello` as the version lock.
- **Transport** (`DaedalusStudio.Rpc/Multiplexing`): framed channels with flow control per channel, and a simulated-latency stream. `IProcessLauncher`/`IHostProcess` now start `SupervisedProcess` and `DebugAdapter`.
- **Workbench** `Remote/`:
  - `RemoteConnection`, `RemoteSession` and `RemoteTargetSpec` (`ssh:`, `wsl:`, and `local` as the stand-in);
  - `RemoteServerBundle`: a checksummed copy into `~/.daedalus-server/<version>/`;
  - `RemotePathMap` and `MappedJsonStream`: paths under `\\wsl.localhost\<distro>`, `\\daedalus-remote.invalid\<id>` or `/daedalus-remote/<id>`;
  - `RemoteFileSystem`, `RemoteProcessLauncher`, `RemotePseudoTerminal` and `PortForwarder`.
- **Workbench** `FileSystem/` (`FileSystems`, the F2 provider routing).
- **Shell:**
  - `ShellHost.Remote.cs` and `BuiltIns.Remote.cs`: Open Remote Window, Close Remote Connection, the status item with the round trip, the remote menu, the Ports window, auto-forward notifications, Run and F5 on the server, and `--remote <target>`;
  - the editor's files go through `FileSystems`;
  - the smoke step `remote.session`;
  - published installs get `remote/server/` and self-contained `remote/linux-x64|linux-arm64/`.

## Test status (Windows, before the pause)

- **New tests, run alone:**
  - `MultiplexerTests`: 8/8.
  - `RemoteServerTests`: 12/12, including the bundle copied into WSL and a damaged bundle refused.
  - `RemoteShellTests`: 4/4, including F5 through the server on the local stand-in, and a full WSL remote window: open, complete, build, run, and F5 stopping at a breakpoint in Ubuntu.
- **Budgets measured** (Windows, local stand-in): completion was 90 ms with no added latency, 108 ms at 50 ms RTT and 196 ms at 150 ms RTT, inside the "local + RTT + 20 ms" budget.
- **A full-suite run was stopped part way.**
  - Rpc, Debugging, Plugins, Release and TargetFrameworks passed.
  - `RemoteServerTests`' port test failed under parallel load: another program's port was forwarded first. It's fixed in this commit but not re-run.
  - The failures outside M21 were M19a's `XamlLanguageTests` and `XamlPreviewerTests` (already failing on Windows on `develop`), `BuildDepthIntegrationTests`' file lock, and `BuildHostIntegrationTests.A_crashed_build_host…`, which timed out opening under load. It goes through the changed `SupervisedProcess`, so **re-run it alone first**.
  - Shell.Tests didn't run in the full suite.

## Next

1. Re-run, alone: `BuildHostIntegrationTests`, the Debugging tests, all of Shell.Tests, and the edited port test. Then run the full suite.
2. Run the Native AOT publish. Check that the `DaedalusRemoteServerForPublish` target builds `remote/server` and the trimmed `remote/linux-*` servers in CI. Then run `eng/smoke-test.sh` (new step `remote.session`).
3. Do the manual WSL session with the published app (`daedalus --remote wsl:Ubuntu <path> --screenshot …`; a remote window's screenshot waits for its C# service). Record it in m21a.md.
4. Validate on Linux with `eng/wsl-test.sh --smoke` (`DAEDALUS_WSL_DIR=/home/akira/daedalus-m21`). On Linux the fake ssh covers the installer, and the local stand-in maps paths under `/daedalus-remote/local`. Push the Mac snapshot with `eng/push-snapshot.sh validate-m21`.
5. Merge `origin/develop`. ADR numbers 0049 and 0050 may need renumbering if M19 or others took them. Fill in m21a.md's results, update the roadmap row, land, and read CI.

## Decisions taken (in the ADRs)

- **A remote window is this window, connected.** Open Remote starts another process only when this window has something open.
- **Remote paths take this machine's form under a root**, and host JSON is rewritten as whole strings only, so the shell's `Path` code works unchanged.
- **The server is self-contained per platform; the hosts are framework-dependent** on the target's `dotnet` (ADR 0025's roll-forward), copied from this install, not downloaded.
- **SSH uses `BatchMode=yes`:** keys and agents only, so the IDE never sees a password.
- **M21b:** Dev Containers, attaching to a container, `testenvironments.json`, `runsOn`, reconnect and resync. Also: a filter so auto-forwarding takes only the session's own processes' ports, and SSH to Windows.
