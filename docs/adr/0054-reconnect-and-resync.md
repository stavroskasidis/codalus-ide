# ADR 0054: Reconnecting a remote window

Status: accepted (M21b, 2026-10-13)

## Context

A remote window (ADR 0050) runs everything that reads the workspace on the server: the build and language hosts, terminals, the debugger, the file system. Its only link is the stdio of `ssh`, `wsl.exe` or `docker exec` (ADR 0051). Until now the server's life was that link's: when stdin ended, the server stopped every process it had started. A dropped Wi-Fi, a VPN reconnecting or a laptop's sleep therefore cost:
- the solution's evaluation;
- the C# host's compilation;
- running programs and terminals;
- a debugging session.

The user then had to open the remote window again. The roadmap's budget for reconnecting is 5 s.

The two ends of a link can't tell a dropped link from a slow one, and bytes in flight when it drops are lost: some were sent and never arrived. Resuming means knowing which.

## Decision

**A resumable stream under the multiplexer.** `ResumableStream` (`DaedalusStudio.Rpc/Multiplexing`) is the stream the multiplexer reads and writes on both sides. The links under it come and go.
- Every byte written has an offset. Written bytes are kept until the other side acknowledges them.
- A link starts with a hello from each side: `'R'` and the offset received so far (`uint64`). Each side resends what the other is missing, from its kept bytes.
- On a link, frames are `'D'` + length (`uint32`) + data, and `'A'` + received offset (`uint64`). Acknowledgements go every 64 KB received and every 500 ms.
- While no link is attached, writes are kept (up to 256 MB) and reads wait. The multiplexer, its channels and the JSON-RPC connections above it don't notice a drop: requests wait and then complete.

**A server that outlives its link: a relay and a daemon.**
- The client starts `daedalus-server --relay <session> --grace <seconds>`, with a session id it made up (a GUID).
- The relay connects to the session's daemon over a Unix socket: `$XDG_RUNTIME_DIR/daedalus/<session>.sock`, else `/tmp/daedalus-<user>/` (macOS's `$TMPDIR` would exceed the socket path limit), created `0700`. If there's no daemon, the relay starts it (`--daemon <session>`): detached, its stdio not the relay's, its log `<session>.log` next to the socket, and SIGHUP ignored.
- The daemon's first line on each connection is its **instance**, a GUID of its own. The relay prints the preamble with it (`DAEDALUS-SERVER <version> <protocol> resumable <instance>`) and copies bytes both ways until either side ends.
- The daemon runs the server exactly as `--stdio` does, on a `ResumableStream`, and attaches each connection as a new link.

**The client reconnects.**
- When the link drops (the transport exits, a read fails, or a ping isn't answered in 20 s), `RemoteConnection` starts the same transport again.
- It checks the instance: a different one means the session's daemon has ended (its grace ran out, the machine restarted), so the session ends as before.
- Otherwise it attaches the new link, and nothing is lost or repeated.
- Attempts back off from 250 ms to 10 s, for as long as the server's grace (15 minutes by default). Then the session ends.
- The status bar shows *reconnecting…*, and the Remote log says how long the link was down.

**The daemon's grace.** A daemon with no link for its grace period closes the stream, which ends the server and everything it started, as a dropped `--stdio` server did at once. A daemon that never gets its first link ends after 60 s.

**Scope.**
- Remote targets (SSH, WSL, containers) are resumable.
- The local stand-in isn't by default (`RemoteSessionOptions.Resumable`). Tests and the smoke test turn it on to exercise reconnecting.
- `--stdio` stays for tools and older clients.

## Alternatives

- **Restarting the server and resynchronizing state** (reload the solution, reopen documents): nothing to keep on the server, but it loses evaluation, compilation, running programs and debugging, and it takes far more than 5 s on a real solution.
- **Reconnecting each channel** (hosts, terminals) separately: every protocol above the multiplexer would need its own resume. One stream under the multiplexer resumes them all.
- **`tmux`/`screen`-style daemons through the user's tools**: not on every machine, and a second process model.
- **mosh's UDP transport**: needs a port opened on the server. ADR 0050 keeps the server off the network: the user's ssh is the only way in.

## Consequences

- The server keeps running on the target for its grace period after a window closes without saying goodbye (a crash, a killed process). A clean close sends `shutdown` and `exit`, and the daemon ends at once.
- Up to 256 MB of unacknowledged output is held per side while the link is down. Beyond that, the stream closes and the session ends.
- Killing a transport kills that process only, never its tree: the relay's child is the daemon.

**Tests**:
- `ResumableStreamTests`: resend after a drop, with nothing twice; 3 MB across two drops; closing.
- `RemoteServerTests`: a dropped link reconnects in under 5 s, a process on the server keeps its channel, and requests waiting on the link complete; a killed daemon ends the session; the relay on the WSL or fake-ssh stand-in reconnects to the same daemon.
- `RemoteShellTests`: the remote window reconnects and its terminal goes on.
- The smoke step `remote.session`: the 5 s budget, a save across the reconnect, and the C# host going on.
