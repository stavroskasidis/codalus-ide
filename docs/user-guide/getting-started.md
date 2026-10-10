# Installing and first start

## Prerequisites

- **The .NET SDK** (8.0 or newer; 10.0 recommended). Daedalus Studio loads and builds solutions with the SDK's MSBuild, so it must be installed for any solution you open, and must match the version a `global.json` asks for.
- **git** (2.30 or newer) on the `PATH`, for the Git features.
- A 64-bit OS: Windows 10 22H2 or Windows 11 (x64); macOS 15 or newer on Apple silicon (the macOS versions .NET supports); or a current Linux distribution (x64 or arm64) with X11, or Wayland through XWayland. The packages are x64 on Linux for now; arm64 builds from source.

The debugger (netcoredbg) is included in the packages.

## Installing

| OS | Package | How |
|---|---|---|
| Windows | `daedalus-studio-<version>-win-x64.zip` | Unzip anywhere and run `daedalus.exe`, or install it with winget once the package is published: `winget install DaedalusStudio.DaedalusStudio`. |
| macOS | `daedalus-studio-<version>-osx-arm64.dmg` | Open the image and drag Daedalus Studio to Applications. Preview builds aren't notarized yet: the first time, right-click the app and choose Open. |
| Linux | `.deb` | `sudo apt install ./daedalus-studio-<version>-linux-x64.deb`, then run `daedalus`. |
| Linux | `.AppImage` | `chmod +x` the file and run it. |
| Linux | `.tar.gz` | Extract it and run `daedalus-studio/daedalus`. |

To build from source, see the repository's README.

## First start

The Start page offers to open a solution, the command palette, settings, keyboard shortcuts and color themes.

1. **Open a solution**: File → Open Solution or Project (`Ctrl+Shift+O`). Pick a `.sln`, `.slnx` or a project file (`.csproj`). You can also pass one on the command line: `daedalus path/to/App.sln`.
2. **Trust the folder.** Opening a solution runs code from it: MSBuild evaluates its project files and imports, and restore runs NuGet targets. The first time you open a folder, Daedalus Studio asks whether you trust its authors.
   - **Trust and Open** loads the projects, restores packages and starts the language service.
   - **Open Without Trusting** shows the solution's structure only, in restricted mode. You can trust it later: click "Restricted mode" in the status bar.
3. The Solution Explorer fills in as projects are evaluated. The status bar shows the progress, the configuration (`Debug | Any CPU`), the startup project and, in a git repository, the branch.

## Command line

```bash
daedalus [solution|project|folder] [--user-data-dir <dir>] [--plugin-dir <dir>] [--version]
```

`--user-data-dir` keeps settings, state and plugins in one folder, for a portable setup or a clean profile.
