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
| "Codalus IDE needs the .NET … runtime, which isn't installed" | The helper processes (build host, language service, plugin host and the others) run on .NET 11 or newer: choose **Install .NET 11.0** in the notification, or Help → Check Environment. SDKs of older versions alone can't start them, and neither can an earlier release candidate. On Windows on Arm, the x64 build needs the x64 .NET (`C:\Program Files\dotnet\x64`): the Arm64 .NET doesn't count, and Install .NET installs the x64 one. If you're sure the runtime is installed and the check is wrong, set `CODALUS_SKIP_RUNTIME_CHECK=1` and report it. |
| A helper process stops at start, with an error that quotes what it wrote | It exited while starting (a missing file, a bad setting, a runtime the check didn't catch): the error has its exit code and the first lines it wrote to standard error (for a helper that crashes later, the exception it reports, from its `Unhandled exception.` line), and its Output channel has all of them. It isn't started again, because it would stop the same way: fix the cause, then reopen the solution or restart Codalus IDE. |
| Projects stay in "Restricted mode" | The folder isn't trusted. Click the status bar item to trust it. |
| Packages show as unresolved | Restore failed: see Output → Restore. |
| No IntelliSense in a project | Its design-time build failed (Output → Build Host). It is often a missing restore or SDK. |
| "The debugger (netcoredbg) was not found" | A source build: run `eng/get-netcoredbg.sh`. Packages include it. |
| Git features missing | Install git and make sure it is on the `PATH` (or set `CODALUS_GIT`). |
| On macOS, git or restore fails with "Failed to connect to … port 443" or "Couldn't connect to server", though the same command works in Terminal | macOS's local network privacy may be blocking Codalus IDE: turn it on in System Settings → Privacy & Security → Local Network. Servers on your company network or a VPN count as local. Codalus IDE also takes the proxy and certificate variables your login shell exports (`HTTPS_PROXY`, `NO_PROXY`, `SSL_CERT_FILE`, …); a proxy set only in `~/.zshrc` reaches it after a restart. |
| Push or pull fails with an authentication error | Configure a git credential helper or SSH keys. Codalus IDE doesn't prompt for passwords. |

A helper process that crashes (build host, language service, plugin host) is restarted automatically, and a notification tells you. One that exits while it starts isn't restarted (see above).

## Crash reports

Crash reports are **off by default** and **never leave your computer**. To turn them on, set Settings → Privacy → **Save Crash Reports**.

- **What a report contains**: the version, OS and runtime, the error with its stack trace, and the last lines of the IDE's log. These can include file paths.
- **Where it goes**: a text file in the `crashes` folder under the logs folder. Unexpected errors the IDE recovered from, and helper processes that died, are saved there too.
- **After a crash**: the next start tells you a report was saved. Help → Open Crash Reports Folder shows them.

To report a problem, open an issue and attach the report after reading it.
