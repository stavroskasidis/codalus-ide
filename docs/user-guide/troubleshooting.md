# Troubleshooting, logs and crash reports

## Output channels

View → Output (`Ctrl+Shift+U`) has a channel per component:

| Channel | Shows |
|---|---|
| Codalus IDE | The IDE's own log: unexpected errors and plugin messages. |
| Build Host | Solution loading, evaluation and design-time builds. |
| Restore | `dotnet restore` output. |
| Build | The last build. |
| Language Services | The C# language service: analyzers that failed to load, project problems. |
| Run: *project* | A program started with Ctrl+F5. |
| Debug | The debug session (also in the Debug Console). |
| Git | Every git command Codalus IDE ran, and its errors. |
| Plugins | Plugin activation and plugin logs. |

Help → Show Logs opens the Codalus IDE channel. Crash reports are saved in the logs folder:

- Windows: `%LOCALAPPDATA%\Codalus\Logs`
- macOS: `~/Library/Logs/Codalus`
- Linux: `~/.local/state/codalus/logs`

## Common problems

| Symptom | What to check |
|---|---|
| "No .NET SDK was found" | Install the .NET SDK. If the solution has a `global.json`, install the SDK version it asks for. |
| Nothing loads, and the helper processes (build host, language service, plugin host) stop at start with "You must install or update .NET" | They run on .NET 11 or newer: install the .NET 11 SDK (Help → Check Environment offers it). SDKs of older versions alone can't start them. |
| Projects stay in "Restricted mode" | The folder isn't trusted. Click the status bar item to trust it. |
| Packages show as unresolved | Restore failed: see Output → Restore. |
| No IntelliSense in a project | Its design-time build failed (Output → Build Host). It is often a missing restore or SDK. |
| "The debugger (netcoredbg) was not found" | A source build: run `eng/get-netcoredbg.sh`. Packages include it. |
| Git features missing | Install git and make sure it is on the `PATH` (or set `CODALUS_GIT`). |
| On macOS, git or restore fails with "Failed to connect to … port 443" or "Couldn't connect to server", though the same command works in Terminal | macOS's local network privacy may be blocking Codalus IDE: turn it on in System Settings → Privacy & Security → Local Network. Servers on your company network or a VPN count as local. Codalus IDE also takes the proxy and certificate variables your login shell exports (`HTTPS_PROXY`, `NO_PROXY`, `SSL_CERT_FILE`, …); a proxy set only in `~/.zshrc` reaches it after a restart. |
| Push or pull fails with an authentication error | Configure a git credential helper or SSH keys. Codalus IDE doesn't prompt for passwords. |

A helper process that crashes (build host, language service, plugin host) is restarted automatically, and a notification tells you.

## Crash reports

Crash reports are **off by default** and **never leave your computer**. To turn them on, set Settings → Privacy → **Save Crash Reports**.

- **What a report contains**: the version, OS and runtime, the error with its stack trace, and the last lines of the IDE's log. These can include file paths.
- **Where it goes**: a text file in the `crashes` folder under the logs folder. Unexpected errors the IDE recovered from, and helper processes that died, are saved there too.
- **After a crash**: the next start tells you a report was saved. Help → Open Crash Reports Folder shows them.

To report a problem, open an issue and attach the report after reading it.
