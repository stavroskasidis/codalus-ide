# Installing and first start

## Prerequisites

- **The .NET SDK**, 10.0 or newer; .NET 11 works too, release candidates included. Daedalus Studio loads and builds solutions with the SDK's MSBuild, so it must be installed for any solution you open. It must also match the version a `global.json` asks for.
  - Older SDKs (8.0, 9.0) work for solutions that pin them in `global.json`, as long as .NET 10 or newer is also installed: Daedalus' helper processes run on it.
- **git** (2.30 or newer) on the `PATH`, for the Git features.
- A 64-bit OS: Windows 11 (x64); macOS 15 or newer on Apple silicon (the macOS versions .NET supports); or a current Linux distribution (x64 or arm64) with X11, or Wayland through XWayland.

The debugger (netcoredbg) is included in the packages.

## Installing

Every release on the [releases page](https://github.com/stavroskasidis/daedalus-studio/releases) has installers for each platform, and `SHA256SUMS.txt` to check them against. Stable releases come from `master`; nightly builds (`0.2.0-nightly.20261011.1`) come from `develop` every night it changed, and are marked as prereleases.

| OS | Download | How |
|---|---|---|
| Windows | `daedalus-studio-<version>-win-x64-setup.exe` | Run it. It installs for you alone (no administrator rights) into `%LocalAppData%\DaedalusStudio`, adds Daedalus Studio to the Start menu and `daedalus` to your `PATH`, and updates itself. Uninstall it from Settings → Apps. Or: `winget install DaedalusStudio.DaedalusStudio`, once the package is published. |
| Windows | `daedalus-studio-<version>-win-x64.zip` | A portable build: unzip anywhere and run `daedalus.exe`. It doesn't update itself. |
| macOS | `daedalus-studio-<version>-osx-arm64.pkg` | Open it and choose to install for you (`~/Applications`) or for every user (`/Applications`). The app updates itself. |
| macOS | `daedalus-studio-<version>-osx-arm64.app.zip` | The app on its own: unzip it and move it where you like. |
| Linux | `.AppImage` | `chmod +x` the file and run it. It updates itself. |
| Linux | `.deb` and `.rpm` | Install from our apt or dnf repository, so that they update with the system (the instructions are on the [packages page](https://stavroskasidis.com/daedalus-studio/)), or install the file: `sudo apt install ./daedalus-studio-<version>-linux-x64.deb`, `sudo dnf install ./daedalus-studio-<version>-linux-x64.rpm`. |
| Linux (openSUSE) | `.rpm` | `zypper` installs from the same rpm repository (the [packages page](https://stavroskasidis.com/daedalus-studio/) has the commands), so it updates with the system. |
| Linux (Arch) | AUR | `daedalus-studio-bin`: `yay -S daedalus-studio-bin`, or `makepkg -si` in a clone of `https://aur.archlinux.org/daedalus-studio-bin.git`. Each stable release also has its PKGBUILD (`aur-daedalus-studio-bin-<version>.zip`). |
| Linux | `.tar.gz` | Extract it and run `daedalus-studio/daedalus`. |

**The installers aren't code-signed yet**, so the first start asks for confirmation:
- **Windows** (SmartScreen, "Windows protected your PC"): choose **More info**, then **Run anyway**.
- **macOS** (Gatekeeper, "Apple could not verify…"): close the message, open System Settings → Privacy & Security, and choose **Open Anyway** next to the installer or the app, then open it again. (Since macOS 15, right-click → Open no longer skips the check.)

From a Windows terminal, `daedalus` runs the installed app and waits for it, so `git config core.editor "daedalus --wait"` works. As a windowed program it prints `--version` and `--help` to the console window rather than to a pipe.

To build from source, see the repository's README.

## First start

The Start page opens with **Get Started**: choose a color theme and a keyboard scheme (Default, VS Code, ReSharper or Emacs), import your VS Code settings and key bindings or a file exported from another machine, and adjust Fonts and Colors. **Done** hides these steps (Help → Get Started shows them again). Below them are the common actions, recent solutions and a tip that runs what it describes.

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
