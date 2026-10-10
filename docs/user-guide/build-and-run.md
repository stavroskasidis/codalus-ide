# Building and running

## Building

| Command | Shortcut |
|---|---|
| Build Solution | `Ctrl+Shift+B` |
| Rebuild Solution | |
| Clean Solution | |
| Build, Rebuild or Clean a project | Solution Explorer context menu, or the Build menu (acts on the selected project or the active document's project) |
| Cancel the build | `Ctrl+Break`, or click the build item in the status bar |

- Builds use the active configuration, restore packages first, and save modified documents before they start (setting **Build and Run → Save All Before Build**).
- The output is in View → Output → Build.
- Errors and warnings go to the **Error List**, which opens when a build fails. A problem that both the build and the language service report is listed once.
- Only one build runs at a time.

Builds run MSBuild in its own worker processes, the same way `dotnet build` does, and only in trusted folders. With an SDK from 2.1.200 to 5.0 pinned in `global.json`, they run in the build host's own process instead ([Older .NET versions](dotnet-versions.md)).

## Running

**Debug → Start Without Debugging** (`Ctrl+F5`) builds the startup project, then starts it:

- **The startup project** is the first executable project in the solution, unless you chose another with **Set as Startup Project** (Solution Explorer) or Debug → Select Startup Project. Your choice is remembered per solution.
- **Launch profiles** come from `Properties/launchSettings.json`. Pick one with the status bar's startup item, or with Debug → Select Launch Profile. `Project` and `Executable` profiles are supported:
  - command-line arguments
  - working directory
  - environment variables
  - `applicationUrl`
  - `launchBrowser`, which opens the app's URL once it is listening
- **Output**: the program's output appears in View → Output → Run: *project*. It has no console input there (reads see end of input). On Windows, the setting **Run Console Apps in an External Console** gives console apps a window of their own instead.
- **Stop**: Debug → Stop Running Programs (`Shift+F5`), or the status bar item. This ends the program and the processes it started.
- **The runtime**: a program runs on the runtimes of the SDK the solution builds with, as with `dotnet run`: its apphost gets that SDK's folder in `DOTNET_ROOT` unless you set it. On Apple silicon, .NET 5 and earlier run as x64 programs on the x64 runtime; on Linux, .NET Core 2.x and 3.x get the system's ICU. The output says when Daedalus changed something, and [Older .NET versions](dotnet-versions.md) says why.
