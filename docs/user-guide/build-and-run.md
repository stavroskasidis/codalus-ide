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

Builds run MSBuild in its own worker processes, the same way `dotnet build` does, and only in trusted folders.

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
