# 0051. Transport and multiplexing

- **Status:** accepted (M21a, 2026-10-12; the maintainer's standing go-ahead for the milestones)
- **Date:** 2026-10-12
- **Deciders:** maintainer

## Context

A remote window (ADR 0050) talks to `daedalus-server` through one byte stream: the stdin and stdout of `ssh host …` or `wsl.exe … --exec …`. Many conversations share it at once: the control requests (spawn, files, ports), the build host's and the C# host's JSON-RPC, netcoredbg's DAP, terminal bytes, and TCP tunnels for forwarded ports. They have different shapes. Hosts are `Content-Length`-framed JSON, terminals and tunnels are raw bytes. A terminal printing a build log, or a tunnel streaming a download, must not hold up completion. The roadmap requires multiplexed JSON-RPC channels over a single stdio stream, and a server that never listens on a network port.

Options considered:
- **One ssh connection per channel** (ssh's own multiplexing with `ControlMaster`). It needs the user's ssh configuration, isn't available for `wsl.exe` or Windows' OpenSSH, and costs a login per host.
- **JSON-RPC only, with byte channels as base64 notifications.** That costs a third more bytes, a JSON parse per terminal chunk, and gives no flow control per channel.
- **An existing multiplexer** (yamux, SSH's channel layer, HTTP/2). Each brings a dependency or a protocol far larger than we need, and none is AOT-clean in .NET without reflection.

## Decision

**A small binary framing of our own** (`DaedalusStudio.Rpc.Multiplexing`), the same code in the shell and the server:

- **The preamble.** The server first prints a line `DAEDALUS-SERVER <version> <protocol>`. The client discards everything before it: a login shell's banner, a profile's echo. After the line comes frames only, and the server sends its own console output to stderr.
- **Frames.** A frame is a 9-byte header and a payload of at most 32 KB:

  | Bytes | Field |
  |---|---|
  | 0–3 | channel (uint32, big-endian) |
  | 4 | type (byte) |
  | 5–8 | payload length (uint32, big-endian) |

  The types:
  - `Data` (1): bytes for the channel.
  - `Close` (2): this side writes nothing more on the channel, a half-close. The reader sees the end of its stream.
  - `Credit` (3): the payload is a uint32 count of bytes the reader consumed.
  - `Ping` (4) and `Pong` (5): 8 bytes, echoed. They measure the round trip on channel 0.

  A larger length, or a stream that ends in the middle of a frame, ends the connection.
- **Channels.** Channel 0 is the control channel, open from the start, carrying JSON-RPC 2.0 with LSP framing (`JsonRpcConnection` over the channel's stream). Only the client opens other channels. It picks the id (counting up from 1), opens it locally, then names it in a control request: `process/spawn`, `pty/open` or `tunnel/open` (docs/remote-protocol.md). The server opens the same id when it takes the request. So data the server sends after answering always finds the channel open, and no handshake frame is needed.
- **Flow control per channel.** Each direction of each channel starts with a window of 1 MB of credit. A writer that has used its credit waits, and only that writer. The reader returns credit as its consumer reads, a frame per quarter-window.
  - A channel nobody reads stops only its own writer: completion goes on while a terminal floods.
  - The read loop never waits on a write. It hands credit frames to a separate write and returns to reading, so two peers writing to each other can't both stall.
  - Data that arrives for a channel this side doesn't know (closed, or never opened) is dropped and its credit returned at once, so a closed terminal can't stall the server's writer.
- **Lifetimes.** Disposing a channel's stream sends `Close` and drops what arrives afterwards. When both halves are closed, the channel is forgotten. When the underlying stream ends, every channel ends with it: reads return the end of the stream and writes fail. JSON-RPC connections on them see the end and report it, and the server stops everything it started. For a process channel the server closes the child's stdin on `Close`, and sends `process/exited` once the child exited and its stdout drained.
- **Writes** are serialized per connection: a frame's header and payload are written and flushed together, under one lock. Frames of different channels interleave at frame boundaries.
- **Latency for budgets and tests.** `LatencyStream` delays what the client reads by an adjustable time, giving a simulated round trip (`RemoteConnection.SimulatedLatency`, `DAEDALUS_REMOTE_LATENCY_MS`). It is never used for a real connection.

**The control channel's methods**, in [docs/remote-protocol.md](../remote-protocol.md):
- `server/hello`, the version lock;
- `process/spawn`, `process/kill`, `process/stderr`, `process/exited`;
- `pty/open`, `pty/resize`;
- `fs/stat`, `fs/readFile`, `fs/writeFile`, `fs/readDirectory`, `fs/createDirectory`, `fs/delete`, `fs/rename`, `fs/watch`, `fs/unwatch`, `fs/changed`;
- `ports/list`, `ports/watch`, `ports/changed`;
- `tunnel/open`;
- `shutdown`, `exit`.

Messages are source-generated JSON (`RemoteJsonContext`), so the shell stays Native-AOT clean. File contents travel base64-encoded in `fs/readFile` and `fs/writeFile`. That is fine for source files. Large files over a channel are a later refinement (`TODO(issue)`).

**Host channels are rewritten for paths** on the client (`MappedJsonStream`, ADR 0050). The server relays bytes and never parses host traffic.

## Threat model

Security is ADR 0050's. For the transport:
- The server never listens; the only way in is the stdio of a process the user started.
- The framing caps every allocation (32 KB per frame, one window per channel). A peer that ignores credit can only grow the reader's queue of that channel.
- An unknown frame type is ignored, and an unknown channel's data is dropped.
- Nothing in the framing is authenticated or encrypted itself. ssh does both; `wsl.exe` stays on this machine.

## Consequences

- One login per session, whatever runs in it. Terminals, hosts and tunnels can't starve each other.
- Each frame costs 9 bytes, and each message on a host channel a parse and a rewrite on the client (ADR 0050). Measured on Windows: a control request's round trip through the local stand-in is 0.3 ms with no simulated latency, and the round trip plus about 12 ms at 50 ms.
- The budget is completion within local + RTT + 20 ms. It holds at a simulated 50 and 150 ms: 90 ms through the server, 108 ms at 50 ms and 196 ms at 150 ms, on Windows (the M21a checkpoint).
- Reconnecting after a network drop is M21b. It needs the server to outlive its stdio (a socket in the user's runtime folder, re-attached by a new ssh session) and the client to resync documents and sessions.
