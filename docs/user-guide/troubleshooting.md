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
| While debugging a Blazor Web App, a notification says its server keeps the code it started with, or a breakpoint says "The server runs the code it started with" | With Hot Reload while debugging, the server runs under the debugger, and the .NET runtime doesn't apply edits to it: WebAssembly components update in the browser, the server's code after Restart (`Ctrl+Shift+F5`). The page still stops at those breakpoints ([Web development](web.md#debugging-the-server-and-the-page-together)). |
| "Address already in use" after an edit restarted a Blazor Web App | A server from before the restart still holds the port: one held at a breakpoint by a debugger other than Codalus IDE's, or left by a crash. Codalus IDE ends the ones it started. Find the process listening on the port (`lsof -i :5000` on Linux and macOS, `netstat -ano` on Windows) and end it. |
| `Ctrl+F5` on a Blazor Web App opens nothing, or the page is "not found" | The startup project is the `.Client` project, whose dev host serves nothing at `/` in a Blazor Web App: set the server project as the startup project. Codalus IDE picks the server by default. |
| The browser doesn't open after Run | No program opens URLs here (on Linux, `xdg-open`): Output → Run says so, with the address to open yourself. |
| On Windows, **Restart to update** starts the same version again | A program still running holds a file or folder of the install, so the update can't replace it: `%LOCALAPPDATA%\velopack\velopack_Codalus.log` ends with "one or more running processes prevented it". Before 0.7.0 it was usually adb's server, started when the IDE listed Android devices: run `adb kill-server` (or restart Windows), then **Restart to update**. |

A helper process that crashes (build host, language service, plugin host) is restarted automatically, and a notification tells you. One that exits while it starts isn't restarted (see above).

## Crash reports

Crash reports are **off by default** and **never leave your computer**. To turn them on, set Settings → Privacy → **Save Crash Reports**.

- **What a report contains**: the version, OS and runtime, the error with its stack trace, and the last lines of the IDE's log. These can include file paths.
- **Where it goes**: a text file in the `crashes` folder under the logs folder. Unexpected errors the IDE recovered from, and helper processes that died, are saved there too.
- **After a crash**: the next start tells you a report was saved. Help → Open Crash Reports Folder shows them.

To report a problem, use **Help → Report a Problem…** (below) and attach the report after reading it.

## Reporting a problem or suggesting a feature

**Help → Report a Problem…** and **Help → Suggest a Feature…** (also in the command palette) open a new issue on [the project's GitHub repository](https://github.com/stavroskasidis/codalus-ide/issues), filled in from a dialog. The IDE sends nothing itself: **Open in Browser** opens the issue's form in your browser, where you review it, sign in to GitHub and submit it, or close the tab.

The dialog asks for:

- a **title** and a description (**What happened** for a problem, **The problem** for a feature);
- **Steps to reproduce** for a problem, or a **Proposal** for a feature.

Under them, **What's attached** shows exactly what goes into the issue besides your text:

| Report | Attaches |
|---|---|
| A problem | The version of Codalus IDE, the OS and its architecture, the .NET runtime, the .NET SDK (`dotnet --version` in the solution's folder) and, unless you clear **Include the last 200 lines of the IDE's log**, the end of the Codalus IDE log (Help → Show Logs). |
| A feature | Only the version and the OS. |

The log is **redacted** first: secrets the IDE recognizes (tokens, private keys, the values of password, secret, token and key settings and connection-string parts) become `[redacted]`, and your home folder becomes `~`. Redaction works by patterns, so read the preview before you open the issue.

A link can carry about 8,000 characters. When the log doesn't fit, **Open in Browser** saves it to `feedback/codalus-log-<date>-<time>.txt` in the logs folder (above), shows the file in your file manager, and leaves a line in the issue's logs field asking you to attach it: drag the file onto the field. A description too long for the link is cut, with a note; **Copy** puts the whole report on the clipboard as Markdown, to paste instead.

A crash report isn't attached automatically: if one was saved, attach it from Help → Open Crash Reports Folder after reading it. A security problem goes through private reporting instead, as [SECURITY.md](../../SECURITY.md) describes.
