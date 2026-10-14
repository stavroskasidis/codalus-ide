# Installing and first start

## Prerequisites

- **The .NET SDK**, 10.0 or newer; .NET 11 works too, release candidates included. Codalus IDE loads and builds solutions with the SDK's MSBuild, so it must be installed for any solution you open. It must also match the version a `global.json` asks for.
  - Older SDKs (8.0, 9.0) work for solutions that pin them in `global.json`, as long as .NET 10 or newer is also installed: Codalus' helper processes run on it.
- **git** (2.30 or newer) on the `PATH`, for the Git features.
- A 64-bit OS: Windows 11 (x64); macOS 15 or newer on Apple silicon (the macOS versions .NET supports); or a current Linux distribution (x64 or arm64) with X11, or Wayland through XWayland.

The debugger (netcoredbg) is included in the packages.

**Missing something?** When Codalus IDE starts without the .NET SDK or git, it offers to install them. When a solution's `global.json` asks for an SDK that isn't installed, it offers that SDK when the solution opens.
- **What's found**: Help → Check Environment lists every tool it uses, with its version, its path and what needs it: the .NET SDK, git and Git LFS, Docker or Podman, and WSL on Windows.
- **How it installs**:
  - Windows: winget, or the download page when winget isn't there.
  - macOS: the .NET SDK into `~/.dotnet` through Microsoft's `dotnet-install` script, and git with Apple's Command Line Tools.
  - Linux: the .NET SDK into `~/.dotnet` the same way. git comes from your distribution's package manager (apt, dnf, zypper, pacman or apk), in the integrated terminal, where `sudo` asks for your password.
- **What it asks first**: every install shows the exact command it runs, and nothing runs until you agree. An SDK in `~/.dotnet` is used from then on, even though it isn't on the system's `PATH`.

## Installing

Every release on the [releases page](https://github.com/stavroskasidis/codalus-ide/releases) has installers for each platform, and `SHA256SUMS.txt` to check them against. Stable releases come from `master`; nightly builds (`0.2.0-nightly.20261011.1`) come from each change to `develop` that passes the tests on every platform, and are marked as prereleases.

| OS | Download | How |
|---|---|---|
| Windows | `codalus-<version>-win-x64-setup.exe` | Run it. It installs for you alone (no administrator rights) into `%LocalAppData%\Codalus`, adds Codalus IDE to the Start menu and `codalus` to your `PATH`, and updates itself. Uninstall it from Settings → Apps. Or: `winget install Codalus.Codalus`, once the package is published. |
| Windows | `codalus-<version>-win-x64.zip` | A portable build: unzip anywhere and run `codalus.exe`. It doesn't update itself. |
| macOS | `codalus-<version>-osx-arm64.pkg` | Open it and choose to install for you (`~/Applications`) or for every user (`/Applications`). The app updates itself. |
| macOS | `codalus-<version>-osx-arm64.app.zip` | The app on its own: unzip it and move it where you like. |
| Linux | `.AppImage` | `chmod +x` the file and run it. It updates itself. |
| Linux | `.deb` and `.rpm` | Install from our apt or dnf repository, so that they update with the system (the instructions are on the [download page](https://stavroskasidis.com/codalus-ide/download/#linux)), or install the file: `sudo apt install ./codalus-<version>-linux-x64.deb`, `sudo dnf install ./codalus-<version>-linux-x64.rpm`. |
| Linux (openSUSE) | `.rpm` | `zypper` installs from the same rpm repository (the [download page](https://stavroskasidis.com/codalus-ide/download/#zypper) has the commands), so it updates with the system. |
| Linux (Arch) | AUR | `codalus-bin`: `yay -S codalus-bin`, or `makepkg -si` in a clone of `https://aur.archlinux.org/codalus-bin.git`. Each stable release also has its PKGBUILD (`aur-codalus-bin-<version>.zip`). |
| Linux | `.tar.gz` | Extract it and run `codalus/codalus`. |

**The installers aren't code-signed yet**, so the first start asks for confirmation:
- **Windows** (SmartScreen, "Windows protected your PC"): choose **More info**, then **Run anyway**.
- **macOS** (Gatekeeper, "Apple could not verify…"): close the message, open System Settings → Privacy & Security, and choose **Open Anyway** next to the installer or the app, then open it again. (Since macOS 15, right-click → Open no longer skips the check.)

From a Windows terminal, `codalus` runs the installed app and waits for it, so `git config core.editor "codalus --wait"` works. As a windowed program it prints `--version` and `--help` to the console window rather than to a pipe.

To build from source, see the repository's README.

## First start

**The start window** asks "What would you like to do?" when Codalus IDE starts without a solution:
- **Recent list**: on the left, your recent solutions, projects and folders, grouped as Pinned, Today, Yesterday, This week, This month and Older.
  - Search them with `Alt+S`; `Enter` opens the first match.
  - Pin one with its pin button.
  - Right-click for Open in New Window, Copy Path and Remove from List (`Delete` removes the selected entry).
  - An entry whose files are gone offers to leave the list.
- **Actions**: on the right, Create a new project, Open a project or solution, Open a folder, and Clone a repository.
- **Continue without code** (or `Esc`) opens the empty IDE.

File → Start Window brings it back. Environment → On Startup (`workbench.startup`) can open an empty window or the last solution instead.

The Start page opens with **Get Started**: choose a color theme and a keyboard scheme (Default, VS Code, ReSharper or Emacs), import your VS Code settings and key bindings or a file exported from another machine, and adjust Fonts and Colors. **Done** hides these steps (Help → Get Started shows them again). Below them are the common actions, recent solutions and a tip that runs what it describes.

1. **Open a solution**: File → Open Solution or Project (`Ctrl+Shift+O`). Pick a `.sln`, `.slnx` or a project file (`.csproj`). You can also pass one on the command line: `codalus path/to/App.sln`.
2. **Trust the folder.** Opening a solution runs code from it: MSBuild evaluates its project files and imports, and restore runs NuGet targets. The first time you open a folder, Codalus IDE asks whether you trust its authors.
   - **Trust and Open** loads the projects, restores packages and starts the language service.
   - **Open Without Trusting** shows the solution's structure only, in restricted mode. You can trust it later: click "Restricted mode" in the status bar.
3. The Solution Explorer fills in as projects are evaluated. The status bar shows the progress (*Loading project 29 of 96: …* with a progress bar), the configuration (`Debug | Any CPU`), the startup project and, in a git repository, the branch.

**Running tasks** (loading projects, restoring packages, builds, and plugins' work) show at the left of the status bar where "Ready" is: the newest task's text and a progress bar. **✕** cancels it, and **…** lists every running task (also the command Show Running Tasks).

## Command line

```bash
codalus [solution|project|folder] [--user-data-dir <dir>] [--plugin-dir <dir>] [--version]
```

`--user-data-dir` keeps settings, state and plugins in one folder, for a portable setup or a clean profile.
