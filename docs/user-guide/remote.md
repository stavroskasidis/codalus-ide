# Remote development: SSH and WSL

A **remote window** works on code that lives on another machine, reached through your own `ssh`, or in a WSL distribution on Windows. The window, its editor and your typing stay on this machine. Opening and building projects, the C# language service, terminals, running and debugging all happen on the other machine, in a small server Daedalus Studio copies there.

## Opening a remote window

**File → Open Remote Window…** lists **SSH…** and, on Windows, your WSL distributions. Then give the folder, solution or project to open, as an absolute path on that machine, or `~` for your home folder there. A window with nothing open connects itself; otherwise another window opens.

From the command line:

```
daedalus --remote ssh:me@build-box /home/me/src/App
daedalus --remote ssh:me@build-box:2222 /home/me/src/App/App.slnx
daedalus --remote wsl:Ubuntu /home/me/src/App
```

- **SSH** runs your system's OpenSSH client, so `~/.ssh/config` (host aliases, `ProxyJump`, keys), your agent and `known_hosts` apply. Daedalus Studio never asks for or sees a password: use a key or an agent (`ssh-add`). A host that needs a password fails, and the **Remote** output channel shows ssh's message. Accept a new host's key once with `ssh` in a terminal first.
- **WSL** runs `wsl.exe` for the distribution.
- The other machine needs Linux or macOS with `sh`, `tar`, `gzip` and `sha256sum` (or `shasum`). To load, build and run .NET projects there, it needs the .NET SDK; editing files and the terminal work without it. Remote Windows machines aren't supported yet.

## The server

On the first connection, Daedalus Studio copies its server into `~/.daedalus-server/<version>/` on the other machine. It checks the copy against its SHA-256 there and starts it through ssh or `wsl.exe`. The server never opens a network port: it talks only through that ssh session's input and output. Later connections reuse the copy. A new Daedalus Studio version copies its own, so the server always matches the window.

## What works in a remote window

- **The status bar** shows the machine and the round trip (`WSL: Ubuntu · 3 ms`). Click it for the remote actions: the Ports window, Forward a Port, the Remote log, Close Remote Connection.
- **Solutions and folders** open as locally. The trust prompt names the folder on the other machine.
- **Editing.** Files open, save and reload when changed on the other machine, and the C#, JSON, XML and YAML services run there: completion, diagnostics, navigation, refactoring. Paths show under `\\wsl.localhost\<distribution>` for WSL, under `\\daedalus-remote.invalid\<host>` for SSH from Windows, and under `/daedalus-remote/<host>` on Linux and macOS.
- **Build**, **Run Without Debugging** (`Ctrl+F5`, in a terminal there) and **Start Debugging** (`F5`) run on the other machine. The first `F5` asks to copy the debugger (netcoredbg) there.
- **Terminals** open your login shell there.
- **Ports.** When a program there starts listening on a port, the port is forwarded to `localhost` here, on the same number when it's free. A notification offers to open it in the browser. A web app's launch URL opens once its port is forwarded. **View → Other Windows → Ports** lists the forwarded and listening ports: forward another one, stop one, or open one in the browser. Turn automatic forwarding off with **Remote → Forward Ports Automatically** (`remote.autoForwardPorts`).

Not yet in a remote window: Git, NuGet's package manager, Find in Files, the Test Explorer, the data, profiler, container and Aspire tools, C# Interactive, Hot Reload, multiple startup projects, plugins and the web language servers. Dev Containers, attaching to a running container, running tests remotely and reconnecting after a network drop come next. If the connection drops, unsaved changes stay in the editor; open the remote window again to reconnect.
