# Troubleshooting, logs and crash reports

## Output channels

View → Output (`Ctrl+Shift+U`) has a channel per component:

| Channel | Shows |
|---|---|
| Daedalus Studio | The IDE's own log: unexpected errors and plugin messages. |
| Build Host | Solution loading, evaluation and design-time builds. |
| Restore | `dotnet restore` output. |
| Build | The last build. |
| Language Services | The C# language service: analyzers that failed to load, project problems. |
| Run: *project* | A program started with Ctrl+F5. |
| Debug | The debug session (also in the Debug Console). |
| Git | Every git command Daedalus Studio ran, and its errors. |
| Plugins | Plugin activation and plugin logs. |

Help → Show Logs opens the Daedalus Studio channel. Crash reports are saved in the logs folder:

- Windows: `%LOCALAPPDATA%\Daedalus Studio\Logs`
- macOS: `~/Library/Logs/Daedalus Studio`
- Linux: `~/.local/state/daedalus-studio/logs`

## Common problems

| Symptom | What to check |
|---|---|
| "No .NET SDK was found" | Install the .NET SDK. If the solution has a `global.json`, install the SDK version it asks for. |
| Projects stay in "Restricted mode" | The folder isn't trusted. Click the status bar item to trust it. |
| Packages show as unresolved | Restore failed: see Output → Restore. |
| No IntelliSense in a project | Its design-time build failed (Output → Build Host). It is often a missing restore or SDK. |
| "The debugger (netcoredbg) was not found" | A source build: run `eng/get-netcoredbg.sh`. Packages include it. |
| Git features missing | Install git and make sure it is on the `PATH` (or set `DAEDALUS_GIT`). |
| Push or pull fails with an authentication error | Configure a git credential helper or SSH keys. Daedalus Studio doesn't prompt for passwords. |

A helper process that crashes (build host, language service, plugin host) is restarted automatically, and a notification tells you.

## Crash reports

Crash reports are **off by default** and **never leave your computer**. To turn them on, set Settings → Privacy → **Save Crash Reports**.

- **What a report contains**: the version, OS and runtime, the error with its stack trace, and the last lines of the IDE's log. These can include file paths.
- **Where it goes**: a text file in the `crashes` folder under the logs folder. Unexpected errors the IDE recovered from, and helper processes that died, are saved there too.
- **After a crash**: the next start tells you a report was saved. Help → Open Crash Reports Folder shows them.

To report a problem, open an issue and attach the report after reading it.
