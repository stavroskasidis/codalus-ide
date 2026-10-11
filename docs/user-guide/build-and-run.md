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

### Build settings

Under **Settings → Build and Run**:

- **Build Output Verbosity**: how much MSBuild writes to Output → Build: *quiet*, *minimal* (the default), *normal* (the projects, command lines and copies), *detailed* or *diagnostic*.
- **Build Log File Verbosity**: also write each build's log to `build.log` in the logs folder (Build → Open Build Log).
- **Maximum Parallel Project Builds**: how many projects build at once (0: one per processor).
- **Fast Up-to-Date Check** (on): see below.
- **Before Running**: what `F5` and `Ctrl+F5` do first: build the project (the default; quick when nothing changed), ask when it is out of date, or never build.
- **Write a Binary Log for Every Build**, **Run Code Analysis on Build** (analyzers during builds; the editor's live analysis isn't affected) and **Run Custom Tools on Save**.

### The fast up-to-date check

After each build, Daedalus Studio records what every project was built from (its files, project file and imports, packages, and the projects it references) and what it built. The next Build skips MSBuild entirely when none of it changed: Output → Build then says *up to date, so MSBuild didn't run*. When something did change, it names the first out-of-date project and why, and MSBuild builds as usual. **Build → Check Whether Projects Are Up to Date** lists each project's verdict. A project can opt out with `<DisableFastUpToDateCheck>true</DisableFastUpToDateCheck>`; files a custom target reads can be declared as `UpToDateCheckInput` items.

### Batch Build

**Build → Batch Build…** lists every project with each configuration and platform it has. Check the ones you want and choose Build, Rebuild or Clean: they build one after another, each with a result. Your checks are kept until you close Daedalus Studio.

### Build timings and binary logs

- **Build → Build Timings** shows the last build: each project build on a timeline with its node and duration, the **critical path** (the chain of project builds that decided how long the build took), and the slowest targets and tasks.
- **Build → Build Solution with Binary Log** builds with MSBuild's binary logger and opens the log; **Build → Open Binary Log…** opens any `.binlog`. The viewer shows the build as a tree (projects, targets, tasks, messages, properties and items), loads it a level at a time, and searches it: pick a result to reveal it in the tree; `Enter` or double-click on a node with a file opens the file. Logs are kept in the logs folder (`binlogs`, the newest 20).

### Build events

The project properties' **Build Events** page edits the commands that run before and after the project builds, and when the post-build event runs (on a successful build, always, or when the build updates the output). They are stored the way Visual Studio stores them: `PreBuild` and `PostBuild` targets with an `Exec` command. MSBuild macros such as `$(TargetDir)` work in them.

### Custom tools

A file whose **Custom Tool** is set (Properties window) generates another file: **Run Custom Tool** (Solution Explorer context menu) or saving the file runs it, writes the output next to it and nests it underneath.

- `ResXFileCodeGenerator` and `PublicResXFileCodeGenerator`: a `.resx` file's strongly typed resource class (`Resources.Designer.cs`).
- `TextTemplatingFileGenerator`: a T4 text template (`.tt`), with its `include`, `import`, `assembly` and `output` directives. Templates run code, so they run only in trusted folders, in a process of their own. `.tt` files are colored, and snippets cover the directives and blocks.
- Plugins can add custom tools.

Errors go to Output → Custom Tool.

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
