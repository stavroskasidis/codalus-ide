# The remote server protocol

`daedalus-server` (`src/DaedalusStudio.Remote.Server`, M21) runs on the remote machine of a remote window. The architecture is [ADR 0050](adr/0050-remote-development-architecture.md); the transport and its framing are [ADR 0051](adr/0051-transport-and-multiplexing.md). The contracts are in `src/DaedalusStudio.Remote.Protocol` (`RemoteMethods`, `RemoteJsonContext`).

## Starting

```
daedalus-server [--stdio]     the server: preamble, then frames on stdin and stdout until stdin ends
daedalus-server --relay <session> [--grace <seconds>]
                              a resumable server (M21b, ADR 0054): connects to the session's daemon, starting it if
                              needed, prints the preamble with the daemon's instance, and copies bytes both ways
daedalus-server --daemon <session> [--grace <seconds>]
                              the session's server on a Unix socket, outliving its links (started by the relay)
daedalus-server --version     prints the version
daedalus-server --echo        copies stdin to stdout, writes two lines to stderr, exits with 3 (tests)
```

The client starts it through `ssh … sh -c '<script>'` or `wsl.exe -d <distribution> --exec sh -c '<script>'`. `RemoteServerBundle.StartScript`:
1. sources `~/.profile` with its output discarded;
2. adds `~/.dotnet` to the PATH;
3. runs `~/.daedalus-server/<version>/server/daedalus-server.dll` with `dotnet`, or the self-contained `daedalus-server`.

It prints `\nDAEDALUS-SERVER <version> <protocol>\n` and then speaks frames. Its console output goes to stderr, which the client shows in the **Remote** output channel.

### Resumable servers

Remote windows start the server with `--relay <session> --grace <seconds>` ([ADR 0054](adr/0054-reconnect-and-resync.md)). The session id is a GUID the client made up (letters, digits, dashes).
- **The preamble** is `\nDAEDALUS-SERVER <version> <protocol> resumable <instance>\n`, where the instance is the daemon's own GUID.
- **The resumable layer.** After the preamble, the bytes on stdio are `ResumableStream` frames, and the multiplexer's frames are inside them:
  - the hello: `'R'` + the offset received so far (`uint64`, big-endian), from each side; each side then resends what the other is missing;
  - data: `'D'` + length (`uint32`) + bytes;
  - acknowledgements: `'A'` + the offset received (`uint64`).
- **The daemon** listens on `<session>.sock` and logs to `<session>.log`. Both are in `$XDG_RUNTIME_DIR/daedalus/`, else in `/tmp/daedalus-<user>/` (mode `0700`; the temporary folder on Windows). It ends when the client says `exit`, or after the grace period (default 900 s) without a link.
- **Reconnecting.** The client starts the same command again. If the instance differs, the session it ran in has ended.

## Channels

Channel 0 carries JSON-RPC 2.0 (LSP framing). Every other channel is opened by the client: it picks the id, opens it, and names it in the request that gives it a meaning. An error answer means the channel was never opened on the server; the client closes its side.

| Request | The channel carries |
|---|---|
| `process/spawn` | the process's stdin (client → server) and stdout (server → client); a `Close` from the client closes stdin |
| `pty/open` | the terminal's input and output bytes |
| `tunnel/open` | a TCP connection the server made to a port on its machine |

## Methods (client → server)

| Method | Params | Result |
|---|---|---|
| `server/hello` | `{ clientVersion, protocolVersion }` | `{ serverVersion, protocolVersion, os, architecture, processId, home, user, dotnet?, dotnetVersion?, shell, serverFolder }`. Another protocol is an error. The client also refuses another server version. |
| `process/spawn` | `{ channel, fileName, arguments[], workingDirectory?, environment? }` | `{ processId }`. `fileName` `dotnet` is the server's `dotnet`; a `.dll` runs on it. |
| `process/kill` | `{ channel }` | `{}`: the process tree, or the terminal. |
| `pty/open` | `{ channel, fileName?, arguments[], workingDirectory?, environment?, columns, rows }` | `{ processId }`. No `fileName`: the user's login shell with `-l`. The environment is added to the server's own, with `TERM=xterm-256color`. A missing folder is the home folder. |
| `pty/resize` | `{ channel, columns, rows }` | `{}` |
| `fs/stat` | `{ path }` | `{ exists, isDirectory, size, modified }` (Unix ms) |
| `fs/readFile` | `{ path }` | `{ content }` (base64) |
| `fs/writeFile` | `{ path, content }` | `{}`: through a temporary file in the same folder, then a replace; folders are created |
| `fs/readDirectory` | `{ path }` | `{ entries: [{ name, isDirectory, size, modified }] }` sorted by name |
| `fs/createDirectory` | `{ path }` | `{}` |
| `fs/delete` | `{ path, recursive }` | `{}` |
| `fs/rename` | `{ source, target, overwrite }` | `{}` |
| `fs/watch` | `{ id, path, recursive }` | `{}`: a file or a folder; changes come as `fs/changed`, gathered for 100 ms |
| `fs/unwatch` | `{ id }` | `{}` |
| `ports/list` | `{}` | `{ ports: [{ port, address, processId?, processName? }] }` in the LISTEN state |
| `ports/watch` | `{ enabled, intervalMilliseconds }` | `{}`: `ports/changed` when the set changes, and once at the start |
| `tunnel/open` | `{ channel, host, port }` | `{}` once connected, or an error when nothing accepts there |
| `shutdown` | — | `null`, after every process and terminal it started was ended |
| `exit` | notification | the server exits |

A missing path is error `-32010` (`RemoteMethods.FileNotFound`), which `RemoteFileSystem` turns into `FileNotFoundException`. Other file-system errors are `-32603` with the OS's message.

## Notifications (server → client)

| Method | Params |
|---|---|
| `process/stderr` | `{ channel, line }` |
| `process/exited` | `{ channel, exitCode? }`: after the process exited and its stdout drained (2 s at most); also for a terminal |
| `fs/changed` | `{ id, changes: [{ path, kind }] }`; `kind` is `created`, `changed` or `deleted`, a rename is both |
| `ports/changed` | `{ ports: [...] }`, as `ports/list` |

## Paths

The server sees only its own machine's paths. The client maps them as ADR 0050 describes: in the arguments of `process/spawn` and `pty/open`, in file-system requests, and in the JSON of the host channels it reads and writes (`RemotePathMap`, `MappedJsonStream`).
