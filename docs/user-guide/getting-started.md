# Installing and first start

## Prerequisites

- **The .NET 11 SDK**. Codalus IDE runs its helper processes on .NET 11 or newer, and loads and builds solutions with the SDK's MSBuild, so an SDK must be installed for any solution you open. It must also match the version a `global.json` asks for.
  - Other SDKs work next to it: .NET 10's, and older ones pinned in `global.json` down to 2.1.200 (see [Older .NET versions](dotnet-versions.md)). On their own, without .NET 11 or newer, they can't start Codalus' helper processes.
  - Projects need the runtimes they target to run and debug, as with `dotnet run`: install .NET 10's runtime (or SDK) for `net10.0` projects.
- **git** (2.30 or newer) on the `PATH`, for the Git features.
- A 64-bit OS: Windows 11 (x64); macOS 15 or newer on Apple silicon (the macOS versions .NET supports); or a current Linux distribution (x64 or arm64) with X11, or Wayland through XWayland.

The debugger is included in the packages: the Codalus debugger, Codalus IDE's own, and netcoredbg as a fallback for one release.

**Missing something?** The [first-run setup](#the-first-run-setup)'s Tools step checks for the .NET SDK and git. On later starts without them, Codalus IDE offers to install them. When a solution's `global.json` asks for an SDK that isn't installed, it offers that SDK when the solution opens.
- **The .NET runtime Codalus IDE needs**: its helper processes (the plugin, build, language, debug and other hosts) ask for a .NET runtime (`11.0.0` or a later version). Before it starts one, Codalus IDE checks that the runtime is installed in the .NET folder it uses, by .NET's own roll-forward rules. When it isn't, you get one notification, not one error per plugin: "Codalus IDE needs the .NET 11.0.0 runtime, which isn't installed (found: …)", with **Install .NET 11.0**. That installs the .NET 11 SDK, which brings the runtime, the same way as the SDK below, and then starts the plugins, language services and solution that couldn't start, without a restart. Check Environment lists it as **.NET runtime for Codalus IDE**.
- **Which .NET folder**: with several (`/usr/local/share/dotnet` and `~/.dotnet` on a Mac, say), Codalus IDE uses the one on your `PATH` or in `DOTNET_ROOT` when it has the runtime, and otherwise a folder that has both an SDK and the runtime. A runtime installed on its own where .NET looks by default (a distribution's `dotnet-runtime-11.0`, Microsoft's runtime package) serves the helper processes as it is. After an install, the folder is chosen again.
- **What's found**: Help → Check Environment lists every tool it uses, with its version, its path and what needs it: the .NET SDK, the .NET runtime Codalus IDE itself needs, git and Git LFS, Docker or Podman, and WSL on Windows. Each start checks only the first three, which it offers to install; the others are checked when Check Environment opens.
- **How it installs**: Install .NET lists the ways to install it on your machine and picks the one that fits. You can pick another. Each way shows its exact command, and says so when the `dotnet` you type in a terminal won't see what it installs. The way that fits installs where that `dotnet` is (the first on your login shell's `PATH`), so the terminal and Codalus IDE use the same SDKs:
  - Windows: winget, or the download page.
  - macOS: Microsoft's installer package (`.pkg`) into `/usr/local/share/dotnet`, which every shell has on its `PATH`. It runs in the integrated terminal (`curl`, then `sudo installer`), where `sudo` asks for your password. This fits unless your `dotnet` is in your home folder: then it's Microsoft's `dotnet-install` script into that folder (`~/.dotnet`), with no password. The download page is the third way. git comes with Apple's Command Line Tools.
  - Linux: your distribution's `dotnet-sdk-11.0` package (apt, dnf or zypper) when it has that version and your `dotnet` is a package's, or there's none yet. It runs in the integrated terminal, where `sudo` asks for your password. When your `dotnet` is the `dotnet-sdk` snap, the snap's channel (this replaces the SDK the snap has). Otherwise `dotnet-install` into `~/.dotnet`, with no password: it doesn't install into a package manager's folder. A version a `global.json` pins comes from `dotnet-install` (or the Mac's `.pkg` of that version), since a package installs only its channel's newest. git comes from your distribution's package manager (apt, dnf, zypper, pacman or apk), in the integrated terminal.
- **What it asks first**: every install shows the exact command it runs, and nothing runs until you agree. When an install in the terminal ends, Codalus IDE checks again and moves to the new .NET without a restart. An SDK in `~/.dotnet` is used from then on, even when it isn't on your `PATH`.

## Installing

Every release on the [releases page](https://github.com/stavroskasidis/codalus-ide/releases) has installers for each platform, and `SHA256SUMS.txt` to check them against. Stable releases come from `master`; nightly builds (`0.2.0-nightly.20261011.1`) come from each change to `develop` that passes the tests on every platform, and are marked as prereleases.

| OS | Download | How |
|---|---|---|
| Windows | `codalus-<version>-win-x64-setup.exe` | Run it. It installs for you alone (no administrator rights) into `%LocalAppData%\Codalus`, adds Codalus IDE to the Start menu and `codalus` to your `PATH`, and updates itself. Uninstall it from Settings → Apps. Or: `winget install Codalus.Codalus`, once the package is published. |
| Windows | `codalus-<version>-win-x64.zip` | A portable build: unzip anywhere and run `codalus.exe`. It doesn't update itself. |
| macOS | `codalus-<version>-osx-arm64.pkg` | Open it and choose to install for you (`~/Applications`) or for every user (`/Applications`). The app belongs to you either way, and updates itself. |
| macOS | `codalus-<version>-osx-arm64.app.zip` | The app on its own: unzip it and move it where you like. |
| Linux (Debian, Ubuntu, Mint) | `.deb` | Install from our apt repository (the [download page](https://codalus.dev/download/#apt) has the commands), so it updates with the system, or install the file: `sudo apt install ./codalus-<version>-linux-x64.deb`. |
| Linux (Fedora, RHEL) | `.rpm` | Install from our dnf repository ([commands](https://codalus.dev/download/#dnf)), so it updates with the system, or install the file: `sudo dnf install ./codalus-<version>-linux-x64.rpm`. |
| Linux (openSUSE) | `.rpm` | `zypper` installs from the same rpm repository ([commands](https://codalus.dev/download/#zypper)), so it updates with the system. |
| Linux (Arch, Manjaro, EndeavourOS) | `codalus-bin` | Run the install command ([below](#arch-linux)) as yourself: it builds and installs the package with pacman. Run it again to update. |
| Linux (any other) | `.AppImage` | One file that runs on any distribution and updates itself; make it executable, then open it ([below](#the-appimage)). |
| Linux | `.tar.gz` | Extract it and run `codalus/codalus`. It doesn't update itself. |

The packages (and the Arch Linux install script's) add Codalus IDE to the applications menu and `codalus` to your `PATH`. The AppImage and the `.tar.gz` don't.

**The macOS installer and app are signed and notarized** (from 0.13.0), so they open without a warning. **The Windows installer isn't code-signed yet**, so its first start asks for confirmation (SmartScreen, "Windows protected your PC"): choose **More info**, then **Run anyway**.

A macOS build from before 0.13.0 isn't signed: if Gatekeeper says "Apple could not verify…", close the message, open System Settings → Privacy & Security, and choose **Open Anyway** next to the installer or the app, then open it again. (Since macOS 15, right-click → Open no longer skips the check.)

From a Windows terminal, `codalus` runs the installed app and waits for it, so `git config core.editor "codalus --wait"` works. As a windowed program it prints `--version` and `--help` to the console window rather than to a pipe.

### Arch Linux

```bash
curl -fsSL https://codalus.dev/install-arch.sh | bash
```

The script ([read it](https://codalus.dev/install-arch.sh)) downloads the newest stable release's PKGBUILD (`aur-codalus-bin-<version>.zip`, on each release), builds it with `makepkg`, which downloads the release's `.tar.gz` and checks its SHA-256, and installs the package, `codalus-bin`, with pacman. Run it as yourself, not as root: pacman asks before it installs, and `sudo` asks for your password. It installs `fakeroot` first when `makepkg` doesn't have it.

- **Updating**: run the same command again. `pacman -Syu` doesn't update it, since no repository serves it; when a release is out, Codalus IDE says so with the command, and **Copy Command** copies it.
- **Options**, after `bash -s --`: `--version 0.10.0` installs that release, and `--noconfirm` doesn't ask (where there's no terminal to ask on, it's needed).
- **Removing**: `sudo pacman -R codalus-bin`.
- **By hand**: unzip the release's `aur-codalus-bin-<version>.zip` and run `makepkg -si` in its folder.

The package isn't on the AUR yet, while the AUR's registrations are closed; the PKGBUILD is the one it will carry.

### The AppImage

1. **Make it executable**: in GNOME Files, right-click it, choose Properties and turn on **Executable as Program**; in Dolphin, Properties → Permissions → **Is executable**. Or in a terminal: `chmod +x codalus-<version>-linux-x64.AppImage`.
2. **Open it**: double-click it, or run `./codalus-<version>-linux-x64.AppImage`.

It needs FUSE, which desktop installations have: the `fuse3` package. (It doesn't need the older `libfuse2` that some AppImages ask for: its runtime has FUSE built in.) Where FUSE isn't available, in a container or a minimal installation, it stops with "Cannot mount AppImage": start it with `--appimage-extract-and-run`, or set `APPIMAGE_EXTRACT_AND_RUN=1`, and it runs from a temporary folder instead.

Updates replace the file where it is, so put it where it will stay (`~/Applications`, say) before the first start. [System integration](settings.md#system-integration)'s Open with Codalus IDE starts the file from where it was when you turned it on.

### The command line

| Command | What it does |
|---|---|
| `codalus App.slnx`, `codalus src/App.csproj`, `codalus folder` | Opens the solution, project or folder: in the running window when it has nothing open (it comes to the front), otherwise in a new window. [System integration](settings.md#system-integration)'s Open with Codalus IDE starts the same command. |
| `codalus Program.cs:12:5` | Opens the file in the running window, at line 12, column 5 (`:column` and `:line` are optional), and brings the window to the front. Without a running window it opens a new one. |
| `codalus --new-window Program.cs` | Opens the file (or solution, or folder) in a new window, even when one is running. |
| `codalus --skip-setup` | Starts without the [first-run setup](#the-first-run-setup), for scripts and automation (`CODALUS_SKIP_SETUP=1` does the same). The setup still shows at a later start. |
| `codalus --wait <file>` | Edits the file and exits when its tab closes (git's `core.editor`). |
| `codalus diff <left> <right>`, `codalus merge <local> <remote> <base> <merged>` | Compares two files, or resolves a merge for `git mergetool` ([git.md](git.md)). |
| `codalus --list-plugins` | Prints the plugins you've installed (`--show-versions`: with their versions), and exits. |
| `codalus --install-plugin <id>[@<version>]` | Installs a plugin from the plugin galleries (the **Plugin Sources** setting, and the one that says which sources must sign their packages; see [settings.md](settings.md)), the newest version unless one is given, and exits. Repeatable. A plugin a running window has replaces it when Codalus IDE next starts. |
| `codalus --install-plugin <file>.nupkg` | Installs a plugin package file. |
| `codalus --help`, `codalus --version` | Prints every option, or the version. |

`--user-data-dir <dir>` keeps settings, state and plugins under `<dir>`, for each of these.

To build from source, see the repository's README.

## First start

### The first-run setup

The first time Codalus IDE starts for a user data folder, **Set up Codalus IDE** opens in its own window, before the start window. It opens on a welcome page, then has five steps, each optional, and ends on a summary.

**The welcome page** says what the setup covers and asks where you come from: **The classic IDE**, **VS Code**, **Rider or ReSharper** or **Somewhere else**. Answering is optional and saves nothing. It only preselects the keyboard scheme on the Keyboard step (the Default keys, VS Code's or ReSharper's) and marks the matching importer on Your settings (a `.vssettings` file, or VS Code's settings); you can still choose otherwise on both. **Get Started** goes on; **Use the defaults** ends the setup there.

1. **Appearance**: the theme families, Classic, Harbor, Graphite, Daylight and Ember, as cards with a preview of each: the IDE itself in miniature, drawn in that theme's colors and shapes (the title bar and menu, the toolbar, a document with code in its syntax colors, Solution Explorer and the status bar), and a mode: Light, Dark, High contrast or **Match system** (the default), which follows your system's light, dark and high-contrast settings. The setup window and the IDE behind it change as you choose. Classic with Match system is preselected; on Classic the modes are its Light, Dark and High Contrast, and its Blue is in Tools → Options → Environment → Appearance ([Themes](settings.md#themes)). Next saves the theme you leave chosen, the preselected one too. Under the cards, **Editor font size** has a smaller and a larger button (8 to 32 pixels) and a sample of code in the chosen theme's colors at that size; Next saves it (`editor.fontSize`, in [Settings](settings.md#settings)).
2. **Keyboard**: the keyboard scheme: **Default** (the classic IDE's keys: F5 debugs, F6 builds, Ctrl+Q searches), VS Code's, ReSharper's or Emacs. Your own key bindings go over any of them ([Keyboard shortcuts](settings.md#keyboard-shortcuts)).
3. **Your settings**: import from VS Code (its settings and key bindings, found in its user folder), from a `.vssettings` file another IDE exported, or from a file exported with Tools → Export Settings ([Moving your settings](settings.md#moving-your-settings)); or turn on [Settings Sync](settings.md#settings-sync) with a folder or a Git repository. When the folder or repository already has settings, they replace this machine's, without asking.
4. **Tools**: the .NET SDK and git, found or offered. **Install…** shows exactly what would run; nothing installs until you choose **Install** (or **Run in Terminal**, or **Open Page**). Help → Check Environment lists everything else. Under **Your system**, two choices, both off: **Open Folders from the File Manager** (Open with Codalus IDE on a folder's right-click) and **Open Solutions and Project Files** (`.sln`, `.slnx`, `.slnf`, `.csproj`, `.vbproj`, `.fsproj` open in Codalus IDE). Leaving the step registers what you checked, for you alone ([System integration](settings.md#system-integration)); Skip setup doesn't.
5. **AI**: your AI experience, as three cards. **No AI** is the IDE as it is: nothing is sent anywhere. The other two carry the same four facts, so you can compare them: who answers, what it does, what leaves this machine, and what you need.

   | | A coding agent | The built-in assistant |
   |---|---|---|
   | Who answers | Claude, Codex, Gemini CLI, GitHub Copilot or another, on your account. | A model you choose, hosted or on this machine. |
   | What it does | Works through a task by itself, and answers the AI actions and, once turned on, suggestions as you type. | AI Chat, the AI actions and, if ticked, suggestions as you type. |
   | What leaves this machine | What the agent reads, to its own provider. | Only what a question needs. Secrets and ignored files are never sent. |
   | What you need | Its CLI or Node.js, and a sign-in. | An API key, or a model server on this machine. |

   The cards are short on purpose; the details are in [AI assistance](ai.md). Without AI, completion, refactorings and quick fixes still work: they are the compiler's. An agent works in [AI Console](ai.md#ai-console) or [AI Chat](ai.md#external-agents-acp), and what it reads goes to its provider under that provider's terms: Codalus IDE checks what it asks the IDE for, not what it reads by itself. The built-in assistant sends only what passes Codalus IDE's own checks ([What is never sent](ai.md#what-is-never-sent)).

   **A coding agent** is marked **(recommended)**: it is the way that gets the most from the IDE. **No AI** is still the one chosen unless you choose another, and nothing on this step turns anything on: what you choose is applied only when you end the setup from the summary.
   - **A coding agent** lists Claude, Codex, Gemini CLI and GitHub Copilot, **More agents…** (pi, OpenCode, goose), and any agent you've connected already, marked **Connected**. Below them the step says, in three lines, why to run an agent from Codalus IDE and not from a plain terminal: it gets [the IDE's tools](ai.md#the-ides-tools) (the compiler's symbols and references, the Error List, builds, tests and debugging; in AI Console, for the CLIs that take them), AI Chat shows its edits in a preview and asks before its commands, and Add Selection and Add File point it at the code you're looking at. Two more lines say how its windows differ: AI Console is the agent's own CLI in a terminal tab, which edits and runs by itself; AI Chat is a conversation in the IDE, with the agent's edits previewed and its commands approved.
   - **The built-in assistant** lists the providers by name (OpenAI-compatible, Ollama, Anthropic, and any a plugin adds) and two boxes, both unticked: **Inline suggestions** and **Next edit suggestions**, each with a line on what it sends as you type ([Inline suggestions](ai.md#inline-suggestions)).
   - One or the other. An agent also answers the AI actions and, once you turn them on in **Tools > Options > AI**, suggestions as you type ([The AI features with an external agent](ai.md#the-ai-features-with-an-external-agent)); the setup's suggestion boxes are the built-in assistant's. You can add the other later from **Tools > AI**.

**The summary** ("You're all set") lists what you chose (the theme, the keys, the font size, the tools found and AI), a few keys worth knowing as your keyboard scheme has them (every command, Go To All, build, start debugging), and what its button does:
- **Done** when AI is as it was: the start window opens.
- **Connect Claude…** (or the agent you chose; **Choose an Agent…** for More agents…): the setup closes and [Connect](ai.md#connecting-one) opens in the main window. It checks what the agent needs, signs you in and tests the connection, and AI assistance turns on only when that test passes and you agree there. Cancel it and AI stays off. An agent that is connected already just becomes AI Chat's agent.
- **Set Up Ollama…** (or the provider you chose; **Choose a Provider…** when you chose none): AI assistance turns on, with the suggestions you ticked, and the main window asks for the provider's key, where it needs one, and the model ([Turning it on](ai.md#turning-it-on)).
- **No AI** chosen while AI was on turns it off; your agents and provider stay for when you turn it on again.

The start window follows once that is done.

**Next** saves the step and moves on (`Alt+Right`); **Back** goes back (`Alt+Left`). **Skip setup** (or `Esc`, or closing the window) ends the setup without saving the step you're on, so a new install keeps the defaults: Classic following the system, the Default keys and no AI. Skipping never applies the AI step, whatever is chosen on it. Either way the setup is recorded (`workbench.setupCompleted`) and doesn't show again; **Help → Set Up Codalus IDE** runs it again, from the Appearance step, and its AI step then shows what is on now. The window works from the keyboard: `Tab` moves through a step, the arrow keys move between the cards and modes, and `Space` or `Enter` chooses.

It doesn't show:
- when the command line opens something: a solution, folder or file, `--remote`, `codalus diff`, `codalus merge` or `--wait`;
- with `--skip-setup` or `CODALUS_SKIP_SETUP=1`, and in the smoke test, benchmarks and `--screenshot` runs;
- when Settings Sync is already on (its target is in your settings): your profile comes from it;
- for a user data folder used before the setup existed (one with settings or a saved window), so an upgrade doesn't ask. That holds for the AI step too: if you set Codalus IDE up before it had one, nothing asks you now; Help → Set Up Codalus IDE shows it.

### The start window

**The start window** asks "What would you like to do?" when Codalus IDE starts without a solution:
- **Recent list**: on the left, your recent solutions, projects and folders, grouped as Pinned, Today, Yesterday, This week, This month and Older.
  - Search them with `Alt+S`; `Enter` opens the first match.
  - Pin one with its pin button.
  - Right-click for Open in New Window, Copy Path and Remove from List (`Delete` removes the selected entry).
  - The list shows at once, each entry's icon taken from its name. Whether each entry is still there is checked in the background, a second at most each, so an entry on a network share that doesn't answer never holds up the window: it shows as missing until it answers. Nothing leaves the list by itself.
  - An entry whose files are gone, or that doesn't answer, offers to leave the list when you open it (from here or File → Open Recent).
- **Actions**: on the right, Create a new project (New Project, [New projects and solutions](solutions.md#new-projects-and-solutions); Back on its first page returns here), Open a project or solution, Open a folder, and Clone a repository.
- **Continue without code** (or `Esc`) opens the empty IDE.

File → Start Window brings it back. Environment → On Startup (`workbench.startup`) can open an empty window or the last solution instead (the first recent entry that's there).

**Started from the Dock, Finder or a desktop menu** on macOS and Linux, Codalus IDE reads your login shell's `PATH` and other variables (Homebrew's tools, `DOTNET_ROOT`, proxies), as a terminal has them. The window shows at once; what starts tools waits for those variables, up to 6 seconds: the environment check, the helper processes, git and the start window.

**Several files at once** (Open with on a selection of files, files dropped on the window, or several `codalus <file>` within a moment) open as tabs behind the active one, and only the last becomes active.

**Closing the IDE** hides its windows at once, and the helper processes shut down in parallel in the background, for up to 3 seconds. A `codalus` started meanwhile opens a window of its own.

The Start page has the common actions, recent solutions and a tip that runs what it describes. (Get Started, the theme, keyboard scheme and imports that were at its top, is the first-run setup since 0.3: Help → Set Up Codalus IDE opens it.)

### Opening a solution

1. **Open a solution**: File → Open Solution or Project (`Ctrl+Shift+O`). Pick a `.sln`, `.slnx` or a project file (`.csproj`). You can also pass one on the command line: `codalus path/to/App.sln`.
2. **Trust the folder.** Opening a solution runs code from it: MSBuild evaluates its project files and imports, and restore runs NuGet targets. The first time you open a folder, Codalus IDE asks whether you trust its authors.
   - **Trust and Open** loads the projects, restores packages and starts the language service.
   - **Open Without Trusting** shows the solution's structure only, in restricted mode. You can trust it later: click "Restricted mode" in the status bar.
   - Trust follows the folder, not how you reach it: a folder trusted through a symbolic link is trusted when you open it through its target, and the other way round.
3. The Solution Explorer fills in as projects are evaluated. The status bar shows the progress (*Loading project 29 of 96: …* with a progress bar), the configuration (`Debug | Any CPU`), the startup project and, in a git repository, the branch.

**Running tasks** (loading projects, restoring packages, builds, and plugins' work) show at the left of the status bar where "Ready" is: the newest task's text and a progress bar. **✕** cancels it, and **…** lists every running task (also the command Show Running Tasks).

## Command line

```bash
codalus [solution|project|folder] [--user-data-dir <dir>] [--plugin-dir <dir>] [--version]
```

`--user-data-dir` keeps settings, state and plugins in one folder, for a portable setup or a clean profile.
